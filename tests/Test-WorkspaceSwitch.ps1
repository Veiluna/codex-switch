$ErrorActionPreference = 'Stop'
$project = Split-Path $PSScriptRoot -Parent
. (Join-Path $project 'AccountSwitch.ps1')
$root = Join-Path $PSScriptRoot ('.tmp-workspace-' + [guid]::NewGuid().ToString('N'))
function Assert($value, [string]$label) { if (-not $value) { throw $label }; Write-Output ('PASS ' + $label) }
function Auth([string]$user,[string]$workspace) {
    $payload = @{ 'https://api.openai.com/auth' = @{chatgpt_user_id=$user;chatgpt_account_id=$workspace} } | ConvertTo-Json -Compress
    $jwt = 'fake.' + [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($payload)).TrimEnd('=').Replace('+','-').Replace('/','_') + '.fake'
    return [Text.Encoding]::UTF8.GetBytes((@{tokens=@{account_id=$workspace;id_token=$jwt;access_token=$jwt};auth_mode='chatgpt'} | ConvertTo-Json -Compress))
}
try {
    $accounts = Join-Path $root 'accounts'
    [void][IO.Directory]::CreateDirectory($accounts)
    $auth = Join-Path $root 'auth.json'; $target = Join-Path $accounts 'target_auth.json'; $marker = Join-Path $accounts '.current'
    $old = Auth 'user-a' 'workspace-a'; $next = Auth 'user-a' 'workspace-b'
    [IO.File]::WriteAllBytes($auth,$old); [IO.File]::WriteAllBytes($target,$next); [IO.File]::WriteAllText($marker,'original')
    $state = @{Running=$true;Close=0;Force=0;Restart=0;Confirm=0;Departure=0}
    $get = { [pscustomobject]@{Known=$true;Processes=@(if($state.Running){42});LaunchId='fake'} }.GetNewClosure()
    $options = @{ Root=$root;Name='target';GetState=$get;ExitTimeoutMilliseconds=0;
        ConfirmAction={param($Owner,$Name,$Phase) $state.Confirm++; return $true}.GetNewClosure();
        RequestClose={param($Snapshot) $state.Close++; $state.Running=$false; return $true}.GetNewClosure();
        ForceClose={param($Snapshot) $state.Force++; return $true}.GetNewClosure();
        Restart={param($Snapshot) $state.Restart++; return $true}.GetNewClosure() }
    $options.BeforeDeparture = [Action[byte[]]]{param($bytes) $state.Departure++; if(-not (Test-SwitchBytesEqual $bytes $old)){throw 'wrong departure'}}.GetNewClosure()
    $result = Invoke-DesktopAccountSwitch @options
    Assert (-not $result.Success -and $result.Code -eq 'WorkspaceSelectionRequired') 'running same-user workspace switch requires client confirmation'
    Assert ($state.Close -eq 0 -and $state.Force -eq 0 -and $state.Restart -eq 0 -and $state.Confirm -eq 0 -and $state.Running) 'no close prompt or process lifecycle action for workspace selection'
    Assert ($state.Departure -eq 1 -and (Test-SwitchBytesEqual ([IO.File]::ReadAllBytes($auth)) $old) -and [IO.File]::ReadAllText($marker) -eq 'original') 'departure captured without claiming a new current identity'
    Assert ($result.Message.Contains('工作空间') -and $result.Message.Contains('workspace-b') -and $result.Message.Contains('尚未确认')) 'actionable client-side path names target and reports unconfirmed state'
    Assert ($result.TargetUserId -ceq 'user-a' -and $result.TargetAccountId -ceq 'workspace-b') 'pending result carries exact locked target identity for client confirmation'
    $held=[IO.File]::Open((Join-Path $accounts '.switch.lock'),'OpenOrCreate','ReadWrite','None')
    try { Assert ((Invoke-DesktopAccountSwitch @options).Code -eq 'Busy') 'workspace route shares switch lock' } finally {$held.Dispose()}
    $options.BeforeDeparture=[Action[byte[]]]{param($bytes) [IO.File]::WriteAllBytes($target,(Auth 'user-other' 'workspace-c'))}.GetNewClosure()
    $result=Invoke-DesktopAccountSwitch @options
    Assert ($result.Code -eq 'ConcurrentChange' -and $state.Close -eq 0) 'target changes during departure abort before client actions'
    [IO.File]::WriteAllBytes($target,$next)
    $options.BeforeDeparture=[Action[byte[]]]{param($bytes) [IO.File]::WriteAllBytes($auth,(Auth 'external' 'workspace-x'))}.GetNewClosure()
    $result=Invoke-DesktopAccountSwitch @options
    Assert ($result.Code -eq 'ConcurrentChange' -and (Get-SwitchIdentity ([IO.File]::ReadAllBytes($auth))).Owner -eq 'external') 'external login update preserved during departure'
    $options.Remove('BeforeDeparture'); [IO.File]::WriteAllBytes($auth,$old)
    $conflict=([Text.Encoding]::UTF8.GetString($next) | ConvertFrom-Json); $conflict.tokens.account_id='conflicting-workspace'
    [IO.File]::WriteAllText($target,($conflict | ConvertTo-Json -Compress))
    Assert ((Invoke-DesktopAccountSwitch @options).Code -eq 'IdentityConflict') 'conflicting workspace claims cannot select no-restart route or close desktop'
    [IO.File]::WriteAllBytes($target,(Auth 'user-other' 'workspace-b'))
    $result=Invoke-DesktopAccountSwitch @options
    Assert ($result.Success -and $state.Close -eq 1 -and $state.Restart -eq 1 -and $state.Confirm -eq 1) 'different user retains confirmed close switch restart'
    [IO.File]::WriteAllBytes($auth,$old); [IO.File]::WriteAllBytes($target,$next); $state.Running=$false
    $result=Invoke-DesktopAccountSwitch @options
    Assert ($result.Success -and (Test-SwitchBytesEqual ([IO.File]::ReadAllBytes($auth)) $next) -and $state.Restart -eq 1) 'closed desktop switches workspace through normal offline transaction'
    [IO.File]::WriteAllBytes($auth,$old); [IO.File]::WriteAllBytes($target,(Auth 'user-other' 'workspace-b')); $state.Running=$true
    $options.RequestClose={param($Snapshot) $state.Running=$false; [IO.File]::WriteAllBytes($target,(Auth 'replacement' 'workspace-c')); return $true}.GetNewClosure()
    $result=Invoke-DesktopAccountSwitch @options
    Assert (-not $result.Success -and $result.Code -eq 'ConcurrentChange' -and (Test-SwitchBytesEqual ([IO.File]::ReadAllBytes($auth)) $old)) 'target replacement during desktop close never installs replacement identity'
} finally {
    if([IO.Path]::GetFullPath($root).StartsWith([IO.Path]::GetFullPath($PSScriptRoot)+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) { Remove-Item -LiteralPath $root -Recurse -Force }
}
