[CmdletBinding()]
param(
    [ValidatePattern('^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$')][string]$Version = '0.9.1',
    [string]$Dotnet = 'dotnet',
    # An xUnit filter for the test step, such as "FullyQualifiedName!~MeasurementTests"; empty runs every test.
    [string]$TestFilter = ''
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = Split-Path $PSScriptRoot -Parent
if ($Dotnet -eq 'dotnet' -and (Test-Path -LiteralPath "$repo/.tools/dotnet/dotnet.exe")) {
    $Dotnet = "$repo/.tools/dotnet/dotnet.exe"
}
$artifacts = Join-Path $repo 'artifacts'
$buildId = [guid]::NewGuid().ToString('N')
$stage = Join-Path $artifacts "package-$buildId/DeadWare"
[IO.Directory]::CreateDirectory($stage) | Out-Null
# A file's path below a folder. [IO.Path]::GetRelativePath is missing from Windows PowerShell's .NET, and this script
# should run there as well as on PowerShell 7.
function Get-PathBelow([string]$Folder, [string]$File) { $File.Substring([IO.Path]::GetFullPath($Folder).TrimEnd([char[]]'\/').Length + 1) }

Push-Location $repo
try {
    $testArgs = @('test', 'DeadWare.slnx', '-c', 'Release', '--nologo')
    if ($TestFilter) { $testArgs += @('--filter', $TestFilter) }
    & $Dotnet @testArgs
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed. No package produced.' }
    # The WinUI 3 app is what ships. Its project file fixes the platform, the self-contained Windows App SDK and the
    # DeadWare assembly name, and copies the XAML resources a plain publish leaves out.
    & $Dotnet publish 'src/DeadWare.WinUI/DeadWare.WinUI.csproj' -c Release -r win-x64 --self-contained true -p:PublishTrimmed=false -p:PublishSingleFile=false "-p:Version=$Version" -p:DebugType=None -o $stage --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed. No package produced.' }
    $helperStage = Join-Path $artifacts "helper-$buildId"
    & $Dotnet publish 'src/DeadWare.Updater/DeadWare.Updater.csproj' -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:PublishTrimmed=false "-p:Version=$Version" -p:DebugType=None -o $helperStage --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Updater publish failed. No package produced.' }
    Copy-Item -LiteralPath (Join-Path $helperStage 'DeadWare.Updater.exe') -Destination $stage
    foreach ($name in @('LICENSE','THIRD-PARTY-NOTICES.md','README.md','CHANGELOG.md')) {
        Copy-Item -LiteralPath (Join-Path $repo $name) -Destination $stage
    }
    # Preserve dependency manifests and license/notice files. Never ship project.assets.json:
    # that restore file embeds local user and cache paths.
    $licenseDir = Join-Path $stage 'licenses'
    [IO.Directory]::CreateDirectory($licenseDir) | Out-Null
    Copy-Item -LiteralPath (Join-Path $repo 'licenses') -Destination (Join-Path $licenseDir 'texts') -Recurse
    $assets = Get-Content -LiteralPath 'src/DeadWare.WinUI/obj/project.assets.json' -Raw | ConvertFrom-Json
    $inventory = foreach ($entry in $assets.libraries.PSObject.Properties) {
        if ($entry.Value.type -ne 'package') { continue }
        $packagePath = $null
        foreach ($folder in $assets.packageFolders.PSObject.Properties.Name) {
            $candidate = Join-Path $folder $entry.Value.path
            if (Test-Path -LiteralPath $candidate) { $packagePath = $candidate; break }
        }
        if (-not $packagePath) { throw "Dependency package is missing: $($entry.Name)" }
        $manifest = Get-ChildItem -LiteralPath $packagePath -Filter '*.nuspec' -File | Select-Object -First 1
        [xml]$metadata = Get-Content -LiteralPath $manifest.FullName -Raw
        $licenseNode = $metadata.SelectSingleNode('//*[local-name()="metadata"]/*[local-name()="license"]')
        $copyrightNode = $metadata.SelectSingleNode('//*[local-name()="metadata"]/*[local-name()="copyright"]')
        $projectNode = $metadata.SelectSingleNode('//*[local-name()="metadata"]/*[local-name()="projectUrl"]')
        $dest = Join-Path $licenseDir ($entry.Name.Replace('/','-'))
        [IO.Directory]::CreateDirectory($dest) | Out-Null
        Copy-Item -LiteralPath $manifest.FullName -Destination $dest
        foreach ($file in (Get-ChildItem -LiteralPath $packagePath -File -Recurse | Where-Object { $_.Name -match '(?i)license|notice|copying' -and $_.Extension -notin @('.dll','.xml','.json','.nupkg','.p7s') })) {
            $relative = Get-PathBelow $packagePath $file.FullName
            $licenseTarget = Join-Path $dest $relative
            [IO.Directory]::CreateDirectory((Split-Path $licenseTarget -Parent)) | Out-Null
            Copy-Item -LiteralPath $file.FullName -Destination $licenseTarget
        }
        [pscustomobject]@{ Package=$entry.Name; License=if($licenseNode){$licenseNode.InnerText}else{'See package manifest'}; Copyright=if($copyrightNode){$copyrightNode.InnerText}else{''}; Project=if($projectNode){$projectNode.InnerText}else{''} }
    }
    $inventory | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $licenseDir 'dependencies.json') -Encoding utf8
    $packageFiles = [ordered]@{}
    foreach ($file in (Get-ChildItem -LiteralPath $stage -File -Recurse | Sort-Object FullName)) {
        $relative = (Get-PathBelow $stage $file.FullName).Replace('\','/')
        $packageFiles[$relative] = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    [pscustomobject]@{ Format=1; Version=$Version; Files=$packageFiles } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $stage 'deadware-package.json') -Encoding utf8
    $archive = Join-Path $artifacts "DeadWare-$Version-win-x64.zip"
    # Written entry by entry rather than with Compress-Archive: Windows PowerShell's version separates folders with
    # backslashes, and the updater refuses such an archive as an unexpected layout.
    Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
    if (Test-Path -LiteralPath $archive) { Remove-Item -LiteralPath $archive -Force }
    $zip = [IO.Compression.ZipFile]::Open($archive, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($file in (Get-ChildItem -LiteralPath $stage -File -Recurse | Sort-Object FullName)) {
            $entry = 'DeadWare/' + (Get-PathBelow $stage $file.FullName).Replace('\','/')
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $file.FullName, $entry, [IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
    } finally { $zip.Dispose() }
    # The updater refuses an archive outside its limits (DeadWare.UpdateCore: Packages.Extract, ReleasePolicy). Check
    # the same ones here, so a package no installed copy could take never reaches a release.
    $check = [IO.Compression.ZipFile]::OpenRead($archive)
    try {
        $bad = @($check.Entries | Where-Object { -not $_.FullName.StartsWith('DeadWare/', [StringComparison]::Ordinal) -or $_.FullName.Contains('\') })
        $expanded = ($check.Entries | Measure-Object Length -Sum).Sum
        if ($bad.Count -or $check.Entries.Count -gt 2048) { throw "The updater would refuse this archive's layout: $($bad.Count) misplaced entries, $($check.Entries.Count) entries (limit 2048)." }
        if ($expanded -gt 768MB) { throw "The package expands to $expanded bytes, over the updater's 768 MB limit." }
    } finally { $check.Dispose() }
    if ((Get-Item -LiteralPath $archive).Length -gt 256MB) { throw "The archive is over the updater's 256 MB download limit." }
    $hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $([IO.Path]::GetFileName($archive))" | Set-Content -LiteralPath "$archive.sha256" -Encoding ascii
    [pscustomobject]@{ Version=$Version; Executable=(Join-Path $stage 'DeadWare.exe'); Archive=$archive; SHA256=$hash } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $artifacts 'last-package.json') -Encoding utf8
    Write-Output "Executable: $(Join-Path $stage 'DeadWare.exe')"
    Write-Output "Archive: $archive"
    Write-Output "SHA256: $hash"
} finally { Pop-Location }
