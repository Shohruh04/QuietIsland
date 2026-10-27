param(
    [ValidatePattern('^\d+\.\d+\.\d+(\.\d+)?$')]
    [string]$Version,
    [string]$IsccPath
)
$ErrorActionPreference = 'Stop'
$project = Split-Path $PSScriptRoot -Parent
if (-not $Version) { $Version = ([xml](Get-Content (Join-Path $project 'QuietIsland.csproj') -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1 }
$artifacts = Join-Path $project 'artifacts'
$publish = Join-Path $artifacts "publish\$Version\win-x64"
$lightPublish = Join-Path $artifacts "publish\$Version\win-x64-light"

function Remove-PublishOutput([string]$Directory) {
    # Remove only generated publish directories, never program or user-data files.
    $allowed = [IO.Path]::GetFullPath($artifacts).TrimEnd('\') + '\publish\'
    $target = [IO.Path]::GetFullPath($Directory)
    if (-not $target.StartsWith($allowed, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe publish output path.' }
    if (Test-Path -LiteralPath $target) {
        $resolved = (Resolve-Path -LiteralPath $target).Path
        if (-not $resolved.StartsWith($allowed, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe publish output path.' }
        if ((Get-Item -LiteralPath $target).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Publish output must not be a link.' }
        Remove-Item -LiteralPath $target -Recurse -Force
    }
}

function Assert-DistributionContents([string]$Directory) {
    $scripts = @(Get-ChildItem -LiteralPath $Directory -Recurse -File | Where-Object Extension -in '.ps1', '.psm1', '.bat', '.cmd')
    if ($scripts.Count) { throw 'User distributions must not contain developer scripts.' }
}

function Copy-DistributionNotices([string]$Directory) {
    $licenses = Join-Path $Directory 'Licenses'
    New-Item -ItemType Directory -Path $licenses -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $project 'THIRD_PARTY_NOTICES.md') -Destination $Directory -Force
    Copy-Item -LiteralPath (Join-Path $project 'LICENSE'),(Join-Path $project 'CHANGELOG.md') -Destination $Directory -Force
    Copy-Item -Path (Join-Path $project 'installer\licenses\*') -Destination $licenses -Force
}

if (-not $IsccPath) {
    $command = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    $candidates = @(
        (Join-Path $artifacts 'tools\inno-7.1.0\{app}\ISCC.exe'),
        "${env:ProgramFiles}\Inno Setup 7\ISCC.exe",
        "${env:ProgramFiles}\Inno Setup 6\ISCC.exe",
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
    )
    if ($command) { $candidates = @($command.Source) + $candidates }
    $IsccPath = $candidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
}
if (-not $IsccPath -or -not (Test-Path -LiteralPath $IsccPath -PathType Leaf)) {
    throw 'Inno Setup compiler not found. Install Inno Setup 6.3+ / 7 from https://jrsoftware.org/isdl.php, or pass -IsccPath to an extracted ISCC.exe.'
}
$IsccPath = (Resolve-Path -LiteralPath $IsccPath).Path

New-Item -ItemType Directory -Path $artifacts -Force | Out-Null
Remove-PublishOutput $publish
& dotnet publish (Join-Path $project 'QuietIsland.csproj') -c Release -r win-x64 --self-contained true -o $publish "-p:Version=$Version" '-p:PublishSingleFile=false' '-p:PublishTrimmed=false'
if ($LASTEXITCODE -ne 0) { throw 'Self-contained publish failed.' }
Assert-DistributionContents $publish
Copy-DistributionNotices $publish
$licenses = Join-Path $publish 'Licenses'

# Include the exact runtime pack licenses selected by restore, not a different installed runtime.
$assets = Get-Content -LiteralPath (Join-Path $project 'obj\project.assets.json') -Raw | ConvertFrom-Json
$packageRoots = @($assets.packageFolders.PSObject.Properties.Name)
$runtimePacks = @($assets.project.frameworks.PSObject.Properties.Value.downloadDependencies | Where-Object { $_.name -match '^Microsoft\.(NETCore|WindowsDesktop)\.App\.Runtime\.win-x64$' })
if ($runtimePacks.Count -lt 2) { throw 'Expected .NET and Windows Desktop runtime packs in self-contained restore.' }
foreach ($pack in $runtimePacks) {
    $packVersion = ($pack.version.Trim('[', ']') -split ',')[0].Trim()
    $relative = $pack.name.ToLowerInvariant() + '/' + $packVersion
    $package = $packageRoots | ForEach-Object { Join-Path $_ $relative } | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    if (-not $package) { throw "Runtime package not found: $($pack.Name)" }
    $licenseFiles = @(Get-ChildItem -LiteralPath $package -File | Where-Object { $_.Name -match '^(LICENSE|THIRD.PARTY.NOTICES)(\..*)?$' })
    if ($licenseFiles.Count -eq 0) { throw "Runtime package license missing: $($pack.Name)" }
    foreach ($file in $licenseFiles) {
        $targetName = $pack.name + '-' + $packVersion + '-' + $file.Name
        Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $licenses $targetName) -Force
    }
}

& $IsccPath "/DAppVersion=$Version" "/DPublishDir=$publish" "/DOutputDir=$artifacts" (Join-Path $project 'installer\QuietIsland.iss')
if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }
$zip = Join-Path $artifacts "QuietIsland-$Version-win-x64-portable.zip"
Compress-Archive -Path (Join-Path $publish '*') -DestinationPath $zip -CompressionLevel Optimal -Force

# Keep runtime-pack license collection above: this restore changes project.assets.json.
Remove-PublishOutput $lightPublish
& dotnet publish (Join-Path $project 'QuietIsland.csproj') -c Release -r win-x64 --self-contained false -o $lightPublish "-p:Version=$Version" '-p:PublishSingleFile=false' '-p:PublishTrimmed=false'
if ($LASTEXITCODE -ne 0) { throw 'Light publish failed.' }
Assert-DistributionContents $lightPublish
$runtimeFiles = @(Get-ChildItem -LiteralPath $lightPublish -Recurse -File | Where-Object Name -in 'coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll', 'System.Private.CoreLib.dll', 'clrjit.dll')
if ($runtimeFiles.Count) { throw 'Light distribution must not contain the .NET runtime.' }
Copy-DistributionNotices $lightPublish
$lightZip = Join-Path $artifacts "QuietIsland-$Version-win-x64-light.zip"
Compress-Archive -Path (Join-Path $lightPublish '*') -DestinationPath $lightZip -CompressionLevel Optimal -Force

$setup = Join-Path $artifacts "QuietIsland-Setup-$Version-win-x64.exe"
Get-FileHash -LiteralPath $setup,$zip,$lightZip -Algorithm SHA256 | ForEach-Object { '{0}  {1}' -f $_.Hash.ToLowerInvariant(), [IO.Path]::GetFileName($_.Path) } | Set-Content -LiteralPath (Join-Path $artifacts "QuietIsland-$Version-SHA256SUMS.txt") -Encoding utf8
Write-Output "Installer: $setup"
Write-Output "Portable:  $zip"
Write-Output "Light:     $lightZip"
