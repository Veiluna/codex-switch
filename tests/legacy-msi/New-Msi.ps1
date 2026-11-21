param(
    [Parameter(Mandatory = $true)][string]$ExecutablePath,
    [string]$CliPath,
    [Parameter(Mandatory = $true)][string]$OutputPath,
    [ValidatePattern('^\d{1,3}\.\d{1,3}\.\d{1,5}$')][string]$Version = '0.3.0',
    [ValidatePattern('^[A-Za-z_][A-Za-z0-9_]*$')][string]$PathVariableName = 'Path',
    [string]$UpgradeCode = '{C5EA4D37-7D49-48DA-B6AE-84B870F59C10}',
    [string]$ProductName = 'Codex Switch',
    [string]$RegistryKey = 'Software\CodexSwitch\Installer',
    [string]$ShortcutFolderName = 'Codex Switch'
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2
if (-not [Environment]::Is64BitProcess) { throw 'Build the x64 MSI with 64-bit Windows PowerShell.' }
$ExecutablePath = [IO.Path]::GetFullPath($ExecutablePath)
$OutputPath = [IO.Path]::GetFullPath($OutputPath)
if (-not (Test-Path -LiteralPath $ExecutablePath -PathType Leaf)) { throw 'Executable is missing.' }
$null = New-Item -ItemType Directory -Path (Split-Path -Parent $OutputPath) -Force
$work = Join-Path (Split-Path -Parent $OutputPath) ('.msi-build-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $work
function Stable-Guid([string]$Text) {
    $hash = [Security.Cryptography.SHA256]::Create()
    try { $bytes = $hash.ComputeHash([Text.Encoding]::UTF8.GetBytes($Text)) } finally { $hash.Dispose() }
    $guidBytes = New-Object byte[] 16
    [Array]::Copy($bytes, $guidBytes, 16)
    return (New-Object Guid (,$guidBytes)).ToString('B').ToUpperInvariant()
}
$productCode = Stable-Guid ($UpgradeCode + ':Product:' + $Version)
$installer = $null
$db = $null
function Invoke-Sql([string]$Sql) {
    $view = $db.OpenView($Sql)
    try { $view.Execute() } finally { $view.Close(); [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($view) }
}
function Add-Row([string]$Table, [string[]]$Columns, [object[]]$Values) {
    $names = ($Columns | ForEach-Object { '`' + $_ + '`' }) -join ', '
    $parameters = (@('?' ) * $Columns.Length) -join ', '
    $view = $db.OpenView(('INSERT INTO `' + $Table + '` (' + $names + ') VALUES (' + $parameters + ')'))
    $record = $installer.CreateRecord($Values.Length)
    try {
        for ($i = 0; $i -lt $Values.Length; $i++) {
            if ($null -eq $Values[$i]) { continue }
            if ($Values[$i] -is [int]) { $record.IntegerData($i + 1) = $Values[$i] }
            else { $record.StringData($i + 1) = [string]$Values[$i] }
        }
        $view.Execute($record)
    } finally {
        $view.Close()
        foreach ($item in @($record, $view)) { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($item) }
    }
}
try {
    # Cabinet entry name must equal File table's primary key, not its display filename.
    Copy-Item -LiteralPath $ExecutablePath -Destination (Join-Path $work 'AppExecutable')
    $cabPath = Join-Path $work 'payload.cab'
    $ddf = @('.OPTION EXPLICIT', '.Set CabinetNameTemplate=payload.cab', ('.Set DiskDirectoryTemplate="' + $work + '"'), '.Set CompressionType=MSZIP', '.Set Cabinet=on', '.Set Compress=on', ('"' + (Join-Path $work 'AppExecutable') + '" AppExecutable'))
    $ddfPath = Join-Path $work 'payload.ddf'
    if ($CliPath) {
        $CliPath = [IO.Path]::GetFullPath($CliPath)
        Copy-Item -LiteralPath $CliPath -Destination (Join-Path $work 'CliExecutable')
        $ddf += '"' + (Join-Path $work 'CliExecutable') + '" CliExecutable'
    }
    [IO.File]::WriteAllLines($ddfPath, $ddf, [Text.Encoding]::Default)
    Push-Location -LiteralPath $work
    try {
        $makecabOutput = & "$env:WINDIR\System32\makecab.exe" /F $ddfPath 2>&1
        if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $cabPath)) { throw ('makecab failed: ' + ($makecabOutput -join [Environment]::NewLine)) }
    } finally { Pop-Location }
    $workingMsi = Join-Path $work 'package.msi'
    $installer = New-Object -ComObject WindowsInstaller.Installer
    $db = $installer.OpenDatabase($workingMsi, 3)
    $schemas = @(
        'CREATE TABLE `Property` (`Property` CHAR(72) NOT NULL, `Value` CHAR(0) NOT NULL LOCALIZABLE PRIMARY KEY `Property`)',
        'CREATE TABLE `Directory` (`Directory` CHAR(72) NOT NULL, `Directory_Parent` CHAR(72), `DefaultDir` CHAR(255) NOT NULL LOCALIZABLE PRIMARY KEY `Directory`)',
        'CREATE TABLE `Component` (`Component` CHAR(72) NOT NULL, `ComponentId` CHAR(38), `Directory_` CHAR(72) NOT NULL, `Attributes` SHORT NOT NULL, `Condition` CHAR(255), `KeyPath` CHAR(72) PRIMARY KEY `Component`)',
        'CREATE TABLE `Feature` (`Feature` CHAR(38) NOT NULL, `Feature_Parent` CHAR(38), `Title` CHAR(64) LOCALIZABLE, `Description` CHAR(255) LOCALIZABLE, `Display` SHORT, `Level` SHORT NOT NULL, `Directory_` CHAR(72), `Attributes` SHORT NOT NULL PRIMARY KEY `Feature`)',
        'CREATE TABLE `FeatureComponents` (`Feature_` CHAR(38) NOT NULL, `Component_` CHAR(72) NOT NULL PRIMARY KEY `Feature_`, `Component_`)',
        'CREATE TABLE `File` (`File` CHAR(72) NOT NULL, `Component_` CHAR(72) NOT NULL, `FileName` CHAR(255) NOT NULL LOCALIZABLE, `FileSize` LONG NOT NULL, `Version` CHAR(72), `Language` CHAR(20), `Attributes` SHORT, `Sequence` LONG NOT NULL PRIMARY KEY `File`)',
        'CREATE TABLE `Media` (`DiskId` SHORT NOT NULL, `LastSequence` LONG NOT NULL, `DiskPrompt` CHAR(64) LOCALIZABLE, `Cabinet` CHAR(255), `VolumeLabel` CHAR(32), `Source` CHAR(72) PRIMARY KEY `DiskId`)',
        'CREATE TABLE `Registry` (`Registry` CHAR(72) NOT NULL, `Root` SHORT NOT NULL, `Key` CHAR(255) NOT NULL LOCALIZABLE, `Name` CHAR(255) LOCALIZABLE, `Value` CHAR(0) LOCALIZABLE, `Component_` CHAR(72) NOT NULL PRIMARY KEY `Registry`)',
        'CREATE TABLE `Shortcut` (`Shortcut` CHAR(72) NOT NULL, `Directory_` CHAR(72) NOT NULL, `Name` CHAR(128) NOT NULL LOCALIZABLE, `Component_` CHAR(72) NOT NULL, `Target` CHAR(255) NOT NULL, `Arguments` CHAR(255), `Description` CHAR(255) LOCALIZABLE, `Hotkey` SHORT, `Icon_` CHAR(72), `IconIndex` SHORT, `ShowCmd` SHORT, `WkDir` CHAR(72) PRIMARY KEY `Shortcut`)',
        'CREATE TABLE `RemoveFile` (`FileKey` CHAR(72) NOT NULL, `Component_` CHAR(72) NOT NULL, `FileName` CHAR(255) LOCALIZABLE, `DirProperty` CHAR(72) NOT NULL, `InstallMode` SHORT NOT NULL PRIMARY KEY `FileKey`)',
        'CREATE TABLE `Upgrade` (`UpgradeCode` CHAR(38) NOT NULL, `VersionMin` CHAR(20), `VersionMax` CHAR(20), `Language` CHAR(255), `Attributes` LONG NOT NULL, `Remove` CHAR(255), `ActionProperty` CHAR(72) NOT NULL PRIMARY KEY `UpgradeCode`, `VersionMin`, `VersionMax`, `Language`, `Attributes`)',
        'CREATE TABLE `LaunchCondition` (`Condition` CHAR(255) NOT NULL, `Description` CHAR(255) NOT NULL LOCALIZABLE PRIMARY KEY `Condition`)',
        'CREATE TABLE `InstallExecuteSequence` (`Action` CHAR(72) NOT NULL, `Condition` CHAR(255), `Sequence` SHORT PRIMARY KEY `Action`)',
        'CREATE TABLE `InstallUISequence` (`Action` CHAR(72) NOT NULL, `Condition` CHAR(255), `Sequence` SHORT PRIMARY KEY `Action`)'
    )
    foreach ($schema in $schemas) { Invoke-Sql $schema }
    if ($CliPath) {
        Invoke-Sql 'CREATE TABLE `Environment` (`Environment` CHAR(72) NOT NULL, `Name` CHAR(255) NOT NULL LOCALIZABLE, `Value` CHAR(0) LOCALIZABLE, `Component_` CHAR(72) NOT NULL PRIMARY KEY `Environment`)'
    }
    $properties = [ordered]@{
        ProductCode = $productCode; ProductName = $ProductName; ProductVersion = $Version; ProductLanguage = '1033'; Manufacturer = 'Codex Switch contributors'; UpgradeCode = $UpgradeCode;
        INSTALLLEVEL = '1'; ARPNOMODIFY = '1'; ARPNOREPAIR = '1'; ARPCOMMENTS = 'Per-user installation. Uninstall preserves Codex accounts and user data.';
        ARPDISPLAYICON = '[INSTALLFOLDER]CodexSwitch.exe'; SecureCustomProperties = 'OLDERPRODUCTS;NEWERPRODUCTS'; MSIFASTINSTALL = '1'
    }
    foreach ($key in $properties.Keys) { Add-Row 'Property' @('Property','Value') @($key, $properties[$key]) }
    foreach ($row in @(
        @('TARGETDIR', $null, 'SourceDir'), @('LocalAppDataFolder','TARGETDIR','.'), @('UserProgramsFolder','LocalAppDataFolder','Programs'), @('INSTALLFOLDER','UserProgramsFolder','CodexSwitch'),
        @('ProgramMenuFolder','TARGETDIR','.'), @('ApplicationProgramsFolder','ProgramMenuFolder',$ShortcutFolderName)
    )) { Add-Row 'Directory' @('Directory','Directory_Parent','DefaultDir') $row }
    Add-Row 'Component' @('Component','ComponentId','Directory_','Attributes','Condition','KeyPath') @('Application', (Stable-Guid ($UpgradeCode + ':Application')), 'INSTALLFOLDER', 256, $null, 'AppExecutable')
    Add-Row 'Component' @('Component','ComponentId','Directory_','Attributes','Condition','KeyPath') @('StartMenu', (Stable-Guid ($UpgradeCode + ':StartMenu')), 'ApplicationProgramsFolder', 260, $null, 'InstallationMarker')
    Add-Row 'Feature' @('Feature','Title','Display','Level','Directory_','Attributes') @('MainFeature',$ProductName,1,1,'INSTALLFOLDER',0)
    foreach ($component in @('Application','StartMenu')) { Add-Row 'FeatureComponents' @('Feature_','Component_') @('MainFeature',$component) }
    $fileVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($ExecutablePath).FileVersion
    if ($fileVersion -notmatch '^\d+\.\d+\.\d+\.\d+$') { $fileVersion = $null }
    Add-Row 'File' @('File','Component_','FileName','FileSize','Version','Attributes','Sequence') @('AppExecutable','Application','CodexSwitch.exe',[int](Get-Item -LiteralPath $ExecutablePath).Length,$fileVersion,512,1)
    $lastSequence = 1
    if ($CliPath) {
        $lastSequence = 2
        $cliVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($CliPath).FileVersion
        if ($cliVersion -notmatch '^\d+\.\d+\.\d+\.\d+$') { $cliVersion = $null }
        Add-Row 'Component' @('Component','ComponentId','Directory_','Attributes','Condition','KeyPath') @('CliApplication', (Stable-Guid ($UpgradeCode + ':CliApplication')), 'INSTALLFOLDER', 256, $null, 'CliExecutable')
        Add-Row 'FeatureComponents' @('Feature_','Component_') @('MainFeature','CliApplication')
        Add-Row 'File' @('File','Component_','FileName','FileSize','Version','Attributes','Sequence') @('CliExecutable','CliApplication','codex-switch.exe',[int](Get-Item -LiteralPath $CliPath).Length,$cliVersion,512,2)
        Add-Row 'Environment' @('Environment','Name','Value','Component_') @('CliPath',('=-' + $PathVariableName),'[~];[INSTALLFOLDER]','CliApplication')
    }
    Add-Row 'Media' @('DiskId','LastSequence','Cabinet') @(1,$lastSequence,'#payload.cab')
    Add-Row 'Registry' @('Registry','Root','Key','Name','Value','Component_') @('InstallationMarker',1,$RegistryKey,'InstallDirectory','[INSTALLFOLDER]','StartMenu')
    Add-Row 'Shortcut' @('Shortcut','Directory_','Name','Component_','Target','Description','ShowCmd','WkDir') @('StartMenuShortcut','ApplicationProgramsFolder','Codex Switch','StartMenu','[#AppExecutable]','Codex account switcher',1,'INSTALLFOLDER')
    # Null FileName removes only the empty shortcut folder, never a wildcard tree.
    Add-Row 'RemoveFile' @('FileKey','Component_','DirProperty','InstallMode') @('RemoveEmptyStartMenuFolder','StartMenu','ApplicationProgramsFolder',2)
    Add-Row 'Upgrade' @('UpgradeCode','VersionMax','Attributes','ActionProperty') @($UpgradeCode,$Version,1,'OLDERPRODUCTS')
    Add-Row 'Upgrade' @('UpgradeCode','VersionMin','Attributes','ActionProperty') @($UpgradeCode,$Version,2,'NEWERPRODUCTS')
    Add-Row 'LaunchCondition' @('Condition','Description') @('NOT NEWERPRODUCTS','A newer version of Codex Switch is already installed.')
    Add-Row 'LaunchCondition' @('Condition','Description') @('NOT ALLUSERS','Codex Switch supports installation for the current user only. Do not set ALLUSERS.')
    Add-Row 'LaunchCondition' @('Condition','Description') @('VersionNT64','Codex Switch requires 64-bit Windows.')
    $sequence = @(
        @('FindRelatedProducts',$null,25), @('LaunchConditions',$null,100), @('CostInitialize',$null,800), @('FileCost',$null,900), @('CostFinalize',$null,1000), @('MigrateFeatureStates',$null,1200),
        @('InstallValidate',$null,1400), @('InstallInitialize',$null,1500), @('RemoveExistingProducts','OLDERPRODUCTS',1501), @('ProcessComponents',$null,1600),
        @('UnpublishFeatures',$null,1800), @('RemoveShortcuts',$null,3200), @('RemoveFiles',$null,3500), @('RemoveRegistryValues',$null,2600), @('InstallFiles',$null,4000),
        @('CreateShortcuts',$null,4500), @('WriteRegistryValues',$null,5000), @('RegisterUser',$null,6000), @('RegisterProduct',$null,6100), @('PublishFeatures',$null,6300), @('PublishProduct',$null,6400), @('InstallFinalize',$null,6600)
    )
    foreach ($row in $sequence) { Add-Row 'InstallExecuteSequence' @('Action','Condition','Sequence') $row }
    if ($CliPath) {
        Add-Row 'InstallExecuteSequence' @('Action','Sequence') @('RemoveEnvironmentStrings',3300)
        Add-Row 'InstallExecuteSequence' @('Action','Sequence') @('WriteEnvironmentStrings',5200)
    }
    foreach ($row in @(@('FindRelatedProducts',$null,25),@('LaunchConditions',$null,100),@('CostInitialize',$null,800),@('FileCost',$null,900),@('CostFinalize',$null,1000),@('ExecuteAction',$null,1300))) { Add-Row 'InstallUISequence' @('Action','Condition','Sequence') $row }
    $streamView = $db.OpenView('INSERT INTO `_Streams` (`Name`, `Data`) VALUES (?, ?)')
    $streamRecord = $installer.CreateRecord(2)
    try { $streamRecord.StringData(1) = 'payload.cab'; $streamRecord.SetStream(2, $cabPath); $streamView.Execute($streamRecord) }
    finally { $streamView.Close(); foreach ($item in @($streamRecord, $streamView)) { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($item) } }
    $summary = $db.SummaryInformation(20)
    try {
        $summary.Property(2) = 'Installation Database'
        $summary.Property(3) = $ProductName
        $summary.Property(4) = 'Codex Switch contributors'
        $summary.Property(7) = 'x64;1033'
        $summary.Property(9) = [Guid]::NewGuid().ToString('B').ToUpperInvariant()
        $summary.Property(14) = 200
        $summary.Property(15) = 10
        $summary.Property(18) = 'Codex Switch native MSI builder'
        $summary.Persist()
    } finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($summary) }
    $db.Commit()
    [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($db); $db = $null
    [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($installer); $installer = $null
    Copy-Item -LiteralPath $workingMsi -Destination $OutputPath -Force
    Write-Host ('Built MSI: ' + $OutputPath + ' (ProductCode ' + $productCode + ')')
} finally {
    foreach ($item in @($db, $installer)) { if ($null -ne $item) { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($item) } }
    # Delete only the freshly-created, checked package staging directory.
    $resolvedWork = [IO.Path]::GetFullPath($work)
    $expectedParent = [IO.Path]::GetFullPath((Split-Path -Parent $OutputPath)).TrimEnd('\') + '\'
    if ($resolvedWork.StartsWith($expectedParent, [StringComparison]::OrdinalIgnoreCase) -and (Split-Path -Leaf $resolvedWork).StartsWith('.msi-build-')) { Remove-Item -LiteralPath $resolvedWork -Recurse -Force }
}
