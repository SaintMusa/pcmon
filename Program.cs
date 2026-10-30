using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PcMon;

internal static unsafe class Program
{
    const uint ENABLE_PROCESSED_OUTPUT = 0x1, ENABLE_VIRTUAL_TERMINAL_PROCESSING = 0x4, DISABLE_NEWLINE_AUTO_RETURN = 0x8;
    const uint ENABLE_WINDOW_INPUT = 0x8, ENABLE_MOUSE_INPUT = 0x10, ENABLE_EXTENDED_FLAGS = 0x80;
    const int RecordSize = 20;      // INPUT_RECORD
    const uint Infinite = 0xFFFFFFFF;

    static nint _in, _out;
    static uint _inMode, _outMode;
    static int _terminalActive;

    static int Main(string[] args)
    {
        int interval = 1000, width = 150, height = 45, tab = -1, bench = 0, etwTest = 0, startTab = 0;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--version" or "-v":
                    Console.WriteLine($"pcmon {typeof(Program).Assembly.GetName().Version?.ToString(3)}");
                    return 0;
                case "--interval": interval = int.Parse(args[++i]); break;
                case "--snapshot": tab = i + 1 < args.Length && int.TryParse(args[i + 1], out int t) ? (i++, t - 1).Item2 : 0; break;
                case "--tab": startTab = Math.Clamp(int.Parse(args[++i]) - 1, 0, 4); break;
                case "--size": width = int.Parse(args[++i]); height = int.Parse(args[++i]); break;
                case "--bench": bench = int.Parse(args[++i]); break;
                case "--etw-test": etwTest = int.Parse(args[++i]); break;
                case "--cpu-test": return CpuTest(int.Parse(args[++i]));
                default:
                    Console.WriteLine("pcmon: a live resource monitor for the terminal\n\n" +
                                      "  pcmon                  start (press ? inside for keys)\n" +
                                      "  --version              print the version\n" +
                                      "  --interval <ms>        time between updates, default 1000\n" +
                                      "  --tab <1-5>            start on this tab\n" +
                                      "  --snapshot [tab]       print one screen as text (tab 1-5)\n" +
                                      "  --size <w> <h>         screen size for --snapshot\n" +
                                      "  --bench <ticks>        measure pcmon's own cost\n" +
                                      "  --etw-test <seconds>   check kernel tracing (needs admin)\n" +
                                      "  --cpu-test <seconds>   compare CPU numbers with Windows");
                    return args[i] is "-h" or "--help" or "/?" ? 0 : 1;
            }
        }

        bool admin = Environment.IsPrivilegedProcess;
        AppDomain.CurrentDomain.UnhandledException += static (_, e) =>
        {
            Cleanup();
            Console.Error.WriteLine($"pcmon stopped because of an error: {e.ExceptionObject}");
        };

        if (etwTest > 0) return EtwTest(etwTest, admin);
        try
        {
            if (tab >= 0) return Snapshot(tab, width, height, admin);
            if (bench > 0) return Bench(bench, width, height, admin);
            return Run(interval, startTab, admin);
        }
        finally
        {
            Cleanup();
        }
    }

    // --- Normal running --------------------------------------------------------
    // Two threads. This one owns the keyboard and the screen. The collector thread
    // gathers data and signals when a new set is ready. A slow system call on the
    // collector therefore cannot delay a keypress.
    static int Run(int interval, int startTab, bool admin)
    {
        _in = Native.GetStdHandle(-10);
        _out = Native.GetStdHandle(-11);
        uint inMode, outMode;
        if (Native.GetConsoleMode(_in, &inMode) == 0 || Native.GetConsoleMode(_out, &outMode) == 0)
        {
            Console.Error.WriteLine("pcmon needs a real terminal. Use --snapshot to print to a file or pipe.");
            return 1;
        }
        _inMode = inMode;
        _outMode = outMode;
        Native.SetConsoleCtrlHandler(&OnConsoleEvent, 1);

        Native.SetConsoleMode(_out, outMode | ENABLE_PROCESSED_OUTPUT | ENABLE_VIRTUAL_TERMINAL_PROCESSING | DISABLE_NEWLINE_AUTO_RETURN);
        Native.SetConsoleMode(_in, ENABLE_WINDOW_INPUT | ENABLE_MOUSE_INPUT | ENABLE_EXTENDED_FLAGS);
        Write("\x1b[?1049h\x1b[?25l\x1b[?7l\x1b[H");      // alternate screen, hide cursor, no line wrap
        Volatile.Write(ref _terminalActive, 1);

        var sampler = new Sampler(admin);
        var screen = new Screen(_out);
        var app = new App(sampler, screen, admin) { IntervalMs = interval };
        app.SetTab(startTab);
        app.Collect();

        nint dataReady = Native.CreateEventW(null, 0, 0, null);
        using var wake = new AutoResetEvent(false);
        app.Wake = () => wake.Set();

        var collector = new Thread(() =>
        {
            int delay = Math.Min(interval, 300);      // the first rates need only a short baseline
            while (!app.Quit)
            {
                wake.WaitOne(delay);
                if (app.Quit) break;
                long started = Stopwatch.GetTimestamp();
                if (!app.Paused)
                {
                    app.Collect();
                    Native.SetEvent(dataReady);
                }
                int spentMs = (int)((Stopwatch.GetTimestamp() - started) * 1000 / Stopwatch.Frequency);
                delay = Math.Max(1, app.IntervalMs - spentMs);
            }
        }) { IsBackground = true, Name = "collector" };
        collector.Start();

        nint* handles = stackalloc nint[2];
        handles[0] = _in;
        handles[1] = dataReady;
        byte* records = stackalloc byte[RecordSize * 32];

        Render(app, screen);
        while (!app.Quit)
        {
            uint signaled = Native.WaitForMultipleObjects(2, handles, 0, Infinite);     // sleeps until there is work
            bool dirty = signaled == 1;
            if (signaled == 0)
            {
                uint read;
                if (Native.ReadConsoleInputW(_in, records, 32, &read) == 0) break;
                lock (app.Gate)
                    for (uint i = 0; i < read; i++) dirty |= HandleInput(app, records + i * RecordSize);
            }
            else if (signaled != 1) break;

            if (dirty && !app.Quit) Render(app, screen);
        }

        app.Quit = true;
        wake.Set();
        collector.Join(1000);        // a stuck system call must not stop pcmon from exiting
        return 0;
    }

    // The grid is built under the lock, then written out without it, so a slow
    // terminal cannot hold up the collector.
    static void Render(App app, Screen screen)
    {
        lock (app.Gate)
        {
            ResizeToWindow(screen);
            app.Draw();
        }
        screen.Flush();
    }

    static bool HandleInput(App app, byte* record)
    {
        ushort type = *(ushort*)record;
        byte* e = record + 4;
        switch (type)
        {
            case 1:      // key
                if (*(int*)e == 0) return false;        // ignore key releases
                ushort vk = *(ushort*)(e + 6);
                char c = *(char*)(e + 10);
                bool shift = (*(uint*)(e + 12) & 0x10) != 0;
                return app.Key(vk, c, shift);
            case 2:      // mouse
                short x = *(short*)e, y = *(short*)(e + 2);
                uint buttons = *(uint*)(e + 4), flags = *(uint*)(e + 12);
                if (flags == 4) return app.Wheel(x, y, (short)(buttons >> 16));
                if (flags == 0 && (buttons & 1) != 0) return app.Click(x, y);
                return false;
            case 4:      // window resized
                return true;
        }
        return false;
    }

    static void ResizeToWindow(Screen screen)
    {
        short* info = stackalloc short[11];
        if (Native.GetConsoleScreenBufferInfo(_out, info) == 0) return;
        int left = info[5], top = info[6], right = info[7], bottom = info[8];
        screen.Resize(right - left + 1, bottom - top + 1);
    }

    static void Write(string text)
    {
        fixed (char* p = text)
        {
            uint written;
            Native.WriteConsoleW(_out, p, (uint)text.Length, &written, null);
        }
    }

    // Puts the terminal back and stops tracing. Safe to call from any exit path.
    static void Cleanup()
    {
        Etw.Stop();
        if (Interlocked.Exchange(ref _terminalActive, 0) == 1)
        {
            Write("\x1b[0m\x1b[?7h\x1b[?25h\x1b[?1049l");
            Native.SetConsoleMode(_in, _inMode);
            Native.SetConsoleMode(_out, _outMode);
        }
    }

    // Runs on Ctrl+Break, when the window is closed, and at logoff or shutdown
    [UnmanagedCallersOnly]
    static int OnConsoleEvent(uint type)
    {
        Cleanup();
        return 0;
    }

    // --- Testing and measuring ---------------------------------------------------
    static int Snapshot(int tab, int width, int height, bool admin)
    {
        var sampler = new Sampler(admin);
        var screen = new Screen(0);
        screen.Resize(width, height);
        var app = new App(sampler, screen, admin);
        app.SetTab(tab);
        app.Collect();
        Thread.Sleep(admin && tab is 3 or 4 ? 3000 : 1000);     // kernel events arrive about once a second
        foreach (string line in app.Snapshot(tab)) Console.Out.WriteLine(line);
        return 0;
    }

    static int Bench(int ticks, int width, int height, bool admin)
    {
        var sampler = new Sampler(admin);
        var screen = new Screen(0);
        screen.Resize(width, height);
        var app = new App(sampler, screen, admin);
        Console.Out.WriteLine($"process list class {Layout.ProcessClass}   disk counters {Layout.HasDiskCounters} {Layout.Problem}");

        for (int tab = 0; tab < 5; tab++)
        {
            app.SetTab(tab);
            app.Collect();
            Thread.Sleep(200);

            double collectTotal = 0, collectMax = 0, drawTotal = 0;
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            int collections = GC.CollectionCount(0);
            for (int i = 0; i < ticks; i++)
            {
                long a = Stopwatch.GetTimestamp();
                app.Collect();
                long b = Stopwatch.GetTimestamp();
                app.Draw();
                long c = Stopwatch.GetTimestamp();
                double ms = (b - a) * 1000.0 / Stopwatch.Frequency;
                collectTotal += ms;
                collectMax = Math.Max(collectMax, ms);
                drawTotal += (c - b) * 1000.0 / Stopwatch.Frequency;
                Thread.Sleep(20);
            }
            long bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
            Console.Out.WriteLine($"tab {tab + 1}: collect avg {collectTotal / ticks:F2} ms (max {collectMax:F2})   draw avg {drawTotal / ticks:F3} ms   allocated {bytes / ticks} bytes/tick   gen0 GCs {GC.CollectionCount(0) - collections}");
        }
        using Process me = Process.GetCurrentProcess();
        Console.Out.WriteLine($"working set {me.WorkingSet64 >> 20} MB   private {me.PrivateMemorySize64 >> 20} MB   processes {sampler.ProcessCount}   threads {sampler.ThreadCount}   network denied {sampler.NetDeniedCount}");
        return 0;
    }

    // Compares pcmon's CPU numbers with Windows' own counters, one line per second
    static int CpuTest(int seconds)
    {
        var sampler = new Sampler(false);
        nint query, time, utility;
        Native.PdhOpenQueryW(null, 0, &query);
        fixed (char* p = @"\Processor(_Total)\% Processor Time") Native.PdhAddEnglishCounterW(query, p, 0, &time);
        fixed (char* p = @"\Processor Information(_Total)\% Processor Utility") Native.PdhAddEnglishCounterW(query, p, 0, &utility);
        Native.PdhCollectQueryData(query);
        sampler.Tick(false, false);
        byte* value = stackalloc byte[16];
        for (int i = 0; i < seconds; i++)
        {
            Thread.Sleep(1000);
            sampler.Tick(false, false);
            Native.PdhCollectQueryData(query);
            Native.PdhGetFormattedCounterValue(time, 0x200, null, value);
            double pdhTime = *(double*)(value + 8);
            Native.PdhGetFormattedCounterValue(utility, 0x200 | 0x8000, null, value);
            double pdhUtility = *(double*)(value + 8);
            double sum = 0;
            foreach (Proc p in sampler.Procs) sum += p.Cpu;
            Console.Out.WriteLine($"pcmon total {sampler.CpuTotal,5:F1}   process sum {sum,5:F1}   Windows time {pdhTime,5:F1}   Windows utility {pdhUtility,5:F1}   elapsed {sampler.Elapsed:F2}");
        }
        return 0;
    }

    static int EtwTest(int seconds, bool admin)
    {
        Console.Out.WriteLine($"administrator: {admin}");
        if (!admin) { Console.Out.WriteLine("Kernel tracing needs administrator rights."); return 2; }
        try
        {
            var sampler = new Sampler(true);
            sampler.Tick(false, false, wantTrace: true);
            if (sampler.TraceProblem.Length > 0) { Console.Out.WriteLine($"Start failed: {sampler.TraceProblem}"); return 1; }

            double pdhDisk = 0, pdhNet = 0;
            for (int i = 0; i < seconds; i++)
            {
                Thread.Sleep(1000);
                sampler.Tick(false, false, wantTrace: true);
                pdhDisk += sampler.DiskRead + sampler.DiskWrite;
                pdhNet += sampler.NetRecv + sampler.NetSend;
            }
            Console.Out.WriteLine($"events {Etw.EventCount}   disk {Etw.DiskEvents}   network {Etw.NetEvents}   file names {Etw.NameEvents}   status {Etw.Status}");
            Console.Out.WriteLine($"machine average: disk {Fmt.Size(pdhDisk / seconds)}/s   network {Fmt.Size(pdhNet / seconds)}/s");
            Console.Out.WriteLine("Top network addresses:");
            foreach (NetRow r in sampler.NetRows.OrderByDescending(r => r.Send + r.Recv).Take(6))
                Console.Out.WriteLine($"  {r.Image,-24} {r.Address,-22} send {Fmt.Size(r.Send)}/s  recv {Fmt.Size(r.Recv)}/s");
            Console.Out.WriteLine("Top files:");
            foreach (FileRow f in sampler.Files.OrderByDescending(f => f.Read + f.Write).Take(6))
                Console.Out.WriteLine($"  {f.Image,-20} {f.File}  {Fmt.Size(f.Read + f.Write)}/s  {f.ResponseMs:F1} ms");
            return 0;
        }
        finally { Etw.Stop(); }
    }
}
