param([string]$DistPath = (Join-Path (Split-Path $PSScriptRoot -Parent) 'dist'))
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2
$dist = [IO.Path]::GetFullPath($DistPath)
$metadata = Get-Content -LiteralPath (Join-Path $dist 'build-info.json') -Raw | ConvertFrom-Json
$setupPath = Join-Path $dist ('CodexSwitch-Setup-' + $metadata.Version + '-x64.exe')
$setup = [Reflection.Assembly]::Load([IO.File]::ReadAllBytes($setupPath))
$type = $setup.GetType('CodexSwitch.Setup.InstallConfiguration',$true)
$fields = [Reflection.BindingFlags]'Public,NonPublic,Instance'
$serviceType = $setup.GetType('CodexSwitch.Setup.InstallService',$true)
$componentsType = $setup.GetType('CodexSwitch.Setup.InstallComponents',$true)
$install = $serviceType.GetMethod('Install',$fields,$null,[type[]]@(),$null)
$selectInstall = $serviceType.GetMethod('Install',$fields,$null,[type[]]@($componentsType),$null)
$uninstall = $serviceType.GetMethod('Uninstall',$fields)
$nativeRegistry = [Microsoft.Win32.Registry]::CurrentUser
function Assert-True([bool]$Condition,[string]$Message) { if (-not $Condition) { throw $Message } }
foreach ($components in @('Desktop','Cli','All')) {
    $config = $type.GetMethod('Default',[Reflection.BindingFlags]'Public,NonPublic,Static').Invoke($null,$null)
    $id = [guid]::NewGuid().ToString('N')
    $scratch = Join-Path $PSScriptRoot ('.tmp-real-install-' + $id)
    $directory = Join-Path $scratch 'Programs/CodexSwitch'
    $key = 'Software\CodexSwitch\DistributionTests\' + $id
    $programs = Join-Path $scratch 'Start Menu'
    $values = @{
        InstallDirectory=$directory; ProgramsDirectory=$programs; UninstallKey=($key+'\Uninstall'); EnvironmentKey=($key+'\Environment');
        LegacyKey=($key+'\Old'); MutexName=('Local\CodexSwitch.Setup.Distribution.'+$id); CheckMachineLegacy=$false; BroadcastEnvironment=$false
    }
    foreach ($field in $type.GetFields($fields)) {
        if ($field.Name -match 'Directory$' -and -not $values.ContainsKey($field.Name)) { $values[$field.Name]=Join-Path $scratch $field.Name }
    }
    foreach ($name in $values.Keys) { $type.GetField($name,$fields).SetValue($config,$values[$name]) }
    $service = $serviceType.GetConstructor($fields,$null,[type[]]@($type),$null).Invoke(@($config))
    [void][IO.Directory]::CreateDirectory($scratch)
    $fixture = $nativeRegistry.CreateSubKey($key+'\Environment')
    try { $fixture.SetValue('Path','%SystemRoot%\suite-fixture',[Microsoft.Win32.RegistryValueKind]::ExpandString) } finally { $fixture.Dispose() }
    try {
        $selection = [Enum]::Parse($componentsType,$components)
        $selectInstall.Invoke($service,@($selection))
        $install.Invoke($service,$null) # Omitted selection must keep the installed components.
        $desktop = $components -ne 'Cli'; $cli = $components -ne 'Desktop'
        foreach ($name in @('CodexSwitch.exe','codex-switch.exe')) {
            $expected = if ($name -ceq 'CodexSwitch.exe') { $desktop } else { $cli }
            $target = Join-Path $directory $name
            Assert-True ((Test-Path -LiteralPath $target) -eq $expected) ('Component file mismatch: '+$components+' '+$name)
            if ($expected) { Assert-True ((Get-FileHash -LiteralPath (Join-Path $dist $name)).Hash -eq (Get-FileHash -LiteralPath $target).Hash) 'Installed executable differs from release.' }
        }
        $shortcutPath = Join-Path $programs 'Codex Switch/Codex Switch.lnk'
        Assert-True ((Test-Path -LiteralPath $shortcutPath) -eq $desktop) 'Shortcut does not match desktop selection.'
        if ($desktop) {
            $shell = New-Object -ComObject WScript.Shell
            $link = $shell.CreateShortcut($shortcutPath)
            try { Assert-True ($link.TargetPath -eq (Join-Path $directory 'CodexSwitch.exe')) 'Real shortcut target differs.' }
            finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($link); [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) }
        }
        $entry = $nativeRegistry.OpenSubKey($key+'\Uninstall')
        try {
            Assert-True ($entry.GetValue('DisplayName') -eq 'Codex Switch') 'Uninstall registration missing.'
            $recorded = if ($components -eq 'All') { 'desktop,cli' } else { $components.ToLowerInvariant() }
            Assert-True ($entry.GetValue('InstalledComponents') -ceq $recorded) 'Registration component set differs.'
            $iconTarget = if ($desktop) { Join-Path $directory 'CodexSwitch.exe' } else { Join-Path $values.HelperDirectory 'uninstall.exe' }
            Assert-True ($entry.GetValue('DisplayIcon') -ceq ('"'+$iconTarget+'",0')) 'Registered icon points at absent component.'
        } finally { $entry.Dispose() }
        $environment = $nativeRegistry.OpenSubKey($key+'\Environment')
        try { Assert-True ($environment.GetValue('Path',$null,[Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames).Contains($directory) -eq $cli) 'PATH does not match CLI selection.' } finally { $environment.Dispose() }
        $userFile = Join-Path $directory 'unowned.txt'
        [IO.File]::WriteAllText($userFile,'preserve')
        # Exercise explicit add/remove using the same final installer assembly.
        $other = if ($components -eq 'Cli') { 'Desktop' } else { 'Cli' }
        $selectInstall.Invoke($service,@([Enum]::Parse($componentsType,$other)))
        Assert-True ((Test-Path -LiteralPath (Join-Path $directory 'CodexSwitch.exe')) -eq ($other -eq 'Desktop')) 'Explicit desktop change failed.'
        Assert-True ((Test-Path -LiteralPath (Join-Path $directory 'codex-switch.exe')) -eq ($other -eq 'Cli')) 'Explicit CLI change failed.'
        $uninstall.Invoke($service,$null)
        Assert-True ((Test-Path -LiteralPath $userFile) -and -not (Test-Path -LiteralPath (Join-Path $directory 'codex-switch.exe')) -and -not (Test-Path -LiteralPath (Join-Path $directory 'CodexSwitch.exe'))) 'Uninstall ownership mismatch.'
        Assert-True (-not (Test-Path -LiteralPath $shortcutPath)) 'Shortcut was not removed.'
        Assert-True ($null -eq $nativeRegistry.OpenSubKey($key+'\Uninstall')) 'Uninstall registration remains.'
        $environment = $nativeRegistry.OpenSubKey($key+'\Environment')
        try {
            $raw = $environment.GetValue('Path',$null,[Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
            Assert-True ($raw -ceq '%SystemRoot%\suite-fixture' -and $environment.GetValueKind('Path') -eq 'ExpandString') 'Original environment value was not restored.'
        } finally { $environment.Dispose() }
        Write-Host ('PASS: final installer isolated '+$components+' install, keep, change and uninstall; actual payload, shortcut, registration and PATH.')
    } finally {
        $nativeRegistry.DeleteSubKeyTree($key,$false)
        $verified = [IO.Path]::GetFullPath($scratch)
        if ((Split-Path -Parent $verified) -eq $PSScriptRoot -and (Split-Path -Leaf $verified) -eq ('.tmp-real-install-'+$id)) { Remove-Item -LiteralPath $verified -Recurse -Force }
    }
}
