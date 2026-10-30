using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using static PcMon.Native;

namespace PcMon;

internal sealed class Proc
{
    public uint Pid, Parent;
    public long CreateTime;
    public string Name = "";
    public int Threads, Handles, Session, Depth;
    public bool Suspended, Pinned;
    public long WorkingSet, Private, Commit;
    public float Cpu, AvgCpu, HardFaults;
    public double DiskRead, DiskWrite, NetSend, NetRecv;
    public ulong TotalDiskRead, TotalDiskWrite, TotalNetIn, TotalNetOut;
    public long Stamp, LastCpu;
    public uint LastHard;
    public nint NetHandle;                    // kept open, because opening it every tick is the slow part
    public bool NetDenied, NetSeen;
    public string? Detail;                    // command line, looked up only when selected
    public long Shareable => WorkingSet - Private;
}

internal sealed class History(int size)
{
    readonly float[] _data = new float[size];
    int _head;
    public int Size => _data.Length;
    public void Push(double value) { _data[_head] = (float)value; _head = (_head + 1) % _data.Length; }
    public float Back(int age) => _data[(_head - 1 - age + _data.Length * 2) % _data.Length];
    public float Max(int count)
    {
        float max = 0;
        for (int i = 0; i < count && i < _data.Length; i++) max = Math.Max(max, Back(i));
        return max;
    }
}

internal sealed class DiskRow
{
    public string Name = "";
    public double Read, Write, Active, Queue, LatencyMs;
}

internal sealed class VolumeRow
{
    public string Name = "";
    public ulong Free, Total;
}

internal sealed class FileRow
{
    public uint Pid;
    public string Image = "", File = "";
    public double Read, Write, ResponseMs;
    public long Stamp;
}

internal sealed class NetRow
{
    public uint Pid;
    public string Image = "", Address = "";
    public double Send, Recv;
    public long Stamp;
}

internal sealed class TcpRow
{
    public uint Pid;
    public string Image = "", Local = "", Remote = "", State = "";
    public int LocalPort, RemotePort;
}

internal sealed class ListenRow
{
    public uint Pid;
    public string Image = "", Address = "", Protocol = "";
    public int Port;
}

internal sealed class ServiceRow
{
    public string Name = "", Display = "";
    public uint Pid;
}

// Collects all the numbers in two steps. Capture makes the system calls, some of which
// can stall (a sleeping drive, a busy counter), and only writes to private buffers.
// Apply turns those buffers into the rows on screen. Only Apply runs under the lock
// shared with the UI thread, so a stalled system call can never freeze the keyboard.
// Buffers and objects are reused, so the garbage collector has almost nothing to do.
internal sealed unsafe class Sampler
{
    public const int HistorySize = 512;
    const int TableCount = 4;
    const uint EtwLingerTicks = 30;

    public readonly int Cores = Environment.ProcessorCount;
    public readonly List<Proc> Procs = new(512);
    readonly Dictionary<uint, Proc> _byPid = new(1024);
    readonly Dictionary<uint, uint> _threadOwner = new(8192);
    byte* _buffer;
    uint _capacity = 4 * 1024 * 1024;
    long _lastTime, _stamp;
    public double Elapsed;

    // CPU
    readonly byte* _cpuRaw;
    public readonly float[] CoreUsage;
    public readonly History[] CoreHistory;
    readonly long[] _lastIdle, _lastBusyTotal;
    public double CpuTotal, MaxFrequencyPct, FrequencyMHz;
    public readonly History CpuHistory = new(HistorySize);

    // Totals
    public int ProcessCount, ThreadCount, HandleCount;
    public TimeSpan Uptime;
    public double ContextSwitches, SystemCalls;

    // Memory
    public ulong InstalledBytes, TotalPhys, AvailPhys, CommitUsed, CommitLimit;
    public double Modified, Standby, Free, PoolPaged, PoolNonPaged, Cache, HardFaults, PageFaults;
    public readonly History MemoryHistory = new(HistorySize), FaultHistory = new(HistorySize);

    // Disk
    public readonly List<DiskRow> Disks = [];
    public readonly List<VolumeRow> Volumes = [];
    List<VolumeRow>? _nextVolumes;
    public readonly List<FileRow> Files = [];
    readonly Dictionary<(uint, ulong), FileRow> _fileRows = [];
    public double DiskRead, DiskWrite, DiskActive;
    public readonly History DiskHistory = new(HistorySize);

    // Network
    public double NetSend, NetRecv, LinkBitsPerSecond;
    public readonly History NetHistory = new(HistorySize);
    public readonly List<NetRow> NetRows = [];
    readonly Dictionary<NetKey, NetRow> _netRows = [];
    public readonly List<TcpRow> Tcp = [];
    public readonly List<ListenRow> Listening = [];
    readonly List<TcpRow> _tcpPool = [];
    readonly List<ListenRow> _listenPool = [];
    readonly Dictionary<(ulong, ulong, bool), string> _addresses = [];
    readonly byte*[] _tables = new byte*[TableCount];
    readonly uint[] _tableCapacity = new uint[TableCount];
    readonly bool[] _tableReady = new bool[TableCount];
    readonly List<ulong> _interfaces = [];
    long _lastInterfaceScan = -1000;
    ulong _lastIn, _lastOut, _capturedIn, _capturedOut, _capturedLink;
    readonly byte* _ifRow;
    public bool NetCountersSupported = true;
    public int NetDeniedCount;

    // Kernel tracing
    public readonly bool CanTrace;
    public string TraceProblem = "";
    long _lastTraceWanted;
    bool _clearTraceRows;

    // Services
    public readonly List<ServiceRow> Services = [];
    List<ServiceRow>? _nextServices;
    long _lastServiceScan = -1000, _lastVolumeScan = -1000;

    // Performance counters
    readonly nint _query;
    readonly nint _cModified, _cStandbyCore, _cStandbyNormal, _cStandbyReserve, _cFree, _cPaged, _cNonPaged, _cCache;
    readonly nint _cHard, _cFaults, _cSwitches, _cCalls, _cMaxFreq, _cFreq;
    readonly nint _dRead, _dWrite, _dIdle, _dQueue, _dLatency;
    readonly byte* _pdhBuffer;
    const uint PdhCapacity = 64 * 1024;
    const uint PDH_FMT_DOUBLE = 0x200, PDH_FMT_NOCAP100 = 0x8000;

    readonly Dictionary<string, string> _devices = [];

    public Sampler(bool canTrace)
    {
        CanTrace = canTrace;
        _buffer = (byte*)NativeMemory.Alloc(_capacity);
        Layout.Verify(_buffer, _capacity);
        for (int i = 0; i < TableCount; i++)
        {
            _tableCapacity[i] = 256 * 1024;
            _tables[i] = (byte*)NativeMemory.Alloc(_tableCapacity[i]);
        }
        _ifRow = (byte*)NativeMemory.Alloc(1352);
        _pdhBuffer = (byte*)NativeMemory.Alloc(PdhCapacity);
        _cpuRaw = (byte*)NativeMemory.Alloc((nuint)(48 * Cores));

        CoreUsage = new float[Cores];
        CoreHistory = new History[Cores];
        for (int i = 0; i < Cores; i++) CoreHistory[i] = new History(HistorySize);
        _lastIdle = new long[Cores];
        _lastBusyTotal = new long[Cores];

        ulong kb;
        if (GetPhysicallyInstalledSystemMemory(&kb) != 0) InstalledBytes = kb * 1024;

        nint query;
        PdhOpenQueryW(null, 0, &query);
        _query = query;
        _cModified = Add(@"\Memory\Modified Page List Bytes");
        _cStandbyCore = Add(@"\Memory\Standby Cache Core Bytes");
        _cStandbyNormal = Add(@"\Memory\Standby Cache Normal Priority Bytes");
        _cStandbyReserve = Add(@"\Memory\Standby Cache Reserve Bytes");
        _cFree = Add(@"\Memory\Free & Zero Page List Bytes");
        _cPaged = Add(@"\Memory\Pool Paged Bytes");
        _cNonPaged = Add(@"\Memory\Pool Nonpaged Bytes");
        _cCache = Add(@"\Memory\Cache Bytes");
        _cHard = Add(@"\Memory\Page Reads/sec");
        _cFaults = Add(@"\Memory\Page Faults/sec");
        _cSwitches = Add(@"\System\Context Switches/sec");
        _cCalls = Add(@"\System\System Calls/sec");
        _cMaxFreq = Add(@"\Processor Information(_Total)\% Processor Performance");
        _cFreq = Add(@"\Processor Information(_Total)\Processor Frequency");
        _dRead = Add(@"\PhysicalDisk(*)\Disk Read Bytes/sec");
        _dWrite = Add(@"\PhysicalDisk(*)\Disk Write Bytes/sec");
        _dIdle = Add(@"\PhysicalDisk(*)\% Idle Time");
        _dQueue = Add(@"\PhysicalDisk(*)\Avg. Disk Queue Length");
        _dLatency = Add(@"\PhysicalDisk(*)\Avg. Disk sec/Transfer");
        PdhCollectQueryData(_query);
    }

    nint Add(string path)
    {
        nint counter = 0;
        fixed (char* p = path) PdhAddEnglishCounterW(_query, p, 0, &counter);
        return counter;
    }

    double Value(nint counter)
    {
        if (counter == 0) return 0;
        byte* value = stackalloc byte[16];
        uint status = PdhGetFormattedCounterValue(counter, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, null, value);
        return status == 0 && *(uint*)value == 0 ? *(double*)(value + 8) : 0;
    }

    public Proc? Find(uint pid) => _byPid.GetValueOrDefault(pid);
    public string NameOf(uint pid) => pid == 0 ? "System Idle" : _byPid.TryGetValue(pid, out Proc? p) ? p.Name : "(ended)";

    // Runs both steps for callers that have no UI thread to protect
    public void Tick(bool wantNetTables, bool wantServices, bool wantTrace = false)
    {
        Capture(wantNetTables, wantServices, wantTrace);
        Apply();
    }

    // --- Step 1: system calls, private buffers only ------------------------------
    public void Capture(bool wantNetTables, bool wantServices, bool wantTrace)
    {
        long now = Stopwatch.GetTimestamp();
        Elapsed = _lastTime == 0 ? 1 : (double)(now - _lastTime) / Stopwatch.Frequency;
        _lastTime = now;
        _stamp++;

        uint returned;
        NtQuerySystemInformation(SystemProcessorPerformanceInformation, _cpuRaw, (uint)(48 * Cores), &returned);

        int status;
        while ((status = NtQuerySystemInformation(Layout.ProcessClass, _buffer, _capacity, &returned)) == STATUS_INFO_LENGTH_MISMATCH)
        {
            _capacity *= 2;
            _buffer = (byte*)NativeMemory.Realloc(_buffer, _capacity);
        }

        PdhCollectQueryData(_query);
        CaptureNetworkTotals();
        CaptureVolumes();
        if (wantNetTables) CaptureSockets();
        if (wantServices) CaptureServices();
        ManageTrace(wantTrace);
    }

    // Tracing runs only while a tab needs it, and lingers briefly so tab flicking is free
    void ManageTrace(bool wanted)
    {
        if (!CanTrace) return;
        if (wanted)
        {
            _lastTraceWanted = _stamp;
            if (!Etw.Running && TraceProblem.Length == 0 && !Etw.Start()) TraceProblem = Etw.Status;
        }
        else if (Etw.Running && _stamp - _lastTraceWanted > EtwLingerTicks)
        {
            Etw.Stop();
            _clearTraceRows = true;
        }
    }

    // GetIfTable2 is slow, so it only finds the adapters every 30 ticks. The cheap
    // GetIfEntry2 then reads those adapters on every tick.
    void CaptureNetworkTotals()
    {
        const int InOctets = 1208, OutOctets = 1280, Flags = 1152, OperStatus = 1156, TransmitSpeed = 1192;
        if (_stamp - _lastInterfaceScan >= 30)
        {
            _lastInterfaceScan = _stamp;
            _interfaces.Clear();
            byte* table;
            if (GetIfTable2(&table) == 0)
            {
                uint entries = *(uint*)table;
                for (uint i = 0; i < entries; i++)
                {
                    byte* row = table + 8 + i * 1352;
                    bool hardware = (row[Flags] & 1) != 0, filter = (row[Flags] & 2) != 0;
                    if (hardware && !filter && *(int*)(row + OperStatus) == 1) _interfaces.Add(*(ulong*)row);
                }
                FreeMibTable(table);
            }
        }

        ulong input = 0, output = 0, link = 0;
        foreach (ulong luid in _interfaces)
        {
            new Span<byte>(_ifRow, 1352).Clear();
            *(ulong*)_ifRow = luid;
            if (GetIfEntry2(_ifRow) != 0) continue;
            input += *(ulong*)(_ifRow + InOctets);
            output += *(ulong*)(_ifRow + OutOctets);
            link += *(ulong*)(_ifRow + TransmitSpeed);
        }
        _capturedIn = input;
        _capturedOut = output;
        _capturedLink = link;
    }

    void CaptureVolumes()
    {
        if (_stamp - _lastVolumeScan < 10) return;
        _lastVolumeScan = _stamp;
        var volumes = new List<VolumeRow>();
        uint drives = GetLogicalDrives();
        char* root = stackalloc char[4];
        root[1] = ':'; root[2] = '\\'; root[3] = '\0';
        for (int i = 0; i < 26; i++)
        {
            if ((drives & (1u << i)) == 0) continue;
            root[0] = (char)('A' + i);
            if (GetDriveTypeW(root) != 3) continue;      // fixed disks only
            ulong available, total, free;
            if (GetDiskFreeSpaceExW(root, &available, &total, &free) == 0) continue;
            volumes.Add(new VolumeRow { Name = $"{(char)('A' + i)}:", Free = free, Total = total });
        }
        _nextVolumes = volumes;
    }

    void CaptureSockets()
    {
        const int OwnerPidAllTcp = 5, OwnerPidUdp = 1;
        const uint AF_INET = 2, AF_INET6 = 23;
        _tableReady[0] = ReadTable(0, true, AF_INET, OwnerPidAllTcp);
        _tableReady[1] = ReadTable(1, true, AF_INET6, OwnerPidAllTcp);
        _tableReady[2] = ReadTable(2, false, AF_INET, OwnerPidUdp);
        _tableReady[3] = ReadTable(3, false, AF_INET6, OwnerPidUdp);
    }

    bool ReadTable(int index, bool tcp, uint family, int cls)
    {
        while (true)
        {
            uint size = _tableCapacity[index];
            uint rc = tcp ? GetExtendedTcpTable(_tables[index], &size, 0, family, cls, 0)
                          : GetExtendedUdpTable(_tables[index], &size, 0, family, cls, 0);
            if (rc == 0) return true;
            if (rc != 122) return false;     // ERROR_INSUFFICIENT_BUFFER
            _tableCapacity[index] = size * 2;
            _tables[index] = (byte*)NativeMemory.Realloc(_tables[index], _tableCapacity[index]);
        }
    }

    void CaptureServices()
    {
        if (_stamp - _lastServiceScan < 10) return;
        _lastServiceScan = _stamp;
        nint manager = OpenSCManagerW(null, null, 0x0004);
        if (manager == 0) return;
        try
        {
            uint needed, returned, resume = 0;
            EnumServicesStatusExW(manager, 0, 0x30, 1, null, 0, &needed, &returned, &resume, null);
            byte* buffer = (byte*)NativeMemory.Alloc(needed + 4096);
            try
            {
                if (EnumServicesStatusExW(manager, 0, 0x30, 1, buffer, needed + 4096, &needed, &returned, &resume, null) == 0) return;
                var services = new List<ServiceRow>((int)returned);
                for (uint i = 0; i < returned; i++)
                {
                    byte* e = buffer + i * 56;
                    services.Add(new ServiceRow
                    {
                        Name = new string(*(char**)e),
                        Display = new string(*(char**)(e + 8)),
                        Pid = *(uint*)(e + 44),
                    });
                }
                _nextServices = services;
            }
            finally { NativeMemory.Free(buffer); }
        }
        finally { CloseServiceHandle(manager); }
    }

    // --- Step 2: fast, runs under the UI lock ----------------------------------
    public void Apply()
    {
        ApplyCpu();
        ApplyProcesses();
        ApplyCounters();
        ApplyMemory();
        ApplyNetworkTotals();

        if (_clearTraceRows)
        {
            _clearTraceRows = false;
            Files.Clear(); _fileRows.Clear();
            NetRows.Clear(); _netRows.Clear();
        }
        if (Etw.Running) ApplyTrace();

        if (_nextVolumes != null)
        {
            Volumes.Clear();
            Volumes.AddRange(_nextVolumes);
            _nextVolumes = null;
        }
        if (_nextServices != null)
        {
            Services.Clear();
            Services.AddRange(_nextServices);
            _nextServices = null;
        }
        ApplySockets();
        Uptime = TimeSpan.FromMilliseconds(GetTickCount64());
    }

    void ApplyCpu()
    {
        long busySum = 0, totalSum = 0;
        for (int i = 0; i < Cores; i++)
        {
            long* c = (long*)(_cpuRaw + i * 48);
            long idle = c[0], kernel = c[1], user = c[2];      // kernel time includes idle time
            long total = kernel + user;
            long dTotal = total - _lastBusyTotal[i], dIdle = idle - _lastIdle[i];
            _lastBusyTotal[i] = total;
            _lastIdle[i] = idle;
            float usage = dTotal > 0 ? (float)(100.0 * (dTotal - dIdle) / dTotal) : 0;
            CoreUsage[i] = usage;
            CoreHistory[i].Push(usage);
            totalSum += dTotal;
            busySum += dTotal - dIdle;
        }
        CpuTotal = totalSum > 0 ? 100.0 * busySum / totalSum : 0;
        CpuHistory.Push(CpuTotal);
    }

    void ApplyProcesses()
    {
        bool mapThreads = Etw.Running;
        if (mapThreads) _threadOwner.Clear();
        double intervals = Elapsed * 10_000_000 * Cores;      // CPU capacity in 100 ns units
        double alpha = Math.Min(1, Elapsed / 60);             // smoothing for the 60-second average
        int threads = 0, handles = 0, denied = 0;
        int threadSize = Layout.ThreadSize;
        Procs.Clear();

        byte* cursor = _buffer;
        while (true)
        {
            SystemProcess* p = (SystemProcess*)cursor;
            uint pid = (uint)p->UniqueProcessId;
            int threadCount = (int)p->NumberOfThreads;
            threads += threadCount;
            handles += (int)p->HandleCount;

            if (pid != 0)
            {
                long cpu = p->UserTime + p->KernelTime;
                if (!_byPid.TryGetValue(pid, out Proc? info) || info.CreateTime != p->CreateTime)
                {
                    if (info != null) Forget(info);          // the PID was reused by a new process
                    info = new Proc { Pid = pid, CreateTime = p->CreateTime, LastCpu = cpu, LastHard = p->HardFaultCount };
                    info.Name = p->ImageName.Buffer != null ? new string(p->ImageName.Buffer, 0, p->ImageName.Length / 2) : "System";
                    _byPid[pid] = info;
                }
                info.Stamp = _stamp;
                info.Parent = (uint)p->InheritedFromUniqueProcessId;
                info.Threads = threadCount;
                info.Handles = (int)p->HandleCount;
                info.Session = (int)p->SessionId;
                info.WorkingSet = (long)p->WorkingSetSize;
                info.Private = p->WorkingSetPrivateSize;
                info.Commit = (long)p->PrivatePageCount;

                info.Cpu = (float)((cpu - info.LastCpu) / intervals * 100);
                info.LastCpu = cpu;
                info.AvgCpu += (float)((info.Cpu - info.AvgCpu) * alpha);

                info.HardFaults = (float)((p->HardFaultCount - info.LastHard) / Elapsed);
                info.LastHard = p->HardFaultCount;

                // A process is suspended when every one of its threads is waiting on "Suspended"
                bool suspended = threadCount > 0;
                byte* thread = cursor + sizeof(SystemProcess);
                for (int i = 0; i < threadCount; i++, thread += threadSize)
                {
                    SystemThread* t = (SystemThread*)thread;
                    if (mapThreads) _threadOwner[(uint)t->UniqueThread] = pid;
                    else if (!suspended) break;
                    if (suspended && (t->ThreadState != ThreadStateWaiting || t->WaitReason != WaitReasonSuspended)) suspended = false;
                }
                info.Suspended = suspended;

                if (Layout.HasDiskCounters)
                {
                    var disk = (ProcessDiskCounters*)(cursor + sizeof(SystemProcess) + threadCount * threadSize);
                    SetDisk(info, disk->BytesRead, disk->BytesWritten);
                }
                else SetDisk(info, (ulong)p->ReadTransferCount, (ulong)p->WriteTransferCount);

                ReadProcessNetwork(info);
                if (info.NetDenied) denied++;
                Procs.Add(info);
            }

            if (p->NextEntryOffset == 0) break;
            cursor += p->NextEntryOffset;
        }

        ProcessCount = Procs.Count;
        ThreadCount = threads;
        HandleCount = handles;
        NetDeniedCount = denied;

        if (_byPid.Count > Procs.Count)
        {
            // Forget processes that ended
            foreach ((uint pid, Proc info) in _byPid)
            {
                if (info.Stamp == _stamp) continue;
                Forget(info);
                _byPid.Remove(pid);
            }
        }
    }

    static void Forget(Proc info)
    {
        if (info.NetHandle != 0) CloseHandle(info.NetHandle);
        info.NetHandle = 0;
    }

    void SetDisk(Proc info, ulong read, ulong write)
    {
        bool first = info.TotalDiskRead == 0 && info.TotalDiskWrite == 0;
        info.DiskRead = first ? 0 : (read - info.TotalDiskRead) / Elapsed;
        info.DiskWrite = first ? 0 : (write - info.TotalDiskWrite) / Elapsed;
        info.TotalDiskRead = read;
        info.TotalDiskWrite = write;
    }

    void ReadProcessNetwork(Proc info)
    {
        if (!NetCountersSupported || info.NetDenied) return;
        if (info.NetHandle == 0)
        {
            info.NetHandle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, 0, info.Pid);
            if (info.NetHandle == 0) { info.NetDenied = true; return; }
        }

        ProcessNetworkCounters counters;
        uint returned;
        int status = NtQueryInformationProcess(info.NetHandle, ProcessNetworkIoCounters, &counters, (uint)sizeof(ProcessNetworkCounters), &returned);
        if (status == STATUS_INVALID_INFO_CLASS) { NetCountersSupported = false; return; }
        if (status < 0) { info.NetDenied = true; return; }

        if (info.NetSeen)
        {
            info.NetRecv = (counters.BytesIn - info.TotalNetIn) / Elapsed;
            info.NetSend = (counters.BytesOut - info.TotalNetOut) / Elapsed;
        }
        info.NetSeen = true;
        info.TotalNetIn = counters.BytesIn;
        info.TotalNetOut = counters.BytesOut;
    }

    void ApplyCounters()
    {
        Modified = Value(_cModified);
        Standby = Value(_cStandbyCore) + Value(_cStandbyNormal) + Value(_cStandbyReserve);
        Free = Value(_cFree);
        PoolPaged = Value(_cPaged);
        PoolNonPaged = Value(_cNonPaged);
        Cache = Value(_cCache);
        HardFaults = Value(_cHard);
        PageFaults = Value(_cFaults);
        ContextSwitches = Value(_cSwitches);
        SystemCalls = Value(_cCalls);
        MaxFrequencyPct = Value(_cMaxFreq);
        FrequencyMHz = Value(_cFreq) * MaxFrequencyPct / 100;
        FaultHistory.Push(HardFaults);

        // One value per disk from each counter, in the same order
        DiskRead = DiskWrite = DiskActive = 0;
        uint size = PdhCapacity, count;
        if (_dRead == 0 || PdhGetFormattedCounterArrayW(_dRead, PDH_FMT_DOUBLE, &size, &count, _pdhBuffer) != 0)
        {
            DiskHistory.Push(0);
            return;
        }

        int used = 0;
        for (uint i = 0; i < count; i++)
        {
            byte* item = _pdhBuffer + i * 24;
            ReadOnlySpan<char> name = MemoryMarshal.CreateReadOnlySpanFromNullTerminated(*(char**)item);
            double value = *(uint*)(item + 8) == 0 ? *(double*)(item + 16) : 0;
            if (name.SequenceEqual("_Total")) { DiskRead = value; continue; }
            if (used == Disks.Count) Disks.Add(new DiskRow());
            DiskRow row = Disks[used++];
            if (!name.SequenceEqual(row.Name)) row.Name = name.ToString();
            row.Read = value;
        }
        if (used < Disks.Count) Disks.RemoveRange(used, Disks.Count - used);

        ReadDiskColumn(_dWrite, static (r, v) => r.Write = v, ref DiskWrite);
        double idle = 100;
        ReadDiskColumn(_dIdle, static (r, v) => r.Active = Math.Clamp(100 - v, 0, 100), ref idle);
        DiskActive = Math.Clamp(100 - idle, 0, 100);
        double ignored = 0;
        ReadDiskColumn(_dQueue, static (r, v) => r.Queue = v, ref ignored);
        ReadDiskColumn(_dLatency, static (r, v) => r.LatencyMs = v * 1000, ref ignored);
        DiskHistory.Push(DiskRead + DiskWrite);
    }

    void ReadDiskColumn(nint counter, Action<DiskRow, double> store, ref double total)
    {
        uint size = PdhCapacity, count;
        if (counter == 0 || PdhGetFormattedCounterArrayW(counter, PDH_FMT_DOUBLE, &size, &count, _pdhBuffer) != 0) return;
        int used = 0;
        for (uint i = 0; i < count; i++)
        {
            byte* item = _pdhBuffer + i * 24;
            double value = *(uint*)(item + 8) == 0 ? *(double*)(item + 16) : 0;
            if (MemoryMarshal.CreateReadOnlySpanFromNullTerminated(*(char**)item).SequenceEqual("_Total")) total = value;
            else if (used < Disks.Count) store(Disks[used++], value);
        }
    }

    void ApplyMemory()
    {
        MemoryStatusEx m = default;
        m.Length = (uint)sizeof(MemoryStatusEx);
        if (GlobalMemoryStatusEx(&m) == 0) return;
        TotalPhys = m.TotalPhys;
        AvailPhys = m.AvailPhys;
        CommitLimit = m.TotalPageFile;
        CommitUsed = m.TotalPageFile - m.AvailPageFile;
        MemoryHistory.Push(100.0 * (TotalPhys - AvailPhys) / TotalPhys);
    }

    void ApplyNetworkTotals()
    {
        if (_lastIn != 0 && _capturedIn >= _lastIn && _capturedOut >= _lastOut)
        {
            NetRecv = (_capturedIn - _lastIn) / Elapsed;
            NetSend = (_capturedOut - _lastOut) / Elapsed;
        }
        _lastIn = _capturedIn;
        _lastOut = _capturedOut;
        LinkBitsPerSecond = _capturedLink;
        NetHistory.Push(NetRecv + NetSend);
    }

    // Turns raw kernel events into per-file and per-address rates
    void ApplyTrace()
    {
        Etw.Drain(out var disk, out var net);

        // ETW delivers events in bursts, so rates are smoothed, and rows fade out after activity stops
        double keep = Math.Exp(-Elapsed / 3), add = 1 - keep;

        foreach (var (key, a) in disk)
        {
            if (key.FileObject == 0) continue;
            uint pid = key.Tid == uint.MaxValue ? 4 : _threadOwner.GetValueOrDefault(key.Tid, 4u);    // unknown means the system

            if (!_fileRows.TryGetValue((pid, key.FileObject), out FileRow? row))
            {
                string? file = Etw.FileName(key.FileObject);
                if (file == null) continue;
                row = new FileRow { Pid = pid, Image = NameOf(pid), File = ToDosPath(file) };
                _fileRows[(pid, key.FileObject)] = row;
                Files.Add(row);
            }
            row.Read += a.Read / Elapsed * add;
            row.Write += a.Write / Elapsed * add;
            if (a.Ops > 0) row.ResponseMs = (double)a.ResponseTicks / a.Ops / Stopwatch.Frequency * 1000;
            row.Stamp = _stamp;
        }

        foreach (var (key, a) in net)
        {
            if (!_netRows.TryGetValue(key, out NetRow? row))
            {
                row = new NetRow { Pid = key.Pid, Image = NameOf(key.Pid), Address = FormatAddress(key.Hi, key.Lo, key.Hi != 0 || key.Lo > uint.MaxValue) };
                _netRows[key] = row;
                NetRows.Add(row);
            }
            row.Send += a.Send / Elapsed * add;
            row.Recv += a.Recv / Elapsed * add;
            row.Stamp = _stamp;
        }

        for (int i = Files.Count - 1; i >= 0; i--)
        {
            FileRow row = Files[i];
            row.Read *= keep; row.Write *= keep;
            if (row.Read + row.Write < 1 && _stamp - row.Stamp > 5)
            {
                Files.RemoveAt(i);
                foreach (var pair in _fileRows)
                    if (ReferenceEquals(pair.Value, row)) { _fileRows.Remove(pair.Key); break; }
            }
        }
        for (int i = NetRows.Count - 1; i >= 0; i--)
        {
            NetRow row = NetRows[i];
            row.Send *= keep; row.Recv *= keep;
            if (row.Send + row.Recv < 1 && _stamp - row.Stamp > 5)
            {
                NetRows.RemoveAt(i);
                foreach (var pair in _netRows)
                    if (ReferenceEquals(pair.Value, row)) { _netRows.Remove(pair.Key); break; }
            }
        }
    }

    // ETW reports paths like \Device\HarddiskVolume3\Users\... ; this turns them into C:\Users\...
    string ToDosPath(string path)
    {
        if (_devices.Count == 0)
        {
            char* target = stackalloc char[512];
            char* drive = stackalloc char[3];
            drive[1] = ':'; drive[2] = '\0';
            for (char c = 'A'; c <= 'Z'; c++)
            {
                drive[0] = c;
                if (QueryDosDeviceW(drive, target, 512) != 0) _devices[new string(target)] = c + ":";
            }
        }
        foreach (var (device, letter) in _devices)
            if (path.StartsWith(device, StringComparison.OrdinalIgnoreCase) && path.Length > device.Length && path[device.Length] == '\\')
                return letter + path[device.Length..];
        return path;
    }

    string FormatAddress(ulong hi, ulong lo, bool v6)
    {
        if (_addresses.TryGetValue((hi, lo, v6), out string? text)) return text;
        if (_addresses.Count > 20_000) _addresses.Clear();
        byte* b = stackalloc byte[16];
        *(ulong*)b = hi;
        *(ulong*)(b + 8) = lo;
        text = v6 ? new IPAddress(new ReadOnlySpan<byte>(b, 16)).ToString() : new IPAddress(new ReadOnlySpan<byte>(b + 8, 4)).ToString();
        _addresses[(hi, lo, v6)] = text;
        return text;
    }

    static readonly string[] TcpStates =
        ["?", "Closed", "Listen", "SynSent", "SynRecv", "Established", "FinWait1", "FinWait2", "CloseWait", "Closing", "LastAck", "TimeWait", "DeleteTcb"];

    static int Port(uint value) => (int)((value & 0xFF) << 8 | (value >> 8) & 0xFF);

    TcpRow NextTcp()
    {
        int index = Tcp.Count;
        if (index == _tcpPool.Count) _tcpPool.Add(new TcpRow());
        TcpRow row = _tcpPool[index];
        Tcp.Add(row);
        return row;
    }

    ListenRow NextListen()
    {
        int index = Listening.Count;
        if (index == _listenPool.Count) _listenPool.Add(new ListenRow());
        ListenRow row = _listenPool[index];
        Listening.Add(row);
        return row;
    }

    void AddSocket(uint state, string local, int localPort, string remote, int remotePort, uint pid)
    {
        if (state == 2)
        {
            ListenRow l = NextListen();
            l.Pid = pid; l.Image = NameOf(pid); l.Address = local; l.Port = localPort; l.Protocol = "TCP";
            return;
        }
        TcpRow t = NextTcp();
        t.Pid = pid; t.Image = NameOf(pid); t.Local = local; t.LocalPort = localPort;
        t.Remote = remote; t.RemotePort = remotePort;
        t.State = state < TcpStates.Length ? TcpStates[state] : "?";
    }

    void ApplySockets()
    {
        if (!_tableReady[0] && !_tableReady[1] && !_tableReady[2] && !_tableReady[3]) return;
        Tcp.Clear();
        Listening.Clear();

        if (_tableReady[0])
        {
            uint n = *(uint*)_tables[0];
            uint* r = (uint*)(_tables[0] + 4);
            for (uint i = 0; i < n; i++, r += 6)
                AddSocket(r[0], FormatAddress(0, r[1], false), Port(r[2]), FormatAddress(0, r[3], false), Port(r[4]), r[5]);
        }
        if (_tableReady[1])
        {
            uint n = *(uint*)_tables[1];
            byte* r = _tables[1] + 4;
            for (uint i = 0; i < n; i++, r += 56)
                AddSocket(*(uint*)(r + 48), FormatAddress(*(ulong*)r, *(ulong*)(r + 8), true), Port(*(uint*)(r + 20)),
                          FormatAddress(*(ulong*)(r + 24), *(ulong*)(r + 32), true), Port(*(uint*)(r + 44)), *(uint*)(r + 52));
        }
        if (_tableReady[2])
        {
            uint n = *(uint*)_tables[2];
            uint* r = (uint*)(_tables[2] + 4);
            for (uint i = 0; i < n; i++, r += 3)
            {
                ListenRow l = NextListen();
                l.Pid = r[2]; l.Image = NameOf(r[2]); l.Address = FormatAddress(0, r[0], false); l.Port = Port(r[1]); l.Protocol = "UDP";
            }
        }
        if (_tableReady[3])
        {
            uint n = *(uint*)_tables[3];
            byte* r = _tables[3] + 4;
            for (uint i = 0; i < n; i++, r += 28)
            {
                uint pid = *(uint*)(r + 24);
                ListenRow l = NextListen();
                l.Pid = pid; l.Image = NameOf(pid); l.Address = FormatAddress(*(ulong*)r, *(ulong*)(r + 8), true); l.Port = Port(*(uint*)(r + 20)); l.Protocol = "UDP";
            }
        }
        Array.Clear(_tableReady);
    }
}
