using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace PcMon;

internal struct DiskKey : IEquatable<DiskKey>
{
    public uint Tid;
    public ulong FileObject;
    public readonly bool Equals(DiskKey o) => Tid == o.Tid && FileObject == o.FileObject;
    public override readonly bool Equals(object? o) => o is DiskKey k && Equals(k);
    public override readonly int GetHashCode() => (int)Tid * 397 ^ FileObject.GetHashCode();
}

internal struct DiskAcc
{
    public long Read, Write, ResponseTicks;
    public int Ops;
}

internal struct NetKey : IEquatable<NetKey>
{
    public uint Pid;
    public ulong Hi, Lo;     // remote address; an IPv4 address uses Lo only and Hi = 0
    public readonly bool Equals(NetKey o) => Pid == o.Pid && Lo == o.Lo && Hi == o.Hi;
    public override readonly bool Equals(object? o) => o is NetKey k && Equals(k);
    public override readonly int GetHashCode() => HashCode.Combine(Pid, Hi, Lo);
}

internal struct NetAcc
{
    public long Send, Recv;
}

// Listens to the kernel's event stream (ETW, Event Tracing for Windows). This is
// where Resource Monitor gets per-process disk and network numbers from. Windows
// only allows it for administrators.
internal static unsafe class Etw
{
    const string SessionName = "PcMonKernelTrace";
    const int PropertiesSize = 120 + 1024;     // EVENT_TRACE_PROPERTIES plus room for the name
    const uint EVENT_TRACE_FLAG_NETWORK_TCPIP = 0x00010000;
    const uint EVENT_TRACE_FLAG_DISK_IO = 0x00000100;
    const uint EVENT_TRACE_FLAG_DISK_FILE_IO = 0x00000200;
    const uint EVENT_TRACE_FLAG_NO_SYSCONFIG = 0x10000000;
    const ulong KERNEL_FILE_KEYWORD_FILENAME = 0x10;
    const uint EVENT_CONTROL_CODE_ENABLE_PROVIDER = 1;
    const uint EVENT_TRACE_REAL_TIME_MODE = 0x00000100;
    const uint EVENT_TRACE_SYSTEM_LOGGER_MODE = 0x02000000;
    const uint ERROR_ALREADY_EXISTS = 183;

    // Provider GUIDs as two 64-bit words, so a check is two integer compares
    static readonly (ulong, ulong) DiskIo = Words("3d6fa8d4-fe05-11d0-9dda-00c04fd7ba7c");
    static readonly (ulong, ulong) FileIo = Words("90cbdc39-4a3e-11d1-84f4-0000f80464e3");
    static readonly (ulong, ulong) TcpIp = Words("9a280ac0-c8e0-11d1-84e2-00c04fb998a2");
    static readonly (ulong, ulong) UdpIp = Words("bf3a50c5-a9c9-4988-a005-2df0b7c80f80");
    static readonly Guid SessionGuid = new("6f2d1a3e-41b7-4c86-9f0e-7a5c2e8d1b44");    // identifies pcmon's private session
    static readonly Guid KernelRundown = new("3b9c9951-3480-4220-9377-9c8e5184f5cd");

    static readonly object Gate = new();
    static Dictionary<DiskKey, DiskAcc> _disk = new(256), _diskSpare = new(256);
    static Dictionary<NetKey, NetAcc> _net = new(256), _netSpare = new(256);
    static readonly Dictionary<ulong, string> FileNames = new(4096);

    static ulong _session, _trace;
    static Thread? _thread;
    public static bool Running { get; private set; }
    public static string Status { get; private set; } = "off";
    public static long EventCount, DiskEvents, NetEvents, NameEvents;

    static (ulong, ulong) Words(string guid)
    {
        Span<byte> b = stackalloc byte[16];
        new Guid(guid).TryWriteBytes(b);
        return (BitConverter.ToUInt64(b), BitConverter.ToUInt64(b[8..]));
    }

    public static bool Start()
    {
        byte* p = stackalloc byte[PropertiesSize];
        fixed (char* name = SessionName)
        {
            uint rc = StartSession(p, name);
            if (rc == ERROR_ALREADY_EXISTS)
            {
                // A leftover session of ours, for example after a crash
                Fill(p);
                Native.ControlTraceW(0, name, p, 1);
                rc = StartSession(p, name);
            }
            if (rc != 0) { Status = $"StartTrace failed ({rc})"; return false; }

            byte* log = stackalloc byte[448];      // EVENT_TRACE_LOGFILEW
            new Span<byte>(log, 448).Clear();
            *(char**)(log + 8) = name;             // LoggerName
            *(uint*)(log + 28) = 0x00000100 | 0x10000000;   // real-time + event-record callbacks
            *(delegate* unmanaged<byte*, void>*)(log + 424) = &OnEvent;

            _trace = Native.OpenTraceW(log);
            if (_trace == ulong.MaxValue) { Status = "OpenTrace failed"; Stop(); return false; }
        }

        _thread = new Thread(static () =>
        {
            ulong handle = _trace;
            Native.ProcessTrace(&handle, 1, null, null);
        }) { IsBackground = true, Name = "etw" };
        _thread.Start();

        // Asks the kernel to name files that were already open. It has to come after
        // OpenTrace, otherwise there is no listener yet and the events are dropped.
        Guid rundown = KernelRundown;
        Native.EnableTraceEx2(_session, &rundown, EVENT_CONTROL_CODE_ENABLE_PROVIDER, 0, KERNEL_FILE_KEYWORD_FILENAME, 0, uint.MaxValue, null);
        Running = true;
        Status = "on";
        return true;
    }

    static void Fill(byte* p)
    {
        new Span<byte>(p, PropertiesSize).Clear();
        *(uint*)p = PropertiesSize;                 // Wnode.BufferSize
        *(Guid*)(p + 24) = SessionGuid;             // Wnode.Guid
        *(uint*)(p + 40) = 1;                       // Wnode.ClientContext: QPC timestamps
        *(uint*)(p + 44) = 0x00020000;              // Wnode.Flags: WNODE_FLAG_TRACED_GUID
        *(uint*)(p + 116) = 120;                    // LoggerNameOffset
    }

    static uint StartSession(byte* p, char* name)
    {
        Fill(p);
        *(uint*)(p + 48) = 64;                      // BufferSize in KB
        *(uint*)(p + 52) = 4;                       // MinimumBuffers
        *(uint*)(p + 56) = 32;                      // MaximumBuffers
        *(uint*)(p + 64) = EVENT_TRACE_REAL_TIME_MODE | EVENT_TRACE_SYSTEM_LOGGER_MODE;
        *(uint*)(p + 68) = 1;                       // FlushTimer in seconds
        *(uint*)(p + 72) = EVENT_TRACE_FLAG_NETWORK_TCPIP | EVENT_TRACE_FLAG_DISK_IO | EVENT_TRACE_FLAG_DISK_FILE_IO | EVENT_TRACE_FLAG_NO_SYSCONFIG;
        ulong session;
        uint rc = Native.StartTraceW(&session, name, p);
        if (rc == 0) _session = session;
        return rc;
    }

    public static void Stop()
    {
        if (_session == 0) return;
        byte* p = stackalloc byte[PropertiesSize];
        Fill(p);
        fixed (char* name = SessionName) Native.ControlTraceW(_session, name, p, 1);
        _session = 0;
        Running = false;
        _thread?.Join(1000);          // ProcessTrace returns once the session has stopped
        _thread = null;
        if (_trace != 0 && _trace != ulong.MaxValue) Native.CloseTrace(_trace);
        _trace = 0;
        lock (Gate)
        {
            _disk.Clear();
            _net.Clear();
            FileNames.Clear();
        }
    }

    // Called by Windows on the ETW thread for every event, so it must stay very short
    [UnmanagedCallersOnly]
    static void OnEvent(byte* e)
    {
        ulong p1 = *(ulong*)(e + 24), p2 = *(ulong*)(e + 32);
        byte opcode = *(e + 45);
        byte* data = *(byte**)(e + 96);
        int length = *(ushort*)(e + 86);
        EventCount++;

        if (p1 == DiskIo.Item1 && p2 == DiskIo.Item2)
        {
            if ((opcode != 10 && opcode != 11) || length < 52) return;
            DiskKey key;
            key.FileObject = *(ulong*)(data + 24);
            key.Tid = *(uint*)(data + 48);        // IssuingThreadId, or uint.MaxValue for the system itself
            uint size = *(uint*)(data + 8);
            long response = *(long*)(data + 40);
            lock (Gate)
            {
                ref DiskAcc a = ref CollectionsMarshal.GetValueRefOrAddDefault(_disk, key, out _);
                if (opcode == 10) a.Read += size; else a.Write += size;
                a.ResponseTicks += response;
                a.Ops++;
            }
            DiskEvents++;
        }
        else if ((p1 == TcpIp.Item1 && p2 == TcpIp.Item2) || (p1 == UdpIp.Item1 && p2 == UdpIp.Item2))
        {
            bool send = opcode is 10 or 26, recv = opcode is 11 or 27;
            bool swapped = recv && p1 == UdpIp.Item1;      // only incoming UDP reverses the address fields
            if (!send && !recv) return;
            NetKey key;
            key.Pid = *(uint*)data;
            uint size = *(uint*)(data + 4);
            if (opcode >= 26)
            {
                if (length < 44) return;
                byte* remote = swapped ? data + 24 : data + 8;
                key.Hi = *(ulong*)remote;
                key.Lo = *(ulong*)(remote + 8);
            }
            else
            {
                if (length < 20) return;
                key.Hi = 0;
                key.Lo = swapped ? *(uint*)(data + 12) : *(uint*)(data + 8);
            }
            lock (Gate)
            {
                ref NetAcc a = ref CollectionsMarshal.GetValueRefOrAddDefault(_net, key, out _);
                if (send) a.Send += size; else a.Recv += size;
            }
            NetEvents++;
        }
        else if (p1 == FileIo.Item1 && p2 == FileIo.Item2)
        {
            if (opcode is 0 or 32 or 36)
            {
                if (length <= 8) return;
                ulong file = *(ulong*)data;
                string name = new((char*)(data + 8), 0, (length - 8) / 2 - 1);
                lock (Gate)
                {
                    if (FileNames.Count > 200_000) FileNames.Clear();
                    FileNames[file] = name;
                }
                NameEvents++;
            }
            else if (opcode == 35)                 // file object closed
            {
                ulong file = *(ulong*)data;
                lock (Gate) FileNames.Remove(file);
            }
        }
    }

    // Hands the events gathered since the last call to the sampler, by swapping
    // dictionaries, so the ETW thread is blocked only for a moment.
    public static void Drain(out Dictionary<DiskKey, DiskAcc> disk, out Dictionary<NetKey, NetAcc> net)
    {
        _diskSpare.Clear();
        _netSpare.Clear();
        lock (Gate)
        {
            (disk, _disk) = (_disk, _diskSpare);
            (net, _net) = (_net, _netSpare);
        }
        _diskSpare = disk;
        _netSpare = net;
    }

    public static string? FileName(ulong fileObject)
    {
        lock (Gate) return FileNames.TryGetValue(fileObject, out string? name) ? name : null;
    }
}
