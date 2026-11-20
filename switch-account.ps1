param(
    [string]$Name,
    [string]$Root = $(if ($env:CODEX_HOME) { $env:CODEX_HOME } else { Join-Path $env:USERPROFILE '.codex' })
)

. (Join-Path $PSScriptRoot 'AccountList.ps1')
. (Join-Path $PSScriptRoot 'AccountSwitch.ps1')

if (-not $Name) {
    try {
        Write-Output '本机已保存的账号：'
        Get-SwitchAccountNames -Root $Root | ForEach-Object { Write-Output ('  ' + $_) }
        exit 0
    } catch { Write-Output '无法读取本机账号列表。'; exit 1 }
}
# CLI only observes the verified desktop state; it never closes any process.
$result = Invoke-SwitchAccount -Root $Root -Name $Name -BeforeWriteGuard { Test-SwitchDesktopClosed }
Write-Output $result.Message
if ($result.Success) { exit 0 } else { exit 1 }
