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
    } finally { $timer.Stop(); $timer.Dispose(); $window.Form.Close(); $window.Form.Dispose() }
    $reopened = New-SwitchWindow -Root $uiRoot
    try { Assert-Save ($reopened.AccountList.Items.Contains('界面中文账号')) 'Reopened window retains saved account' }
    finally { $reopened.Form.Dispose() }
    Write-Output 'PASS: actual WinForms save/cancel input dialog, immediate list refresh, reopened list.'
    Write-Output 'PASS: ticket 03 fake-directory save, Chinese name, collision, unknown bytes, lock, failures and junction protection.'
} finally {
    $verified = [IO.Path]::GetFullPath($fixture)
    $prefix = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '.tmp-save-'))
    if (-not $verified.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe test cleanup target' }
    if (Test-Path -LiteralPath $verified) { Remove-Item -LiteralPath $verified -Recurse -Force }
}
