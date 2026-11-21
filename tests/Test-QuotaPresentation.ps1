$ErrorActionPreference = 'Stop'
$project = Split-Path $PSScriptRoot -Parent
$temporary = Join-Path $PSScriptRoot ('.tmp-quota-test-' + [guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($temporary)
try {
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    $automation = Join-Path $env:WINDIR 'Microsoft.NET\assembly\GAC_MSIL\System.Management.Automation\v4.0_3.0.0.0__31bf3856ad364e35\System.Management.Automation.dll'
    $exe = Join-Path $temporary 'QuotaPresentationTests.exe'
    $arguments = @('/nologo','/langversion:5','/codepage:65001',('/out:' + $exe),'/r:System.Windows.Forms.dll','/r:System.Web.Extensions.dll',('/r:' + $automation))
    $arguments += @('AccountService.cs','AccountStore.cs','QuotaReader.cs','LegacyOperations.cs' | ForEach-Object { Join-Path (Join-Path $project 'src') $_ })
    $arguments += Join-Path $PSScriptRoot 'QuotaPresentationTests.cs'
    & $compiler $arguments
    if ($LASTEXITCODE -ne 0) { throw 'Quota presentation compilation failed.' }
    & $exe
    if ($LASTEXITCODE -ne 0) { throw 'Quota presentation tests failed.' }
} finally {
    if ([IO.Path]::GetFullPath($temporary).StartsWith([IO.Path]::GetFullPath($PSScriptRoot) + [IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) { Remove-Item -LiteralPath $temporary -Recurse -Force }
}
