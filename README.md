# pcmon

**See what your Windows PC is doing, live, right in your terminal.**

pcmon is a free, lightweight alternative to Task Manager and Resource Monitor. It shows what is using your processor, memory, disk and internet, and it runs as a single small program.

## What it does

- **Shows your PC at a glance:** live graphs for processor, memory, disk and network
- **Finds what is slowing you down:** see which apps are using the most, and sort the list however you like
- **Shows what is using your internet:** see which apps are connected, and how much data they use
- **Shows what is using your disk:** see which apps are reading and writing, and which files they touch
- **Lets you take control:** search for any app, pause it, or close it
- **Stays out of your way:** uses about 20 MB of memory and works with your keyboard or mouse

## What you need

- A 64-bit Windows 11 PC
- Windows 11 version 24H2 or later to see each app's network use

Nothing else needs to be installed.

## Install

**1. Download pcmon**

Go to the [latest release](https://github.com/SaintMusa/pcmon/releases/latest) and download the file ending in `win-x64.zip`. Leave it in your Downloads folder.

**2. Open PowerShell**

Open the Start menu, type `PowerShell`, and press Enter.

**3. Paste these lines and press Enter**

They unzip pcmon into your user folder and let you start it from anywhere. They do not need administrator rights.

```powershell
$dir = "$env:LOCALAPPDATA\Programs\pcmon"
New-Item -ItemType Directory -Force $dir | Out-Null
$zip = Get-ChildItem "$HOME\Downloads\pcmon-*-win-x64.zip" | Sort-Object LastWriteTime | Select-Object -Last 1
Expand-Archive $zip.FullName $dir -Force
$path = (Get-Item HKCU:\Environment).GetValue('Path', '', 'DoNotExpandEnvironmentNames')
if (($path -split ';') -notcontains $dir) { Set-ItemProperty HKCU:\Environment Path "$($path.TrimEnd(';'));$dir" -Type ExpandString }
[Environment]::SetEnvironmentVariable('PCMON_REFRESH', $null, 'User')
```

**4. Start it**

Close PowerShell, open it again, and run:

```powershell
pcmon
```

## How to use it

Click a tab or press its number to switch views.

| Tab | What it shows |
| --- | --- |
| **1 Overview** | Live graphs and every running app |
| **2 CPU** | Each processor core, plus apps and Windows services |
| **3 Memory** | How your memory is being used, and by which apps |
| **4 Disk** | Which apps are using your disk, plus your drives and free space |
| **5 Network** | Which apps are using your internet, plus connections and open ports |

These are the keys you will use most:

| Key | What it does |
| --- | --- |
| `Tab` | Move to the next table on the screen |
| Arrow keys | Move up and down the list, or change how it is sorted |
| `/` | Search by name (press `Esc` to clear) |
| `Enter` | Show details about the selected app |
| `t` | Show which apps started which |
| `e` | Make the current table fill the window |
| `Space` | Pin an app, so the other tables show only that app |
| `z` | Pause or resume the selected app |
| `k` | Close the selected app (it asks first) |
| `?` | Show every key |
| `q` | Quit |

**Be careful with `k` and `z`.** Closing an app loses any unsaved work, and pausing it makes it stop responding until you resume it.

## See even more with administrator mode

Right-click PowerShell or Terminal and choose **Run as administrator**, then start pcmon. You will also see:

- Which individual files are being read and written
- Which internet addresses each app is talking to
- Network use for system apps that Windows otherwise hides

## Update

Close pcmon, download the new zip, and repeat steps 2 and 3.

## Uninstall

Close pcmon, open PowerShell, then paste these lines and press Enter:

```powershell
$dir = "$env:LOCALAPPDATA\Programs\pcmon"
Remove-Item $dir -Recurse -Force
$path = (Get-Item HKCU:\Environment).GetValue('Path', '', 'DoNotExpandEnvironmentNames')
Set-ItemProperty HKCU:\Environment Path ((($path -split ';') | Where-Object { $_ -and $_ -ne $dir }) -join ';') -Type ExpandString
[Environment]::SetEnvironmentVariable('PCMON_REFRESH', $null, 'User')
```

## Is it safe?

pcmon is open source, so anyone can read the code. It does not connect to the internet or send your data anywhere.

It is not code-signed yet, so Windows or your antivirus may show a warning. If you want to check that your download is genuine, run the line below and compare the result with the `.sha256` file on the release page:

```powershell
Get-FileHash "$HOME\Downloads\pcmon-*-win-x64.zip" -Algorithm SHA256
```

pcmon also relies on some undocumented parts of Windows. A future Windows update could change them. If that happens, pcmon detects it and shows simpler disk numbers.

## Build it yourself

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download) and Visual Studio with the **Desktop development with C++** workload.

```powershell
git clone https://github.com/SaintMusa/pcmon.git
cd pcmon
.\build.ps1
.\dist\pcmon.exe
```

## Credits

pcmon was built with help from Claude, an AI assistant.

Ideas came from [wttop](https://github.com/jchomarat/wttop), [btop](https://github.com/aristocratos/btop), [bottom](https://github.com/ClementTsang/bottom) and [System Informer](https://github.com/winsiderss/systeminformer). pcmon contains no code from these projects.

## License

MIT. See [LICENSE](LICENSE).
