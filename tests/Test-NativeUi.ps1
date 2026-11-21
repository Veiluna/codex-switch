$ErrorActionPreference = 'Stop'
$project = Split-Path $PSScriptRoot -Parent
$temporary = Join-Path $PSScriptRoot ('.tmp-native-ui-' + [guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($temporary)
try {
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    $automation = Join-Path $env:WINDIR 'Microsoft.NET\assembly\GAC_MSIL\System.Management.Automation\v4.0_3.0.0.0__31bf3856ad364e35\System.Management.Automation.dll'
    $exe = Join-Path $temporary 'NativeUiTests.exe'
    $arguments = @('/nologo', '/codepage:65001', '/target:exe', '/main:CodexSwitch.NativeUiTests', ('/out:' + $exe), ('/win32manifest:' + (Join-Path $project 'installer\app.manifest')),
        '/r:System.Windows.Forms.dll', '/r:System.Drawing.dll', '/r:System.Web.Extensions.dll', ('/r:' + $automation))
    $arguments += '/win32icon:' + (Join-Path $project 'assets\CodexSwitch.ico')
    foreach ($file in @('UiStyle.ps1', 'Desktop.ps1', 'AccountList.ps1', 'AccountSwitch.ps1', 'SaveAccount.ps1')) {
        $arguments += '/resource:' + (Join-Path $project $file) + ',CodexSwitch.' + $file
    }
    $arguments += @(Get-ChildItem -LiteralPath (Join-Path $project 'src') -Filter '*.cs' | ForEach-Object FullName)
    $arguments += Join-Path $PSScriptRoot 'NativeUiTests.cs'
    & $compiler $arguments
    if ($LASTEXITCODE -ne 0) { throw 'Native UI test compilation failed.' }
    & $exe
    if ($LASTEXITCODE -ne 0) { throw 'Native UI test failed.' }
} finally {
    if ([IO.Path]::GetFullPath($temporary).StartsWith([IO.Path]::GetFullPath($PSScriptRoot) + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $temporary -Force -Recurse -ErrorAction SilentlyContinue
    }
}
