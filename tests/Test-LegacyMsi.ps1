param([switch]$SkipExecution)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2
$root = Split-Path -Parent $PSScriptRoot
$builder = Join-Path $root 'tests\legacy-msi\New-Msi.ps1'
if (-not (Test-Path -LiteralPath $builder)) { throw 'Installer builder is missing.' }
$scratch = Join-Path $PSScriptRoot ('.tmp-installer-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $scratch
$family = [Guid]::NewGuid().ToString('B').ToUpperInvariant()
$tag = 'CodexSwitch-InstallerFixture-' + [Guid]::NewGuid().ToString('N')
$testVariable = 'CODEX_SWITCH_MSI_' + [Guid]::NewGuid().ToString('N')
$installDir = Join-Path $scratch 'installed'
$shortcutDir = Join-Path ([Environment]::GetFolderPath('Programs')) $tag
$registryKey = 'Software\CodexSwitch\InstallerTests\' + $tag
$sentinelHome = Join-Path $scratch 'fake-codex-home'
$null = New-Item -ItemType Directory -Path $sentinelHome
[IO.File]::WriteAllText((Join-Path $sentinelHome 'auth.json'), '{"fixture":true}')
$originalCodeXHome = $env:CODEX_HOME
$env:CODEX_HOME = $sentinelHome
$installed = $false
$productCode1 = $null
$productCode2 = $null
function Assert-True([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Read-MsiRows([string]$Path, [string]$Sql) {
    $installer = New-Object -ComObject WindowsInstaller.Installer
    $db = $installer.OpenDatabase($Path, 0)
    $view = $db.OpenView($Sql)
    [void]$view.Execute()
    $rows = @()
    while ($null -ne ($record = $view.Fetch())) {
        $row = @()
        $count = $record.GetType().InvokeMember('FieldCount', [Reflection.BindingFlags]::GetProperty, $null, $record, $null)
        for ($i = 1; $i -le $count; $i++) { $row += $record.GetType().InvokeMember('StringData', [Reflection.BindingFlags]::GetProperty, $null, $record, @($i)) }
        $rows += [pscustomobject]@{ Values = $row }
        [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($record)
    }
    [void]$view.Close()
    foreach ($item in @($view, $db, $installer)) { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($item) }
    return $rows
}
function Invoke-Msi([string[]]$Arguments, [string]$LogName, [int]$ExpectedExitCode = 0) {
    $log = Join-Path $scratch $LogName
    $process = Start-Process -FilePath "$env:WINDIR\System32\msiexec.exe" -ArgumentList ($Arguments + @('/qn', '/norestart', '/L*v', ('"' + $log + '"'))) -WindowStyle Hidden -PassThru -Wait
    Assert-True ($process.ExitCode -eq $ExpectedExitCode) ('msiexec returned ' + $process.ExitCode + ', expected ' + $ExpectedExitCode + '; log: ' + $log)
}
function Read-InstalledProduct([string]$Code) {
    $installer = New-Object -ComObject WindowsInstaller.Installer
    try {
        $state = $installer.GetType().InvokeMember('ProductState', [Reflection.BindingFlags]::GetProperty, $null, $installer, @($Code))
        if ($state -eq -1) { return $null }
        return $installer.GetType().InvokeMember('ProductInfo', [Reflection.BindingFlags]::GetProperty, $null, $installer, @($Code, 'InstalledProductName'))
    } finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($installer) }
}
try {
    $fixture = Join-Path $scratch 'CodexSwitch.exe'
    [IO.File]::WriteAllText($fixture, 'isolated fixture version 1')
    $msi1 = Join-Path $scratch 'fixture-v1.msi'
    & $builder -ExecutablePath $fixture -OutputPath $msi1 -Version '1.0.0' -UpgradeCode $family -ProductName $tag -RegistryKey $registryKey -ShortcutFolderName $tag
    $productCode1 = @(Read-MsiRows $msi1 'SELECT `Value` FROM `Property` WHERE `Property` = ''ProductCode''')[0].Values[0]
    $properties = @(Read-MsiRows $msi1 'SELECT `Property`, `Value` FROM `Property`')
    Assert-True (@($properties | Where-Object { $_.Values[0] -eq 'ALLUSERS' }).Count -eq 0) 'MSI must default to per-user context.'
    $shortcuts = @(Read-MsiRows $msi1 'SELECT `Directory_`, `Target` FROM `Shortcut`')
    Assert-True ($shortcuts.Count -eq 1 -and $shortcuts[0].Values[0] -eq 'ApplicationProgramsFolder') 'Only a Start menu shortcut is allowed.'
    $components = @(Read-MsiRows $msi1 'SELECT `Attributes` FROM `Component`')
    Assert-True (@($components | Where-Object { ([int]$_.Values[0] -band 16) -ne 0 }).Count -eq 0) 'No permanent components are allowed.'
    if (-not $SkipExecution) {
        Invoke-Msi @('/i', ('"' + $msi1 + '"'), ('INSTALLFOLDER="' + $installDir + '"')) 'install.log'
        $installed = $true
        Assert-True ((Get-Content -LiteralPath (Join-Path $installDir 'CodexSwitch.exe') -Raw) -eq 'isolated fixture version 1') 'Installed payload differs.'
        $shortcut = Join-Path $shortcutDir 'Codex Switch.lnk'
        Assert-True (Test-Path -LiteralPath $shortcut) 'Start menu shortcut was not installed.'
        $shell = New-Object -ComObject WScript.Shell
        $link = $shell.CreateShortcut($shortcut)
        Assert-True ($link.TargetPath -eq (Join-Path $installDir 'CodexSwitch.exe')) 'Shortcut target differs.'
        foreach ($item in @($link, $shell)) { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($item) }
        Assert-True ((Read-InstalledProduct $productCode1) -eq $tag) 'Windows Installer app-list registration is missing.'
        [IO.File]::WriteAllText((Join-Path $installDir 'user-data.txt'), 'preserve arbitrary user files')
        [IO.File]::WriteAllText($fixture, 'isolated fixture version 2 upgraded')
        $msi2 = Join-Path $scratch 'fixture-v2.msi'
        $cli = Join-Path $scratch 'codex-switch.exe'
        [IO.File]::WriteAllText($cli, 'isolated CLI fixture')
        [Environment]::SetEnvironmentVariable($testVariable,'original-entry','User')
        & $builder -ExecutablePath $fixture -CliPath $cli -PathVariableName $testVariable -OutputPath $msi2 -Version '1.1.0' -UpgradeCode $family -ProductName $tag -RegistryKey $registryKey -ShortcutFolderName $tag
        $productCode2 = @(Read-MsiRows $msi2 'SELECT `Value` FROM `Property` WHERE `Property` = ''ProductCode''')[0].Values[0]
        Invoke-Msi @('/i', ('"' + $msi2 + '"'), ('INSTALLFOLDER="' + $installDir + '"')) 'upgrade.log'
        Assert-True ($null -eq (Read-InstalledProduct $productCode1)) 'Upgrade left the previous product registered.'
        Assert-True ((Get-Content -Raw -LiteralPath (Join-Path $installDir 'codex-switch.exe')) -eq 'isolated CLI fixture') 'CLI missing after upgrade.'
        $updatedPath = [Environment]::GetEnvironmentVariable($testVariable,'User')
        Assert-True ($updatedPath.TrimEnd('\') -eq ('original-entry;' + $installDir)) 'Upgrade did not append the installation directory.'
        Assert-True ((Get-Content -LiteralPath (Join-Path $installDir 'CodexSwitch.exe') -Raw) -eq 'isolated fixture version 2 upgraded') 'Upgrade did not replace the payload.'
        Assert-True (Test-Path -LiteralPath (Join-Path $installDir 'user-data.txt')) 'Upgrade removed unowned data.'
        Invoke-Msi @('/i', ('"' + $msi1 + '"'), ('INSTALLFOLDER="' + $installDir + '"')) 'blocked-downgrade.log' 1603
        Assert-True ((Get-Content -LiteralPath (Join-Path $installDir 'CodexSwitch.exe') -Raw) -eq 'isolated fixture version 2 upgraded') 'Blocked downgrade changed the payload.'
        Invoke-Msi @('/x', $productCode2) 'uninstall.log'
        $installed = $false
        Assert-True (-not (Test-Path -LiteralPath (Join-Path $installDir 'codex-switch.exe'))) 'Uninstall left the CLI.'
        Assert-True ([Environment]::GetEnvironmentVariable($testVariable,'User') -eq 'original-entry') 'Uninstall changed an unrelated environment entry.'
        Assert-True (-not (Test-Path -LiteralPath (Join-Path $installDir 'CodexSwitch.exe'))) 'Uninstall left the managed executable.'
        Assert-True (-not (Test-Path -LiteralPath $shortcut)) 'Uninstall left the Start menu shortcut.'
        Assert-True (-not (Test-Path -LiteralPath $shortcutDir)) 'Uninstall left the empty shortcut folder.'
        Assert-True (-not (Test-Path -LiteralPath ('HKCU:\' + $registryKey))) 'Uninstall left the installation marker.'
        Assert-True ($null -eq (Read-InstalledProduct $productCode2)) 'Uninstall left the Windows app-list entry.'
        Assert-True (Test-Path -LiteralPath (Join-Path $installDir 'user-data.txt')) 'Uninstall removed unowned data.'
        Assert-True ((Get-Content -LiteralPath (Join-Path $sentinelHome 'auth.json') -Raw) -eq '{"fixture":true}') 'Installer changed CODEX_HOME.'
    }
    if ($SkipExecution) { Write-Host ('PASS: MSI structure only; installation was skipped. Evidence: ' + $scratch) }
    else { Write-Host ('PASS: isolated MSI structure, install, major upgrade, shortcut, app-list registration, uninstall and data preservation. Evidence: ' + $scratch) }
} finally {
    if ($installed) {
        if ($productCode2) { & "$env:WINDIR\System32\msiexec.exe" /x $productCode2 /qn /norestart }
        if ($productCode1) { & "$env:WINDIR\System32\msiexec.exe" /x $productCode1 /qn /norestart }
    }
    $env:CODEX_HOME = $originalCodeXHome
    if ([Environment]::GetEnvironmentVariable($testVariable,'User')) { [Environment]::SetEnvironmentVariable($testVariable,$null,'User') }
}
