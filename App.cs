namespace PcMon;

// What the user sees: five tabs, each made of graphs and tables
internal sealed unsafe class App
{
    enum Mode { Normal, Filter, ConfirmKill, Help, Detail }

    static readonly string[] TabNames = ["Overview", "CPU", "Memory", "Disk", "Network"];
    static readonly char[] Blocks = [' ', '▁', '▂', '▃', '▄', '▅', '▆', '▇', '█'];
    static readonly int[] Intervals = [250, 500, 1000, 2000, 5000];

    readonly Sampler _s;
    readonly Screen _scr;
    readonly bool _admin;
    readonly HashSet<uint> _pins = [];
    readonly Panel[][] _tabs;
    readonly int[] _focus = new int[5];
    readonly int[] _tabX = new int[6];
    readonly List<ServiceRowView> _serviceViews = [];

    volatile int _tab;
    Mode _mode;
    bool _expanded, _tree;
    uint _detailPid;
    readonly Dictionary<uint, List<Proc>> _children = [];
    readonly List<Proc> _roots = [], _treeOut = [];
    readonly HashSet<uint> _present = [];
    string _filter = "";
    string _message = "";
    Proc? _killTarget;
    public volatile int IntervalMs = 1000;
    public volatile bool Paused, Quit;
    public readonly object Gate = new();          // held while shared data is read or changed
    public Action? Wake;                          // asks the collector to refresh now

    // What the collector needs for the tab on screen
    public bool WantSockets => _tab == 4;
    public bool WantServices => _tab == 1;
    public bool WantTrace => _tab is 3 or 4;

    sealed class ServiceRowView
    {
        public ServiceRow Row = null!;
        public float Cpu;
    }

    public App(Sampler sampler, Screen screen, bool admin)
    {
        _s = sampler; _scr = screen; _admin = admin;

        string adminHint = _admin ? "Waiting for activity..." : "Run as administrator to see this (Windows limits it to admins).";

        var overview = ProcessTable("Processes", 4, true, All,
            Name(), PidCol(), Status(), Threads(), CpuCol(), AvgCol(),
            MemCol("Working Set", 12, p => p.WorkingSet),
            RateCol(Layout.HasDiskCounters ? "Disk/s" : "I/O/s", p => p.DiskRead + p.DiskWrite),
            RateCol("Net/s", p => p.NetSend + p.NetRecv));

        var cpu = ProcessTable("Processes", 6, true, All,
            Name(), PidCol(), Status(), Threads(),
            new("Handles", 8, true, static (p, d) => Fmt.Int(d, p.Handles), static (a, b) => a.Handles.CompareTo(b.Handles)),
            new("Session", 8, true, static (p, d) => Fmt.Int(d, p.Session), static (a, b) => a.Session.CompareTo(b.Session)),
            CpuCol(), AvgCol());

        var services = new Table<ServiceRowView>("Services",
        [
            new("Name", 26, false, static (r, d) => Fmt.Str(d, r.Row.Name), static (a, b) => string.Compare(a.Row.Name, b.Row.Name, StringComparison.OrdinalIgnoreCase)),
            new("PID", 7, true, static (r, d) => Fmt.Int(d, r.Row.Pid), static (a, b) => a.Row.Pid.CompareTo(b.Row.Pid)),
            new("CPU", 6, true, static (r, d) => Fmt.Pct(d, r.Cpu), static (a, b) => a.Cpu.CompareTo(b.Cpu)),
            new("Description", 0, false, static (r, d) => Fmt.Str(d, r.Row.Display), static (a, b) => string.Compare(a.Row.Display, b.Row.Display, StringComparison.OrdinalIgnoreCase)),
        ], FillServices, static r => r.Row.Pid, 2, true, static (a, b) => string.CompareOrdinal(a.Row.Name, b.Row.Name));

        var memory = ProcessTable("Processes", 4, true, All,
            Name(), PidCol(),
            new("Hard Faults/s", 13, true, static (p, d) => Fmt.Rate(d, p.HardFaults), static (a, b) => a.HardFaults.CompareTo(b.HardFaults), HeatFault),
            MemCol("Commit", 11, p => p.Commit), MemCol("Working Set", 12, p => p.WorkingSet),
            MemCol("Shareable", 11, p => p.Shareable), MemCol("Private", 11, p => p.Private));

        var storage = new Table<DiskRow>("Physical disks",
        [
            new("Disk", 0, false, static (r, d) => Fmt.Str(d, r.Name), static (a, b) => string.CompareOrdinal(a.Name, b.Name)),
            new("Read/s", 11, true, static (r, d) => Fmt.Bytes(d, r.Read), static (a, b) => a.Read.CompareTo(b.Read)),
            new("Write/s", 11, true, static (r, d) => Fmt.Bytes(d, r.Write), static (a, b) => a.Write.CompareTo(b.Write)),
            new("Active %", 9, true, static (r, d) => Fmt.Pct(d, r.Active), static (a, b) => a.Active.CompareTo(b.Active)),
            new("Queue", 7, true, static (r, d) => Fmt.Pct(d, r.Queue), static (a, b) => a.Queue.CompareTo(b.Queue)),
            new("Latency ms", 11, true, static (r, d) => Fmt.Pct(d, r.LatencyMs), static (a, b) => a.LatencyMs.CompareTo(b.LatencyMs)),
        ], l => l.AddRange(_s.Disks), static _ => 0, 0, false, static (a, b) => string.CompareOrdinal(a.Name, b.Name));

        var volumes = new Table<VolumeRow>("Volumes",
        [
            new("Drive", 7, false, static (r, d) => Fmt.Str(d, r.Name), static (a, b) => string.CompareOrdinal(a.Name, b.Name)),
            new("Free", 11, true, static (r, d) => Fmt.Bytes(d, r.Free), static (a, b) => a.Free.CompareTo(b.Free)),
            new("Total", 11, true, static (r, d) => Fmt.Bytes(d, r.Total), static (a, b) => a.Total.CompareTo(b.Total)),
            new("Used %", 8, true, static (r, d) => Fmt.Pct(d, 100.0 * (r.Total - r.Free) / r.Total), static (a, b) => ((double)a.Free / a.Total).CompareTo((double)b.Free / b.Total)),
        ], l => l.AddRange(_s.Volumes), static _ => 0, 0, false, static (a, b) => string.CompareOrdinal(a.Name, b.Name));

        Table<Proc> diskProcs = ProcessTable(Layout.HasDiskCounters ? "Disk by process" : "I/O by process (disk counters unavailable)", 4, true,
            p => p.DiskRead + p.DiskWrite >= 1 && Keep(p),
            Name(), PidCol(), RateCol("Read/s", p => p.DiskRead), RateCol("Write/s", p => p.DiskWrite), RateCol("Total/s", p => p.DiskRead + p.DiskWrite));

        var files = new Table<FileRow>("Disk by file",
        [
            new("Image", 18, false, static (r, d) => Fmt.Str(d, r.Image), static (a, b) => string.Compare(a.Image, b.Image, StringComparison.OrdinalIgnoreCase)),
            new("PID", 7, true, static (r, d) => Fmt.Int(d, r.Pid), static (a, b) => a.Pid.CompareTo(b.Pid)),
            new("File", 0, false, static (r, d) => Fmt.Str(d, r.File), static (a, b) => string.Compare(a.File, b.File, StringComparison.OrdinalIgnoreCase)),
            new("Read/s", 10, true, static (r, d) => Fmt.Bytes(d, r.Read), static (a, b) => a.Read.CompareTo(b.Read)),
            new("Write/s", 10, true, static (r, d) => Fmt.Bytes(d, r.Write), static (a, b) => a.Write.CompareTo(b.Write)),
            new("Resp ms", 8, true, static (r, d) => Fmt.Pct(d, r.ResponseMs), static (a, b) => a.ResponseMs.CompareTo(b.ResponseMs)),
        ], l => { foreach (FileRow r in _s.Files) if (Pass(r.Pid, r.Image, r.File)) l.Add(r); }, static r => r.Pid, 3, true,
           static (a, b) => string.CompareOrdinal(a.File, b.File)) { Note = adminHint };

        Table<Proc> netProcs = ProcessTable("Network by process", 4, true, p => p.NetSend + p.NetRecv >= 1 && Keep(p),
            Name(), PidCol(), RateCol("Send/s", p => p.NetSend), RateCol("Receive/s", p => p.NetRecv), RateCol("Total/s", p => p.NetSend + p.NetRecv));
        netProcs.Note = "No network activity right now";

        var netRows = new Table<NetRow>("Network by address",
        [
            new("Image", 14, false, static (r, d) => Fmt.Str(d, r.Image), static (a, b) => string.Compare(a.Image, b.Image, StringComparison.OrdinalIgnoreCase)),
            new("PID", 6, true, static (r, d) => Fmt.Int(d, r.Pid), static (a, b) => a.Pid.CompareTo(b.Pid)),
            new("Address", 0, false, static (r, d) => Fmt.Str(d, r.Address), static (a, b) => string.CompareOrdinal(a.Address, b.Address)),
            new("Send/s", 9, true, static (r, d) => Fmt.Bytes(d, r.Send), static (a, b) => a.Send.CompareTo(b.Send)),
            new("Receive/s", 9, true, static (r, d) => Fmt.Bytes(d, r.Recv), static (a, b) => a.Recv.CompareTo(b.Recv)),
        ], l => { foreach (NetRow r in _s.NetRows) if (Pass(r.Pid, r.Image, r.Address)) l.Add(r); }, static r => r.Pid, 4, true,
           static (a, b) => string.CompareOrdinal(a.Address, b.Address)) { Note = adminHint };

        var tcp = new Table<TcpRow>("TCP connections",
        [
            new("Image", 14, false, static (r, d) => Fmt.Str(d, r.Image), static (a, b) => string.Compare(a.Image, b.Image, StringComparison.OrdinalIgnoreCase)),
            new("PID", 6, true, static (r, d) => Fmt.Int(d, r.Pid), static (a, b) => a.Pid.CompareTo(b.Pid)),
            new("Local", 12, false, static (r, d) => Fmt.Str(d, r.Local), static (a, b) => string.CompareOrdinal(a.Local, b.Local)),
            new("Port", 5, true, static (r, d) => Fmt.Int(d, r.LocalPort), static (a, b) => a.LocalPort.CompareTo(b.LocalPort)),
            new("Remote", 0, false, static (r, d) => Fmt.Str(d, r.Remote), static (a, b) => string.CompareOrdinal(a.Remote, b.Remote)),
            new("Port", 5, true, static (r, d) => Fmt.Int(d, r.RemotePort), static (a, b) => a.RemotePort.CompareTo(b.RemotePort)),
            new("State", 11, false, static (r, d) => Fmt.Str(d, r.State), static (a, b) => string.CompareOrdinal(a.State, b.State)),
        ], l => { foreach (TcpRow r in _s.Tcp) if (Pass(r.Pid, r.Image, r.Remote)) l.Add(r); }, static r => r.Pid, 0, false,
           static (a, b) => a.LocalPort.CompareTo(b.LocalPort));

        var listening = new Table<ListenRow>("Listening ports",
        [
            new("Image", 14, false, static (r, d) => Fmt.Str(d, r.Image), static (a, b) => string.Compare(a.Image, b.Image, StringComparison.OrdinalIgnoreCase)),
            new("PID", 6, true, static (r, d) => Fmt.Int(d, r.Pid), static (a, b) => a.Pid.CompareTo(b.Pid)),
            new("Address", 0, false, static (r, d) => Fmt.Str(d, r.Address), static (a, b) => string.CompareOrdinal(a.Address, b.Address)),
            new("Port", 5, true, static (r, d) => Fmt.Int(d, r.Port), static (a, b) => a.Port.CompareTo(b.Port)),
            new("Proto", 5, false, static (r, d) => Fmt.Str(d, r.Protocol), static (a, b) => string.CompareOrdinal(a.Protocol, b.Protocol)),
        ], l => { foreach (ListenRow r in _s.Listening) if (Pass(r.Pid, r.Image, r.Address)) l.Add(r); }, static r => r.Pid, 3, false,
           static (a, b) => a.Pid.CompareTo(b.Pid));

        _tabs =
        [
            [overview],
            [cpu, services],
            [memory],
            [diskProcs, files, storage, volumes],
            [netProcs, netRows, tcp, listening],
        ];
    }

    // --- Table helpers --------------------------------------------------------
    Table<Proc> ProcessTable(string title, int sort, bool desc, Func<Proc, bool> filter, params Col<Proc>[] cols) =>
        new(title, cols,
            l => { foreach (Proc p in _s.Procs) if (filter(p)) l.Add(p); },
            static p => p.Pid, sort, desc,
            static (a, b) => { int c = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase); return c != 0 ? c : a.Pid.CompareTo(b.Pid); },
            static p => p.Pinned) { Reorder = TreeOrder };

    // Tree view: places each child under its parent, keeping the sort order among siblings.
    // The start-time check is needed because Windows reuses process IDs.
    void TreeOrder(List<Proc> list)
    {
        foreach (Proc p in list) p.Depth = 0;
        if (!_tree) return;

        _present.Clear();
        _roots.Clear();
        _treeOut.Clear();
        foreach (List<Proc> kids in _children.Values) kids.Clear();
        foreach (Proc p in list) _present.Add(p.Pid);

        foreach (Proc p in list)
        {
            Proc? parent = _present.Contains(p.Parent) ? _s.Find(p.Parent) : null;
            if (parent != null && parent.Pid != p.Pid && parent.CreateTime <= p.CreateTime)
            {
                if (!_children.TryGetValue(parent.Pid, out List<Proc>? kids)) _children[parent.Pid] = kids = [];
                kids.Add(p);
            }
            else _roots.Add(p);
        }

        foreach (Proc root in _roots) Visit(root, 0);
        list.Clear();
        list.AddRange(_treeOut);
    }

    void Visit(Proc p, int depth)
    {
        p.Depth = depth;
        _treeOut.Add(p);
        if (depth < 32 && _children.TryGetValue(p.Pid, out List<Proc>? kids))
            foreach (Proc child in kids) Visit(child, depth + 1);
    }

    bool All(Proc p) => _filter.Length == 0 || p.Name.Contains(_filter, StringComparison.OrdinalIgnoreCase);
    bool Keep(Proc p) => (_pins.Count == 0 || p.Pinned) && All(p);

    // Rows outside the process lists are limited to pinned processes and the text filter
    bool Pass(uint pid, string image, string extra) =>
        (_pins.Count == 0 || _pins.Contains(pid)) &&
        (_filter.Length == 0 || image.Contains(_filter, StringComparison.OrdinalIgnoreCase) || extra.Contains(_filter, StringComparison.OrdinalIgnoreCase));

    void FillServices(List<ServiceRowView> list)
    {
        while (_serviceViews.Count < _s.Services.Count) _serviceViews.Add(new ServiceRowView());
        for (int i = 0; i < _s.Services.Count; i++)
        {
            ServiceRow row = _s.Services[i];
            if (!Pass(row.Pid, row.Name, row.Display)) continue;
            ServiceRowView view = _serviceViews[i];
            view.Row = row;
            view.Cpu = _s.Find(row.Pid)?.Cpu ?? 0;
            list.Add(view);
        }
    }

    static byte HeatCpu(Proc p) => p.Cpu >= 25 ? Color.Red : p.Cpu >= 8 ? Color.Orange : p.Cpu >= 2 ? Color.Yellow : Color.Text;
    static byte HeatFault(Proc p) => p.HardFaults >= 100 ? Color.Red : p.HardFaults >= 10 ? Color.Orange : Color.Text;

    static Col<Proc> Name() => new("Image", 0, false, static (p, d) => Fmt.Tree(d, p),
        static (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
    static Col<Proc> PidCol() => new("PID", 7, true, static (p, d) => Fmt.Int(d, p.Pid), static (a, b) => a.Pid.CompareTo(b.Pid));
    static Col<Proc> Status() => new("Status", 9, false, static (p, d) => Fmt.Str(d, p.Suspended ? "Suspended" : "Running"),
        static (a, b) => a.Suspended.CompareTo(b.Suspended), static p => p.Suspended ? Color.Orange : Color.Dim);
    static Col<Proc> Threads() => new("Threads", 8, true, static (p, d) => Fmt.Int(d, p.Threads), static (a, b) => a.Threads.CompareTo(b.Threads));
    static Col<Proc> CpuCol() => new("CPU %", 7, true, static (p, d) => Fmt.Pct(d, p.Cpu), static (a, b) => a.Cpu.CompareTo(b.Cpu), HeatCpu);
    static Col<Proc> AvgCol() => new("Avg %", 7, true, static (p, d) => Fmt.Pct(d, p.AvgCpu), static (a, b) => a.AvgCpu.CompareTo(b.AvgCpu));
    static Col<Proc> MemCol(string title, int width, Func<Proc, long> get) =>
        new(title, width, true, (p, d) => Fmt.Bytes(d, get(p)), (a, b) => get(a).CompareTo(get(b)));
    static Col<Proc> RateCol(string title, Func<Proc, double> get) =>
        new(title, 11, true, (p, d) => Fmt.Bytes(d, get(p)), (a, b) => get(a).CompareTo(get(b)));

    // --- Data ------------------------------------------------------------------
    // Slow system calls happen outside the lock, so the UI thread is never held up by them
    public void Collect()
    {
        _s.Capture(WantSockets, WantServices, WantTrace);
        lock (Gate)
        {
            _s.Apply();
            UpdatePanels();
        }
    }

    void UpdatePanels()
    {
        // A tracing failure must read as a failure, not as an empty table
        if (_s.TraceProblem.Length > 0)
        {
            _tabs[3][1].Note = $"Kernel tracing is unavailable: {_s.TraceProblem}";
            _tabs[4][1].Note = $"Kernel tracing is unavailable: {_s.TraceProblem}";
        }
        foreach (Panel p in _tabs[_tab]) p.Update();
    }

    public void SetTab(int tab)
    {
        if (tab == _tab) return;
        _tab = tab;
        _mode = Mode.Normal;
        UpdatePanels();
        Wake?.Invoke();       // the new tab may need data the last one did not
    }

    Panel Focused => _tabs[_tab][_focus[_tab]];

    // --- Drawing ---------------------------------------------------------------
    public void Draw()
    {
        Screen s = _scr;
        s.Clear();
        if (s.W < 60 || s.H < 16)
        {
            s.Text(0, 0, "Window too small for pcmon (need 60 x 16)", Color.Yellow);
            return;
        }

        DrawTabs();
        DrawSummary();

        int top = 2, bottom = s.H - 2;
        switch (_tab)
        {
            case 0: top = DrawOverviewGraphs(top); Stack(top, bottom, 1); break;
            case 1: top = DrawCores(top); Stack(top, bottom, 3, 2); break;
            case 2: top = DrawMemory(top); Stack(top, bottom, 1); break;
            case 3: DrawDisk(top, bottom); break;
            case 4: DrawNetwork(top, bottom); break;
        }

        Panel[] panels = _tabs[_tab];
        if (_expanded)
        {
            // One panel takes the whole body, like the expand key in bottom
            for (int i = 0; i < panels.Length; i++) Place(panels[i], 0, 0, 0, 0);
            s.Fill(0, 2, s.W, bottom - 2);
            Place(Focused, 0, 2, s.W, bottom - 2);
            Focused.Draw(s, true);
        }
        else for (int i = 0; i < panels.Length; i++) panels[i].Draw(s, i == _focus[_tab]);

        DrawFooter();
        if (_mode == Mode.Help) DrawHelp();
        if (_mode == Mode.Detail) DrawDetail();
    }

    void DrawDetail()
    {
        Screen s = _scr;
        Proc? p = _s.Find(_detailPid);
        if (p == null) { _mode = Mode.Normal; return; }

        int w = Math.Min(s.W - 4, 100), h = 14, x = (s.W - w) / 2, y = Math.Max(0, (s.H - h) / 2);
        s.Fill(x, y, w, h, ' ', Color.Text, Color.BarBg);
        s.Frame(x, y, w, h, "Process details", Color.Accent, Color.Accent, default, Color.Dim, Color.BarBg);
        s.Text(x + 2, y + 1, p.Name, Color.Accent, Color.BarBg, 1);
        string state = p.Suspended ? "Suspended" : "Running";
        s.Text(x + 2, y + 2, $"PID {p.Pid}   Parent {_s.NameOf(p.Parent)} ({p.Parent})   Session {p.Session}   {state}", Color.Text, Color.BarBg, 0, w - 4);
        s.Text(x + 2, y + 3, $"Started {DateTime.FromFileTime(p.CreateTime):yyyy-MM-dd HH:mm:ss}   Threads {p.Threads}   Handles {p.Handles}", Color.Text, Color.BarBg, 0, w - 4);
        s.Text(x + 2, y + 5, $"CPU {p.Cpu:F1}%   Avg {p.AvgCpu:F1}%   Hard faults {p.HardFaults:F0}/s", Color.Text, Color.BarBg, 0, w - 4);
        s.Text(x + 2, y + 6, $"Working set {Fmt.Size(p.WorkingSet)}   Private {Fmt.Size(p.Private)}   Commit {Fmt.Size(p.Commit)}", Color.Text, Color.BarBg, 0, w - 4);
        s.Text(x + 2, y + 7, $"Disk now {Fmt.Size(p.DiskRead)}/s read, {Fmt.Size(p.DiskWrite)}/s write   Total {Fmt.Size(p.TotalDiskRead)} read, {Fmt.Size(p.TotalDiskWrite)} written", Color.Text, Color.BarBg, 0, w - 4);
        s.Text(x + 2, y + 8, p.NetDenied ? "Network: Windows denies access to this process" : $"Network now {Fmt.Size(p.NetRecv)}/s in, {Fmt.Size(p.NetSend)}/s out   Total {Fmt.Size(p.TotalNetIn)} in, {Fmt.Size(p.TotalNetOut)} out", Color.Text, Color.BarBg, 0, w - 4);

        string command = Describe(p);
        int line = y + 10, width = w - 4;
        for (int i = 0; i < 2 && (i * width) < command.Length; i++)
            s.Text(x + 2, line + i, command.AsSpan(i * width), Color.Dim, Color.BarBg, 0, width);
        s.Text(x + 2, y + h - 2, "Press any key to close", Color.Dim, Color.BarBg);
    }

    static string Describe(Proc p) =>
        p.Detail ??= Native.ProcessCommandLine(p.Pid) ?? Native.ProcessPath(p.Pid) ?? "(protected process: Windows hides its path)";

    void DrawTabs()
    {
        Screen s = _scr;
        s.Fill(0, 0, s.W, 1, ' ', Color.Text, Color.BarBg);
        int x = s.Text(1, 0, "pcmon", Color.Accent, Color.BarBg, 1) + 2;
        for (int i = 0; i < TabNames.Length; i++)
        {
            _tabX[i] = x;
            bool active = i == _tab;
            byte bg = active ? Color.Accent : Color.BarBg, fg = active ? Color.Black : Color.Text;
            s.Put(x++, 0, ' ', fg, bg);
            s.Put(x++, 0, (char)('1' + i), active ? Color.Black : Color.Yellow, bg);
            s.Put(x++, 0, ' ', fg, bg);
            x = s.Text(x, 0, TabNames[i], fg, bg, active ? (byte)1 : (byte)0);
            s.Put(x++, 0, ' ', fg, bg);
            x++;
        }
        _tabX[5] = x;

        string right = (Paused ? "PAUSED  " : "") + (_admin ? "admin  " : "limited  ") + IntervalMs + " ms";
        s.Text(s.W - right.Length - 1, 0, right, Paused ? Color.Yellow : Color.Dim, Color.BarBg);
    }

    void DrawSummary()
    {
        Screen s = _scr;
        int x = 1, y = 1;
        x = Meter(x, y, "CPU", _s.CpuTotal, Color.Green);
        double ramPct = 100.0 * (_s.TotalPhys - _s.AvailPhys) / Math.Max(1, _s.TotalPhys);
        x = Meter(x, y, "RAM", ramPct, Color.Blue);
        x = Meter(x, y, "Disk", _s.DiskActive, Color.Yellow);
        x = Label(x, y, "Net ", Fmt.Size(_s.NetRecv + _s.NetSend) + "/s");
        x = Label(x, y, "Proc ", _s.ProcessCount.ToString());
        x = Label(x, y, "Thr ", _s.ThreadCount.ToString());
        Label(x, y, "Up ", $"{(int)_s.Uptime.TotalDays}d {_s.Uptime.Hours}h {_s.Uptime.Minutes}m");
    }

    int Label(int x, int y, string label, string value)
    {
        x = _scr.Text(x, y, label, Color.Dim);
        x = _scr.Text(x, y, value, Color.Text);
        return x + 2;
    }

    int Meter(int x, int y, string label, double pct, byte color)
    {
        Screen s = _scr;
        x = s.Text(x, y, label, Color.Dim) + 1;
        const int Width = 10;
        double fill = Math.Clamp(pct, 0, 100) / 100 * Width;
        for (int i = 0; i < Width; i++)
        {
            // Block characters from 1/8 to 7/8 wide make the bar's end smooth
            int eighths = (int)Math.Round(Math.Clamp(fill - i, 0, 1) * 8);
            char c = eighths == 0 ? ' ' : eighths == 8 ? (char)0x2588 : (char)(0x2590 - eighths);
            s.Put(x + i, y, c, color, Color.BarBg);
        }
        x += Width + 1;
        Span<char> text = stackalloc char[8];
        pct.TryFormat(text, out int n, "F0");
        text[n++] = '%';
        return s.Text(x, y, text[..n], color, 0, 1) + 2;
    }
    void Graph(int x, int y, int w, int h, History history, double max, byte color)
    {
        Screen s = _scr;
        for (int c = 0; c < w && c < history.Size; c++)
        {
            int column = x + w - 1 - c;
            int level = (int)Math.Round(Math.Clamp(history.Back(c) / max, 0, 1) * h * 8);
            for (int r = 0; r < h; r++)
            {
                int eighths = Math.Clamp(level - r * 8, 0, 8);
                s.Put(column, y + h - 1 - r, eighths == 0 ? ' ' : Blocks[eighths], color);
            }
        }
    }

    void GraphBox(int x, int y, int w, int h, string title, string value, History history, double max, byte color)
    {
        _scr.Frame(x, y, w, h, title, Color.Line, Color.Text, value, color);
        Graph(x + 1, y + 1, w - 2, h - 2, history, max, color);
    }

    int DrawOverviewGraphs(int y)
    {
        int h = _scr.H >= 30 ? 9 : 6;
        int w = _scr.W / 4, x = 0;
        GraphBox(x, y, w, h, "CPU", $"{_s.CpuTotal:F0}%  {_s.MaxFrequencyPct:F0}% freq", _s.CpuHistory, 100, Color.Green);
        x += w;
        GraphBox(x, y, w, h, "Disk", $"{Fmt.Size(_s.DiskRead + _s.DiskWrite)}/s  {_s.DiskActive:F0}%", _s.DiskHistory, Scale(_s.DiskHistory, w, 1 << 20), Color.Yellow);
        x += w;
        GraphBox(x, y, w, h, "Network", $"{Fmt.Size(_s.NetRecv + _s.NetSend)}/s", _s.NetHistory, Scale(_s.NetHistory, w, 1 << 16), Color.Purple);
        x += w;
        GraphBox(x, y, _scr.W - x, h, "Memory", $"{_s.HardFaults:F0} hard faults/s", _s.FaultHistory, Scale(_s.FaultHistory, w, 50), Color.Blue);
        return y + h;
    }
    static double Scale(History h, int width, double minimum) => Math.Max(minimum, h.Max(width) * 1.1);

    int DrawCores(int y)
    {
        Screen s = _scr;
        int columns = Math.Clamp((s.W - 2) / 26, 2, 8);
        int rows = (int)Math.Ceiling(_s.Cores / (double)columns);
        int cellH = s.H >= 44 ? 3 : s.H >= 28 ? 2 : 1;
        int h = rows * (cellH + 1) + 2;
        int w = (s.W - 2 - (columns - 1)) / columns;

        s.Frame(0, y, s.W, h, "CPU cores", Color.Line, Color.Text,
                $"{_s.FrequencyMHz / 1000:F2} GHz  {_s.MaxFrequencyPct:F0}% of base  {_s.ContextSwitches:F0} switches/s  {_s.SystemCalls:F0} calls/s");

        // Thin lines between the columns of cores
        for (int c = 1; c < columns; c++)
        {
            int line = c * (w + 1);
            for (int r = y + 1; r < y + h - 1; r++) s.Put(line, r, (char)0x2502, Color.Line);
        }

        Span<char> label = stackalloc char[16];
        for (int i = 0; i < _s.Cores; i++)
        {
            int cx = 2 + i % columns * (w + 1), cy = y + 1 + i / columns * (cellH + 1);
            label[0] = 'C'; label[1] = 'P'; label[2] = 'U';
            int n = 3 + Fmt.Int(label[3..], i);
            s.Text(cx, cy, label[..n], Color.Dim);
            byte color = _s.CoreUsage[i] >= 85 ? Color.Red : _s.CoreUsage[i] >= 50 ? Color.Orange : Color.Green;
            _s.CoreUsage[i].TryFormat(label, out int m, "F0");
            label[m++] = '%';
            s.Text(cx + w - 2 - m, cy, label[..m], color, 0, 1);
            Graph(cx, cy + 1, w - 2, cellH, _s.CoreHistory[i], 100, color);
        }
        return y + h;
    }

    int DrawMemory(int y)
    {
        Screen s = _scr;
        double total = Math.Max(_s.InstalledBytes, _s.TotalPhys);
        double reserved = total - _s.TotalPhys;
        double inUse = Math.Max(0, _s.TotalPhys - _s.Modified - _s.Standby - _s.Free);
        double commitPct = 100.0 * _s.CommitUsed / Math.Max(1, _s.CommitLimit);

        s.Frame(0, y, s.W, 5, "Physical memory", Color.Line, Color.Text, $"Commit {Fmt.Size(_s.CommitUsed)} / {Fmt.Size(_s.CommitLimit)} ({commitPct:F0}%)");

        // The same physical memory bar Resource Monitor shows
        int w = s.W - 4, x = 2;
        Span<(string Name, double Bytes, byte Color)> parts =
        [
            ("Hardware Reserved", reserved, 244),
            ("In Use", inUse, Color.Green),
            ("Modified", _s.Modified, Color.Orange),
            ("Standby", _s.Standby, Color.Blue),
            ("Free", _s.Free, 67),
        ];

        double used = 0;
        int lastEnd = x;
        foreach (var part in parts)
        {
            used += part.Bytes;
            int end = x + (int)Math.Round(used / total * w);
            s.Fill(lastEnd, y + 1, end - lastEnd, 1, ' ', Color.Text, part.Color);
            lastEnd = end;
        }

        int lx = 2;
        foreach (var part in parts)
        {
            s.Put(lx, y + 2, (char)0x2588, part.Color);
            lx = s.Text(lx + 2, y + 2, part.Name, Color.Dim) + 1;
            lx = s.Text(lx, y + 2, Fmt.Size(part.Bytes), Color.Text) + 3;
        }

        s.Text(2, y + 3, $"Cached {Fmt.Size(_s.Cache)}   Paged pool {Fmt.Size(_s.PoolPaged)}   Non-paged pool {Fmt.Size(_s.PoolNonPaged)}   Hard faults {_s.HardFaults:F0}/s   Page faults {_s.PageFaults:F0}/s", Color.Dim, 0, 0, w);
        return y + 5;
    }
    void DrawDisk(int top, int bottom)
    {
        Panel[] p = _tabs[3];
        int total = bottom - top;
        int storage = Math.Min(_s.Disks.Count + 3, Math.Max(4, total / 4));
        int half = _scr.W / 2;
        Place(p[2], 0, bottom - storage, half, storage);
        Place(p[3], half, bottom - storage, _scr.W - half, storage);

        int rest = total - storage;
        int first = rest / 2;
        Place(p[0], 0, top, _scr.W, first);
        Place(p[1], 0, top + first, _scr.W, rest - first);
    }

    void DrawNetwork(int top, int bottom)
    {
        Panel[] p = _tabs[4];
        p[0].Info = $"Down {Fmt.Size(_s.NetRecv)}/s  Up {Fmt.Size(_s.NetSend)}/s";
        string link = $"Link {_s.LinkBitsPerSecond / 1e6:F0} Mbps";
        p[1].Info = _s.NetDeniedCount > 0 ? $"{_s.NetDeniedCount} hidden  {link}" : link;
        int total = bottom - top, half = _scr.W / 2, upper = total / 2;
        Place(p[0], 0, top, half, upper);
        Place(p[1], half, top, _scr.W - half, upper);
        int tcpWidth = _scr.W * 3 / 5;
        Place(p[2], 0, top + upper, tcpWidth, total - upper);
        Place(p[3], tcpWidth, top + upper, _scr.W - tcpWidth, total - upper);
    }
    static void Place(Panel p, int x, int y, int w, int h) { p.X = x; p.Y = y; p.W = w; p.H = h; }

    // Divides the space between this tab's tables according to the given weights
    void Stack(int top, int bottom, params int[] weights)
    {
        Panel[] p = _tabs[_tab];
        int sum = 0, y = top, height = bottom - top;
        foreach (int weight in weights) sum += weight;
        for (int i = 0; i < p.Length; i++)
        {
            int h = i == p.Length - 1 ? bottom - y : height * weights[i] / sum;
            Place(p[i], 0, y, _scr.W, h);
            y += h;
        }
    }

    void DrawFooter()
    {
        Screen s = _scr;
        int y = s.H - 2;
        uint pid = Focused.SelectedPid;
        Proc? proc = _s.Find(pid);
        if (proc != null)
        {
            int x = s.Text(1, y, proc.Name, Color.Accent, 0, 1) + 1;
            x = s.Text(x, y, "← ", Color.Dim);
            x = s.Text(x, y, _s.NameOf(proc.Parent), Color.Dim) + 2;
            s.Text(x, y, Describe(proc), Color.Dim, 0, 0, s.W - x - 1);
        }

        y = s.H - 1;
        s.Fill(0, y, s.W, 1, ' ', Color.Text, Color.BarBg);
        switch (_mode)
        {
            case Mode.Filter:
                int x = s.Text(1, y, "Filter: ", Color.Yellow, Color.BarBg, 1);
                x = s.Text(x, y, _filter, Color.White, Color.BarBg);
                s.Put(x, y, '█', Color.White, Color.BarBg);
                s.Text(x + 3, y, "Enter keep   Esc clear", Color.Dim, Color.BarBg);
                break;
            case Mode.ConfirmKill:
                s.Text(1, y, $"End {_killTarget?.Name} (PID {_killTarget?.Pid})? Unsaved work in it will be lost.  y = end it, any other key = cancel", Color.Red, Color.BarBg, 1);
                break;
            default:
                if (_message.Length > 0) s.Text(1, y, _message, Color.Yellow, Color.BarBg);
                else
                {
                    string filter = _filter.Length > 0 ? $"filter '{_filter}'   " : "";
                    string pins = _pins.Count > 0 ? $"{_pins.Count} pinned   " : "";
                    s.Text(1, y, filter + pins + (_tree ? "tree   " : "") + (_expanded ? "expanded   " : "") + "1-5 tabs  Tab panel  ←→ sort  e expand  t tree  Enter details  Space pin  / filter  k end  z suspend  ? help  q quit", Color.Dim, Color.BarBg, 0, s.W - 2);
                }
                break;
        }
    }

    void DrawHelp()
    {
        Screen s = _scr;
        string[] lines =
        [
            "pcmon keys",
            "",
            "1-5          switch tab (or click a tab)",
            "Tab          move between tables",
            "Arrows       move the selection; PgUp PgDn Home End jump",
            "Left/Right   change the sort column (or click a heading)",
            "r            reverse the sort",
            "e            expand the current table to fill the window",
            "t            show processes as a tree (child under parent)",
            "Enter        details of the selected process",
            "Space        pin a process: other tables then show only it",
            "/            filter by name; Esc clears filter and pins",
            "k            end the selected process (asks first)",
            "z            suspend or resume the selected process",
            "p            pause or resume updates",
            "+ / -        update faster or slower",
            "Ctrl+L       repaint the screen",
            "q or Ctrl+C  quit",
            "",
            _admin ? "Running as administrator: file and address detail are available."
                   : "Run as administrator to add file and address detail.",
            "",
            "Press any key to close",
        ];
        int w = 0;
        foreach (string line in lines) w = Math.Max(w, line.Length);
        w += 4;
        int h = lines.Length + 2, x = (s.W - w) / 2, y = Math.Max(0, (s.H - h) / 2);
        s.Fill(x, y, w, h, ' ', Color.Text, Color.BarBg);
        s.Frame(x, y, w, h, "", Color.Accent, Color.Accent, default, Color.Dim, Color.BarBg);
        for (int i = 0; i < lines.Length; i++)
            s.Text(x + 2, y + 1 + i, lines[i], i == 0 ? Color.Accent : Color.Text, Color.BarBg, i == 0 ? (byte)1 : (byte)0);
    }

    // --- Input -----------------------------------------------------------------
    // Returns true when the screen needs redrawing
    public bool Key(ushort vk, char c, bool shift)
    {
        _message = "";
        switch (_mode)
        {
            case Mode.Help:
            case Mode.Detail:
                _mode = Mode.Normal;
                return true;
            case Mode.ConfirmKill:
                _mode = Mode.Normal;
                if (c is 'y' or 'Y' && _killTarget != null) EndProcess(_killTarget);
                _killTarget = null;
                return true;
            case Mode.Filter:
                FilterKey(vk, c);
                return true;
        }

        switch (vk)
        {
            case 0x26: Focused.Move(-1); return true;       // up
            case 0x28: Focused.Move(1); return true;        // down
            case 0x21: Focused.Move(-(Focused.H - 4)); return true;   // page up
            case 0x22: Focused.Move(Focused.H - 4); return true;      // page down
            case 0x24: Focused.MoveTo(0); return true;      // home
            case 0x23: Focused.MoveTo(int.MaxValue); return true;     // end
            case 0x25: Focused.NextSort(-1); return true;   // left
            case 0x27: Focused.NextSort(1); return true;    // right
            case 0x09:                                       // tab
                int n = _tabs[_tab].Length;
                _focus[_tab] = (_focus[_tab] + (shift ? n - 1 : 1)) % n;
                return true;
            case 0x1B:                                       // escape
                ClearFilters();
                return true;
            case 0x0D:                                       // enter
                if (_s.Find(Focused.SelectedPid) is { } selected) { _detailPid = selected.Pid; _mode = Mode.Detail; }
                else _message = "Select a process first";
                return true;
        }

        switch (c)
        {
            case '\x0C': _scr.Invalidate(); return true;       // Ctrl+L repaints the screen
            case >= '1' and <= '5': SetTab(c - '1'); return true;
            case 'q' or 'Q' or '\x03': Quit = true; return false;      // \x03 is Ctrl+C
            case 'e': _expanded = !_expanded; return true;
            case 't': _tree = !_tree; UpdatePanels(); return true;
            case '?': _mode = Mode.Help; return true;
            case '/': _mode = Mode.Filter; return true;
            case 'r': Focused.ReverseSort(); return true;
            case 'p': Paused = !Paused; return true;
            case '+' or '=': Speed(-1); return true;
            case '-': Speed(1); return true;
            case ' ': TogglePin(); return true;
            case 'z': ToggleSuspend(); return true;
            case 'k':
                _killTarget = _s.Find(Focused.SelectedPid);
                if (_killTarget != null) _mode = Mode.ConfirmKill;
                else _message = "Select a process first";
                return true;
        }
        return false;
    }

    void FilterKey(ushort vk, char c)
    {
        if (vk == 0x0D) _mode = Mode.Normal;                 // enter
        else if (vk == 0x1B) { _filter = ""; _mode = Mode.Normal; }
        else if (vk == 0x08) { if (_filter.Length > 0) _filter = _filter[..^1]; }
        else if (c >= ' ') _filter += c;
        else return;
        UpdatePanels();
    }

    void ClearFilters()
    {
        _filter = "";
        foreach (uint pid in _pins)
            if (_s.Find(pid) is { } p) p.Pinned = false;
        _pins.Clear();
        UpdatePanels();
    }

    void Speed(int step)
    {
        int i = Math.Max(0, Array.IndexOf(Intervals, IntervalMs));
        IntervalMs = Intervals[Math.Clamp(i + step, 0, Intervals.Length - 1)];
    }

    void TogglePin()
    {
        Proc? p = _s.Find(Focused.SelectedPid);
        if (p == null) return;
        p.Pinned = !p.Pinned;
        if (p.Pinned) _pins.Add(p.Pid); else _pins.Remove(p.Pid);
        UpdatePanels();
    }

    void EndProcess(Proc p)
    {
        if (p.Pid <= 4) { _message = "Refusing to end a core system process"; return; }
        nint h = Native.OpenProcess(Native.PROCESS_TERMINATE, 0, p.Pid);
        if (h == 0) { _message = $"Windows denied access to {p.Name}"; return; }
        bool done = Native.TerminateProcess(h, 1) != 0;
        Native.CloseHandle(h);
        _message = done ? $"Ended {p.Name} ({p.Pid})" : $"Could not end {p.Name}";
    }

    void ToggleSuspend()
    {
        Proc? p = _s.Find(Focused.SelectedPid);
        if (p == null) { _message = "Select a process first"; return; }
        if (p.Pid <= 4) { _message = "Refusing to suspend a core system process"; return; }
        nint h = Native.OpenProcess(Native.PROCESS_SUSPEND_RESUME, 0, p.Pid);
        if (h == 0) { _message = $"Windows denied access to {p.Name}"; return; }
        int status = p.Suspended ? Native.NtResumeProcess(h) : Native.NtSuspendProcess(h);
        Native.CloseHandle(h);
        _message = status < 0 ? $"Could not change {p.Name}" : p.Suspended ? $"Resumed {p.Name}" : $"Suspended {p.Name}";
    }

    public bool Click(int x, int y)
    {
        if (y == 0)
        {
            for (int i = 0; i < 5; i++)
                if (x >= _tabX[i] && x < _tabX[i + 1] - 1) { SetTab(i); return true; }
            return false;
        }
        Panel[] panels = _tabs[_tab];
        for (int i = 0; i < panels.Length; i++)
        {
            if (!panels[i].Contains(x, y)) continue;
            _focus[_tab] = i;
            panels[i].Click(x, y);
            return true;
        }
        return false;
    }

    public bool Wheel(int x, int y, int delta)
    {
        foreach (Panel p in _tabs[_tab])
        {
            if (!p.Contains(x, y)) continue;
            p.Move(delta > 0 ? -3 : 3);
            return true;
        }
        return false;
    }

    public string[] Snapshot(int tab)
    {
        SetTab(tab);
        Collect();
        Draw();
        return _scr.Lines();
    }
}
