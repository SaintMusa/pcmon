using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

[assembly: DisableRuntimeMarshalling]

namespace PcMon;

// Every Windows API the program calls. Pointers are used instead of managed
// types so no data is copied or converted when crossing into native code.
internal static unsafe partial class Native
{
    public const int STATUS_INFO_LENGTH_MISMATCH = unchecked((int)0xC0000004);
    public const int STATUS_INVALID_INFO_CLASS = unchecked((int)0xC0000003);
    public const int SystemProcessInformation = 5;
    public const int SystemProcessorPerformanceInformation = 8;
    public const int SystemExtendedProcessInformation = 57;
    public const int ProcessCommandLineInformation = 60;
    public const int ProcessNetworkIoCounters = 114;

    public const uint PROCESS_TERMINATE = 0x0001;
    public const uint PROCESS_SUSPEND_RESUME = 0x0800;
    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    // --- undocumented process-list layouts --------------------------------------
    // Windows does not document these (the field names come from the phnt headers),
    // so Layout.Verify checks them against live data before they are trusted.
    [StructLayout(LayoutKind.Sequential)]
    public struct UnicodeString
    {
        public ushort Length, MaximumLength;
        public char* Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SystemProcess
    {
        public uint NextEntryOffset, NumberOfThreads;
        public long WorkingSetPrivateSize;
        public uint HardFaultCount, NumberOfThreadsHighWatermark;
        public ulong CycleTime;
        public long CreateTime, UserTime, KernelTime;
        public UnicodeString ImageName;
        public int BasePriority;
        public nint UniqueProcessId, InheritedFromUniqueProcessId;
        public uint HandleCount, SessionId;
        public nuint UniqueProcessKey, PeakVirtualSize, VirtualSize;
        public uint PageFaultCount;
        public nuint PeakWorkingSetSize, WorkingSetSize;
        public nuint QuotaPeakPagedPoolUsage, QuotaPagedPoolUsage, QuotaPeakNonPagedPoolUsage, QuotaNonPagedPoolUsage;
        public nuint PagefileUsage, PeakPagefileUsage, PrivatePageCount;
        public long ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public long ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SystemThread
    {
        public long KernelTime, UserTime, CreateTime;
        public uint WaitTime;
        public nint StartAddress, UniqueProcess, UniqueThread;
        public int Priority, BasePriority;
        public uint ContextSwitches, ThreadState, WaitReason;
    }

    // Class 57 appends these fields to every thread entry
    [StructLayout(LayoutKind.Sequential)]
    public struct SystemExtendedThread
    {
        public SystemThread Thread;
        public nint StackBase, StackLimit, Win32StartAddress, TebBase, Reserved2, Reserved3, Reserved4;
    }

    // Class 57 places this straight after the thread entries
    [StructLayout(LayoutKind.Sequential)]
    public struct ProcessDiskCounters
    {
        public ulong BytesRead, BytesWritten, ReadOperationCount, WriteOperationCount, FlushOperationCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ProcessNetworkCounters
    {
        public ulong BytesIn, BytesOut;
    }

    public const uint ThreadStateWaiting = 5, WaitReasonSuspended = 5;

    // --- ntdll ---------------------------------------------------------------
    [LibraryImport("ntdll")] public static partial int NtQuerySystemInformation(int cls, void* buffer, uint length, uint* returned);
    [LibraryImport("ntdll")] public static partial int NtQueryInformationProcess(nint process, int cls, void* buffer, uint length, uint* returned);
    [LibraryImport("ntdll")] public static partial int NtSuspendProcess(nint process);
    [LibraryImport("ntdll")] public static partial int NtResumeProcess(nint process);

    // --- console -------------------------------------------------------------
    [LibraryImport("kernel32")] public static partial nint GetStdHandle(int id);
    [LibraryImport("kernel32")] public static partial int GetConsoleMode(nint handle, uint* mode);
    [LibraryImport("kernel32")] public static partial int SetConsoleMode(nint handle, uint mode);
    [LibraryImport("kernel32")] public static partial int WriteConsoleW(nint handle, char* buffer, uint count, uint* written, void* reserved);
    [LibraryImport("kernel32")] public static partial int ReadConsoleInputW(nint handle, byte* records, uint count, uint* read);
    [LibraryImport("kernel32")] public static partial int GetConsoleScreenBufferInfo(nint handle, short* info);
    [LibraryImport("kernel32")] public static partial int SetConsoleCtrlHandler(delegate* unmanaged<uint, int> handler, int add);
    [LibraryImport("kernel32")] public static partial uint WaitForMultipleObjects(uint count, nint* handles, int waitAll, uint milliseconds);
    [LibraryImport("kernel32")] public static partial nint CreateEventW(void* attributes, int manualReset, int initialState, char* name);
    [LibraryImport("kernel32")] public static partial int SetEvent(nint handle);

    // --- processes -----------------------------------------------------------
    [LibraryImport("kernel32")] public static partial nint OpenProcess(uint access, int inherit, uint pid);
    [LibraryImport("kernel32")] public static partial int CloseHandle(nint handle);
    [LibraryImport("kernel32")] public static partial int TerminateProcess(nint process, uint exitCode);
    [LibraryImport("kernel32")] public static partial int QueryFullProcessImageNameW(nint process, uint flags, char* name, uint* size);

    // --- memory and storage --------------------------------------------------
    [StructLayout(LayoutKind.Sequential)]
    public struct MemoryStatusEx
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
    }
    [LibraryImport("kernel32")] public static partial int GlobalMemoryStatusEx(MemoryStatusEx* status);
    [LibraryImport("kernel32")] public static partial int GetPhysicallyInstalledSystemMemory(ulong* kilobytes);
    [LibraryImport("kernel32")] public static partial ulong GetTickCount64();
    [LibraryImport("kernel32")] public static partial uint GetLogicalDrives();
    [LibraryImport("kernel32")] public static partial uint GetDriveTypeW(char* root);
    [LibraryImport("kernel32")] public static partial int GetDiskFreeSpaceExW(char* root, ulong* available, ulong* total, ulong* free);
    [LibraryImport("kernel32")] public static partial uint QueryDosDeviceW(char* device, char* target, uint size);

    // --- network -------------------------------------------------------------
    [LibraryImport("iphlpapi")] public static partial uint GetExtendedTcpTable(void* table, uint* size, int sort, uint family, int cls, uint reserved);
    [LibraryImport("iphlpapi")] public static partial uint GetExtendedUdpTable(void* table, uint* size, int sort, uint family, int cls, uint reserved);
    [LibraryImport("iphlpapi")] public static partial uint GetIfTable2(byte** table);
    [LibraryImport("iphlpapi")] public static partial uint GetIfEntry2(byte* row);
    [LibraryImport("iphlpapi")] public static partial void FreeMibTable(void* memory);

    // --- services ------------------------------------------------------------
    [LibraryImport("advapi32")] public static partial nint OpenSCManagerW(char* machine, char* database, uint access);
    [LibraryImport("advapi32")] public static partial int EnumServicesStatusExW(nint manager, int level, uint type, uint state, byte* services, uint size, uint* needed, uint* returned, uint* resume, char* group);
    [LibraryImport("advapi32")] public static partial int CloseServiceHandle(nint handle);

    // --- performance counters (PDH) -------------------------------------------
    [LibraryImport("pdh")] public static partial uint PdhOpenQueryW(char* source, nuint userData, nint* query);
    [LibraryImport("pdh")] public static partial uint PdhAddEnglishCounterW(nint query, char* path, nuint userData, nint* counter);
    [LibraryImport("pdh")] public static partial uint PdhCollectQueryData(nint query);
    [LibraryImport("pdh")] public static partial uint PdhGetFormattedCounterValue(nint counter, uint format, uint* type, byte* value);
    [LibraryImport("pdh")] public static partial uint PdhGetFormattedCounterArrayW(nint counter, uint format, uint* size, uint* count, byte* items);
    [LibraryImport("pdh")] public static partial uint PdhCloseQuery(nint query);

    // --- kernel event tracing (ETW) -------------------------------------------
    [LibraryImport("advapi32")] public static partial uint StartTraceW(ulong* session, char* name, byte* properties);
    [LibraryImport("advapi32")] public static partial uint ControlTraceW(ulong session, char* name, byte* properties, uint control);
    [LibraryImport("advapi32")] public static partial ulong OpenTraceW(byte* logfile);
    [LibraryImport("advapi32")] public static partial uint ProcessTrace(ulong* handles, uint count, void* start, void* end);
    [LibraryImport("advapi32")] public static partial uint CloseTrace(ulong handle);
    [LibraryImport("advapi32")] public static partial uint EnableTraceEx2(ulong session, Guid* provider, uint control, byte level, ulong anyKeyword, ulong allKeyword, uint timeout, void* parameters);

    // Resolves the full path of a process, or null when Windows refuses access.
    public static string? ProcessPath(uint pid)
    {
        nint h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, 0, pid);
        if (h == 0) return null;
        try
        {
            char* name = stackalloc char[1024];
            uint size = 1024;
            return QueryFullProcessImageNameW(h, 0, name, &size) != 0 ? new string(name, 0, (int)size) : null;
        }
        finally { CloseHandle(h); }
    }

    public static string? ProcessCommandLine(uint pid)
    {
        nint h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, 0, pid);
        if (h == 0) return null;
        try
        {
            const int Size = 16384;
            byte* buffer = stackalloc byte[Size];
            uint returned;
            if (NtQueryInformationProcess(h, ProcessCommandLineInformation, buffer, Size, &returned) < 0) return null;
            ushort bytes = *(ushort*)buffer;
            char* text = *(char**)(buffer + 8);
            return bytes > 0 && text != null ? new string(text, 0, bytes / 2) : null;
        }
        finally { CloseHandle(h); }
    }
}
