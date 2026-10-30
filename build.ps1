# Builds pcmon.exe into dist\. The native link step needs vswhere.exe on PATH.
$env:PATH = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer;$env:PATH"
dotnet publish "$PSScriptRoot\pcmon.csproj" -c Release -o "$PSScriptRoot\dist"
