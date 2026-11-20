param()
$ErrorActionPreference = 'Stop'
$project = Split-Path $PSScriptRoot -Parent
$testRoot = Join-Path $project ('.test-switch-' + [guid]::NewGuid().ToString('N'))
function Assert($Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Put([string]$Path, [string]$Text) { [IO.File]::WriteAllText($Path, $Text, (New-Object Text.UTF8Encoding($false))) }
function Read([string]$Path) { [IO.File]::ReadAllText($Path) }
function FakeLogin([string]$User, [string]$Workspace, [string]$Version) {
    $claims = @{ 'https://api.openai.com/auth' = @{ chatgpt_user_id=$User; chatgpt_account_id=$Workspace } } | ConvertTo-Json -Compress
    $payload = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($claims)).TrimEnd('=').Replace('+','-').Replace('/','_')
    return (@{ tokens=@{ id_token=('FAKE.'+$payload+'.FAKE'); account_id=$Workspace; access_token=('FAKE-'+$Version); refresh_token=('FAKE-REFRESH-'+$Version) }; unknown=@{ nested=@('keep',42,$null) } } | ConvertTo-Json -Depth 8)
}
try {
    $accounts = Join-Path $testRoot 'accounts'
    New-Item -ItemType Directory -Path $accounts -Force | Out-Null
    $auth = Join-Path $testRoot 'auth.json'
    $marker = Join-Path $accounts '.current'
    $a = Join-Path $accounts '甲_auth.json'
    $b = Join-Path $accounts '乙.json'
    Put $auth '{"fake":"current-refreshed"}'
    Put $a '{"fake":"archived-a"}'
    Put $b '{"fake":"archived-b"}'
    Put $marker '甲'
    . (Join-Path $project 'AccountSwitch.ps1')
    $allow = { $true }
    $result = Invoke-SwitchAccount -Root $testRoot -Name '乙' -BeforeWriteGuard $allow
    Assert ($result.Success -and $result.Changed) 'Switch should succeed'
    Assert ((Read $auth) -ceq (Read $b)) 'Target bytes become active'
    Assert ((Read $a) -ceq '{"fake":"archived-a"}') 'Stale marker never overwrites named archive'
    Assert ((Read $result.BackupPath) -ceq '{"fake":"current-refreshed"}') 'Original login has an independent backup'
    Assert ((Read $marker) -ceq '乙') 'Chinese marker round trips'
    $backupCount = @(Get-ChildItem $accounts -Filter 'backup-*.json').Count
    $again = Invoke-SwitchAccount -Root $testRoot -Name '乙' -BeforeWriteGuard $allow
    Assert ($again.Success -and -not $again.Changed) 'Repeated clicks are idempotent'
    Assert (@(Get-ChildItem $accounts -Filter 'backup-*.json').Count -eq $backupCount) 'Repeated click creates no backup'
    $missing = Invoke-SwitchAccount -Root $testRoot -Name '不存在' -BeforeWriteGuard $allow
    Assert (-not $missing.Success -and (Read $auth) -ceq (Read $b)) 'Missing target preserves login'
    foreach ($name in @('../escape', 'backup-danger', 'CON', 'bad:name', 'trailing.')) {
        Assert (-not (Invoke-SwitchAccount -Root $testRoot -Name $name -BeforeWriteGuard $allow).Success) 'Unsafe name rejected'
    }
    Assert ((Invoke-SwitchAccount -Root 'G:relative' -Name '乙' -BeforeWriteGuard $allow).Code -eq 'InvalidPath') 'Drive-relative roots are not absolute roots'
    foreach ($badJson in @('', 'not-json', '[1,2]', '{broken')) {
        Put (Join-Path $accounts 'broken.json') $badJson
        $invalid = Invoke-SwitchAccount -Root $testRoot -Name 'broken' -BeforeWriteGuard $allow
        Assert ($invalid.Code -eq 'InvalidTarget' -and (Read $auth) -ceq (Read $b)) 'Corrupt target cannot replace active login'
    }
    $denied = Invoke-SwitchAccount -Root $testRoot -Name '甲' -BeforeWriteGuard { $false }
    Assert (-not $denied.Success -and $denied.Code -eq 'DesktopRunning') 'Guard blocks active desktop'
    $held = [IO.File]::Open((Join-Path $accounts '.switch.lock'), 'OpenOrCreate', 'ReadWrite', 'None')
    try {
        Assert ((Invoke-SwitchAccount -Root $testRoot -Name '甲' -BeforeWriteGuard $allow).Code -eq 'Busy') 'Concurrent writer is refused'
    } finally { $held.Dispose() }
    # Fail the marker write after auth replacement, exercising rollback using production I/O.
    $held = [IO.File]::Open($marker, 'Open', 'Read', 'Read')
    try {
        $failed = Invoke-SwitchAccount -Root $testRoot -Name '甲' -BeforeWriteGuard $allow
        Assert (-not $failed.Success -and $failed.Code -eq 'WriteFailed') 'Mid-transaction write failure is reported'
        Assert ((Read $auth) -ceq (Read $b)) 'Failed switch restores original active bytes'
        Assert ((Read $marker) -ceq '乙') 'Failed switch preserves previous marker'
    } finally { $held.Dispose() }
    Assert (@(Get-ChildItem $testRoot -Recurse -Filter '*.tmp').Count -eq 0) 'Temporary credential files are cleaned'
    # A shared workspace ID does not identify an individual user.
    $archived = FakeLogin 'fake-user-1' 'fake-shared-workspace' 'old'
    $otherUser = FakeLogin 'fake-user-2' 'fake-shared-workspace' 'new'
    Put $a $archived
    Put $auth $otherUser
    Put $marker '甲'
    $other = Invoke-SwitchAccount -Root $testRoot -Name '乙' -BeforeWriteGuard $allow
    Assert ($other.Success -and (Read $a) -ceq $archived) 'Different users in the same workspace never overwrite the archive'
    Assert ((Read $other.BackupPath) -ceq $otherUser) 'Unknown ownership retains exact current bytes'
    # Same user plus workspace is sufficient for refreshing the proven named archive.
    $refreshed = FakeLogin 'fake-user-1' 'fake-shared-workspace' 'rotated'
    Put $auth $refreshed
    Put $marker '甲'
    $rotated = Invoke-SwitchAccount -Root $testRoot -Name '乙' -BeforeWriteGuard $allow
    Assert ($rotated.Success -and (Read $a) -ceq $refreshed) 'Token rotation updates only the proven previous archive, preserving unknown fields byte for byte'
    $return = Invoke-SwitchAccount -Root $testRoot -Name '甲' -BeforeWriteGuard $allow
    Assert ($return.Success -and (Read $auth) -ceq $refreshed) 'Returning to the previous account restores rotated tokens'
    $sameIdentity = FakeLogin 'fake-user-1' 'fake-shared-workspace' 'latest'
    Put $auth $sameIdentity
    $sameResult = Invoke-SwitchAccount -Root $testRoot -Name '甲' -BeforeWriteGuard $allow
    Assert ($sameResult.Success -and (Read $auth) -ceq $sameIdentity -and (Read $a) -ceq $sameIdentity) 'Selecting the current identity does not restore stale archived tokens'
    Put $marker '乙'
    $beforeRepair = @(Get-ChildItem $accounts -Filter 'backup-*.json').Count
    $repair = Invoke-SwitchAccount -Root $testRoot -Name '甲' -BeforeWriteGuard $allow
    Assert ($repair.Success -and -not $repair.Changed -and (Read $marker) -ceq '甲') 'Equal bytes repair a stale selection marker'
    Assert (@(Get-ChildItem $accounts -Filter 'backup-*.json').Count -eq $beforeRepair) 'Marker repair creates no credential backup'
    $afterRepair = FakeLogin 'fake-user-1' 'fake-shared-workspace' 'after-marker-repair'
    Put $auth $afterRepair
    $null = Invoke-SwitchAccount -Root $testRoot -Name '乙' -BeforeWriteGuard $allow
    $backAfterRepair = Invoke-SwitchAccount -Root $testRoot -Name '甲' -BeforeWriteGuard $allow
    Assert ($backAfterRepair.Success -and (Read $auth) -ceq $afterRepair) 'Select equal bytes, refresh, leave and return retains the refreshed login'
    Remove-Item -LiteralPath $marker -Force
    $repairMissing = Invoke-SwitchAccount -Root $testRoot -Name '甲' -BeforeWriteGuard $allow
    Assert ($repairMissing.Success -and (Read $marker) -ceq '甲') 'Equal bytes also repair a missing marker'
    Put $marker '乙'
    $held = [IO.File]::Open($marker, 'Open', 'Read', 'Read')
    try {
        $blockedRepair = Invoke-SwitchAccount -Root $testRoot -Name '甲' -BeforeWriteGuard $allow
        Assert (-not $blockedRepair.Success -and $blockedRepair.Code -eq 'WriteFailed') 'Failed marker repair is not reported as success'
        Assert ((Read $auth) -ceq $afterRepair) 'Failed marker repair preserves active login'
    } finally { $held.Dispose() }
    # A failed first login restores the original absence of auth.json.
    Remove-Item -LiteralPath $auth -Force
    $held = [IO.File]::Open($marker, 'Open', 'Read', 'Read')
    try {
        $firstFailed = Invoke-SwitchAccount -Root $testRoot -Name '甲' -BeforeWriteGuard $allow
        Assert (-not $firstFailed.Success -and -not [IO.File]::Exists($auth)) 'Failure without a previous active file restores signed-out state'
    } finally { $held.Dispose() }
    $first = Invoke-SwitchAccount -Root $testRoot -Name '甲' -BeforeWriteGuard $allow
    Assert ($first.Success -and (Read $auth) -ceq (Read $a)) 'Switch works when no active file existed'
    Put (Join-Path $accounts 'legacy_auth_auth.json') '{"fake":"legacy"}'
    Assert ((Invoke-SwitchAccount -Root $testRoot -Name 'legacy_auth' -BeforeWriteGuard $allow).Success) 'Safe existing names ending in _auth remain selectable'
    $longName = 'n' * 110
    Put (Join-Path $accounts ($longName + '.json')) '{"fake":"long-legacy"}'
    Assert ((Invoke-SwitchAccount -Root $testRoot -Name $longName -BeforeWriteGuard $allow).Success) 'Safe long existing names remain selectable'
    # Review regression: an existing zero-length auth must remain a real byte[] snapshot.
    Put $auth ''
    $emptySource = Invoke-SwitchAccount -Root $testRoot -Name '乙' -BeforeWriteGuard $allow
    Assert ($emptySource.Success -and [IO.File]::Exists($emptySource.BackupPath) -and
        (Get-Item -LiteralPath $emptySource.BackupPath).Length -eq 0) 'An empty active file is backed up and switching succeeds'
    # A guard can take time: snapshot only after it has confirmed the desktop is closed.
    Put $auth '{"fake":"before-guard"}'
    $guardUpdate = { Put $auth '{"fake":"during-guard"}'; $true }.GetNewClosure()
    $guardChanged = Invoke-SwitchAccount -Root $testRoot -Name '乙' -BeforeWriteGuard $guardUpdate
    Assert ($guardChanged.Success -and (Read $guardChanged.BackupPath) -ceq '{"fake":"during-guard"}') 'Guard-time updates are the snapshot that gets preserved'
    # Inject external updates at real I/O boundaries; delegates still use production File.Replace.
    $productionWrite = (Get-Command Write-SwitchAtomic).ScriptBlock
    $productionReplace = (Get-Command Invoke-SwitchFileReplace).ScriptBlock
    $productionMove = (Get-Command Invoke-SwitchFileMove).ScriptBlock
    try {
        $raceState = @{ Injected=$false }
        $beforeCommit = {
            param($Path, [byte[]]$Bytes, $BackupPath, [switch]$CheckExpected, [bool]$ExpectedExists, [byte[]]$ExpectedBytes)
            if ($Path -ceq $auth -and -not $raceState.Injected) {
                $raceState.Injected=$true
                Put $auth '{"fake":"external-before-commit"}'
            }
            & $productionWrite @PSBoundParameters
        }.GetNewClosure()
        Set-Item Function:\Write-SwitchAtomic -Value $beforeCommit
        Put $auth '{"fake":"snapshot-before-commit"}'
        $drift = Invoke-SwitchAccount -Root $testRoot -Name '乙' -BeforeWriteGuard $allow
        Assert (-not $drift.Success -and $drift.Code -eq 'ConcurrentChange' -and
            (Read $auth) -ceq '{"fake":"external-before-commit"}') 'Precommit drift refuses to overwrite external login'
        Set-Item Function:\Write-SwitchAtomic -Value $productionWrite

        $raceState = @{ Injected=$false }
        $atReplace = {
            param($Source, $Destination, $BackupPath)
            if ($Destination -ceq $auth -and -not $raceState.Injected) {
                $raceState.Injected=$true
                Put $auth '{"fake":"external-at-replace"}'
            }
            & $productionReplace @PSBoundParameters
        }.GetNewClosure()
        Set-Item Function:\Invoke-SwitchFileReplace -Value $atReplace
        Put $auth '{"fake":"snapshot-before-replace"}'
        $replaced = Invoke-SwitchAccount -Root $testRoot -Name '乙' -BeforeWriteGuard $allow
        Assert (-not $replaced.Success -and $replaced.Code -eq 'ConcurrentChange' -and
            (Read $auth) -ceq '{"fake":"external-at-replace"}') 'The actual version displaced during Replace is restored after a competing update'
        Assert ((Read $replaced.BackupPath) -ceq '{"fake":"external-at-replace"}') 'Atomic backup captures the actual displaced file rather than a stale snapshot'
        Set-Item Function:\Invoke-SwitchFileReplace -Value $productionReplace

        $markerFailure = {
            param($Path, [byte[]]$Bytes, $BackupPath, [switch]$CheckExpected, [bool]$ExpectedExists, [byte[]]$ExpectedBytes)
            if ($Path -ceq $marker) {
                Put $auth '{"fake":"external-before-rollback"}'
                throw 'Simulated marker failure'
            }
            & $productionWrite @PSBoundParameters
        }.GetNewClosure()
        Set-Item Function:\Write-SwitchAtomic -Value $markerFailure
        Put $auth '{"fake":"snapshot-before-rollback"}'
        $foreignRollback = Invoke-SwitchAccount -Root $testRoot -Name '乙' -BeforeWriteGuard $allow
        Assert (-not $foreignRollback.Success -and $foreignRollback.Code -eq 'ConcurrentChange' -and
            (Read $auth) -ceq '{"fake":"external-before-rollback"}') 'Rollback never replaces an observed external update with the old snapshot'
        Assert ((Read $foreignRollback.BackupPath) -ceq '{"fake":"snapshot-before-rollback"}') 'Original login remains recoverable when rollback is refused'

        $failMarkerOnly = {
            param($Path, [byte[]]$Bytes, $BackupPath, [switch]$CheckExpected, [bool]$ExpectedExists, [byte[]]$ExpectedBytes)
            if ($Path -ceq $marker) { throw 'Simulated marker failure' }
            & $productionWrite @PSBoundParameters
        }.GetNewClosure()
        Set-Item Function:\Write-SwitchAtomic -Value $failMarkerOnly
        $raceState = @{ Replacements=0 }
        $duringRollback = {
            param($Source, $Destination, $BackupPath)
            if ($Destination -ceq $auth) {
                $raceState.Replacements++
                if ($raceState.Replacements -eq 2) { Put $auth '{"fake":"external-at-rollback-replace"}' }
            }
            & $productionReplace @PSBoundParameters
        }.GetNewClosure()
        Set-Item Function:\Invoke-SwitchFileReplace -Value $duringRollback
        Put $auth '{"fake":"snapshot-before-rollback-race"}'
        $rollbackRace = Invoke-SwitchAccount -Root $testRoot -Name '乙' -BeforeWriteGuard $allow
        Assert (-not $rollbackRace.Success -and (Read $auth) -ceq '{"fake":"external-at-rollback-replace"}') 'A compare/replace race during rollback retains and restores the actual external version'
        Set-Item Function:\Write-SwitchAtomic -Value $productionWrite
        Set-Item Function:\Invoke-SwitchFileReplace -Value $productionReplace

        $raceState = @{ Injected=$false }
        $atMove = {
            param($Source, $Destination)
            if ($Destination -ceq $auth -and -not $raceState.Injected) {
                $raceState.Injected=$true
                Put $auth '{"fake":"appeared-before-move"}'
            }
            & $productionMove @PSBoundParameters
        }.GetNewClosure()
        Set-Item Function:\Invoke-SwitchFileMove -Value $atMove
        Remove-Item -LiteralPath $auth -Force
        $appeared = Invoke-SwitchAccount -Root $testRoot -Name '乙' -BeforeWriteGuard $allow
        Assert (-not $appeared.Success -and $appeared.Code -eq 'ConcurrentChange' -and
            (Read $auth) -ceq '{"fake":"appeared-before-move"}') 'A file appearing after the absence check makes Move fail without overwriting it'
        Set-Item Function:\Invoke-SwitchFileMove -Value $productionMove

        $raceState = @{ Injected=$false }
        $vanishAtReplace = {
            param($Source, $Destination, $BackupPath)
            if ($Destination -ceq $auth -and -not $raceState.Injected) {
                $raceState.Injected=$true
                [IO.File]::Delete($auth)
            }
            & $productionReplace @PSBoundParameters
        }.GetNewClosure()
        Set-Item Function:\Invoke-SwitchFileReplace -Value $vanishAtReplace
        Put $auth '{"fake":"before-external-removal"}'
        $vanished = Invoke-SwitchAccount -Root $testRoot -Name '乙' -BeforeWriteGuard $allow
        Assert (-not $vanished.Success -and $vanished.Code -eq 'ConcurrentChange' -and
            -not [IO.File]::Exists($auth)) 'A file disappearing after the existence check makes Replace fail instead of recreating it'
    } finally {
        Set-Item Function:\Write-SwitchAtomic -Value $productionWrite
        Set-Item Function:\Invoke-SwitchFileReplace -Value $productionReplace
        Set-Item Function:\Invoke-SwitchFileMove -Value $productionMove
    }
    Assert (@(Get-ChildItem $testRoot -Recurse -Filter '*.tmp').Count -eq 0) 'Concurrency failures clean every temporary credential file'
    # The GUI callback is replaceable and selection/refresh alone never switches.
    . (Join-Path $project 'Window.ps1')
    $calls = New-Object 'System.Collections.Generic.List[string]'
    $action = { param($Root, $Name); $calls.Add($Name); [pscustomobject]@{ Success=$true; Message='虚构切换完成' } }.GetNewClosure()
    $window = New-SwitchWindow -Root $testRoot -SwitchAction $action
    try {
        $window.AccountList.SelectedItem = '乙'
        & $window.RefreshAccounts
        Assert ($calls.Count -eq 0) 'Selection and refresh do not switch'
        Assert $window.SwitchButton.Enabled 'Selection enables auxiliary button'
        & $window.InvokeSelectedAccount
        Assert ($calls.Count -eq 1 -and $calls[0] -ceq '乙') 'Shared GUI action switches the selected account'
        $onClick = [System.Windows.Forms.Button].GetMethod('OnClick', [Reflection.BindingFlags]'Instance,NonPublic')
        [void]$onClick.Invoke($window.SwitchButton, @([EventArgs]::Empty))
        Assert ($calls.Count -eq 2) 'Button uses the same callback'
        $onMouseClick = [System.Windows.Forms.ListBox].GetMethod('OnMouseClick', [Reflection.BindingFlags]'Instance,NonPublic')
        $null = $window.AccountList.Handle
        $row = $window.AccountList.GetItemRectangle(0)
        $mouse = New-Object System.Windows.Forms.MouseEventArgs([System.Windows.Forms.MouseButtons]::Left, 1, ($row.X+2), ($row.Y+2), 0)
        [void]$onMouseClick.Invoke($window.AccountList, @($mouse.PSObject.BaseObject))
        Assert ($calls.Count -eq 3) 'A single left click on a real row switches immediately'
        $blank = New-Object System.Windows.Forms.MouseEventArgs([System.Windows.Forms.MouseButtons]::Left, 1, 10, 185, 0)
        [void]$onMouseClick.Invoke($window.AccountList, @($blank.PSObject.BaseObject))
        Assert ($calls.Count -eq 3) 'Clicking blank list space never switches a previous selection'
        $replacement = { param($Root,$Name); $calls.Add('replacement'); [pscustomobject]@{Success=$true; Message='替换回调'} }.GetNewClosure()
        & $window.SetSwitchAction $replacement
        & $window.InvokeSelectedAccount
        Assert ($calls[3] -eq 'replacement') 'Desktop workflow can replace the callback without rebinding events'
    } finally { $window.Form.Dispose() }
    Write-Output 'PASS: ticket 02 isolated switch, backup, rollback, lock, paths, idempotence and GUI callback.'
} finally {
    if (Test-Path -LiteralPath $testRoot) { Remove-Item -LiteralPath $testRoot -Recurse -Force }
}
