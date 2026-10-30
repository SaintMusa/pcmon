# Builds pcmon.exe into dist\. With -Package it also makes the release zip and its checksum.
#   .\build.ps1
#   .\build.ps1 -Version 1.2.0 -Package
param([string]$Version, [switch]$Package)

$ErrorActionPreference = 'Stop'

# The native link step needs vswhere.exe on PATH
$env:PATH = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer;$env:PATH"

$dist = "$PSScriptRoot\dist"
$arguments = @('publish', "$PSScriptRoot\pcmon.csproj", '-c', 'Release', '-o', $dist)
if ($Version) { $arguments += "-p:Version=$Version" }
dotnet @arguments
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }

if ($Package) {
    if (-not $Version) { throw '-Package needs -Version' }
    $name = "pcmon-$Version-win-x64"
    $folder = "$dist\$name"
    Remove-Item $folder, "$folder.zip", "$folder.zip.sha256" -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Path $folder | Out-Null
    Copy-Item "$dist\pcmon.exe", "$PSScriptRoot\README.md", "$PSScriptRoot\LICENSE" $folder
    Compress-Archive -Path "$folder\*" -DestinationPath "$folder.zip"
    Remove-Item $folder -Recurse -Force

    $hash = (Get-FileHash "$folder.zip" -Algorithm SHA256).Hash.ToLowerInvariant()
    Set-Content "$folder.zip.sha256" -Value "$hash  $name.zip" -Encoding ascii
    Write-Output "Packaged $name.zip"
}
