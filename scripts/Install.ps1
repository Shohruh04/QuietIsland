param([switch]$NoStartup)
$ErrorActionPreference = 'Stop'
$project = Split-Path $PSScriptRoot -Parent
$destination = Join-Path $env:LOCALAPPDATA 'Programs\QuietIsland'
$publish = Join-Path $project 'publish'
$exe = Join-Path $destination 'QuietIsland.exe'
if (Test-Path -LiteralPath $exe) {
    $closing = Start-Process -FilePath $exe -ArgumentList '--exit' -WindowStyle Hidden -PassThru
    $closing.WaitForExit(10000) | Out-Null
    Get-Process -Name QuietIsland -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe } | ForEach-Object { $_.WaitForExit(10000) | Out-Null }
}
dotnet publish (Join-Path $project 'QuietIsland.csproj') -c Release -r win-x64 --self-contained false -o $publish
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
New-Item -ItemType Directory -Path $destination -Force | Out-Null
Copy-Item -Path (Join-Path $publish '*') -Destination $destination -Recurse -Force
$startupArgument = if ($NoStartup) { '--startup' } else { '--enable-startup' }
Start-Process -FilePath $exe -ArgumentList $startupArgument -WindowStyle Hidden
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut((Join-Path ([Environment]::GetFolderPath('Desktop')) 'Quiet Island.lnk'))
$shortcut.TargetPath = $exe
$shortcut.WorkingDirectory = $destination
$shortcut.IconLocation = (Join-Path $destination 'Assets\QuietIsland.ico') + ',0'
$shortcut.Description = 'Музыка и текст песни прямо в панели задач'
$shortcut.Save()
Write-Output "Installed: $exe"
