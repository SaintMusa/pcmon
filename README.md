# pcmon

A fast, live resource monitor for the Windows terminal, in the spirit of Resource Monitor. It is written in C# and compiled to a single native executable with NativeAOT.

## Features

- Five tabs: Overview, CPU, Memory, Disk and Network
- History graphs, per-core CPU, and Resource Monitor's physical memory bar
- Per-process CPU, memory, hard faults, disk and network, without administrator rights
- Process tree, process details, filtering, and pinning a process to focus every table on it
- Suspend, resume and end processes, with a confirmation before ending one
- Services, TCP connections and listening ports
- Keyboard and mouse support
- As administrator: disk activity by file and network activity by address, through kernel event tracing

Press `?` inside pcmon to see every key.

## Requirements

- Windows 11, x64. Per-process network numbers need Windows 11 24H2 or later.
- [.NET 10 SDK](https://dotnet.microsoft.com/download) and the Visual Studio C++ build tools, to build it

## Build and run

```powershell
.\build.ps1
.\dist\pcmon.exe
```

`build.ps1` puts the Visual Studio installer folder on `PATH`, which the native link step needs in order to find `vswhere.exe`.

## How it stays light

- It reads the process list with one system call instead of WMI.
- Slow system calls run on a collector thread outside the lock shared with the UI, so a stalled drive cannot freeze the keyboard.
- The screen is a grid of cells, and only cells that changed are sent to the terminal.
- Buffers and objects are reused, so the garbage collector rarely runs.
- It sleeps until a timer or key wakes it, and collects only what the visible tab needs.

## Good to know

pcmon relies on some undocumented Windows structures, which could change in a future version. At startup it checks them against live data, and falls back to basic I/O numbers if they do not match.

## Acknowledgements

Ideas came from [wttop](https://github.com/jchomarat/wttop), [btop](https://github.com/aristocratos/btop), [bottom](https://github.com/ClementTsang/bottom) and [System Informer](https://github.com/winsiderss/systeminformer). pcmon contains no code from these projects.
