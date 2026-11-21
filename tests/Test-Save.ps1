param()
$ErrorActionPreference = 'Stop'
$project = Split-Path $PSScriptRoot -Parent
$fixture = Join-Path $PSScriptRoot ('.tmp-save-' + [guid]::NewGuid().ToString('N'))
function Assert-Save($Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function New-Fixture([string]$Name) {
    $path = Join-Path $fixture $Name
    [void][IO.Directory]::CreateDirectory($path)
    [IO.File]::WriteAllText((Join-Path $path 'auth.json'), '{"fake":"fixture-only","unknown":{"keep":true}}')
    return $path
}
try {
    . (Join-Path $project 'SaveAccount.ps1')
    . (Join-Path $project 'AccountList.ps1')
    $root = New-Fixture 'normal'
    $before = [Convert]::ToBase64String([IO.File]::ReadAllBytes((Join-Path $root 'auth.json')))
    $result = Save-SwitchAccount -Root $root -Name '中文账号'
    Assert-Save $result.Success 'Chinese account saves successfully'
    Assert-Save ([Convert]::ToBase64String([IO.File]::ReadAllBytes((Join-Path $root 'accounts/中文账号_auth.json'))) -ceq $before) 'Saved bytes preserve every unknown field'
    Assert-Save ((@(Get-SwitchAccountNames -Root $root) -join '|') -ceq '中文账号') 'Saved account appears through production list reader'
    Assert-Save (-not (Test-Path -LiteralPath (Join-Path $root 'accounts/.current'))) 'Saving does not claim active account ownership'
    foreach ($name in @('中文账号', '', ' ', '../escape', '..', 'bad/name', 'bad\name', 'x:y', 'x?', 'tail.', 'tail ', ' con', 'CON', 'NUL.json', 'COM1', 'LPT9', 'backup-hidden', 'BACKUP-hidden', 'alias_auth', 'alias_AUTH', '.current')) {
        $failed = Save-SwitchAccount -Root $root -Name $name
        Assert-Save (-not $failed.Success) ('Invalid or duplicate name refused: ' + $name)
    }
    [IO.File]::WriteAllText((Join-Path $root 'accounts/legacy.json'), 'FAKE-OLD-BYTES')
    Assert-Save (-not (Save-SwitchAccount -Root $root -Name 'legacy').Success) 'Legacy short file prevents overwrite'
    Assert-Save ([IO.File]::ReadAllText((Join-Path $root 'accounts/legacy.json')) -ceq 'FAKE-OLD-BYTES') 'Conflict preserves legacy bytes'
    Assert-Save ([Convert]::ToBase64String([IO.File]::ReadAllBytes((Join-Path $root 'auth.json'))) -ceq $before) 'All saves leave active auth intact'
    $missing = New-Fixture 'missing'
    [IO.File]::Delete((Join-Path $missing 'auth.json'))
    Assert-Save (-not (Save-SwitchAccount -Root $missing -Name 'new').Success) 'No active login is refused'
    Assert-Save (-not (Test-Path -LiteralPath (Join-Path $missing 'accounts/new_auth.json'))) 'Missing login leaves no partial archive'
    $blocked = New-Fixture 'blocked'
    [IO.File]::WriteAllText((Join-Path $blocked 'accounts'), 'FAKE-BLOCKER')
    Assert-Save (-not (Save-SwitchAccount -Root $blocked -Name 'new').Success) 'Account directory failure is handled'
    $lock = [IO.File]::Open((Join-Path $root 'accounts/.switch.lock'), [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    try { Assert-Save (-not (Save-SwitchAccount -Root $root -Name 'busy').Success) 'Concurrent mutation lock blocks save' } finally { $lock.Dispose() }
    Assert-Save (-not (Test-Path -LiteralPath (Join-Path $root 'accounts/busy_auth.json'))) 'Lock failure leaves no archive'
    $sourceLock = [IO.File]::Open((Join-Path $root 'auth.json'), [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    try { Assert-Save (-not (Save-SwitchAccount -Root $root -Name 'locked').Success) 'Unreadable active auth failure is handled' } finally { $sourceLock.Dispose() }
    Assert-Save (@(Get-ChildItem -LiteralPath (Join-Path $root 'accounts') -Filter '.save-*.tmp' -Force).Count -eq 0) 'Failure cleans temporary files'
    # A directory junction does not need developer mode or symbolic-link privilege.
    $junctionRoot = New-Fixture 'junction'
    $outside = New-Fixture 'outside'
    $junctionPath = Join-Path $junctionRoot 'accounts'
    & cmd.exe /d /c "mklink /J `"$junctionPath`" `"$outside`"" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Fixture junction creation failed' }
    try {
        Assert-Save (-not (Save-SwitchAccount -Root $junctionRoot -Name 'escape').Success) 'Reparse account directory is refused'
        Assert-Save (-not (Test-Path -LiteralPath (Join-Path $outside 'escape_auth.json'))) 'No write escapes through junction'
    } finally { [IO.Directory]::Delete($junctionPath) }
    # Exercise the production Click handler and its real modal input dialog.
    function Invoke-SaveDialogScenario($Window, [scriptblock]$Step) {
        $scenarioTimer = New-Object System.Windows.Forms.Timer
        $scenarioTimer.Interval = 60
        $scenarioTimer.Tag = [pscustomobject]@{ Step = $Step; Stage = 0; Ticks = 0; Failure = $null; Dialog = $null; Running = $false }
        $scenarioTimer.Add_Tick({
            param($sender, $eventArgs)
            $state = $sender.Tag
            if ($state.Running) { return }
            # Scaling and PerformClick can pump nested window messages. Never
            # allow a second tick to execute a partly completed scenario step.
            $sender.Stop()
            $state.Running = $true
            $state.Ticks++
            $dialog = @([System.Windows.Forms.Application]::OpenForms | Where-Object { $_.Name -eq 'SaveAccountDialog' }) | Select-Object -First 1
            try {
                if ($state.Ticks -gt 50) { throw 'Save scenario timed out' }
                if ($dialog) { & $state.Step $dialog $state }
            } catch {
                $state.Failure = $_.Exception.Message
                if ($dialog -and -not $dialog.IsDisposed) {
                    try { $dialog.CancelButton.PerformClick() }
                    finally {
                        if ($dialog.Visible -and -not $dialog.IsDisposed) {
                            $dialog.DialogResult = [System.Windows.Forms.DialogResult]::Cancel
                        }
                    }
                }
            } finally {
                $state.Running = $false
                if (-not $state.Failure -and (-not $dialog -or
                    (-not $dialog.IsDisposed -and $dialog.Visible -and
                     $dialog.DialogResult -eq [System.Windows.Forms.DialogResult]::None))) {
                    $sender.Start()
                }
            }
        })
        try {
            $scenarioTimer.Start()
            $Window.SaveButton.PerformClick()
            if ($scenarioTimer.Tag.Failure) { throw $scenarioTimer.Tag.Failure }
            Assert-Save ($scenarioTimer.Tag.Stage -gt 0) 'Actual modal scenario was exercised'
        } finally { $scenarioTimer.Stop(); $scenarioTimer.Dispose() }
    }

    . (Join-Path $project 'Window.ps1')
    $uiRoot = New-Fixture 'ui'
    $window = New-SwitchWindow -Root $uiRoot
    Assert-Save $window.SaveButton.Enabled 'Production window connects save by default'
    $timer = New-Object System.Windows.Forms.Timer
    $timer.Interval = 50
    $timer.Add_Tick({
        param($sender, $eventArgs)
        $sender.Tag.Ticks++
        $dialog = @([System.Windows.Forms.Application]::OpenForms | Where-Object { $_.Name -eq 'SaveAccountDialog' }) | Select-Object -First 1
        if ($dialog) {
            $sender.Stop()
            $sender.Tag.Seen = $true
            if ($sender.Tag.Cancel) { $dialog.Controls['CancelSaveButton'].PerformClick() }
            else {
                $dialog.Controls['AccountNameInput'].Text = '界面中文账号'
                $dialog.Controls['ConfirmSaveButton'].PerformClick()
            }
        } elseif ($sender.Tag.Ticks -gt 100) { $sender.Stop() }
    })
    try {
        $window.Form.Show()
        $window.SearchBox.Text = '不匹配的新存档筛选'
        Invoke-SaveDialogScenario $window {
            param($dialog, $state)
            $nameBox = $dialog.Controls['AccountNameInput']
            $errorLabel = $dialog.Controls['SaveErrorLabel']
            if ($state.Stage -eq 0) {
                Assert-Save ($dialog.BackColor.ToArgb() -eq [Drawing.Color]::White.ToArgb()) 'Save dialog follows white E surface'
                Assert-Save (-not $dialog.ShowIcon) 'Save dialog has no decorative icon'
                Assert-Save ($null -ne $errorLabel) 'Save dialog has inline error space'
                Assert-Save ($dialog.Controls['ConfirmSaveButton'].SwitchButtonRole -eq 'Primary') 'Save uses the shared primary treatment'
                Assert-Save ($dialog.Controls['CancelSaveButton'].SwitchButtonRole -eq 'Secondary') 'Cancel uses the shared secondary treatment'
                Assert-Save ($null -ne $nameBox.SwitchInputFrame) 'Name input has a visible focus border'
                Assert-Save ($nameBox.SwitchInputFrame.Right -eq $dialog.Controls['ConfirmSaveButton'].Right) 'Input outer right edge aligns with primary action'
                Assert-Save ($nameBox.SwitchInputFrame.Left -eq $errorLabel.Left) 'Field and error surface share the same outer left edge'
                Assert-Save $nameBox.Focused 'Name input receives initial keyboard focus'
                Assert-Save ($dialog.AcceptButton.Name -eq 'ConfirmSaveButton') 'Enter submits the name'
                Assert-Save ($dialog.CancelButton.Name -eq 'CancelSaveButton') 'Escape cancels the dialog'
                $dialog.Scale((New-Object Drawing.SizeF(1.5, 1.5)))
                $dialog.PerformLayout()
                Assert-Save ($nameBox.SwitchInputFrame.Bounds.Contains($nameBox.Bounds)) 'Scaled save input remains fully framed'
                Assert-Save (($nameBox.SwitchInputFrame.Width - $nameBox.Width) -lt 30) 'Scaled save frame is not enlarged twice'
                $state.Dialog = $dialog
                $nameBox.Text = '   '
                $dialog.AcceptButton.PerformClick()
                $state.Stage = 1
            } elseif ($state.Stage -eq 1) {
                Assert-Save ([object]::ReferenceEquals($dialog, $state.Dialog)) 'Invalid input stays in the original dialog'
                Assert-Save ($nameBox.Text -ceq '   ') 'Failed name remains editable'
                Assert-Save (-not [string]::IsNullOrWhiteSpace($errorLabel.Text)) 'Backend validation appears inline'
                Assert-Save $nameBox.SwitchInputHasError 'Failed input has a persistent error border as well as text'
                Assert-Save $nameBox.Focused 'Validation returns focus to the name'
                $nameBox.Text = '中文长名称' * 15
                Assert-Save ([string]::IsNullOrEmpty($errorLabel.Text)) 'Editing clears stale inline feedback'
                Assert-Save (-not $nameBox.SwitchInputHasError) 'Editing restores the normal focused input border'
                $dialog.AcceptButton.PerformClick()
                $state.Stage = 2
            }
        }
        $longName = '中文长名称' * 15
        Assert-Save ($window.AccountList.Items.Contains($longName)) 'Long Chinese name is saved through the real dialog'
        Assert-Save ($window.AccountList.SelectedItem -ceq $longName) 'Save selects the new archive for preview'
        Assert-Save ([string]::IsNullOrEmpty($window.SearchBox.Text)) 'Successful save clears a hiding search filter'
        Assert-Save ($window.DetailNameLabel.Text -ceq $longName) 'Saved archive appears in the detail preview'
        Invoke-SaveDialogScenario $window {
            param($dialog, $state)
            $nameBox = $dialog.Controls['AccountNameInput']
            if ($state.Stage -eq 0) {
                $state.Dialog = $dialog
                $nameBox.Text = '中文长名称' * 15
                $dialog.AcceptButton.PerformClick()
                $state.Stage = 1
            } elseif ($state.Stage -eq 1) {
                Assert-Save ([object]::ReferenceEquals($dialog, $state.Dialog)) 'Duplicate stays in the same dialog'
                Assert-Save ($nameBox.Text -ceq ('中文长名称' * 15)) 'Duplicate retains the submitted name'
                Assert-Save ($dialog.Controls['SaveErrorLabel'].Text -match '同名') 'Duplicate backend message is inline'
                $nameBox.Text = '重试后账号'
                $dialog.AcceptButton.PerformClick()
                $state.Stage = 2
            }
        }
        Assert-Save ($window.AccountList.Items.Contains('重试后账号')) 'Duplicate can be corrected and retried immediately'
        Assert-Save (-not (Test-Path -LiteralPath (Join-Path $uiRoot 'accounts/.current'))) 'Saving and preview do not switch accounts'

        $timer.Tag = [pscustomobject]@{ Seen = $false; Cancel = $false; Ticks = 0 }
        $timer.Start()
        $window.SaveButton.PerformClick()
        Assert-Save $timer.Tag.Seen 'Save button opens the real name dialog'
        Assert-Save ($window.StatusLabel.Text -ceq '已保存账号“界面中文账号”。') 'Save handler reports success'
        Assert-Save ($window.AccountList.Items.Contains('界面中文账号')) 'Save refreshes visible account list immediately'
        Assert-Save $window.SaveButton.Enabled 'Save button recovers after success'
        $filesBeforeCancel = @(Get-ChildItem -LiteralPath (Join-Path $uiRoot 'accounts') -Force).Count
        $timer.Tag = [pscustomobject]@{ Seen = $false; Cancel = $true; Ticks = 0 }
        $timer.Start()
        $window.SaveButton.PerformClick()
        Assert-Save $timer.Tag.Seen 'Cancel scenario opens actual name dialog'
        Assert-Save ($window.StatusLabel.Text -ceq '已取消保存。') 'Cancel has an accurate UI message'
        Assert-Save (@(Get-ChildItem -LiteralPath (Join-Path $uiRoot 'accounts') -Force).Count -eq $filesBeforeCancel) 'Cancel creates no files'
        Assert-Save $window.SaveButton.Enabled 'Save button recovers after cancellation'
        Assert-Save $window.SaveButton.Focused 'Cancel restores focus to the save launcher'
        Invoke-SaveDialogScenario $window {
            param($dialog, $state)
            if ($state.Stage -eq 0) {
                $dialog.Controls['AccountNameInput'].Text = ''
                $dialog.AcceptButton.PerformClick()
                $state.Stage = 1
            } else {
                Assert-Save (-not [string]::IsNullOrWhiteSpace($dialog.Controls['SaveErrorLabel'].Text)) 'Empty -Name goes through backend validation'
                $dialog.CancelButton.PerformClick()
                $state.Stage = 2
            }
        }
        Assert-Save ($window.StatusLabel.Text -ceq '已取消保存。') 'Cancel remains available after failed validation'
        Assert-Save (@(Get-ChildItem -LiteralPath (Join-Path $uiRoot 'accounts') -Force).Count -eq $filesBeforeCancel) 'Cancel after failure creates no archive'
    } finally { $timer.Stop(); $timer.Dispose(); $window.Form.Close(); $window.Form.Dispose() }
    $reopened = New-SwitchWindow -Root $uiRoot
    try { Assert-Save ($reopened.AccountList.Items.Contains('界面中文账号')) 'Reopened window retains saved account' }
    finally { $reopened.Form.Dispose() }
    Write-Output 'PASS: actual WinForms save/cancel, inline empty/duplicate retry, long Chinese name, focus, filtered preview, reopened list.'
    Write-Output 'PASS: ticket 03 fake-directory save, Chinese name, collision, unknown bytes, lock, failures and junction protection.'
} finally {
    $verified = [IO.Path]::GetFullPath($fixture)
    $prefix = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '.tmp-save-'))
    if (-not $verified.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe test cleanup target' }
    if (Test-Path -LiteralPath $verified) { Remove-Item -LiteralPath $verified -Recurse -Force }
}
