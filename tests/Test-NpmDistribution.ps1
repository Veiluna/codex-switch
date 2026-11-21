param([string]$DistPath)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2
$root = Split-Path -Parent $PSScriptRoot
$temporary = Join-Path $PSScriptRoot ('.tmp-npm-' + [guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($temporary)
$fixture = Join-Path $temporary 'fixture'
[void][IO.Directory]::CreateDirectory($fixture)
$source = Join-Path $temporary 'Probe.cs'
[IO.File]::WriteAllText($source, @'
using System;
using System.Reflection;
using System.Web.Script.Serialization;
[assembly: AssemblyFileVersion("0.4.0.0")]
class Probe {
    static int Main(string[] args) {
        Console.OutputEncoding = new System.Text.UTF8Encoding(false);
        Console.WriteLine(new JavaScriptSerializer().Serialize(new { args=args, input=Console.ReadLine(), exe=System.IO.Path.GetFileName(Assembly.GetExecutingAssembly().Location) }));
        Console.Error.WriteLine("probe-stderr");
        return Array.IndexOf(args,"--fail") >= 0 ? 37 : 0;
    }
}
'@)
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $compiler '/nologo' '/platform:x64' '/target:exe' '/reference:System.Web.Extensions.dll' ('/out:' + (Join-Path $fixture 'codex-switch.exe')) $source
if ($LASTEXITCODE -ne 0) { throw 'Probe compilation failed.' }
Copy-Item -LiteralPath (Join-Path $fixture 'codex-switch.exe') -Destination (Join-Path $fixture 'installer.exe')
# A hostile neighbor must never enter the tarball, even when it has a conventional npm filename.
[IO.File]::WriteAllText((Join-Path $fixture 'auth.json'), 'fixture-secret-never-package')
[IO.File]::WriteAllText((Join-Path $fixture 'README.md'), 'fixture-secret-never-package')
$npmCommand = (Get-Command npm.cmd -ErrorAction Stop).Source
$npmCli = Join-Path (Split-Path -Parent $npmCommand) 'node_modules\npm\bin\npm-cli.js'
$npxCli = Join-Path (Split-Path -Parent $npmCommand) 'node_modules\npm\bin\npx-cli.js'
$tarball = Join-Path $temporary 'veiluna-codex-switch-0.4.0.tgz'
& (Join-Path $root 'npm\Pack.ps1') -Version '0.4.0' -DistPath $fixture -OutputDirectory $temporary
if (-not (Test-Path -LiteralPath $tarball)) { throw 'Expected npm tarball is missing.' }
& node (Join-Path $PSScriptRoot 'npm-distribution.cjs') $temporary $npmCli $npxCli
if ($LASTEXITCODE -ne 0) { throw 'npm distribution checks failed.' }
function Assert-PackRejected([string]$InputPath, [string]$RequestedVersion, [string]$Label) {
    $rejected = $false
    try { & (Join-Path $root 'npm\Pack.ps1') -Version $RequestedVersion -DistPath $InputPath -OutputDirectory $temporary }
    catch { $rejected = $true }
    if (-not $rejected) { throw ('Pack accepted ' + $Label) }
    Write-Host ('PASS: Pack rejects ' + $Label)
}
Assert-PackRejected $fixture '0.4.1' 'mismatched native version'
$invalid = Join-Path $temporary 'invalid-payload'
[void][IO.Directory]::CreateDirectory($invalid)
Copy-Item -LiteralPath (Join-Path $fixture 'codex-switch.exe') -Destination $invalid
Assert-PackRejected $invalid '0.4.0' 'missing installer'
& $compiler '/nologo' '/platform:x86' '/target:exe' '/reference:System.Web.Extensions.dll' ('/out:' + (Join-Path $invalid 'installer.exe')) $source
if ($LASTEXITCODE -ne 0) { throw 'x86 probe compilation failed.' }
Assert-PackRejected $invalid '0.4.0' 'x86 native installer'
if ($DistPath) {
    $realVersion = (Get-Content -LiteralPath (Join-Path $DistPath 'build-info.json') -Raw | ConvertFrom-Json).Version
    $real = Join-Path $temporary 'real'
    [void][IO.Directory]::CreateDirectory($real)
    & (Join-Path $root 'npm\Pack.ps1') -Version $realVersion -DistPath $DistPath -OutputDirectory $real
    & node (Join-Path $PSScriptRoot 'npm-distribution.cjs') $real $npmCli $npxCli '--real' $realVersion
    if ($LASTEXITCODE -ne 0) { throw 'Real native package smoke checks failed.' }
}
Write-Host ('PASS: npm distribution checks. Isolated evidence: ' + $temporary)
