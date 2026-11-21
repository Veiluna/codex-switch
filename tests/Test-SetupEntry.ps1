param([string]$DistPath = (Join-Path (Split-Path $PSScriptRoot -Parent) 'dist'))
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class SetupEntryUi {
    [DllImport("user32.dll")]
    public static extern IntPtr SendMessage(IntPtr hwnd, uint message, IntPtr wparam, IntPtr lparam);
}
'@
$dist = [IO.Path]::GetFullPath($DistPath)
$metadata = Get-Content -LiteralPath (Join-Path $dist 'build-info.json') -Raw | ConvertFrom-Json
$setup = Join-Path $dist ('CodexSwitch-Setup-' + $metadata.Version + '-x64.exe')
function Start-Setup([string]$Arguments) {
    $start = New-Object Diagnostics.ProcessStartInfo
    $start.FileName = $setup
    $start.Arguments = $Arguments
    $start.UseShellExecute = $false
    $start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    return [Diagnostics.Process]::Start($start)
}
foreach ($probe in @(@('--help',0),@('install --invalid',2),@('install --quiet --components',2),@('uninstall --quiet --components cli',2),@('--quiet --components cli',2),@('install --quiet --components cli,cli',2))) {
    $process = Start-Setup $probe[0]
    try {
        if (-not $process.WaitForExit(5000) -or $process.ExitCode -ne $probe[1]) { throw 'Unexpected setup command result.' }
    } finally { if (-not $process.HasExited) { $process.Kill(); $process.WaitForExit() }; $process.Dispose() }
}
foreach ($verb in @('', 'uninstall')) {
    $process = Start-Setup $verb
    try {
        $deadline = [DateTime]::UtcNow.AddSeconds(12)
        $cancel = $null
        while ([DateTime]::UtcNow -lt $deadline -and -not $process.HasExited -and -not $cancel) {
            Start-Sleep -Milliseconds 100
            $byPid = New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ProcessIdProperty, $process.Id)
            $window = [Windows.Automation.AutomationElement]::RootElement.FindFirst([Windows.Automation.TreeScope]::Children, $byPid)
            if ($window) {
                $byName = New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::NameProperty, '取消')
                $cancel = $window.FindFirst([Windows.Automation.TreeScope]::Descendants,$byName)
            }
        }
        if (-not $cancel) { throw 'The real setup EXE did not show a cancellable confirmation page.' }
        $invoker = $null
        if ($cancel.TryGetCurrentPattern([Windows.Automation.InvokePattern]::Pattern,[ref]$invoker)) {
            $invoker.Invoke()
        } else {
            # Framework's MSAA proxy can expose the native button without InvokePattern.
            if ($cancel.Current.ProcessId -ne $process.Id -or $cancel.Current.ClassName -notlike 'WindowsForms10.BUTTON.*' -or !$cancel.Current.NativeWindowHandle) { throw 'Unexpected cancel control.' }
            [void][SetupEntryUi]::SendMessage([IntPtr]$cancel.Current.NativeWindowHandle,0x00F5,[IntPtr]::Zero,[IntPtr]::Zero)
        }
        if (-not $process.WaitForExit(5000) -or $process.ExitCode -ne 3) { throw 'Cancelling setup returned an unexpected exit code.' }
    } finally { if (-not $process.HasExited) { $process.Kill(); $process.WaitForExit() }; $process.Dispose() }
}
Write-Host 'PASS: real installer process help, invalid arguments, GUI install and uninstall cancellation.'
