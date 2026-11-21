param()
$ErrorActionPreference = 'Stop'
$project = Split-Path $PSScriptRoot -Parent
. (Join-Path $project 'Desktop.ps1')
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
function Assert($Condition, [string]$Message) { if (-not $Condition) { throw $Message } }

# Each timer operates only on the modal form created in this process. No input
# is sent to the desktop, and no real account or desktop process is accessed.
function Test-NoticeModal {
    param([string]$Mode, [string]$Action, [string]$Message, [switch]$WithoutOwner, [switch]$Wrapper)
    $state = @{ Error=$null; Observed=$false }
    $owner = New-Object Windows.Forms.Form
    $timer = New-Object Windows.Forms.Timer
    $timer.Interval = 100
    $tick = {
        $timer.Stop()
        $dialog = @([Windows.Forms.Application]::OpenForms | Where-Object { $_.Name -ceq 'SwitchNoticeDialog' }) | Select-Object -First 1
        try {
            Assert ($null -ne $dialog -and $dialog.Visible -and $dialog.IsHandleCreated) 'The notice is a real visible modal Windows form'
            $state.Observed = $true
            Assert ($dialog.Modal -and -not $dialog.ShowIcon) 'Native notice is modal and has no decorative icon'
            Assert ($dialog.Text -ceq 'Codex 账号切换') 'The system title consistently identifies the product'
            if (-not $WithoutOwner) { Assert ($dialog.Owner -eq $owner) 'Notice belongs to the calling window' }
            $body = $dialog.Controls.Find('NoticeMessage', $true)[0]
            Assert ($body.Text -ceq $Message) 'Complete message is retained without truncating account names or results'
            Assert ($body.ReadOnly -and $body.Multiline -and $body.WordWrap -and $body.BorderStyle -eq 'None') 'Message is a copyable wrapped paragraph'
            Assert (-not $body.AcceptsTab) 'Message does not trap tab navigation'
            $buttonName = if ($Mode -eq 'Result') { 'NoticeClose' } else { 'NoticeCancel' }
            $cancel = $dialog.Controls.Find($buttonName, $true)[0]
            Assert ($dialog.AcceptButton -eq $cancel -and $dialog.CancelButton -eq $cancel) 'Enter and Esc default to the safe dismissal'
            Assert ($dialog.ActiveControl -eq $cancel) 'Safe dismissal has initial focus'
            foreach ($button in @($dialog.Controls.Find('NoticeActions', $true)[0].Controls)) {
                Assert ($button.Top -ge 0 -and $button.Right -le $button.Parent.ClientSize.Width -and $button.Bottom -le $button.Parent.ClientSize.Height) 'Every button fits the aligned bottom action row'
            }
            Assert ($body.Bottom -le $body.Parent.ClientSize.Height) 'The wrapped message remains within its allocated region'
            if ($Message.Length -gt 3000) {
                Assert ($body.ScrollBars -eq 'Vertical') 'Long results remain fully readable through a vertical scrollbar'
                $body.SelectionStart = $body.TextLength
                $body.ScrollToCaret()
                $lastPosition = $body.GetPositionFromCharIndex($body.TextLength - 1)
                Assert ($lastPosition.Y -ge 0 -and $lastPosition.Y -lt $body.ClientSize.Height) 'The last character can actually be scrolled into view'
                $body.SelectionStart = 0
                $body.ScrollToCaret()
            }
            if ($Mode -ne 'Result') {
                $actionButton = $dialog.Controls.Find('NoticeAction', $true)[0]
                $expectedText = if ($Mode -eq 'Force') { '强制关闭并切换' } else { '退出并切换' }
                Assert ($actionButton.Text -ceq $expectedText) 'The explicit action names its consequence'
                Assert ($actionButton.Bottom -eq $cancel.Bottom) 'Confirm and cancel buttons share a baseline'
                if ($Mode -eq 'Force') { Assert ($dialog.Controls.Find('NoticeTitle', $true)[0].Text.Contains('强制')) 'Force notice identifies severity in its visual title' }
            }
            switch ($Action) {
                'Explicit' { $dialog.Controls.Find($(if ($Mode -eq 'Result') { 'NoticeClose' } else { 'NoticeAction' }), $true)[0].PerformClick() }
                'Cancel' { $cancel.PerformClick() }
                'Close' { $dialog.Close() }
                default {
                    # Exercise Form's real dialog-key routing without globally
                    # injecting keystrokes into an unrelated foreground app.
                    $method = [Windows.Forms.Form].GetMethod('ProcessDialogKey', [Reflection.BindingFlags]'Instance,NonPublic')
                    $key = if ($Action -eq 'Esc') { [Windows.Forms.Keys]::Escape } else { [Windows.Forms.Keys]::Enter }
                    Assert ([bool]$method.Invoke($dialog, @($key))) 'The dialog handles its safe default keyboard action'
                }
            }
        } catch { $state.Error = $_ }
        finally { if ($dialog -and -not $dialog.IsDisposed) { $dialog.Close() } }
    }.GetNewClosure()
    $timer.Add_Tick($tick)
    try {
        if (-not $WithoutOwner) { $owner.Show() }
        $timer.Start()
        if ($Wrapper) {
            $actual = Confirm-SwitchDesktop -Name $script:longName -Phase $(if ($Mode -eq 'Force') { 'Force' } else { 'Close' })
        } else {
            $options = @{ Title=$(if ($Mode -eq 'Force') { '强制关闭 Codex？' } else { '退出 Codex 并切换账号？' }); Message=$Message; Mode=$Mode }
            if (-not $WithoutOwner) { $options.Owner=$owner }
            $actual = Show-SwitchNotice @options
        }
        if ($state.Error) { throw $state.Error }
        Assert $state.Observed 'The modal test driver observed the notice'
        Assert ($actual -is [bool]) 'A notice returns exactly one boolean'
        Assert ($actual -eq ($Mode -ne 'Result' -and $Action -eq 'Explicit')) 'Only an explicitly chosen process action approves the notice'
    } finally { $timer.Stop(); $timer.Dispose(); $owner.Dispose() }
}

$longName = ('虚构账号-long-account-' * 15)
foreach ($mode in @('Confirm','Force')) {
    $message = '将要切换到“{0}”。请确认当前任务已保存。' -f $longName
    foreach ($action in @('Enter','Esc','Cancel','Close','Explicit')) {
        Test-NoticeModal -Mode $mode -Action $action -Message $message
    }
}
foreach ($mode in @('Confirm','Force')) {
    $message = if ($mode -eq 'Force') {
        ('目标账号为“{0}”。' + [Environment]::NewLine + 'Codex 未能正常退出。将仅强制关闭已识别的 Codex 桌面端，这可能中断正在执行的任务；切换成功后将重新打开 Codex。') -f $longName
    } else { ('目标账号为“{0}”。' + [Environment]::NewLine + '将先退出 Codex，切换成功后重新打开。') -f $longName }
    Test-NoticeModal -Mode $mode -Action 'Explicit' -Message $message -WithoutOwner -Wrapper
}
$longResult = ('虚构账号操作结果：活动登录未更改，请检查后重试。abcdefghijklmnopqrstuvwxyz 0123456789。' + [Environment]::NewLine) * 80
foreach ($action in @('Enter','Esc','Close','Explicit')) {
    Test-NoticeModal -Mode 'Result' -Action $action -Message $longResult
}
Test-NoticeModal -Mode 'Result' -Action 'Explicit' -Message ('abcdefghijklmnopqrstuvwxyz' * 140)
Write-Output 'PASS: native modal notices, owner and ownerless calls, safe Enter/Esc/X/cancel, explicit force, complete long text and aligned actions.'
