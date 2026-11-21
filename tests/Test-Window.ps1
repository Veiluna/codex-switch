param()
$ErrorActionPreference = 'Stop'
$project = Split-Path $PSScriptRoot -Parent
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('codex-switch-01-' + [guid]::NewGuid().ToString('N'))
function Assert-Equal($Actual, $Expected, [string]$Message) {
    if ($Actual -cne $Expected) { throw "$Message (actual: $Actual; expected: $Expected)" }
}
try {
    $accounts = Join-Path $testRoot 'fake .codex/accounts'
    New-Item -ItemType Directory -Path $accounts -Force | Out-Null
    foreach ($file in @('alpha_auth.json', 'alpha.json', '中文账号.json', 'backup-old_auth.json', 'BACKUP-other.json', 'ignore.txt')) {
        [IO.File]::WriteAllText((Join-Path $accounts $file), 'FAKE-NOT-JSON')
    }
    New-Item -ItemType Directory -Path (Join-Path $accounts 'directory.json') | Out-Null
    . (Join-Path $project 'AccountList.ps1')
    $fakeRoot = Split-Path $accounts -Parent
    Assert-Equal (@(Get-SwitchAccountNames -Root $fakeRoot) -join '|') 'alpha|中文账号' 'List recognizes both formats, excludes backups/directories and deduplicates'
    $missing = Join-Path $testRoot 'new-user'
    Assert-Equal @(Get-SwitchAccountNames -Root $missing).Count 0 'Missing account directory is empty'
    Assert-Equal (Test-Path -LiteralPath $missing) $false 'Listing must not create directories'

    . (Join-Path $project 'Window.ps1')
    $window = New-SwitchWindow -Root $fakeRoot
    try {
        Assert-Equal ($window.AccountList.Items -join '|') 'alpha|中文账号' 'Form displays names only'
        Assert-Equal $window.SwitchButton.Enabled $true 'First account is previewed by default'
        Assert-Equal ([string]$window.AccountList.SelectedItem) 'alpha' 'Default preview selects a saved name only'
        Assert-Equal $window.DetailNameLabel.Text 'alpha' 'Detail displays the selected saved name'
        Assert-Equal ($window.DetailHintLabel.Text.Contains('若 Codex 桌面版正在运行')) $true 'Instructions are conditional instead of claiming a running desktop'
        Assert-Equal $window.SaveButton.Enabled $true 'Saving the current login is available'
        Assert-Equal $window.CountLabel.Text '已保存 2 个账号。' 'Count is visible in the sidebar'
        Assert-Equal $window.StatusLabel.Text '' 'Right side is reserved for actual operation feedback'
        Assert-Equal $window.Form.ShowIcon $false 'Window has no decorative application icon'
        Assert-Equal $window.SwitchButton.FlatAppearance.BorderSize 1 'Primary action has a visible deliberate edge'
        Assert-Equal $window.SwitchButton.SwitchButtonRole 'Primary' 'Main action uses the shared primary treatment'
        Assert-Equal $window.SaveButton.SwitchButtonRole 'Secondary' 'Save launcher uses the shared secondary treatment'
        Assert-Equal ($window.SwitchButton.FlatAppearance.MouseOverBackColor.ToArgb() -ne $window.SwitchButton.FlatAppearance.MouseDownBackColor.ToArgb()) $true 'Hover and press are distinguishable'
        Assert-Equal ($null -ne $window.SearchBox.SwitchInputFrame) $true 'Search has a visible focus border'
        Assert-Equal $window.SearchBox.SwitchInputFrame.Left $window.SaveButton.Left 'Search outer edge aligns with sidebar action'
        Assert-Equal $window.SearchBox.SwitchInputFrame.Right $window.SaveButton.Right 'Sidebar fields share a right edge'
        [IO.File]::WriteAllText((Join-Path $accounts 'later.json'), 'FAKE')
        & $window.RefreshAccounts
        Assert-Equal $window.AccountList.Items.Count 3 'Refresh hook updates list'
        Assert-Equal $window.CountLabel.Text '已保存 3 个账号。' 'Catalog count remains accurate after refresh'
        $window.SearchBox.Text = '中文'
        Assert-Equal ($window.AccountList.Items -join '|') '中文账号' 'Search filters saved names'
        Assert-Equal $window.DetailNameLabel.Text '中文账号' 'Filtered preview matches the visible selection'
        $window.SearchBox.Text = '不存在'
        Assert-Equal $window.AccountList.Items.Count 0 'No-match search is empty'
        Assert-Equal $window.SwitchButton.Enabled $false 'A hidden stale selection cannot be switched'
        Assert-Equal $window.DetailNameLabel.Text '没有匹配的账号' 'Empty filter clears the old detail'
        & $window.RefreshAccounts
        Assert-Equal $window.SearchBox.Text '不存在' 'Ordinary refresh preserves the search query'
        & $window.RefreshAccounts -SelectName 'later'
        Assert-Equal $window.SearchBox.Text '' 'Explicit selection clears search after saving'
        Assert-Equal ([string]$window.AccountList.SelectedItem) 'later' 'Explicit refresh previews the newly saved account'
        $window.StatusLabel.Text = '上次切换结果仍应可见'
        & $window.RefreshAccounts
        Assert-Equal $window.StatusLabel.Text '上次切换结果仍应可见' 'List refresh never overwrites an action result'
        $longName = '长中文名称' * 18
        [IO.File]::WriteAllText((Join-Path $accounts ($longName + '.json')), 'FAKE')
        & $window.RefreshAccounts -SelectName $longName
        Assert-Equal $window.DetailNameLabel.Text $longName 'Long names are retained in the real label'
        Assert-Equal $window.DetailNameLabel.AutoEllipsis $true 'Long detail uses bounded ellipsis'
        $window.Form.Show()
        $window.Form.Size = $window.Form.MinimumSize
        [System.Windows.Forms.Application]::DoEvents()
        [void]$window.SearchBox.Focus()
        [System.Windows.Forms.Application]::DoEvents()
        Assert-Equal $window.SearchBox.SwitchInputFrame.Padding.Left 2 'Focused search uses a stronger border'
        [void]$window.SwitchButton.Focus()
        [System.Windows.Forms.Application]::DoEvents()
        Assert-Equal $window.SwitchButton.FlatAppearance.BorderSize 2 'Keyboard focus has a stronger button edge'
        $window.SwitchButton.Enabled = $false
        Assert-Equal ($window.SwitchButton.BackColor.ToArgb() -ne $window.SwitchButton.FlatAppearance.MouseOverBackColor.ToArgb()) $true 'Disabled button separates from hover'
        $window.SwitchButton.Enabled = $true
        Assert-Equal ($window.SwitchButton.Right -le $window.SwitchButton.Parent.ClientSize.Width) $true 'Primary button fits at minimum width'
        Assert-Equal ($window.StatusLabel.Bottom -le $window.StatusLabel.Parent.ClientSize.Height) $true 'Result fits at minimum height'
        $window.Form.Scale((New-Object Drawing.SizeF(1.5, 1.5)))
        [System.Windows.Forms.Application]::DoEvents()
        Assert-Equal ($window.SearchBox.SwitchInputFrame.Bounds.Contains($window.SearchBox.Bounds)) $true 'Input frame still surrounds the native text after parent scaling'
        Assert-Equal (($window.SearchBox.SwitchInputFrame.Width - $window.SearchBox.Width) -lt 30) $true 'Frame is not scaled twice as a sibling control'
        $window.Form.Close()
    } finally { $window.Form.Dispose() }
    $emptyWindow = New-SwitchWindow -Root $missing
    try {
        Assert-Equal $emptyWindow.AccountList.Items.Count 0 'New user has an empty list'
        Assert-Equal $emptyWindow.CountLabel.Text '还没有已保存的账号。' 'New user gets an empty hint'
        Assert-Equal $emptyWindow.StatusLabel.Text '' 'Empty window does not imply a completed operation'
        Assert-Equal $emptyWindow.SwitchButton.Enabled $false 'Empty account list disables switching'
        Assert-Equal $emptyWindow.DetailNameLabel.Text '还没有已保存的账号' 'Empty detail guides saving the current login'
    } finally { $emptyWindow.Form.Dispose() }

    $oldCodexHome = $env:CODEX_HOME
    try {
        $env:CODEX_HOME = $fakeRoot
        $defaultWindow = New-SwitchWindow
        try {
            Assert-Equal $defaultWindow.AccountList.Items.Count 4 'Default GUI root honors CODEX_HOME like the CLI'
        } finally { $defaultWindow.Form.Dispose() }
    } finally { $env:CODEX_HOME = $oldCodexHome }

    # Run the real CLI launcher from another directory against an isolated stub.
    $stubProject = Join-Path $testRoot 'launcher project'
    New-Item -ItemType Directory -Path $stubProject | Out-Null
    Copy-Item -LiteralPath (Join-Path $project 'codex-switch.cmd') -Destination $stubProject
    [IO.File]::WriteAllText((Join-Path $stubProject 'switch-account.ps1'), 'param([string]$Name); if ($Name) { "NAME=$Name" } else { "LIST" }; exit 0')
    Push-Location $testRoot
    try {
        $launcher = Join-Path $stubProject 'codex-switch.cmd'
        Assert-Equal ((& cmd.exe /d /c "`"$launcher`"" | Out-String).Trim()) 'LIST' 'CLI no-argument entry survives external cwd'
        Assert-Equal ((& cmd.exe /d /c "`"$launcher`" alpha" | Out-String).Trim()) 'NAME=alpha' 'CLI named entry forwards name'
    } finally { Pop-Location }
    Write-Output 'PASS: ticket 01 list, window states, refresh and CLI launcher (Windows PowerShell 5.1).'
} finally {
    $resolvedRoot = [IO.Path]::GetFullPath($testRoot)
    $tempParent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    if (-not $resolvedRoot.StartsWith($tempParent, [StringComparison]::OrdinalIgnoreCase) -or
        (Split-Path $resolvedRoot -Leaf) -notlike 'codex-switch-01-*') { throw 'Unsafe test cleanup path.' }
    if (Test-Path -LiteralPath $resolvedRoot) { Remove-Item -LiteralPath $resolvedRoot -Recurse -Force }
}
