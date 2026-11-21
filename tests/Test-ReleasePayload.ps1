$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2
$root = Split-Path -Parent $PSScriptRoot
. (Join-Path $root 'installer\Test-ReleasePayload.ps1')
$temporary = Join-Path $PSScriptRoot ('.tmp-release-scan-' + [guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($temporary)
$fixture = Join-Path $temporary 'plain-text-fixture.txt'
$script:ScanCalls = 0
$script:Scenario = 'clean'
$script:EngineUnavailable = $false
function Get-ReleaseDefenderCommand {
    if ($script:EngineUnavailable) { throw 'Windows Defender command is unavailable.' }
    return 'fixture-only-no-process'
}
function Invoke-ReleaseDefenderScan([string]$Command, [string]$Path) {
    $script:ScanCalls++
    if ($Command -ne 'fixture-only-no-process') { throw 'Tests must not launch any executable.' }
    $clean = "Scan starting...`r`nScan finished.`r`nScanning $Path found no threats.`r`n"
    switch ($script:Scenario) {
        'clean' { return [pscustomobject]@{ ExitCode = 0; Output = $clean } }
        'error' { return [pscustomobject]@{ ExitCode = 2; Output = $clean } }
        'exception' { throw 'Fixture scanner failed.' }
        'detection' { return [pscustomobject]@{ ExitCode = 0; Output = 'Threat detected and successfully remediated.' } }
        'mixed' { return [pscustomobject]@{ ExitCode = 0; Output = ($clean + 'Threat detected and successfully remediated.') } }
        'unknown' { return [pscustomobject]@{ ExitCode = 0; Output = 'Scan finished.' } }
        'empty' { return [pscustomobject]@{ ExitCode = 0; Output = '' } }
        'wrong-path' { return [pscustomobject]@{ ExitCode = 0; Output = $clean.Replace($Path, 'other-file.txt') } }
        'removed' { Remove-Item -LiteralPath $Path; return [pscustomobject]@{ ExitCode = 0; Output = $clean } }
        'changed' { [IO.File]::WriteAllText($Path, 'changed text'); return [pscustomobject]@{ ExitCode = 0; Output = $clean } }
    }
    throw 'Unknown test scenario.'
}
function Assert-Rejected([string]$Scenario, [string]$ExpectedMessage) {
    [IO.File]::WriteAllText($fixture, 'ordinary plain text fixture')
    $script:Scenario = $Scenario
    $caught = $null
    try { Assert-ReleasePayload -Path $fixture } catch { $caught = $_ }
    if ($null -eq $caught -or $caught.Exception.Message -notlike ('*' + $ExpectedMessage + '*')) {
        throw ('Expected rejection for ' + $Scenario + ': ' + $ExpectedMessage + '; actual: ' + $caught)
    }
    Write-Host ('PASS: rejects ' + $Scenario)
}
try {
    [IO.File]::WriteAllText($fixture, 'ordinary plain text fixture')
    Assert-ReleasePayload -Path $fixture
    if ($script:ScanCalls -ne 1) { throw 'Clean fixture must be scanned once.' }
    Write-Host 'PASS: accepts affirmative clean result with unchanged bytes'
    Assert-Rejected 'error' 'Defender scan failed'
    Assert-Rejected 'exception' 'Fixture scanner failed'
    Assert-Rejected 'detection' 'affirmative no-threat result'
    Assert-Rejected 'mixed' 'affirmative no-threat result'
    Assert-Rejected 'unknown' 'affirmative no-threat result'
    Assert-Rejected 'empty' 'affirmative no-threat result'
    Assert-Rejected 'wrong-path' 'affirmative no-threat result'
    Assert-Rejected 'removed' 'disappeared'
    Assert-Rejected 'changed' 'changed'
    Remove-Item -LiteralPath $fixture
    $before = $script:ScanCalls
    $caught = $null
    try { Assert-ReleasePayload -Path $fixture } catch { $caught = $_ }
    if ($null -eq $caught -or $caught.Exception.Message -notlike '*missing*' -or $script:ScanCalls -ne $before) { throw 'Missing payload must fail before scanning.' }
    Write-Host 'PASS: rejects missing file before scanning'
    [IO.File]::WriteAllText($fixture, 'ordinary plain text fixture')
    $script:EngineUnavailable = $true
    $caught = $null
    try { Assert-ReleasePayload -Path $fixture } catch { $caught = $_ }
    if ($null -eq $caught -or $caught.Exception.Message -notlike '*unavailable*' -or $script:ScanCalls -ne $before) { throw 'Unavailable engine must fail before scanning.' }
    Write-Host 'PASS: rejects unavailable engine'
    $script:EngineUnavailable = $false
    $script:Scenario = 'clean'
    $secondFixture = Join-Path $temporary 'second-plain-text.txt'
    [IO.File]::WriteAllText($secondFixture, 'another ordinary plain text fixture')
    Assert-ReleasePayload -Path @($fixture, $secondFixture)
    if ($script:ScanCalls -ne ($before + 2)) { throw 'Every payload must receive its own scan.' }
    Write-Host 'PASS: scans every payload separately'

    # Check the placement of the gates without compiling or creating release artifacts.
    $build = Get-Content -LiteralPath (Join-Path $root 'Build.ps1') -Raw
    $pack = Get-Content -LiteralPath (Join-Path $root 'npm\Pack.ps1') -Raw
    if ($build.IndexOf('Assert-ReleasePayload -Path @($exe, $cli)') -lt 0 -or
        $build.IndexOf('Assert-ReleasePayload -Path @($exe, $cli)') -gt $build.IndexOf('$payloadArgs =') -or
        $build.IndexOf('Assert-ReleasePayload -Path $uninstaller') -gt $build.IndexOf('$payloadArgs =') -or
        $build.IndexOf('Assert-ReleasePayload -Path $uninstaller') -lt 0 -or
        $build.IndexOf('Assert-ReleasePayload -Path $installer') -lt 0 -or
        $build.IndexOf('Assert-ReleasePayload -Path $installer') -gt $build.IndexOf('$artifacts += $installer')) { throw 'Build must scan its actual executables before embedding or publication.' }
    $stageGate = $pack.IndexOf("Assert-ReleasePayload -Path @(`$payloads | ForEach-Object { Join-Path `$stage ('native/' + `$_) })")
    if ($stageGate -lt 0 -or $stageGate -gt $pack.IndexOf('$json = & $npm pack')) { throw 'Independent Pack must scan staged payloads before packing.' }
    Write-Host 'PASS: build and independent npm packaging contain scan gates'
} finally {
    $resolved = [IO.Path]::GetFullPath($temporary)
    $parent = [IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\') + '\'
    if ($resolved.StartsWith($parent, [StringComparison]::OrdinalIgnoreCase) -and (Split-Path -Leaf $resolved).StartsWith('.tmp-release-scan-')) {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
