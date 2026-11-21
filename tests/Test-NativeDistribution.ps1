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
$setup = [Reflection.Assembly]::Load([IO.File]::ReadAllBytes((Join-Path $DistPath 'installer.exe')))
Assert-True ($setup.EntryPoint.DeclaringType.FullName -eq 'CodexSwitch.Setup.SetupProgram') 'Unexpected installer entry point.'
$resources = @($setup.GetManifestResourceNames())
Assert-True ($resources.Count -eq 3) 'Unexpected installer payload count.'
foreach ($fileName in @('CodexSwitch.exe','codex-switch.exe','uninstall.exe')) {
    Assert-True ($resources -ccontains ('Payload.' + $fileName)) ('Missing installer resource: ' + $fileName)
    $stream = $setup.GetManifestResourceStream('Payload.' + $fileName)
    $image = New-Object IO.MemoryStream
    try {
        $stream.CopyTo($image)
        if ($fileName -eq 'uninstall.exe') {
            $helper = [Reflection.Assembly]::Load($image.ToArray())
            Assert-True ($helper.EntryPoint.DeclaringType.FullName -eq 'CodexSwitch.Setup.UninstallHost') 'Incorrect uninstaller.'
        } else {
            $digest = [Security.Cryptography.SHA256]::Create()
            try { $embedded = [BitConverter]::ToString($digest.ComputeHash($image.ToArray())).Replace('-','') } finally { $digest.Dispose() }
            Assert-True ($embedded -eq (Get-Sha256 (Join-Path $DistPath $fileName))) ('Wrong embedded program: ' + $fileName)
        }
    } finally { $stream.Dispose(); $image.Dispose() }
}
Write-Host 'PASS: verified source hashes, independent installer entry, matching GUI and CLI bytes, shared core and uninstall helper.'
