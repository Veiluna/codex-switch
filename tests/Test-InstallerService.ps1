param()
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2
$root = Split-Path -Parent $PSScriptRoot
$scratch = Join-Path $PSScriptRoot ('.tmp-native-installer-' + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($scratch)
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$exe = Join-Path $scratch 'InstallerServiceTests.exe'
& $compiler /nologo /langversion:5 /codepage:65001 /platform:x64 /target:exe /main:InstallerServiceTests ('/out:' + $exe) /reference:System.dll /reference:System.Core.dll /reference:System.Xml.dll /reference:Microsoft.CSharp.dll /reference:System.Windows.Forms.dll (Join-Path $root 'installer\native\InstallService.cs') (Join-Path $root 'installer\native\UninstallHost.cs') (Join-Path $root 'installer\native\SetupProgram.cs') (Join-Path $PSScriptRoot 'InstallerServiceTests.cs')
if ($LASTEXITCODE -ne 0) { throw '安装服务隔离测试编译失败。' }
& $exe $scratch
if ($LASTEXITCODE -ne 0) { throw '安装服务隔离测试失败。' }
Write-Host ('PASS: 每用户安装服务隔离测试；证据目录：' + $scratch)
