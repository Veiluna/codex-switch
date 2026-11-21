# Fail closed: return code 0 also covers successful remediation, so it is not a clean verdict.
# Context7 /microsoftdocs/defender-docs: command-line-arguments-microsoft-defender-antivirus.md.
function Get-ReleaseDefenderCommand {
    $platform = Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'Microsoft\Windows Defender\Platform'
    $candidates = @()
    if (Test-Path -LiteralPath $platform -PathType Container) {
        $candidates = @(Get-ChildItem -LiteralPath $platform -Directory |
            Where-Object { $_.Name -match '^\d+\.\d+\.\d+\.\d+(?:-\d+)?$' } |
            Sort-Object { [version]($_.Name -replace '-\d+$', '') } -Descending |
            ForEach-Object { Join-Path $_.FullName 'MpCmdRun.exe' } |
            Where-Object { Test-Path -LiteralPath $_ -PathType Leaf })
    }
    if ($candidates.Count -eq 0) {
        $candidates = @(Join-Path ([Environment]::GetFolderPath('ProgramFiles')) 'Windows Defender\MpCmdRun.exe')
    }
    $command = $candidates[0]
    if (-not (Test-Path -LiteralPath $command -PathType Leaf)) { throw 'Windows Defender MpCmdRun.exe is missing.' }
    $signature = Get-AuthenticodeSignature -LiteralPath $command
    if ($signature.Status -ne 'Valid' -or $null -eq $signature.SignerCertificate -or
        $signature.SignerCertificate.Subject -notmatch '(^|,\s*)O=Microsoft Corporation(,|$)') {
        throw ('Windows Defender command does not have a valid Microsoft signature: ' + $command)
    }
    return $command
}

function Get-ReleasePayloadHash([string]$Path) {
    $hash = [Security.Cryptography.SHA256]::Create()
    $stream = [IO.File]::OpenRead($Path)
    try { return ([BitConverter]::ToString($hash.ComputeHash($stream))).Replace('-', '') }
    finally { $stream.Dispose(); $hash.Dispose() }
}

function Invoke-ReleaseDefenderScan([string]$Command, [string]$Path) {
    # Keep normal Defender remediation and exclusions behavior; never change machine settings.
    $lines = @(& $Command -Scan -ScanType 3 -File $Path 2>&1)
    return [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = ($lines -join [Environment]::NewLine) }
}

function Assert-ReleasePayload {
    param([Parameter(Mandatory = $true)][ValidateNotNullOrEmpty()][string[]]$Path)
    $command = Get-ReleaseDefenderCommand
    foreach ($entry in $Path) {
        $fullPath = [IO.Path]::GetFullPath($entry)
        if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) { throw ('Release payload is missing: ' + $fullPath) }
        $before = Get-ReleasePayloadHash $fullPath
        $result = Invoke-ReleaseDefenderScan -Command $command -Path $fullPath
        if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) { throw ('Release payload disappeared during Defender scan: ' + $fullPath) }
        if ((Get-ReleasePayloadHash $fullPath) -ne $before) { throw ('Release payload changed during Defender scan: ' + $fullPath) }
        if ($result.ExitCode -ne 0) { throw ('Defender scan failed for ' + $fullPath + ': ' + $result.Output) }
        # Recognize only the complete affirmative verdict for this exact file. Unknown/localized
        # output stops packaging rather than treating an ambiguous successful exit as clean.
        $clean = '^\s*Scan starting\.\.\.\s*Scan finished\.\s*Scanning ' + [regex]::Escape($fullPath) + ' found no threats\.\s*$'
        if ($result.Output -cnotmatch $clean) { throw ('Defender did not report an affirmative no-threat result for ' + $fullPath + ': ' + $result.Output) }
        Write-Host ('Defender accepted unchanged payload: ' + $fullPath)
    }
}
