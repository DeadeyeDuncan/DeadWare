<#
.SYNOPSIS
Measures a windowed app from outside its process: start-up time, then memory and CPU with the window shown and hidden.

.DESCRIPTION
Written for the WinUI 3 prototype comparison (docs/superpowers/specs/2026-11-11-winui3-migration-prototype-design.md),
but it measures any executable, so the same script covers the WPF side. It asserts nothing about the numbers; it only
records them. Each run starts the app afresh and always ends by killing the process it started.

Exit code 0 means the output file was written, even when a run recorded nulls with a reason. A non-zero exit means the
measurement could not be attempted: bad parameters, an unwritable output path, or the app already running.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Exe,
    [string[]]$Arguments = @(),
    [Parameter(Mandatory)][string]$Label,
    [Parameter(Mandatory)][string]$Out,
    [ValidateRange(1, 20)][int]$Runs = 3,
    [ValidateRange(1, 3600)][int]$SteadySeconds = 60,
    [ValidateRange(1, 600)][int]$StartupTimeoutSeconds = 60
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Add-Type -Namespace MeasureUi -Name Native -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool PostMessage(IntPtr window, uint message, IntPtr w, IntPtr l);
[DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr window);
[DllImport("user32.dll")] public static extern uint GetDpiForSystem();
[DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr window);
[DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr window, IntPtr dc);
[DllImport("gdi32.dll")] public static extern int GetDeviceCaps(IntPtr dc, int index);
'@

function Fail([string]$Message) {
    [Console]::Error.WriteLine("measure-ui: $Message")
    exit 2
}

# Resolve the executable: a path, or a name found on PATH.
$exePath = if (Test-Path -LiteralPath $Exe -PathType Leaf) { (Resolve-Path -LiteralPath $Exe).Path }
else {
    $found = Get-Command -Name $Exe -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $found) { Fail "Executable not found: $Exe" }
    $found.Source
}
$processName = [IO.Path]::GetFileNameWithoutExtension($exePath)
if (Get-Process -Name $processName -ErrorAction SilentlyContinue) {
    Fail "$processName is already running. Quit it first, so the measurement is of one instance only."
}

# Prove the output is writable before spending minutes measuring.
$outPath = [IO.Path]::GetFullPath($Out, (Get-Location).Path)
try {
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($outPath)) | Out-Null
    [IO.File]::WriteAllText($outPath, '')
} catch { Fail "Cannot write to $outPath ($($_.Exception.Message))" }

function Get-ScalePercent {
    # A DPI-aware host reports the scale as DPI; an unaware one only shows it as a ratio of real to virtual pixels.
    $dc = [MeasureUi.Native]::GetDC([IntPtr]::Zero)
    try {
        $virtual = [MeasureUi.Native]::GetDeviceCaps($dc, 8)   # HORZRES
        $real = [MeasureUi.Native]::GetDeviceCaps($dc, 118)    # DESKTOPHORZRES
    } finally { [MeasureUi.Native]::ReleaseDC([IntPtr]::Zero, $dc) | Out-Null }
    $ratio = if ($virtual -gt 0) { $real / $virtual } else { 1 }
    [int][Math]::Round([Math]::Max($ratio, [MeasureUi.Native]::GetDpiForSystem() / 96.0) * 100)
}

function Get-Machine {
    $adapters = @(Get-CimInstance Win32_VideoController | ForEach-Object { $_.Name })
    [ordered]@{
        osBuild       = "$((Get-CimInstance Win32_OperatingSystem).Caption) $([Environment]::OSVersion.Version)"
        processor     = (@(Get-CimInstance Win32_Processor)[0].Name).Trim()
        videoAdapters = $adapters
        # Names are recorded too, so a reader can judge this guess.
        hardwareGpu   = [bool](@($adapters | Where-Object { $_ -notmatch 'Basic|Hyper-V|Remote|Virtual' }).Count)
        scalePercent  = Get-ScalePercent
    }
}

function Measure-Steady($Process) {
    $Process.Refresh()
    $before = $Process.TotalProcessorTime
    Start-Sleep -Seconds $SteadySeconds
    $Process.Refresh()
    if ($Process.HasExited) { return $null }
    [ordered]@{
        workingSetBytes = $Process.WorkingSet64
        privateBytes    = $Process.PrivateMemorySize64
        cpuSeconds      = [Math]::Round(($Process.TotalProcessorTime - $before).TotalSeconds, 3)
    }
}

function Invoke-Run {
    $run = [ordered]@{ startupMs = $null; startupReason = $null; windowTitle = $null; visible = $null; hidden = $null; hiddenReason = $null }
    $start = [Diagnostics.ProcessStartInfo]::new($exePath)
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true   # keeps a console program's window away; a windowed app still shows its own
    $start.WorkingDirectory = [IO.Path]::GetDirectoryName($exePath)
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $process = [Diagnostics.Process]::Start($start)
    try {
        $window = [IntPtr]::Zero
        while ($clock.Elapsed.TotalSeconds -lt $StartupTimeoutSeconds) {
            if ($process.HasExited) { break }
            $process.Refresh()
            $candidate = $process.MainWindowHandle
            if ($candidate -ne [IntPtr]::Zero -and [MeasureUi.Native]::IsWindowVisible($candidate)) { $window = $candidate; break }
            Start-Sleep -Milliseconds 25
        }
        if ($window -eq [IntPtr]::Zero) {
            $run.startupReason = if ($process.HasExited) { "process exited with code $($process.ExitCode) before showing a window" }
            else { "no visible window within $StartupTimeoutSeconds s" }
            return $run
        }
        $run.startupMs = [int]$clock.ElapsedMilliseconds
        $run.windowTitle = $process.MainWindowTitle   # shows when what appeared was an error box, not the app

        $run.visible = Measure-Steady $process
        if ($null -eq $run.visible) { $run.hiddenReason = 'process exited while visible'; return $run }

        [MeasureUi.Native]::PostMessage($window, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null   # WM_CLOSE
        $process.WaitForExit(5000) | Out-Null
        if ($process.HasExited) { $run.hiddenReason = 'process exited on close'; return $run }
        if ([MeasureUi.Native]::IsWindowVisible($window)) { $run.hiddenReason = 'window did not hide'; return $run }
        $run.hidden = Measure-Steady $process
        if ($null -eq $run.hidden) { $run.hiddenReason = 'process exited while hidden' }
        return $run
    } finally {
        if (-not $process.HasExited) {
            try { $process.Kill($true) } catch [InvalidOperationException] { }
            $process.WaitForExit(15000) | Out-Null
        }
        $process.Dispose()
    }
}

$result = [ordered]@{
    label         = $Label
    exe           = [IO.Path]::GetFileName($exePath)   # the name only: a full path can hold a user name
    steadySeconds = $SteadySeconds
    machine       = Get-Machine
    runs          = @()
}
for ($index = 1; $index -le $Runs; $index++) {
    Write-Host "[$Label] run $index of $Runs"
    $result.runs += , (Invoke-Run)
}
$result | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $outPath -Encoding utf8
Write-Host "Wrote $outPath"

if ($env:GITHUB_STEP_SUMMARY) {
    function Format-Megabytes($Bytes) { if ($null -eq $Bytes) { '—' } else { '{0:N1}' -f ($Bytes / 1MB) } }
    $lines = @("### $Label", '', '| Run | Start-up ms | Visible private MB | Visible CPU s | Hidden private MB | Hidden CPU s | Note |', '| --- | --- | --- | --- | --- | --- | --- |')
    $number = 0
    foreach ($run in $result.runs) {
        $number++
        $visible = $run.visible; $hidden = $run.hidden
        $lines += '| {0} | {1} | {2} | {3} | {4} | {5} | {6} |' -f $number,
            $(if ($null -eq $run.startupMs) { '—' } else { $run.startupMs }),
            $(if ($visible) { Format-Megabytes $visible.privateBytes } else { '—' }), $(if ($visible) { $visible.cpuSeconds } else { '—' }),
            $(if ($hidden) { Format-Megabytes $hidden.privateBytes } else { '—' }), $(if ($hidden) { $hidden.cpuSeconds } else { '—' }),
            (@($run.startupReason, $run.hiddenReason) | Where-Object { $_ }) -join '; '
    }
    ($lines + '') -join "`n" | Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Encoding utf8
}
exit 0
