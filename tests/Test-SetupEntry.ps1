param([string]$DistPath = (Join-Path (Split-Path $PSScriptRoot -Parent) 'dist'))
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class SetupEntryUi {
    [DllImport("user32.dll")]
    public static extern IntPtr SendMessage(IntPtr hwnd, uint message, IntPtr wparam, IntPtr lparam);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)]
    public static extern IntPtr SendMessageW(IntPtr hwnd, uint message, IntPtr wparam, string text);
    [DllImport("user32.dll", EntryPoint="SendMessageW", CharSet=CharSet.Unicode, ExactSpelling=true)]
    public static extern IntPtr ReadEditText(IntPtr hwnd, uint message, IntPtr capacity, System.Text.StringBuilder text);
    [DllImport("user32.dll")]
    public static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wparam, IntPtr lparam);
    [DllImport("user32.dll")]
    public static extern IntPtr GetLastActivePopup(IntPtr owner);
    [DllImport("user32.dll")]
    public static extern IntPtr GetDlgItem(IntPtr window, int id);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)]
    public static extern int GetWindowText(IntPtr hwnd, System.Text.StringBuilder text, int capacity);
    [DllImport("user32.dll")]
    public static extern int GetWindowLong(IntPtr hwnd, int index);
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
function Read-WindowText([IntPtr]$Handle) {
    $text = New-Object Text.StringBuilder 512
    [void][SetupEntryUi]::GetWindowText($Handle,$text,$text.Capacity)
    return $text.ToString()
}
function Test-FolderPicker($Window) {
    $controls = $Window.FindAll([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.Condition]::TrueCondition)
    $edit = $controls | Where-Object { $_.Current.ClassName -like 'WindowsForms10.EDIT.*' } | Select-Object -First 1
    $browse = $controls | Where-Object { $_.Current.ClassName -like 'WindowsForms10.BUTTON.*' -and (Read-WindowText ([IntPtr]$_.Current.NativeWindowHandle)) -like '浏览*' } | Select-Object -First 1
    if (!$edit -or !$browse) { throw 'Missing path input or browse button on the release executable.' }
    $editHandle = [IntPtr]$edit.Current.NativeWindowHandle
    $owner = [IntPtr]$Window.Current.NativeWindowHandle
    if ([SetupEntryUi]::GetWindowLong($editHandle,-16) -band 0x00300004) { throw 'Path box uses multiline or scrollbar styles.' }
    $typed = Join-Path $PSScriptRoot ('.tmp-uncreated-' + [guid]::NewGuid().ToString('N'))
    [void][SetupEntryUi]::SendMessageW($editHandle,0x000C,[IntPtr]::Zero,$typed)
    foreach ($command in @(2,1)) { # Cancel, then select the nearest existing parent.
        [void][SetupEntryUi]::PostMessage([IntPtr]$browse.Current.NativeWindowHandle,0x00F5,[IntPtr]::Zero,[IntPtr]::Zero)
        $deadline = [DateTime]::UtcNow.AddSeconds(10)
        do {
            Start-Sleep -Milliseconds 75
            $dialog = [SetupEntryUi]::GetLastActivePopup($owner)
            $button = if ($dialog -ne $owner -and (Read-WindowText $dialog) -eq '选择安装目录') { [SetupEntryUi]::GetDlgItem($dialog,$command) }
        } until (($null -ne $button -and $button -ne [IntPtr]::Zero) -or [DateTime]::UtcNow -ge $deadline)
        if ($null -eq $button -or $button -eq [IntPtr]::Zero) { throw 'Browse did not open the owned Windows folder picker.' }
        [void][SetupEntryUi]::PostMessage($button,0x00F5,[IntPtr]::Zero,[IntPtr]::Zero)
        $expected = if ($command -eq 2) { $typed } else { $PSScriptRoot }
        $deadline = [DateTime]::UtcNow.AddSeconds(10)
        do {
            Start-Sleep -Milliseconds 75
            $buffer = New-Object Text.StringBuilder 512
            [void][SetupEntryUi]::ReadEditText($editHandle,0x000D,[IntPtr]$buffer.Capacity,$buffer)
            $value = $buffer.ToString()
        } until (([SetupEntryUi]::GetLastActivePopup($owner) -eq $owner -and $value -eq $expected) -or [DateTime]::UtcNow -ge $deadline)
        if ($value -ne $expected -or [SetupEntryUi]::GetLastActivePopup($owner) -ne $owner) { throw 'Folder choice or cancellation returned an unexpected path.' }
    }
    if ([IO.Directory]::Exists($typed)) { throw 'Opening the picker created an unconfirmed installation directory.' }
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
        if ($verb -eq '') { Test-FolderPicker $window }
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
Write-Host 'PASS: real installer help, argument errors, native folder picker select/cancel, and cancelling install/uninstall.'
