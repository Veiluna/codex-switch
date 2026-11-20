# Desktop discovery never reads account data. Only the current session and the
# verified current-user MSIX application's exact executable are eligible.
function Initialize-SwitchDesktopNative {
    if ('CodexSwitch.NativePackage' -as [type]) { return }
    Add-Type -TypeDefinition @'
using System;
using System.Text;
using System.Runtime.InteropServices;
namespace CodexSwitch {
    public static class NativePackage {
        [DllImport("kernel32.dll", CharSet=CharSet.Unicode)]
        static extern int GetPackageFamilyName(IntPtr process, ref uint length, StringBuilder name);
        public static string Family(IntPtr process) {
            uint length = 0;
            int result = GetPackageFamilyName(process, ref length, null);
            if (result == 15700) return "";
            if (result != 122) throw new InvalidOperationException("Package identity unavailable.");
            var name = new StringBuilder((int)length);
            if (GetPackageFamilyName(process, ref length, name) != 0)
                throw new InvalidOperationException("Package identity unavailable.");
            return name.ToString();
        }
    }
}
'@ -ErrorAction Stop
}

function Get-SwitchDesktopInstallation {
    $packages = @(Get-AppxPackage -Name 'OpenAI.Codex' -ErrorAction Stop |
        Where-Object { $_.PackageFamilyName -ceq 'OpenAI.Codex_2p2nqsd0c76g0' })
    if ($packages.Count -eq 0) { return $null }
    if ($packages.Count -ne 1) { throw '桌面端安装不明确。' }
    $package = $packages[0]
    $manifest = Get-AppxPackageManifest -Package $package.PackageFullName -ErrorAction Stop
    $apps = @($manifest.Package.Applications.Application | Where-Object { $_.Id -ceq 'App' })
    if ($apps.Count -ne 1 -or $apps[0].Executable.Replace('\','/') -cne 'app/ChatGPT.exe') {
        throw '桌面端安装入口未经核实。'
    }
    $location = [IO.Path]::GetFullPath($package.InstallLocation)
    $executable = [IO.Path]::GetFullPath((Join-Path $location $apps[0].Executable))
    if (-not [IO.File]::Exists($executable)) { throw '桌面端安装入口不可用。' }
    [pscustomobject]@{ Path=$executable; Family=$package.PackageFamilyName; LaunchId=($package.PackageFamilyName + '!App') }
}

function Get-SwitchDesktopState {
    [CmdletBinding()]
    param(
        [scriptblock]$GetInstallation = { Get-SwitchDesktopInstallation },
        [scriptblock]$GetProcesses = { [Diagnostics.Process]::GetProcesses() },
        [scriptblock]$GetFamily = { param($Process) Initialize-SwitchDesktopNative; [CodexSwitch.NativePackage]::Family($Process.Handle) }
    )
    $desktopProcesses = New-Object 'System.Collections.Generic.List[object]'
    try {
        $installation = & $GetInstallation
        $session = [Diagnostics.Process]::GetCurrentProcess().SessionId
        foreach ($process in @(& $GetProcesses)) {
            try {
                if ($process.SessionId -ne $session -or $process.ProcessName -notin @('ChatGPT', 'Codex')) { continue }
                # Opening a handle pins the process object before inspecting its identity.
                $null = $process.Handle
                $path = $process.MainModule.FileName
                $family = & $GetFamily $process
                if (-not $installation) {
                    if ($family -ceq 'OpenAI.Codex_2p2nqsd0c76g0') { throw '安装信息不可用。' }
                    continue
                }
                if (-not [string]::Equals($path, $installation.Path, [StringComparison]::OrdinalIgnoreCase)) {
                    # The packaged resources/codex.exe is a CLI/service. A desktop
                    # executable from an old or unknown package location is unsafe.
                    if ($family -ceq $installation.Family -and $path -notmatch '(?i)\\app\\resources\\codex\.exe$') {
                        throw '桌面端安装路径已改变。'
                    }
                    continue
                }
                if ($family -cne $installation.Family) { throw '桌面端进程身份不一致。' }
                $desktopProcesses.Add([pscustomobject]@{ Id=$process.Id; StartTicks=$process.StartTime.ToUniversalTime().Ticks;
                    SessionId=$session; Path=$path; Family=$family })
            } catch {
                if (-not $process.HasExited) { throw }
            } finally { $process.Dispose() }
        }
        return [pscustomobject]@{ Known=$true; Processes=@($desktopProcesses.ToArray()); LaunchId=$(if ($installation) { $installation.LaunchId } else { $null }) }
    } catch { return [pscustomobject]@{ Known=$false; Processes=@(); LaunchId=$null } }
}

function Test-SwitchDesktopClosed {
    $state = Get-SwitchDesktopState
    return [bool]($state.Known -and @($state.Processes).Count -eq 0)
}

function Invoke-SwitchDesktopProcessAction {
    param($Snapshot, [switch]$Force,
        [scriptblock]$GetFamily = { param($Process) Initialize-SwitchDesktopNative; [CodexSwitch.NativePackage]::Family($Process.Handle) })
    $requested = $false
    foreach ($expected in @($Snapshot.Processes)) {
        $process = $null
        try {
            try { $process = [Diagnostics.Process]::GetProcessById($expected.Id) }
            catch [ArgumentException] { continue }
            $null = $process.Handle
            if ($process.HasExited) { continue }
            if ($process.Id -eq $PID -or $process.StartTime.ToUniversalTime().Ticks -ne $expected.StartTicks -or
                $process.SessionId -ne $expected.SessionId -or $process.SessionId -ne [Diagnostics.Process]::GetCurrentProcess().SessionId -or
                -not [string]::Equals($process.MainModule.FileName, $expected.Path, [StringComparison]::OrdinalIgnoreCase) -or
                (& $GetFamily $process) -cne $expected.Family) { throw '桌面端进程身份已改变。' }
            if ($Force) { $process.Kill(); $requested = $true }
            elseif ($process.CloseMainWindow()) { $requested = $true }
        } catch {
            if ($process -and $process.HasExited) { continue }
            throw
        } finally { if ($process) { $process.Dispose() } }
    }
    return $requested
}

function Wait-SwitchDesktopClosed {
    param([scriptblock]$GetState, [int]$TimeoutMilliseconds)
    $watch = [Diagnostics.Stopwatch]::StartNew()
    do {
        $state = & $GetState
        if (-not $state.Known) { throw '无法确认桌面端状态。' }
        if (@($state.Processes).Count -eq 0) { return $true }
        if ($watch.ElapsedMilliseconds -ge $TimeoutMilliseconds) { return $false }
        Start-Sleep -Milliseconds 100
    } while ($true)
}

function Start-SwitchDesktop {
    param($Snapshot)
    $installation = Get-SwitchDesktopInstallation
    if (-not $installation -or $installation.LaunchId -cne $Snapshot.LaunchId) { return $false }
    # The AUMID names the verified current-user package and manifest application.
    Start-Process -FilePath (Join-Path $env:WINDIR 'explorer.exe') -ArgumentList ('shell:AppsFolder\' + $installation.LaunchId) -WindowStyle Hidden -ErrorAction Stop | Out-Null
    $watch = [Diagnostics.Stopwatch]::StartNew()
    do {
        $state = Get-SwitchDesktopState
        if (-not $state.Known) { return $false }
        if (@($state.Processes).Count -gt 0) { return $true }
        Start-Sleep -Milliseconds 100
    } while ($watch.ElapsedMilliseconds -lt 5000)
    return $false
}

function Confirm-SwitchDesktop {
    param($Owner, [string]$Name, [string]$Phase)
    Add-Type -AssemblyName System.Windows.Forms
    $message = if ($Phase -eq 'Force') {
        'Codex 未能正常退出。强制关闭可能中断正在执行的任务。是否强制关闭已识别的 Codex 桌面端，然后切换到“{0}”？' -f $Name
    } else { '是否关闭 Codex 桌面端并切换到“{0}”？切换成功后将重新打开 Codex。' -f $Name }
    $answer = if ($Owner) {
        [System.Windows.Forms.MessageBox]::Show($Owner, $message, '切换 Codex 账号', 'YesNo', 'Warning', 'Button2')
    } else { [System.Windows.Forms.MessageBox]::Show($message, '切换 Codex 账号', 'YesNo', 'Warning', 'Button2') }
    return ($answer -eq [System.Windows.Forms.DialogResult]::Yes)
}

function Invoke-DesktopAccountSwitch {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory=$true)][string]$Root,
        [Parameter(Mandatory=$true)][string]$Name,
        $Owner,
        [scriptblock]$GetState = { Get-SwitchDesktopState },
        [Alias('Confirm')][scriptblock]$ConfirmAction = { param($Owner,$Name,$Phase) Confirm-SwitchDesktop -Owner $Owner -Name $Name -Phase $Phase },
        [scriptblock]$RequestClose = { param($Snapshot) Invoke-SwitchDesktopProcessAction -Snapshot $Snapshot },
        [scriptblock]$ForceClose = { param($Snapshot) Invoke-SwitchDesktopProcessAction -Snapshot $Snapshot -Force },
        [scriptblock]$Restart = { param($Snapshot) Start-SwitchDesktop -Snapshot $Snapshot },
        [ValidateRange(0,60000)][int]$ExitTimeoutMilliseconds = 5000
    )
    try {
        $snapshot = & $GetState
        if (-not $snapshot.Known) { return (New-SwitchResult $false $false 'DesktopUnknown' '无法安全识别 Codex 桌面端，未执行关闭或切换。') }
        $closedByTool = $false
        if (@($snapshot.Processes).Count -gt 0) {
            $approval = @(& $ConfirmAction $Owner $Name 'Close')
            if ($approval.Count -ne 1 -or $approval[0] -isnot [bool] -or -not $approval[0]) {
                return (New-SwitchResult $false $false 'Cancelled' '已取消，原登录保持不变。')
            }
            $closeRequest = @(& $RequestClose $snapshot)
            $closedByTool = ($closeRequest.Count -eq 1 -and $closeRequest[0] -is [bool] -and $closeRequest[0])
            if (-not (Wait-SwitchDesktopClosed -GetState $GetState -TimeoutMilliseconds $ExitTimeoutMilliseconds)) {
                $approval = @(& $ConfirmAction $Owner $Name 'Force')
                if ($approval.Count -ne 1 -or $approval[0] -isnot [bool] -or -not $approval[0]) {
                    return (New-SwitchResult $false $false 'Cancelled' '已取消强制关闭，原登录保持不变。桌面端可能已响应退出请求。')
                }
                # Only the exact original process identities are eligible for force.
                $forceRequest = @(& $ForceClose $snapshot)
                if ($forceRequest.Count -eq 1 -and $forceRequest[0] -is [bool] -and $forceRequest[0]) { $closedByTool = $true }
                if (-not (Wait-SwitchDesktopClosed -GetState $GetState -TimeoutMilliseconds $ExitTimeoutMilliseconds)) {
                    return (New-SwitchResult $false $false 'DesktopRunning' 'Codex 仍未完全退出，未执行切换。')
                }
            }
        }
        $guard = { $latest = & $GetState; return [bool]($latest.Known -and @($latest.Processes).Count -eq 0) }.GetNewClosure()
        $result = Invoke-SwitchAccount -Root $Root -Name $Name -BeforeWriteGuard $guard
        if (-not $result.Success -or -not $closedByTool) { return $result }
        try {
            $restarted = @(& $Restart $snapshot)
            if ($restarted.Count -eq 1 -and $restarted[0] -is [bool] -and $restarted[0]) {
                return (New-SwitchResult $true $result.Changed 'SwitchedAndRestarted' ('已切换到“{0}”，并重新打开 Codex。' -f $Name) $result.BackupPath)
            }
        } catch { $restarted = @($false) }
        return (New-SwitchResult $true $result.Changed 'RestartFailed' ('已切换到“{0}”，但未能确认 Codex 已重新打开。请手动打开 Codex。' -f $Name) $result.BackupPath)
    } catch { return (New-SwitchResult $false $false 'DesktopFailed' '桌面端操作未完成，未执行账号切换。请检查桌面端状态后重试。') }
}
