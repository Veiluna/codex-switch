param(
    [Parameter(Mandatory = $true)][ValidatePattern('^\d{1,3}\.\d{1,3}\.\d{1,5}$')][string]$Version,
    [Parameter(Mandatory = $true)][string]$DistPath,
    [string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2
. (Join-Path (Split-Path -Parent $PSScriptRoot) 'installer\Test-ReleasePayload.ps1')
$dist = [IO.Path]::GetFullPath($DistPath)
if (-not $OutputDirectory) { $OutputDirectory = $dist }
$output = [IO.Path]::GetFullPath($OutputDirectory)
$npm = (Get-Command npm.cmd -ErrorAction Stop).Source

function Assert-NativePayload([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw ('Native npm payload is missing: ' + $Path) }
    $fileVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($Path).FileVersion
    if ($fileVersion -ne ($Version + '.0')) { throw ('Native npm payload version differs from ' + $Version + ': ' + $Path) }
    $stream = [IO.File]::OpenRead($Path)
    $reader = New-Object IO.BinaryReader($stream)
    try {
        if ($reader.ReadUInt16() -ne 0x5A4D) { throw ('Invalid executable: ' + $Path) }
        $stream.Position = 0x3C
        $stream.Position = $reader.ReadInt32()
        if ($reader.ReadUInt32() -ne 0x00004550 -or $reader.ReadUInt16() -ne 0x8664) { throw ('Expected Windows x64 payload: ' + $Path) }
    } finally { $reader.Dispose(); $stream.Dispose() }
}

$payloads = @('codex-switch.exe', 'installer.exe')
foreach ($name in $payloads) { Assert-NativePayload (Join-Path $dist $name) }
[void][IO.Directory]::CreateDirectory($output)
$stage = Join-Path $output ('.npm-stage-' + [guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($stage)
try {
    # Copy only static, reviewed inputs. Never pack the project or the dist directory itself.
    $manifest = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'package.json') -Raw | ConvertFrom-Json
    $manifest.version = $Version
    $utf8 = New-Object Text.UTF8Encoding($false)
    [IO.File]::WriteAllText((Join-Path $stage 'package.json'), ($manifest | ConvertTo-Json -Depth 5), $utf8)
    $staticFiles = @('README.md', 'bin/codex-switch.cjs', 'bin/codex-switch-install.cjs', 'bin/run-native.cjs')
    foreach ($name in $staticFiles) {
        $target = Join-Path $stage $name
        [void][IO.Directory]::CreateDirectory((Split-Path -Parent $target))
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination $target
    }
    [void][IO.Directory]::CreateDirectory((Join-Path $stage 'native'))
    foreach ($name in $payloads) { Copy-Item -LiteralPath (Join-Path $dist $name) -Destination (Join-Path $stage ('native/' + $name)) }
    Assert-ReleasePayload -Path @($payloads | ForEach-Object { Join-Path $stage ('native/' + $_) })
    Push-Location -LiteralPath $stage
    try {
        $json = & $npm pack --offline --ignore-scripts --json --pack-destination $stage
        if ($LASTEXITCODE -ne 0) { throw 'npm pack failed.' }
    } finally { Pop-Location }
    $packed = @($json | ConvertFrom-Json)[0]
    $expectedName = 'veiluna-codex-switch-' + $Version + '.tgz'
    if ($packed.filename -ne $expectedName) { throw 'Unexpected npm tarball name.' }
    $expectedFiles = @('package.json') + $staticFiles + @($payloads | ForEach-Object { 'native/' + $_ })
    $actualFiles = @($packed.files | ForEach-Object { $_.path })
    if (@(Compare-Object $expectedFiles $actualFiles).Count -ne 0) { throw 'npm tarball differs from the static file allowlist.' }
    $targetTarball = Join-Path $output $expectedName
    Copy-Item -LiteralPath (Join-Path $stage $expectedName) -Destination $targetTarball -Force
    Write-Host ('Packed npm tarball: ' + $targetTarball)
} finally {
    $resolvedStage = [IO.Path]::GetFullPath($stage)
    $expectedParent = $output.TrimEnd('\') + '\'
    if ($resolvedStage.StartsWith($expectedParent, [StringComparison]::OrdinalIgnoreCase) -and (Split-Path -Leaf $resolvedStage).StartsWith('.npm-stage-')) {
        Remove-Item -LiteralPath $resolvedStage -Recurse -Force
    }
}
