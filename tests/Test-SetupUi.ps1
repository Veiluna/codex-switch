$ErrorActionPreference = 'Stop'
$project = Split-Path $PSScriptRoot -Parent
$temporary = Join-Path $PSScriptRoot ('.tmp-setup-ui-' + [guid]::NewGuid().ToString('N'))
$screenshots = Join-Path $project 'dist/setup-ui-evidence'
[void][IO.Directory]::CreateDirectory($temporary)
[void][IO.Directory]::CreateDirectory($screenshots)
try {
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    $exe = Join-Path $temporary 'SetupUiTests.exe'
    & $compiler /nologo /langversion:5 /codepage:65001 /target:exe /warn:4 ('/out:' + $exe) ('/win32manifest:' + (Join-Path $project 'installer/app.manifest')) /r:System.Windows.Forms.dll /r:System.Drawing.dll (Join-Path $project 'installer/native/SetupForm.cs') (Join-Path $PSScriptRoot 'SetupUiTests.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Setup UI test compilation failed.' }
    & $exe $screenshots run
    if ($LASTEXITCODE -ne 0) { throw 'Setup UI message-loop test failed.' }
    & $exe $screenshots
    if ($LASTEXITCODE -ne 0) { throw 'Setup UI test failed.' }
} finally {
    $resolved = [IO.Path]::GetFullPath($temporary)
    if ($resolved.StartsWith([IO.Path]::GetFullPath($PSScriptRoot) + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $temporary -Force -Recurse -ErrorAction SilentlyContinue
    }
}
