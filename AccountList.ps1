# Read file names only. Credential contents and .current are deliberately unused.
function Get-SwitchAccountNames {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string]$Root)

    $accounts = Join-Path $Root 'accounts'
    if (-not (Test-Path -LiteralPath $accounts)) { return }
    if (-not (Test-Path -LiteralPath $accounts -PathType Container)) {
        throw '账号目录不可用。'
    }
    Get-ChildItem -LiteralPath $accounts -File -Filter '*.json' -ErrorAction Stop |
        Where-Object { $_.Name -notlike 'backup-*' } |
        ForEach-Object { $_.BaseName -replace '_auth$', '' } |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
        Sort-Object -Unique
}
