# Builds StockPanel.exe into the project folder (next to config.json, the two lists and web\) and, unless
# -NoShortcut, puts a "stockpanel" shortcut on the desktop.
param([switch]$NoShortcut)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$exe = Join-Path $repo 'StockPanel.exe'
$out = Join-Path $repo 'build'

if (Get-Process StockPanel -ErrorAction SilentlyContinue) {
    throw 'stockpanel is running: close it first (right-click the panel > Exit), then build again.'
}

dotnet publish (Join-Path $repo 'src\StockPanel\StockPanel.csproj') -c Release -r win-x64 --self-contained false `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $out
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }
Copy-Item (Join-Path $out 'StockPanel.exe') $exe -Force
# the exe serves the page from web\ next to it, which is the source folder itself
"Built $exe"

if (-not $NoShortcut) {
    $link = Join-Path ([Environment]::GetFolderPath('Desktop')) 'stockpanel.lnk'
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($link)
    $shortcut.TargetPath = $exe
    $shortcut.WorkingDirectory = $repo
    $shortcut.IconLocation = "$exe,0"
    $shortcut.Description = 'stockpanel stock monitor'
    $shortcut.Save()
    "Shortcut: $link"
}
