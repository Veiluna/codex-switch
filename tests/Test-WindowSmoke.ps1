param()
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type @'
using System;
using System.Text;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public static class SwitchWindowSmokeNative {
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr SendMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr SendMessage(IntPtr hwnd, uint msg, IntPtr wParam, StringBuilder text);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry {
        public uint Size, Usage, ProcessId;
        public UIntPtr DefaultHeapId;
        public uint ModuleId, Threads, ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExeFile;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32FirstW(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32NextW(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
    public static uint[] PowerShellChildren(uint parentId) {
        IntPtr snapshot = CreateToolhelp32Snapshot(2, 0);
        if (snapshot == new IntPtr(-1)) throw new InvalidOperationException("Process snapshot failed.");
        try {
            ProcessEntry entry = new ProcessEntry();
            entry.Size = (uint)Marshal.SizeOf(typeof(ProcessEntry));
            if (!Process32FirstW(snapshot, ref entry)) throw new InvalidOperationException("Process enumeration failed.");
            List<uint> children = new List<uint>();
            do {
                if (entry.ParentProcessId == parentId && String.Equals(entry.ExeFile, "powershell.exe", StringComparison.OrdinalIgnoreCase)) children.Add(entry.ProcessId);
            } while (Process32NextW(snapshot, ref entry));
            if (Marshal.GetLastWin32Error() != 18) throw new InvalidOperationException("Process enumeration failed.");
            return children.ToArray();
        } finally { CloseHandle(snapshot); }
    }
}
'@
$project = Split-Path $PSScriptRoot -Parent
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('codex-switch-ui-' + [guid]::NewGuid().ToString('N'))
$desktop = [System.Windows.Automation.AutomationElement]::RootElement
$title = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, 'Codex 账号切换')
$window = $null
$windowProcess = $null
$process = $null
$decoyWindow = $null
$decoyProcess = $null
function Find-WindowByProcess([int]$OwnerProcessId) {
    $owner = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $OwnerProcessId)
    $condition = New-Object System.Windows.Automation.AndCondition($title, $owner)
    $desktop.FindFirst([System.Windows.Automation.TreeScope]::Children, $condition)
}
function Get-OwnedToolWindow([System.Diagnostics.Process]$LauncherProcess) {
    if ($LauncherProcess.HasExited) { return }
    $childIds = [SwitchWindowSmokeNative]::PowerShellChildren($LauncherProcess.Id)
    foreach ($childId in $childIds) {
        $candidate = Find-WindowByProcess -OwnerProcessId $childId
        if ($candidate) {
            $owner = Get-Process -Id $childId -ErrorAction Stop
            # Retain a process handle so PID reuse cannot turn cleanup into an unrelated close.
            [void]$owner.Handle
            if ($owner.StartTime -lt $LauncherProcess.StartTime) { $owner.Dispose(); continue }
            return [pscustomobject]@{ Window = $candidate; Process = $owner }
        }
    }
}
function Close-TestWindow($TargetWindow, [System.Diagnostics.Process]$OwnerProcess) {
    if (-not $TargetWindow -or -not $OwnerProcess -or $OwnerProcess.HasExited) { return }
    if ($TargetWindow.Current.ProcessId -ne $OwnerProcess.Id) { throw 'Refusing to close an unowned test window.' }
    [void][SwitchWindowSmokeNative]::SendMessage([IntPtr]$TargetWindow.Current.NativeWindowHandle, 0x10, [IntPtr]::Zero, [IntPtr]::Zero)
}
function Assert-VisibleText([string]$Text) {
    $condition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $Text)
    if (-not $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)) { throw "Missing UI text: $Text" }
}
try {
    New-Item -ItemType Directory -Path $testRoot -Force | Out-Null
    $decoyScript = Join-Path $testRoot 'same-title-decoy.ps1'
    [IO.File]::WriteAllText($decoyScript, @'
Add-Type -AssemblyName System.Windows.Forms
$form = New-Object System.Windows.Forms.Form
$form.Text = 'Codex 账号切换'
$list = New-Object System.Windows.Forms.ListBox
[void]$list.Items.Add('DECOY-DO-NOT-READ-OR-CLOSE')
$form.Controls.Add($list)
try { [void]$form.ShowDialog() } finally { $form.Dispose() }
'@, (New-Object Text.UTF8Encoding($true)))
    foreach ($scenario in @('populated', 'empty')) {
        $fakeRoot = Join-Path $testRoot "$scenario .codex"
        if ($scenario -eq 'populated') {
            $accounts = Join-Path $fakeRoot 'accounts'
            New-Item -ItemType Directory -Path $accounts -Force | Out-Null
            foreach ($file in @('测试甲_auth.json', '测试乙.json', 'backup-hidden_auth.json')) {
                [IO.File]::WriteAllText((Join-Path $accounts $file), 'FAKE-ONLY')
            }
        } else { New-Item -ItemType Directory -Path $fakeRoot -Force | Out-Null }
        # A newly opened same-title window must never become the test target.
        $decoyProcess = Start-Process -FilePath powershell.exe -ArgumentList ('-NoProfile -STA -ExecutionPolicy Bypass -WindowStyle Hidden -File "{0}"' -f $decoyScript) -WindowStyle Hidden -PassThru
        [void]$decoyProcess.Handle
        $deadline = [DateTime]::UtcNow.AddSeconds(12)
        do {
            $decoyWindow = Find-WindowByProcess -OwnerProcessId $decoyProcess.Id
            if ($decoyWindow) { break }
            Start-Sleep -Milliseconds 100
        } while ([DateTime]::UtcNow -lt $deadline)
        if (-not $decoyWindow) { throw 'Same-title isolation fixture did not open.' }
        if (Get-OwnedToolWindow -LauncherProcess $decoyProcess) { throw 'A process without a launcher child was accepted.' }
        $launcher = Join-Path $project 'Open-Codex-Switch.cmd'
        $command = '/d /c ""{0}" -Root "{1}""' -f $launcher, $fakeRoot
        $process = Start-Process -FilePath cmd.exe -ArgumentList $command -WorkingDirectory $testRoot -WindowStyle Hidden -PassThru
        [void]$process.Handle
        $deadline = [DateTime]::UtcNow.AddSeconds(12)
        do {
            $owned = Get-OwnedToolWindow -LauncherProcess $process
            if ($owned) { $window = $owned.Window; $windowProcess = $owned.Process; break }
            Start-Sleep -Milliseconds 100
        } while ([DateTime]::UtcNow -lt $deadline)
        if (-not $window) { throw 'GUI launcher did not create a window within 12 seconds.' }
        if ($window.Current.ProcessId -eq $decoyProcess.Id) { throw 'Same-title isolation failed.' }
        if ($window.Current.IsOffscreen) { throw 'GUI window is offscreen.' }
        Assert-VisibleText '已保存的账号'
        # On this Windows runtime UIA exposes the classic ListBox as a Pane.
        # Read only our newly launched fixture window's list through LB_GET*.
        $list = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition) |
            Where-Object { $_.Current.ClassName -like '*LISTBOX*' } | Select-Object -First 1
        if (-not $list) { throw 'The visible account ListBox was not found.' }
        $handle = [IntPtr]$list.Current.NativeWindowHandle
        $count = [SwitchWindowSmokeNative]::SendMessage($handle, 0x18B, [IntPtr]::Zero, [IntPtr]::Zero).ToInt32()
        if ($count -lt 0) { throw 'Could not read ListBox count.' }
        $items = @(for ($index = 0; $index -lt $count; $index++) {
            $length = [SwitchWindowSmokeNative]::SendMessage($handle, 0x18A, [IntPtr]$index, [IntPtr]::Zero).ToInt32()
            if ($length -lt 0) { throw 'Could not read ListBox text length.' }
            $text = New-Object System.Text.StringBuilder($length + 1)
            [void][SwitchWindowSmokeNative]::SendMessage($handle, 0x189, [IntPtr]$index, $text)
            $text.ToString()
        })
        if ($scenario -eq 'populated') {
            if (($items | Sort-Object) -join '|' -cne '测试甲|测试乙') { throw 'Unexpected visible account list.' }
            Assert-VisibleText '已保存 2 个账号。'
        } else {
            if ($items.Count -ne 0) { throw 'Empty scenario contains accounts.' }
            Assert-VisibleText '还没有已保存的账号。'
        }
        Write-Output "PASS: actual visible GUI via .cmd from external cwd, $scenario fixture."
        Close-TestWindow -TargetWindow $window -OwnerProcess $windowProcess
        $window = $null
        $windowProcess.Dispose()
        $windowProcess = $null
        if (-not $process.WaitForExit(5000)) { throw 'Launcher did not exit after closing the window.' }
        if ($process.ExitCode -ne 0) { throw "Launcher exited with $($process.ExitCode)." }
        $process.Dispose()
        $process = $null
        if ($decoyProcess.HasExited) { throw 'An unrelated same-title window was closed.' }
        Write-Output 'PASS: independent same-title fixture remained open.'
        Close-TestWindow -TargetWindow $decoyWindow -OwnerProcess $decoyProcess
        $decoyWindow = $null
        if (-not $decoyProcess.WaitForExit(5000)) { throw 'Same-title fixture did not exit.' }
        $decoyProcess.Dispose()
        $decoyProcess = $null
    }
} finally {
    Close-TestWindow -TargetWindow $window -OwnerProcess $windowProcess
    if ($windowProcess) { $windowProcess.Dispose() }
    if ($process) { [void]$process.WaitForExit(5000); $process.Dispose() }
    Close-TestWindow -TargetWindow $decoyWindow -OwnerProcess $decoyProcess
    if ($decoyProcess) { [void]$decoyProcess.WaitForExit(5000); $decoyProcess.Dispose() }
    $resolvedRoot = [IO.Path]::GetFullPath($testRoot)
    $tempParent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    if (-not $resolvedRoot.StartsWith($tempParent, [StringComparison]::OrdinalIgnoreCase) -or
        (Split-Path $resolvedRoot -Leaf) -notlike 'codex-switch-ui-*') { throw 'Unsafe test cleanup path.' }
    if (Test-Path -LiteralPath $resolvedRoot) { Remove-Item -LiteralPath $resolvedRoot -Recurse -Force }
}
