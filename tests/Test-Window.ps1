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
        Assert-Equal $window.SwitchButton.Enabled $false 'Switch requires a selection'
        Assert-Equal $window.SaveButton.Enabled $true 'Saving the current login is available'
        Assert-Equal $window.StatusLabel.Text '已保存 2 个账号。' 'Count is visible'
        [IO.File]::WriteAllText((Join-Path $accounts 'later.json'), 'FAKE')
        & $window.RefreshAccounts
        Assert-Equal $window.AccountList.Items.Count 3 'Refresh hook updates list'
    } finally { $window.Form.Dispose() }
    $emptyWindow = New-SwitchWindow -Root $missing
    try {
        Assert-Equal $emptyWindow.AccountList.Items.Count 0 'New user has an empty list'
        Assert-Equal $emptyWindow.StatusLabel.Text '还没有已保存的账号。' 'New user gets an empty hint'
    } finally { $emptyWindow.Form.Dispose() }

    $oldCodexHome = $env:CODEX_HOME
    try {
        $env:CODEX_HOME = $fakeRoot
        $defaultWindow = New-SwitchWindow
        try {
            Assert-Equal $defaultWindow.AccountList.Items.Count 3 'Default GUI root honors CODEX_HOME like the CLI'
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
