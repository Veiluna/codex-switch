param([string]$Archive = (Join-Path (Split-Path $PSScriptRoot -Parent) 'dist/codex-switch-windows.zip'))
$ErrorActionPreference = 'Stop'
$project = Split-Path $PSScriptRoot -Parent
$dist = [IO.Path]::GetFullPath((Join-Path $project 'dist'))
$fixture = Join-Path $dist ('.tmp-distribution-' + [guid]::NewGuid().ToString('N'))
$zip = $null
$window = $null
$timer = $null
function Assert-Distribution($Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Assert-PlainDistributionPath([string]$Path) {
    $cursor = [IO.Path]::GetFullPath($Path)
    while ($cursor) {
        if (Test-Path -LiteralPath $cursor) {
            $item = Get-Item -LiteralPath $cursor -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Distribution fixture path contains a reparse point' }
        }
        $cursor = [IO.Path]::GetDirectoryName($cursor)
    }
}
function Read-DistributionBytes([string]$Path) { [Convert]::ToBase64String([IO.File]::ReadAllBytes($Path)) }
try {
    $archivePath = [IO.Path]::GetFullPath($Archive)
    if (-not [IO.File]::Exists($archivePath)) { throw 'Distribution ZIP is missing; build the release package before running this test.' }
    Assert-PlainDistributionPath $dist
    Assert-Distribution (-not (Test-Path -LiteralPath $fixture)) 'GUID fixture must be a new directory'
    [void][IO.Directory]::CreateDirectory($fixture)
    $package = Join-Path $fixture 'clean extracted package'
    [void][IO.Directory]::CreateDirectory($package)
    $allowed = @('AccountList.ps1','AccountSwitch.ps1','Desktop.ps1','Open-Codex-Switch.cmd','SaveAccount.ps1','UiStyle.ps1','Window.ps1','codex-switch.cmd','switch-account.ps1','README.md','docs/window.png')
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead($archivePath)
    Assert-Distribution ($zip.Entries.Count -eq $allowed.Count) 'ZIP must contain exactly the eleven distribution files'
    $seen = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    $boundary = [IO.Path]::GetFullPath($package) + [IO.Path]::DirectorySeparatorChar
    # Validate every entry before writing any entry. Directory links are never restored.
    foreach ($entry in $zip.Entries) {
        Assert-Distribution ($allowed -ccontains $entry.FullName) 'ZIP contains a file outside the explicit distribution allowlist'
        Assert-Distribution ($seen.Add($entry.FullName)) 'ZIP contains duplicate file names'
        $destination = [IO.Path]::GetFullPath((Join-Path $package $entry.FullName))
        Assert-Distribution ($destination.StartsWith($boundary, [StringComparison]::Ordinal)) 'ZIP entry escapes the clean extraction directory'
    }
    foreach ($entry in $zip.Entries) {
        $destination = Join-Path $package $entry.FullName
        [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination))
        [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $destination)
    }
    $zip.Dispose()
    $zip = $null
    Assert-Distribution (@(Get-ChildItem -LiteralPath $package -Recurse -File).Count -eq $allowed.Count) 'Clean extraction contains only release files'
    # Load only extracted product scripts. Tests/Test-Save.ps1 and Test-Switch.ps1
    # separately cover cancellation and natural row-click behavior in greater depth.
    . (Join-Path $package 'Window.ps1')
    foreach ($function in @('New-SwitchWindow','Save-SwitchAccount','Invoke-SwitchAccount','Invoke-DesktopAccountSwitch','Set-SwitchButtonStyle','Show-SwitchNotice')) {
        Assert-Distribution ((Get-Command $function).ScriptBlock.File.StartsWith($boundary, [StringComparison]::OrdinalIgnoreCase)) 'All exercised functions must come from the extracted ZIP'
    }
    $fakeRoot = Join-Path $fixture 'fake .codex'
    [void][IO.Directory]::CreateDirectory($fakeRoot)
    $auth = Join-Path $fakeRoot 'auth.json'
    $accounts = Join-Path $fakeRoot 'accounts'
    $observed = [pscustomobject]@{ ActionCalls=0; StateReads=0; UnexpectedActions=0; Result=$null }
    # Explicitly injected CLOSED state: this test never discovers, closes or restarts real Codex.
    $closed = { $observed.StateReads++; [pscustomobject]@{Known=$true; Processes=@(); LaunchId=$null} }.GetNewClosure()
    $forbidden = { $observed.UnexpectedActions++; throw 'A closed-desktop distribution test must not confirm, close or restart a desktop.' }.GetNewClosure()
    $action = {
        param($Root, $Name)
        $observed.ActionCalls++
        $observed.Result = Invoke-DesktopAccountSwitch -Root $Root -Name $Name -GetState $closed -ConfirmAction $forbidden -RequestClose $forbidden -ForceClose $forbidden -Restart $forbidden
        return $observed.Result
    }.GetNewClosure()
    $window = New-SwitchWindow -Root $fakeRoot -SwitchAction $action
    $window.Form.Show()
    Assert-Distribution $window.SaveButton.Enabled 'Packaged window binds the save button'
    $timer = New-Object System.Windows.Forms.Timer
    $timer.Interval = 50
    $timer.Add_Tick({
        param($sender, $eventArgs)
        $dialog = @([System.Windows.Forms.Application]::OpenForms | Where-Object { $_.Name -eq 'SaveAccountDialog' }) | Select-Object -First 1
        if ($dialog) {
            $sender.Stop()
            $sender.Tag.Seen = $true
            $dialog.Controls['AccountNameInput'].Text = $sender.Tag.Name
            $dialog.Controls['ConfirmSaveButton'].PerformClick()
        }
    })
    $assertPreviewPassive = {
        param([string]$ExpectedBytes)
        Assert-Distribution ($observed.ActionCalls -eq 0 -and $observed.StateReads -eq 0 -and $observed.UnexpectedActions -eq 0) 'Packaged save, selection and search never invoke the switch workflow'
        Assert-Distribution ((Read-DistributionBytes $auth) -ceq $ExpectedBytes) 'Packaged save, selection and search preserve active credential bytes'
        Assert-Distribution (-not (Test-Path -LiteralPath (Join-Path $accounts '.current'))) 'Packaged preview never records an active account'
        Assert-Distribution (@(Get-ChildItem -LiteralPath $accounts -File -Filter 'backup-*_auth.json').Count -eq 0) 'Packaged preview creates no switch backup'
    }.GetNewClosure()
    $snapshots = @{}
    foreach ($name in @('分发账号甲','分发账号乙')) {
        [IO.File]::WriteAllText($auth, ('{"fake_distribution_account":"' + $name + '","unknown":{"preserve":true}}'), (New-Object Text.UTF8Encoding($false)))
        $snapshots[$name] = Read-DistributionBytes $auth
        $timer.Tag = [pscustomobject]@{Name=$name; Seen=$false}
        $timer.Start()
        $window.SaveButton.PerformClick()
        Assert-Distribution $timer.Tag.Seen 'Packaged save button opens its actual input dialog'
        Assert-Distribution ($window.AccountList.Items.Contains($name)) 'Packaged save refreshes the visible list'
        Assert-Distribution ($window.AccountList.SelectedItem -ceq $name) 'Packaged save selects the newly saved alias for preview'
        Assert-Distribution ($window.DetailNameLabel.Text -ceq $name) 'Packaged detail displays the newly saved alias'
        Assert-Distribution ((Read-DistributionBytes (Join-Path $accounts ($name + '_auth.json'))) -ceq $snapshots[$name]) 'Packaged save preserves the complete fake credential bytes'
        & $assertPreviewPassive $snapshots[$name]
    }
    Assert-Distribution ($null -ne $window.SearchBox) 'Packaged window exposes the account search box'
    $window.AccountList.SelectedItem = '分发账号甲'
    Assert-Distribution ($window.DetailNameLabel.Text -ceq '分发账号甲') 'Packaged selection updates the detail name'
    & $assertPreviewPassive $snapshots['分发账号乙']
    $window.SearchBox.Text = '乙'
    Assert-Distribution ($window.AccountList.Items.Count -eq 1 -and $window.AccountList.Items.Contains('分发账号乙')) 'Packaged search filters names immediately as text changes'
    Assert-Distribution ($window.AccountList.SelectedItem -ceq '分发账号乙' -and $window.DetailNameLabel.Text -ceq '分发账号乙') 'Packaged filtered preview and detail agree'
    & $assertPreviewPassive $snapshots['分发账号乙']
    $window.SearchBox.Text = '不存在'
    Assert-Distribution ($window.AccountList.Items.Count -eq 0 -and -not $window.SwitchButton.Enabled) 'Packaged empty search cannot switch a hidden selection'
    & $assertPreviewPassive $snapshots['分发账号乙']
    $window.SearchBox.Text = ''
    Assert-Distribution ($window.AccountList.Items.Count -eq 2) 'Clearing packaged search restores both saved names'
    $window.AccountList.SelectedItem = '分发账号甲'
    Assert-Distribution ($window.DetailNameLabel.Text -ceq '分发账号甲') 'Packaged detail displays the selected account before an explicit switch'
    & $assertPreviewPassive $snapshots['分发账号乙']
    Assert-Distribution $window.SwitchButton.Enabled 'Packaged window enables switching after selecting an account'
    $window.SwitchButton.PerformClick()
    Assert-Distribution ($observed.Result.Success -and $observed.Result.Changed -and $observed.Result.Code -eq 'Switched') 'Packaged GUI invokes the real closed-desktop switch workflow'
    Assert-Distribution ((Read-DistributionBytes $auth) -ceq $snapshots['分发账号甲']) 'Packaged switch activates the selected fake account'
    $backups = @(Get-ChildItem -LiteralPath $accounts -File -Filter 'backup-*_auth.json')
    Assert-Distribution ($backups.Count -eq 1 -and (Read-DistributionBytes $backups[0].FullName) -ceq $snapshots['分发账号乙']) 'Packaged switch backs up the exact departing login'
    Assert-Distribution ([IO.File]::ReadAllText((Join-Path $accounts '.current')) -ceq '分发账号甲') 'Packaged switch records the selected Chinese name'
    foreach ($name in @('分发账号甲','分发账号乙')) {
        Assert-Distribution ((Read-DistributionBytes (Join-Path $accounts ($name + '_auth.json'))) -ceq $snapshots[$name]) 'Packaged switch preserves both named archives'
    }
    $window.SwitchButton.PerformClick()
    Assert-Distribution ($observed.Result.Success -and -not $observed.Result.Changed) 'Repeated packaged GUI switch is idempotent'
    Assert-Distribution (@(Get-ChildItem -LiteralPath $accounts -File -Filter 'backup-*_auth.json').Count -eq 1) 'Repeated switch creates no redundant backup'
    Assert-Distribution ($observed.StateReads -ge 2 -and $observed.UnexpectedActions -eq 0) 'Injected closed state is checked and no confirmation, close, force or restart occurs'
    $window.Form.Close()
    $window.Form.Dispose()
    $window = New-SwitchWindow -Root $fakeRoot -SwitchAction $action
    Assert-Distribution ($window.AccountList.Items.Count -eq 2 -and $window.AccountList.Items.Contains('分发账号甲') -and $window.AccountList.Items.Contains('分发账号乙')) 'Reopened packaged window retains named accounts and hides backups'
    Write-Output 'PASS: clean ZIP allowlist, extracted GUI save/preview/search/switch, passive selection, exact active/backup bytes, repeat guard and reopened list.'
    Write-Output 'SCOPE: desktop CLOSED state was injected; no real Codex discovery, exit or restart was exercised.'
} finally {
    if ($zip) { $zip.Dispose() }
    if ($timer) { $timer.Stop(); $timer.Dispose() }
    if ($window) { $window.Form.Close(); $window.Form.Dispose() }
    $verified = [IO.Path]::GetFullPath($fixture)
    $expectedParent = [IO.Path]::GetFullPath($dist)
    if ([IO.Path]::GetDirectoryName($verified) -cne $expectedParent -or [IO.Path]::GetFileName($verified) -notmatch '^\.tmp-distribution-[a-f0-9]{32}$') { throw 'Unsafe distribution cleanup target' }
    if (Test-Path -LiteralPath $verified) {
        Assert-PlainDistributionPath $verified
        Remove-Item -LiteralPath $verified -Recurse -Force
    }
}
