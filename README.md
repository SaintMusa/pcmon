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

## Install

pcmon needs 64-bit Windows 11. Per-process network numbers need Windows 11 24H2 or later. Nothing else is required.

1. Download `pcmon-<version>-win-x64.zip` from [Releases](https://github.com/SaintMusa/pcmon/releases/latest).

2. Unzip it into a folder of your own. Run this in PowerShell, using the name of the file you downloaded:

   ```powershell
   $dir = "$env:LOCALAPPDATA\Programs\pcmon"
   New-Item -ItemType Directory -Force $dir
   Expand-Archive "$HOME\Downloads\pcmon-1.0.0-win-x64.zip" $dir -Force
   ```

3. Add that folder to your `PATH`, so `pcmon` works from anywhere. Run this to open the window:

   ```powershell
   rundll32 sysdm.cpl,EditEnvironmentVariables
   ```

   Under **User variables**, select **Path** and click **Edit**. Click **New**, paste `%LOCALAPPDATA%\Programs\pcmon`, then click **OK** in each window.

4. Open a new terminal and run:

   ```powershell
   pcmon
   ```

Run the terminal as administrator to add disk activity by file and network activity by address.

To update, close pcmon and repeat steps 1 and 2. To uninstall, delete the folder and remove its `PATH` entry.

### Security warnings

pcmon is not code-signed, so Windows SmartScreen or your antivirus may warn about it. To check that your download is genuine, compare its hash with the `.sha256` file on the release page:

```powershell
Get-FileHash "$HOME\Downloads\pcmon-1.0.0-win-x64.zip" -Algorithm SHA256
```

If you have the [GitHub CLI](https://cli.github.com/), you can also confirm that the zip was built from this repository:

```powershell
gh attestation verify "$HOME\Downloads\pcmon-1.0.0-win-x64.zip" --repo SaintMusa/pcmon
```

## Build it yourself

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download) and Visual Studio with the **Desktop development with C++** workload.

```powershell
git clone https://github.com/SaintMusa/pcmon.git
cd pcmon
.\build.ps1
.\dist\pcmon.exe
```

`build.ps1` puts the Visual Studio installer folder on `PATH`, which the native link step needs in order to find `vswhere.exe`.

## Publish a release

Update `<Version>` in `pcmon.csproj`, then push a matching tag. GitHub builds, checks and publishes the release:

```powershell
git tag v1.0.1
git push origin v1.0.1
```

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

## License

MIT. See [LICENSE](LICENSE).
