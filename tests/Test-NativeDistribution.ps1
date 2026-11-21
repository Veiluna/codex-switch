param([string]$DistPath = (Join-Path (Split-Path -Parent $PSScriptRoot) 'dist'))
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2
$root = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $DistPath 'CodexSwitch.exe'
$metadata = Get-Content -LiteralPath (Join-Path $DistPath 'build-info.json') -Raw | ConvertFrom-Json
function Assert-True([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Get-Sha256([string]$Path) {
    $hash = [Security.Cryptography.SHA256]::Create(); $stream = [IO.File]::OpenRead($Path)
    try { return ([BitConverter]::ToString($hash.ComputeHash($stream))).Replace('-', '') } finally { $stream.Dispose(); $hash.Dispose() }
}
foreach ($inputFile in $metadata.Inputs) { Assert-True ((Get-Sha256 (Join-Path $root $inputFile.Path)) -eq $inputFile.SHA256) ('Source changed after build: ' + $inputFile.Path) }
foreach ($artifact in $metadata.Artifacts) { Assert-True ((Get-Sha256 (Join-Path $DistPath $artifact.Name)) -eq $artifact.SHA256) ('Artifact differs from build metadata: ' + $artifact.Name) }
$cli = Join-Path $DistPath 'codex-switch.exe'
foreach ($entry in @($exe, $cli)) {
    $hostAssembly = [Reflection.Assembly]::Load([IO.File]::ReadAllBytes($entry))
    Assert-True ($hostAssembly.EntryPoint.DeclaringType.FullName -eq 'CodexSwitch.Bootstrap') 'Unexpected executable entry point.'
    Assert-True ($hostAssembly.GetName().ProcessorArchitecture.ToString() -eq 'Amd64') 'Expected x64 executable.'
    $stream = $hostAssembly.GetManifestResourceStream('CodexSwitch.Core.dll')
    Assert-True ($null -ne $stream) 'Shared core missing.'
    $memory = New-Object IO.MemoryStream
    $hash = [Security.Cryptography.SHA256]::Create()
    try {
        $stream.CopyTo($memory)
        $coreBytes = $memory.ToArray()
        Assert-True (([BitConverter]::ToString($hash.ComputeHash($coreBytes))).Replace('-','') -eq $metadata.SharedCoreSHA256) 'Embedded shared core differs.'
    } finally { $hash.Dispose(); $memory.Dispose(); $stream.Dispose() }
}
$assembly = [Reflection.Assembly]::Load($coreBytes)
$names = @($assembly.GetManifestResourceNames())
Assert-True ($names.Count -eq 5) 'Application must contain exactly the five reviewed script resources.'
foreach ($name in @('UiStyle.ps1','Desktop.ps1','AccountList.ps1','AccountSwitch.ps1','SaveAccount.ps1')) {
    $stream = $assembly.GetManifestResourceStream('CodexSwitch.' + $name)
    Assert-True ($null -ne $stream) ('Embedded resource is missing: ' + $name)
    $memory = New-Object IO.MemoryStream
    try { $stream.CopyTo($memory); Assert-True ([Convert]::ToBase64String($memory.ToArray()) -eq [Convert]::ToBase64String([IO.File]::ReadAllBytes((Join-Path $root $name)))) ('Embedded resource differs: ' + $name) }
    finally { $memory.Dispose(); $stream.Dispose() }
}
Assert-True ($assembly.GetName().Name -eq 'CodexSwitch.Core') 'Incorrect core assembly name.'
Assert-True ($assembly.GetName().ProcessorArchitecture.ToString() -eq 'Amd64') 'Application is not x64.'
$scratch = Join-Path $PSScriptRoot ('.tmp-native-package-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $scratch
$msi = Join-Path $DistPath ('CodexSwitch-' + $metadata.Version + '-x64.msi')
$installer = New-Object -ComObject WindowsInstaller.Installer
$db = $installer.OpenDatabase([IO.Path]::GetFullPath($msi), 0)
$view = $db.OpenView('SELECT `Data` FROM `_Streams` WHERE `Name` = ''payload.cab''')
[void]$view.Execute()
$record = $view.Fetch()
try {
    Assert-True ($null -ne $record) 'MSI payload cabinet is missing.'
    # msiReadStreamBytes (1) returns BSTR with one byte per character, not a byte SAFEARRAY.
    $data = $record.GetType().InvokeMember('ReadStream', [Reflection.BindingFlags]::InvokeMethod, $null, $record, @(1, [int](Get-Item -LiteralPath $msi).Length, 1))
    $cab = Join-Path $scratch 'payload.cab'
    [IO.File]::WriteAllBytes($cab, [Text.Encoding]::GetEncoding(28591).GetBytes([string]$data))
    & "$env:WINDIR\System32\expand.exe" $cab '-F:*' $scratch | Out-Null
    Assert-True ($LASTEXITCODE -eq 0) 'MSI cabinet could not be extracted.'
    Assert-True ((Get-Sha256 (Join-Path $scratch 'AppExecutable')) -eq (Get-Sha256 $exe)) 'MSI does not contain the final application executable.'
    Assert-True ((Get-Sha256 (Join-Path $scratch 'CliExecutable')) -eq (Get-Sha256 $cli)) 'MSI does not contain the final CLI.'
} finally {
    [void]$view.Close()
    foreach ($item in @($record,$view,$db,$installer)) { if ($null -ne $item) { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($item) } }
}
Write-Host ('PASS: current sources, recorded hashes, x64 app entry point, exact embedded scripts and exact MSI executable payload. Evidence: ' + $scratch)
