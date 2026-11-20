param()
$ErrorActionPreference = 'Stop'
$project = Split-Path $PSScriptRoot -Parent
. (Join-Path $project 'AccountSwitch.ps1')
function Assert($Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
$testRoot = Join-Path $project ('.test-desktop-' + [guid]::NewGuid().ToString('N'))
$children = New-Object 'System.Collections.Generic.List[object]'
try {
    New-Item -ItemType Directory -Path (Join-Path $testRoot 'accounts') -Force | Out-Null
    $auth = Join-Path $testRoot 'auth.json'
    [IO.File]::WriteAllText($auth, '{"fake":"original"}')
    [IO.File]::WriteAllText((Join-Path $testRoot 'accounts/目标.json'), '{"fake":"target"}')
    $state = @{ Running=$true; Close=0; Force=0; Restart=0; Confirm=0; ForceApproved=$false; FirstApproved=$false; Graceful=$false; RestartOk=$true }
    $discover = { [pscustomobject]@{ Known=$true; Processes=@(if ($state.Running) { [pscustomobject]@{Id=777} }); LaunchId='FAKE!App' } }.GetNewClosure()
    $confirmAction = { param($Owner,$Name,$Phase); $state.Confirm++; Assert ($Name -ceq '目标') 'Confirmation identifies target'; if ($Phase -eq 'Close') { return $state.FirstApproved }; return $state.ForceApproved }.GetNewClosure()
    $close = { param($Snapshot); $state.Close++; if ($state.Graceful) { $state.Running=$false }; return $true }.GetNewClosure()
    $force = { param($Snapshot); $state.Force++; $state.Running=$false; return $true }.GetNewClosure()
    $restart = { param($Snapshot); $state.Restart++; return $state.RestartOk }.GetNewClosure()
    $options = @{ Root=$testRoot; Name='目标'; GetState=$discover; ConfirmAction=$confirmAction; RequestClose=$close; ForceClose=$force; Restart=$restart; ExitTimeoutMilliseconds=0 }
    $result = Invoke-DesktopAccountSwitch @options
    Assert ($result.Code -eq 'Cancelled' -and $state.Close -eq 0 -and $state.Running) 'First cancellation keeps desktop running'
    Assert ([IO.File]::ReadAllText($auth) -ceq '{"fake":"original"}') 'First cancellation preserves login'
    $state.FirstApproved=$true
    $result = Invoke-DesktopAccountSwitch @options
    Assert ($result.Code -eq 'Cancelled' -and $state.Force -eq 0 -and $state.Running) 'Timeout cancellation never forces'
    Assert ([IO.File]::ReadAllText($auth) -ceq '{"fake":"original"}') 'Second cancellation preserves login'
    $state.ForceApproved=$true
    $result = Invoke-DesktopAccountSwitch @options
    Assert ($result.Success -and $result.Code -eq 'SwitchedAndRestarted' -and $state.Force -eq 1 -and $state.Restart -eq 1) 'Explicit second approval enables force and restart'
    $state.Running=$true; $state.Graceful=$true; $state.RestartOk=$false
    $result = Invoke-DesktopAccountSwitch @options
    Assert ($result.Success -and $result.Code -eq 'RestartFailed' -and $state.Force -eq 1) 'Graceful exit avoids force; restart failure preserves successful switch'
    $state.Running=$false; $before=$state.Restart
    $result = Invoke-DesktopAccountSwitch @options
    Assert ($result.Success -and $state.Restart -eq $before) 'Initially closed desktop stays closed'
    $state.Running=$true; $options.Name='missing'
    $confirmMissing = { param($Owner,$Name,$Phase); $true }
    $options.ConfirmAction=$confirmMissing
    $before=$state.Restart
    $result = Invoke-DesktopAccountSwitch @options
    Assert (-not $result.Success -and $state.Restart -eq $before) 'Failed switch never reopens desktop'
    $options.Name='目标'; $options.GetState={ [pscustomobject]@{Known=$false; Processes=@(); LaunchId=$null} }
    $before=$state.Close
    $result = Invoke-DesktopAccountSwitch @options
    Assert ($result.Code -eq 'DesktopUnknown' -and $state.Close -eq $before) 'Unknown desktop fails closed'
    $options.GetState=$discover; $state.Running=$true; $options.RequestClose={ param($Snapshot); $state.Running=$false; return $false }.GetNewClosure()
    $before=$state.Restart
    $result = Invoke-DesktopAccountSwitch @options
    Assert ($result.Success -and $state.Restart -eq $before) 'Externally closed desktop is not reopened by the tool'
    $race = @{Calls=0}
    $options.GetState={ $race.Calls++; [pscustomobject]@{Known=$true; Processes=@(if ($race.Calls -gt 1) { [pscustomobject]@{Id=888} }); LaunchId='FAKE!App'} }.GetNewClosure()
    $result = Invoke-DesktopAccountSwitch @options
    Assert ($result.Code -eq 'DesktopRunning') 'Reopened desktop is checked again inside the file lock'
    # These are owned local fake processes. No production discovery or real Codex
    # process action is invoked in this test.
    $fixture = Join-Path $testRoot 'ChatGPT.exe'
    Add-Type -OutputAssembly $fixture -OutputType WindowsApplication -ReferencedAssemblies 'System.Windows.Forms','System.Drawing' -TypeDefinition @'
using System;
using System.IO;
using System.Drawing;
using System.Windows.Forms;
public static class DesktopFixture {
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool ShowWindow(IntPtr window, int state);
    [STAThread] public static void Main(string[] args) {
        var form = new Form { Text = "Owned Codex Switch Test Fixture", StartPosition = FormStartPosition.Manual,
            Location = new Point(-32000,-32000), Size = new Size(100,100) };
        bool ignore = args.Length > 1 && args[1] == "ignore";
        form.FormClosing += (s,e) => { if (ignore) e.Cancel = true; };
        var deadline = new Timer { Interval = 30000 };
        deadline.Tick += (s,e) => Environment.Exit(0);
        form.Shown += (s,e) => { form.BeginInvoke(new Action(() => {
            ShowWindow(form.Handle, 4); File.WriteAllText(args[0], "ready"); deadline.Start();
        })); };
        Application.Run(form);
    }
}
'@
    $unrelatedDir = Join-Path $testRoot 'unrelated'
    New-Item -ItemType Directory -Path $unrelatedDir | Out-Null
    $unrelatedExe = Join-Path $unrelatedDir 'ChatGPT.exe'
    Copy-Item -LiteralPath $fixture -Destination $unrelatedExe
    function Start-Fixture([string]$Executable, [string]$Mode) {
        $ready = Join-Path $testRoot ([guid]::NewGuid().ToString('N') + '.ready')
        $child = Start-Process -FilePath $Executable -ArgumentList ('"' + $ready + '" ' + $Mode) -WindowStyle Hidden -PassThru
        $children.Add($child)
        $watch = [Diagnostics.Stopwatch]::StartNew()
        while (-not (Test-Path -LiteralPath $ready)) {
            if ($child.HasExited -or $watch.ElapsedMilliseconds -gt 5000) { throw 'Owned fixture failed to start.' }
            Start-Sleep -Milliseconds 50
        }
        do {
            $child.Refresh()
            if ($child.MainWindowHandle -ne [IntPtr]::Zero) { break }
            if ($watch.ElapsedMilliseconds -gt 5000) { throw 'Owned fixture main window unavailable.' }
            Start-Sleep -Milliseconds 50
        } while ($true)
        return $child
    }
    $normalChild = Start-Fixture $fixture 'normal'
    $otherChild = Start-Fixture $unrelatedExe 'ignore'
    $fakeInstall = { [pscustomobject]@{Path=$fixture; Family='FAKE-PACKAGE'; LaunchId='FAKE!App'} }.GetNewClosure()
    $getFamily = { param($Process) if ($Process.MainModule.FileName -ceq $fixture) { 'FAKE-PACKAGE' } else { 'OTHER-PACKAGE' } }.GetNewClosure()
    $fixtureIds = @($normalChild.Id,$otherChild.Id)
    $getOwnedProcesses = { foreach ($ownedId in $fixtureIds) { try { [Diagnostics.Process]::GetProcessById($ownedId) } catch [ArgumentException] { continue } } }.GetNewClosure()
    $getFixtureState = { Get-SwitchDesktopState -GetInstallation $fakeInstall -GetProcesses $getOwnedProcesses -GetFamily $getFamily }.GetNewClosure()
    $snapshot = & $getFixtureState
    Assert ($snapshot.Known -and @($snapshot.Processes).Count -eq 1 -and $snapshot.Processes[0].Id -eq $normalChild.Id) 'Exact executable excludes unrelated same-name app'
    $updatedInstall = { [pscustomobject]@{Path=(Join-Path $testRoot 'updated/app/ChatGPT.exe'); Family='FAKE-PACKAGE'; LaunchId='FAKE!App'} }.GetNewClosure()
    $updatedState = Get-SwitchDesktopState -GetInstallation $updatedInstall -GetProcesses $getOwnedProcesses -GetFamily $getFamily
    Assert (-not $updatedState.Known) 'Old desktop path after package update fails closed'
    Assert (Invoke-SwitchDesktopProcessAction -Snapshot $snapshot -GetFamily $getFamily) 'Normal close reaches owned main window'
    Assert ($normalChild.WaitForExit(5000)) 'Owned main window exits normally'
    Assert (-not $otherChild.HasExited) 'Unrelated same-name process survives normal close'
    $stubbornChild = Start-Fixture $fixture 'ignore'
    $fixtureIds = @($stubbornChild.Id,$otherChild.Id)
    $getOwnedProcesses = { foreach ($ownedId in $fixtureIds) { try { [Diagnostics.Process]::GetProcessById($ownedId) } catch [ArgumentException] { continue } } }.GetNewClosure()
    $snapshot = Get-SwitchDesktopState -GetInstallation $fakeInstall -GetProcesses $getOwnedProcesses -GetFamily $getFamily
    [void](Invoke-SwitchDesktopProcessAction -Snapshot $snapshot -GetFamily $getFamily)
    Assert (-not $stubbornChild.WaitForExit(200)) 'Owned fixture can refuse normal close'
    $expected = $snapshot.Processes[0]
    foreach ($property in @('StartTicks','SessionId','Path','Family')) {
        $bad = $expected | Select-Object *
        if ($property -in @('StartTicks','SessionId')) { $bad.$property++ } else { $bad.$property='WRONG' }
        $rejected=$false
        try { [void](Invoke-SwitchDesktopProcessAction -Snapshot ([pscustomobject]@{Processes=@($bad)}) -Force -GetFamily $getFamily) }
        catch { $rejected=$true }
        Assert ($rejected -and -not $stubbornChild.HasExited) 'Changed process identity is never killed'
    }
    Assert (Invoke-SwitchDesktopProcessAction -Snapshot $snapshot -Force -GetFamily $getFamily) 'Approved force targets owned original identity'
    Assert ($stubbornChild.WaitForExit(5000) -and -not $otherChild.HasExited) 'Only identified owned fake process is killed'
    Assert (-not (Invoke-SwitchDesktopProcessAction -Snapshot $snapshot -Force -GetFamily $getFamily)) 'Already exited original process is an idempotent no-op'
    $missingInstall = Get-SwitchDesktopState -GetInstallation { $null } -GetProcesses { @() }
    Assert ($missingInstall.Known -and @($missingInstall.Processes).Count -eq 0) 'Uninstalled desktop permits switching'
    $onlyUnrelated = Get-SwitchDesktopState -GetInstallation $fakeInstall -GetProcesses { [Diagnostics.Process]::GetProcessById($otherChild.Id) }.GetNewClosure() -GetFamily $getFamily
    Assert ($onlyUnrelated.Known -and @($onlyUnrelated.Processes).Count -eq 0) 'Unrelated same-name app is not desktop activity'
    $cliDir=Join-Path $testRoot 'app/resources'
    New-Item -ItemType Directory -Path $cliDir -Force | Out-Null
    $cliExe=Join-Path $cliDir 'codex.exe'
    Copy-Item -LiteralPath $fixture -Destination $cliExe
    $cliChild=Start-Fixture $cliExe 'ignore'
    $getCli={ [Diagnostics.Process]::GetProcessById($cliChild.Id) }.GetNewClosure()
    $onlyCli=Get-SwitchDesktopState -GetInstallation $fakeInstall -GetProcesses $getCli -GetFamily { param($Process) 'FAKE-PACKAGE' }
    Assert ($onlyCli.Known -and @($onlyCli.Processes).Count -eq 0 -and -not $cliChild.HasExited) 'Packaged resources CLI alone permits switching and is never closed'
    $flow = @{Child=(Start-Fixture $fixture 'normal'); Restarts=0}
    $getFlowProcesses={ try { [Diagnostics.Process]::GetProcessById($flow.Child.Id) } catch [ArgumentException] { return } }.GetNewClosure()
    $getFlowState={ Get-SwitchDesktopState -GetInstallation $fakeInstall -GetProcesses $getFlowProcesses -GetFamily $getFamily }.GetNewClosure()
    $closeFlow={ param($Snapshot) Invoke-SwitchDesktopProcessAction -Snapshot $Snapshot -GetFamily $getFamily }.GetNewClosure()
    $restartFlow={ param($Snapshot); $flow.Child=Start-Fixture $fixture 'normal'; $flow.Restarts++; return (-not $flow.Child.HasExited) }.GetNewClosure()
    $flowResult=Invoke-DesktopAccountSwitch -Root $testRoot -Name '目标' -GetState $getFlowState -ConfirmAction { $true } -RequestClose $closeFlow -Restart $restartFlow
    Assert ($flowResult.Code -eq 'SwitchedAndRestarted' -and $flow.Restarts -eq 1 -and -not $flow.Child.HasExited) 'Controlled real fake desktop closes, switches and restarts'
    Write-Output 'PASS: ticket 04 isolated workflow cancellation, normal exit, explicit force, switch failure, restart failure and write-time recheck.'
    Write-Output 'PASS: owned local fake Windows processes: real CloseMainWindow, refusal, Kill, PID/start/session/path/package boundaries; real Codex never closed.'
} finally {
    foreach ($child in $children) {
        try { if (-not $child.HasExited) { $child.Kill(); [void]$child.WaitForExit(5000) } }
        finally { $child.Dispose() }
    }
    $resolved = [IO.Path]::GetFullPath($testRoot)
    if ((Split-Path $resolved -Parent) -cne [IO.Path]::GetFullPath($project) -or
        (Split-Path $resolved -Leaf) -notmatch '^\.test-desktop-[0-9a-f]{32}$') { throw 'Unsafe test cleanup path.' }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
