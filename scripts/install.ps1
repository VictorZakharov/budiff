param(
    [string]$Version = "latest",
    [string]$InstallDir = "$HOME\.budiff\bin"
)

$ErrorActionPreference = "Stop"
$repo = "wellscoped/budiff"
$asset = "budiff-win-x64.zip"
$release = if ($Version -eq "latest") { "latest/download" } else { "download/$Version" }
$url = "https://github.com/$repo/releases/$release/$asset"
$tmp = Join-Path ([System.IO.Path]::GetTempPath()) ("budiff-" + [Guid]::NewGuid())
$zip = Join-Path $tmp $asset

New-Item -ItemType Directory -Force $tmp, $InstallDir | Out-Null
Invoke-WebRequest -Uri $url -OutFile $zip
Expand-Archive -Path $zip -DestinationPath $InstallDir -Force

$userPath = [Environment]::GetEnvironmentVariable("PATH", "User")
if (($userPath -split ';') -notcontains $InstallDir) {
    [Environment]::SetEnvironmentVariable("PATH", ($userPath.TrimEnd(';') + ";$InstallDir"), "User")
    Write-Host "Added $InstallDir to the user PATH. Open a new terminal before running budiff."
}

Write-Host "Installed budiff to $InstallDir"
