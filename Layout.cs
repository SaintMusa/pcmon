using static PcMon.Native;

namespace PcMon;

// Decides how to read the process list. The extended list (class 57) adds real disk
// counters, but its layout is undocumented, so it is only used after this check has
// proved it against live data. If a future Windows changes it, pcmon falls back to
// the basic list and shows total I/O in place of disk.
internal static unsafe class Layout
{
    public static int ProcessClass { get; private set; } = SystemProcessInformation;
    public static int ThreadSize { get; private set; } = sizeof(SystemThread);
    public static bool HasDiskCounters { get; private set; }
    public static string Problem { get; private set; } = "";

    public static void Verify(byte* buffer, uint capacity)
    {
        if (sizeof(SystemProcess) != 256 || sizeof(SystemThread) != 80 || sizeof(SystemExtendedThread) != 136 || sizeof(ProcessDiskCounters) != 40)
        {
            Problem = "unexpected structure sizes";
            return;
        }

        uint returned;
        int status = NtQuerySystemInformation(SystemExtendedProcessInformation, buffer, capacity, &returned);
        if (status < 0)
        {
            Problem = $"extended process list unavailable (0x{status:X8})";
            return;
        }

        // Every thread entry records which process owns it, so a wrong size shows up at once
        int checkedProcesses = 0;
        byte* cursor = buffer;
        while (true)
        {
            SystemProcess* p = (SystemProcess*)cursor;
            if (p->UniqueProcessId != 0)
            {
                byte* thread = cursor + sizeof(SystemProcess);
                for (uint i = 0; i < p->NumberOfThreads; i++, thread += sizeof(SystemExtendedThread))
                {
                    if (((SystemExtendedThread*)thread)->Thread.UniqueProcess != p->UniqueProcessId)
                    {
                        Problem = "extended process list has an unknown layout";
                        return;
                    }
                }
                checkedProcesses++;
            }
            if (p->NextEntryOffset == 0) break;
            cursor += p->NextEntryOffset;
        }

        if (checkedProcesses == 0)
        {
            Problem = "extended process list was empty";
            return;
        }
        ProcessClass = SystemExtendedProcessInformation;
        ThreadSize = sizeof(SystemExtendedThread);
        HasDiskCounters = true;
    }
}
