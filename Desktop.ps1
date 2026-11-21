# Desktop discovery never reads account data. Only the current session and the
# verified current-user MSIX application's exact executable are eligible.
. (Join-Path $PSScriptRoot 'UiStyle.ps1')

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

function Show-SwitchNotice {
    param($Owner, [string]$Title, [string]$Message,
        [ValidateSet('Confirm','Force','Result')][string]$Mode = 'Result')
    Add-Type -AssemblyName System.Windows.Forms
    Add-Type -AssemblyName System.Drawing
    $palette = Get-SwitchPalette
    $dialog = New-Object Windows.Forms.Form
    try {
        $dialog.Name = 'SwitchNoticeDialog'
        $dialog.Text = 'Codex 账号切换'
        $dialog.FormBorderStyle = 'FixedDialog'
        $dialog.MaximizeBox = $false
        $dialog.MinimizeBox = $false
        $dialog.ShowIcon = $false
        $dialog.ShowInTaskbar = $false
        $dialog.BackColor = $palette.Surface
        $dialog.ForeColor = $palette.Ink
        $dialog.Font = New-Object Drawing.Font('Microsoft YaHei UI', 10)
        $dialog.AutoScaleMode = 'Font'
        $dialog.StartPosition = if ($Owner) { 'CenterParent' } else { 'CenterScreen' }
        $screen = if ($Owner) { [Windows.Forms.Screen]::FromControl($Owner) } else { [Windows.Forms.Screen]::PrimaryScreen }
        $width = [Math]::Min(520, [Math]::Max(340, $screen.WorkingArea.Width - 48))
        $contentWidth = $width - 48
        $flags = [Windows.Forms.TextFormatFlags]::WordBreak -bor [Windows.Forms.TextFormatFlags]::NoPrefix
        $heading = New-Object Windows.Forms.Label
        $heading.Name = 'NoticeTitle'
        $heading.Text = $Title
        $heading.UseMnemonic = $false
        $heading.Font = New-Object Drawing.Font('Microsoft YaHei UI', 14, [Drawing.FontStyle]::Bold)
        $heading.Location = New-Object Drawing.Point(24, 22)
        $headingHeight = [Math]::Max(30, [Windows.Forms.TextRenderer]::MeasureText($Title, $heading.Font, (New-Object Drawing.Size($contentWidth, 300)), $flags).Height)
        $heading.Size = New-Object Drawing.Size($contentWidth, $headingHeight)
        $dialog.Controls.Add($heading)

        # Short notices look like selectable paragraphs. Longer names and results
        # retain their complete text and gain a vertical scrollbar when needed.
        $body = New-Object Windows.Forms.TextBox
        $body.Name = 'NoticeMessage'
        $body.AccessibleName = '提示内容'
        $body.Multiline = $true
        $body.ReadOnly = $true
        $body.WordWrap = $true
        $body.AcceptsTab = $false
        $body.BorderStyle = 'None'
        $body.BackColor = $palette.Surface
        $body.ForeColor = $palette.Muted
        $body.Font = $dialog.Font
        $body.Text = $Message
        $body.Location = New-Object Drawing.Point(24, ($heading.Bottom + 14))
        $maximumBody = [Math]::Max(64, [Math]::Min(420, ($screen.WorkingArea.Height - $body.Top - 150)))
        $body.Size = New-Object Drawing.Size($contentWidth, $maximumBody)
        $body.ScrollBars = 'Vertical'
        $dialog.Controls.Add($body)
        # Ask the native edit control for physical wrapped lines. GDI's generic
        # WordBreak measurement can treat an unbroken account alias as one line.
        $null = $body.Handle
        $measured = ($body.GetLineFromCharIndex($body.TextLength) + 1) * $body.Font.Height + 12
        $bodyHeight = [Math]::Max(48, [Math]::Min($maximumBody, $measured))
        $body.Size = New-Object Drawing.Size($contentWidth, $bodyHeight)
        $body.ScrollBars = if ($measured -gt $maximumBody) { 'Vertical' } else { 'None' }

        $actions = New-Object Windows.Forms.Panel
        $actions.Name = 'NoticeActions'
        $actions.Location = New-Object Drawing.Point(24, ($body.Bottom + 24))
        $actions.Size = New-Object Drawing.Size($contentWidth, 38)
        $dialog.Controls.Add($actions)
        $cancel = New-Object Windows.Forms.Button
        $cancel.Name = if ($Mode -eq 'Result') { 'NoticeClose' } else { 'NoticeCancel' }
        $cancel.Text = if ($Mode -eq 'Result') { '知道了' } else { '取消' }
        $cancel.Size = New-Object Drawing.Size(94, 38)
        $cancel.DialogResult = [Windows.Forms.DialogResult]::Cancel
        Set-SwitchButtonStyle -Button $cancel -Role $(if ($Mode -eq 'Result') { 'Primary' } else { 'Secondary' })
        if ($Mode -eq 'Result') {
            $cancel.Location = New-Object Drawing.Point(($contentWidth - $cancel.Width), 0)
        } else {
            $action = New-Object Windows.Forms.Button
            $action.Name = 'NoticeAction'
            $action.Text = if ($Mode -eq 'Force') { '强制关闭并切换' } else { '退出并切换' }
            $action.Size = New-Object Drawing.Size(156, 38)
            $action.Location = New-Object Drawing.Point(($contentWidth - $action.Width), 0)
            $action.DialogResult = [Windows.Forms.DialogResult]::OK
            $action.TabIndex = 1
            Set-SwitchButtonStyle -Button $action -Role $(if ($Mode -eq 'Force') { 'Danger' } else { 'Primary' })
            $actions.Controls.Add($action)
            $cancel.Location = New-Object Drawing.Point(($action.Left - 10 - $cancel.Width), 0)
        }
        $cancel.TabIndex = 0
        $actions.Controls.Add($cancel)
        $body.TabIndex = 1
        $actions.TabIndex = 0
        $dialog.ClientSize = New-Object Drawing.Size($width, ($actions.Bottom + 24))
        # Enter, Escape and title-bar dismissal never approve a process action.
        # Only selecting the named action button can return OK.
        $dialog.AcceptButton = $cancel
        $dialog.CancelButton = $cancel
        $dialog.ActiveControl = $cancel
        $dialog.Add_Shown({ $cancel.Select() }.GetNewClosure())
        $answer = if ($Owner) { $dialog.ShowDialog($Owner) } else { $dialog.ShowDialog() }
        return [bool]($Mode -ne 'Result' -and $answer -eq [Windows.Forms.DialogResult]::OK)
    } finally { $dialog.Dispose() }
}

function Confirm-SwitchDesktop {
    param($Owner, [string]$Name, [string]$Phase)
    $message = if ($Phase -eq 'Force') {
        ('目标账号为“{0}”。' + [Environment]::NewLine + 'Codex 未能正常退出。将仅强制关闭已识别的 Codex 桌面端，这可能中断正在执行的任务；切换成功后将重新打开 Codex。') -f $Name
    } else { ('目标账号为“{0}”。' + [Environment]::NewLine + '将先退出 Codex，切换成功后重新打开。') -f $Name }
    $mode = if ($Phase -eq 'Force') { 'Force' } else { 'Confirm' }
    $title = if ($Phase -eq 'Force') { '强制关闭 Codex 并切换账号？' } else { '退出 Codex 并切换账号？' }
    return (Show-SwitchNotice -Owner $Owner -Title $title -Message $message -Mode $mode)
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
        [ValidateRange(0,60000)][int]$ExitTimeoutMilliseconds = 5000,
        [scriptblock]$SwitchAction = { param($Root,$Name,$Guard) Invoke-SwitchAccount -Root $Root -Name $Name -BeforeWriteGuard $Guard }
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
        $result = & $SwitchAction $Root $Name $guard
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
