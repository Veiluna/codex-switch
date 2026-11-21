$ErrorActionPreference = 'Stop'
$project = Split-Path $PSScriptRoot -Parent
$output = Join-Path $PSScriptRoot ('.tmp-native-' + [guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($output)
try {
    $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    & $csc /nologo /langversion:5 /r:System.Web.Extensions.dll /out:"$output\tests.exe" "$project\src\AccountStore.cs" "$project\src\QuotaReader.cs" "$PSScriptRoot\NativeAccountsTests.cs"
    if ($LASTEXITCODE -ne 0) { throw 'Native accounts compilation failed' }
    & "$output\tests.exe"
    if ($LASTEXITCODE -ne 0) { throw 'Native accounts tests failed' }
} finally { Remove-Item -LiteralPath $output -Recurse -Force }
