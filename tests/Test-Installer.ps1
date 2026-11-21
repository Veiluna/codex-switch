param([string]$DistPath = (Join-Path (Split-Path $PSScriptRoot -Parent) 'dist'))
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2
$dist = [IO.Path]::GetFullPath($DistPath)
$setup = [Reflection.Assembly]::Load([IO.File]::ReadAllBytes((Join-Path $dist 'installer.exe')))
$type = $setup.GetType('CodexSwitch.Setup.InstallConfiguration',$true)
$fields = [Reflection.BindingFlags]'Public,NonPublic,Instance'
$config = $type.GetMethod('Default',[Reflection.BindingFlags]'Public,NonPublic,Static').Invoke($null,$null)
$id = [guid]::NewGuid().ToString('N')
$scratch = Join-Path $PSScriptRoot ('.tmp-real-install-' + $id)
$directory = Join-Path $scratch 'Programs\CodexSwitch'
$key = 'Software\CodexSwitch\DistributionTests\' + $id
$nativeRegistry = [Microsoft.Win32.Registry]::CurrentUser
$programs = Join-Path $scratch 'Start Menu'
$values = @{
    InstallDirectory=$directory; ProgramsDirectory=$programs; UninstallKey=($key+'\Uninstall'); EnvironmentKey=($key+'\Environment');
    LegacyKey=($key+'\Old'); MutexName=('Local\CodexSwitch.Setup.Distribution.'+$id); CheckMachineLegacy=$false; BroadcastEnvironment=$false
}
foreach ($field in $type.GetFields($fields)) {
    if ($field.Name -match 'Directory$' -and -not $values.ContainsKey($field.Name)) { $values[$field.Name]=Join-Path $scratch $field.Name }
}
foreach ($name in $values.Keys) { $type.GetField($name,$fields).SetValue($config,$values[$name]) }
$serviceType = $setup.GetType('CodexSwitch.Setup.InstallService',$true)
$service = $serviceType.GetConstructor($fields,$null,[type[]]@($type),$null).Invoke(@($config))
$install = $serviceType.GetMethod('Install',$fields)
$uninstall = $serviceType.GetMethod('Uninstall',$fields)
function Assert-True([bool]$Condition,[string]$Message) { if (-not $Condition) { throw $Message } }
[void][IO.Directory]::CreateDirectory($scratch)
$fixture = $nativeRegistry.CreateSubKey($key+'\Environment')
try { $fixture.SetValue('Path','%SystemRoot%\suite-fixture',[Microsoft.Win32.RegistryValueKind]::ExpandString) } finally { $fixture.Dispose() }
try {
    $install.Invoke($service,$null)
    $install.Invoke($service,$null)
    foreach ($name in @('CodexSwitch.exe','codex-switch.exe')) {
        Assert-True ((Get-FileHash -LiteralPath (Join-Path $dist $name)).Hash -eq (Get-FileHash -LiteralPath (Join-Path $directory $name)).Hash) 'Installed executable differs from the release.'
    }
    $shortcut = @(Get-ChildItem -Recurse -File -Filter '*.lnk' -LiteralPath $programs)
    Assert-True ($shortcut.Count -eq 1) 'Expected one real Start menu shortcut.'
    $shell = New-Object -ComObject WScript.Shell
    $link = $shell.CreateShortcut($shortcut[0].FullName)
    try { Assert-True ($link.TargetPath -eq (Join-Path $directory 'CodexSwitch.exe')) 'Real shortcut target differs.' }
    finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($link); [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) }
    $entry = $nativeRegistry.OpenSubKey($key+'\Uninstall')
    try { Assert-True ($entry.GetValue('DisplayName') -eq 'Codex Switch') 'Uninstall registration missing.' } finally { $entry.Dispose() }
    $userFile = Join-Path $directory 'unowned.txt'
    [IO.File]::WriteAllText($userFile,'preserve')
    $uninstall.Invoke($service,$null)
    Assert-True ((Test-Path -LiteralPath $userFile) -and -not (Test-Path -LiteralPath (Join-Path $directory 'codex-switch.exe'))) 'Uninstall ownership mismatch.'
    Assert-True (-not (Test-Path -LiteralPath $shortcut[0].FullName)) 'Shortcut was not removed.'
    Assert-True ($null -eq $nativeRegistry.OpenSubKey($key+'\Uninstall')) 'Uninstall registration remains.'
    $environment = $nativeRegistry.OpenSubKey($key+'\Environment')
    try {
        $raw = $environment.GetValue('Path',$null,[Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
        Assert-True ($raw -ceq '%SystemRoot%\suite-fixture' -and $environment.GetValueKind('Path') -eq 'ExpandString') 'Original environment value was not restored.'
    } finally { $environment.Dispose() }
    Write-Host 'PASS: release assembly installed its actual payload twice and uninstalled; verified shortcut target, registration, files and PATH using isolated destinations.'
} finally {
    $nativeRegistry.DeleteSubKeyTree($key,$false)
    $verified = [IO.Path]::GetFullPath($scratch)
    if ((Split-Path -Parent $verified) -eq $PSScriptRoot -and (Split-Path -Leaf $verified) -eq ('.tmp-real-install-'+$id)) {
        Remove-Item -LiteralPath $verified -Recurse -Force
    }
}
