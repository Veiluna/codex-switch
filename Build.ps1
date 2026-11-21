param([ValidatePattern('^\d{1,3}\.\d{1,3}\.\d{1,5}$')][string]$Version = '0.4.4', [switch]$SkipInstaller, [switch]$SkipNpm, [switch]$IncludeNpm)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2
$root = $PSScriptRoot
. (Join-Path $root 'installer\Test-ReleasePayload.ps1')
if (-not [Environment]::Is64BitProcess) { throw 'Run build.cmd from 64-bit Windows, or use 64-bit Windows PowerShell.' }
# A parent PowerShell 7 process can pass its module search path into Windows PowerShell.
# Resolve the two inbox modules explicitly so no third-party/PowerShell 7 module is loaded.
Import-Module (Join-Path $PSHOME 'Modules\Microsoft.PowerShell.Security\Microsoft.PowerShell.Security.psd1') -Force
Import-Module (Join-Path $PSHOME 'Modules\Microsoft.PowerShell.Utility\Microsoft.PowerShell.Utility.psd1') -Force
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$sma = Join-Path $env:WINDIR 'Microsoft.NET\assembly\GAC_MSIL\System.Management.Automation\v4.0_3.0.0.0__31bf3856ad364e35\System.Management.Automation.dll'
foreach ($dependency in @($compiler, $sma)) { if (-not (Test-Path -LiteralPath $dependency -PathType Leaf)) { throw ('Required Windows component is missing: ' + $dependency) } }
$dist = Join-Path $root 'dist'
$null = New-Item -ItemType Directory -Path $dist -Force
$work = Join-Path $dist ('.build-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $work
function Get-Sha256([string]$Path) {
    $hash = [Security.Cryptography.SHA256]::Create()
    $stream = [IO.File]::OpenRead($Path)
    try { return ([BitConverter]::ToString($hash.ComputeHash($stream))).Replace('-', '') }
    finally { $stream.Dispose(); $hash.Dispose() }
}
try {
    $versionSource = Join-Path $work 'AssemblyInfo.cs'
    $assemblyInfo = @(
        'using System.Reflection;',
        '[assembly: AssemblyTitle("Codex Switch")]',
        '[assembly: AssemblyDescription("Windows Codex account switcher")]',
        '[assembly: AssemblyCompany("Codex Switch contributors")]',
        '[assembly: AssemblyProduct("Codex Switch")]',
        ('[assembly: AssemblyVersion("' + $Version + '.0")]'),
        ('[assembly: AssemblyFileVersion("' + $Version + '.0")]')
    )
    [IO.File]::WriteAllLines($versionSource, $assemblyInfo, [Text.Encoding]::UTF8)
    $exe = Join-Path $work 'CodexSwitch.exe'
    $cli = Join-Path $work 'codex-switch.exe'
    $icon = Join-Path $root 'assets\CodexSwitch.ico'
    $core = Join-Path $work 'CodexSwitch.Core.dll'
    $common = @('/nologo','/platform:x64','/langversion:5','/codepage:65001','/optimize+','/debug-','/warn:4')
    foreach ($reference in @('System.dll','System.Core.dll','System.Drawing.dll','System.Windows.Forms.dll','System.Web.Extensions.dll',$sma)) { $common += '/reference:' + $reference }
    $friends = Join-Path $work 'Friends.cs'
    [IO.File]::WriteAllLines($friends, @('using System.Runtime.CompilerServices;', '[assembly: InternalsVisibleTo("CodexSwitch")]', '[assembly: InternalsVisibleTo("codex-switch")]'))
    $coreArgs = @('/target:library', ('/out:' + $core)) + $common
    $embeddedNames = @('UiStyle.ps1','Desktop.ps1','AccountList.ps1','AccountSwitch.ps1','SaveAccount.ps1')
    foreach ($name in $embeddedNames) { $coreArgs += '/resource:' + (Join-Path $root $name) + ',CodexSwitch.' + $name }
    $sources = @(Get-ChildItem -LiteralPath (Join-Path $root 'src') -Filter '*.cs' -File -Recurse | Sort-Object FullName)
    $guiSources = @($sources | Where-Object { $_.Name -in @('Program.cs','MainForm.cs') -or $_.Directory.Name -eq 'Desktop' })
    $cliSources = @($sources | Where-Object { $_.Directory.Name -eq 'Cli' })
    $hostSources = @($sources | Where-Object { $_.Directory.Name -eq 'Host' })
    $coreSources = @($sources | Where-Object { $_ -notin ($guiSources + $cliSources + $hostSources) })
    & $compiler @coreArgs $friends $versionSource $coreSources.FullName
    if ($LASTEXITCODE -ne 0) { throw 'Shared core compilation failed.' }
    $hostArgs = $common + @('/main:CodexSwitch.Bootstrap', ('/win32icon:' + $icon), ('/reference:' + $core), ('/resource:' + $core + ',CodexSwitch.Core.dll'))
    & $compiler @hostArgs '/target:winexe' ('/out:' + $exe) ('/win32manifest:' + (Join-Path $root 'installer\app.manifest')) $versionSource $hostSources.FullName $guiSources.FullName
    if ($LASTEXITCODE -ne 0) { throw 'Desktop compilation failed.' }
    & $compiler @hostArgs '/target:exe' '/define:CLI' ('/out:' + $cli) $versionSource $hostSources.FullName $cliSources.FullName
    if ($LASTEXITCODE -ne 0) { throw 'CLI compilation failed.' }
    Assert-ReleasePayload -Path @($exe, $cli)
    $artifacts = @($exe, $cli)
    $inputFiles = @($sources.FullName) + @($embeddedNames | ForEach-Object { Join-Path $root $_ }) + @((Join-Path $root 'Build.ps1'),(Join-Path $root 'installer\app.manifest'),(Join-Path $root 'installer\Test-ReleasePayload.ps1'))
    if (-not $SkipInstaller) {
        $setupSources = @(Get-ChildItem -LiteralPath (Join-Path $root 'installer\native') -Filter '*.cs' -File | Sort-Object Name)
        $sharedSetup = @($setupSources | Where-Object { $_.Name -notin @('SetupProgram.cs','UninstallHost.cs') } | ForEach-Object { $_.FullName })
        $setupArgs = @('/target:winexe', '/reference:Microsoft.CSharp.dll', ('/win32icon:' + $icon), ('/win32manifest:' + (Join-Path $root 'installer\app.manifest'))) + $common
        $uninstaller = Join-Path $work 'uninstall.exe'
        & $compiler @setupArgs '/main:CodexSwitch.Setup.UninstallHost' ('/out:' + $uninstaller) $versionSource $sharedSetup (Join-Path $root 'installer\native\UninstallHost.cs')
        if ($LASTEXITCODE -ne 0) { throw 'Uninstaller compilation failed.' }
        Assert-ReleasePayload -Path $uninstaller
        $installer = Join-Path $work ('CodexSwitch-Setup-' + $Version + '-x64.exe')
        $payloadArgs = @('/resource:' + $exe + ',Payload.CodexSwitch.exe'
                         '/resource:' + $cli + ',Payload.codex-switch.exe'
                         '/resource:' + $uninstaller + ',Payload.uninstall.exe')
        & $compiler @setupArgs @payloadArgs '/main:CodexSwitch.Setup.SetupProgram' ('/out:' + $installer) $versionSource $sharedSetup (Join-Path $root 'installer\native\SetupProgram.cs')
        if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }
        Assert-ReleasePayload -Path $installer
        $artifacts += $installer
        $inputFiles += $setupSources.FullName
        if ($IncludeNpm -and -not $SkipNpm) {
            & (Join-Path $root 'npm\Pack.ps1') -Version $Version -DistPath $work -OutputDirectory $work
            $artifacts += Join-Path $work ('veiluna-codex-switch-' + $Version + '.tgz')
            $inputFiles += @(Get-ChildItem -Recurse -File -LiteralPath (Join-Path $root 'npm') | ForEach-Object { $_.FullName })
        }
    }
    $inputFiles += $icon
    $report = [ordered]@{
        Version = $Version; Target = 'Windows x64; .NET Framework 4.x; Windows PowerShell 5.1'; Compiler = $compiler;
        CompilerVersion = (Get-Item -LiteralPath $compiler).VersionInfo.FileVersion;
        FrameworkRelease = (Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full').Release;
        PowerShellVersion = $PSVersionTable.PSVersion.ToString(); AutomationReference = $sma;
        AutomationFileVersion = (Get-Item -LiteralPath $sma).VersionInfo.FileVersion;
        SharedCoreSHA256 = Get-Sha256 $core; EmbeddedResources = @($embeddedNames | ForEach-Object { 'CodexSwitch.' + $_ });
        Signing = 'Unsigned; no signing certificate or private key was supplied. No self-signed publisher identity was created.';
        Reproducibility = 'Offline repeatable build using OS tools. Compiler PE timestamps and MSI PackageCode vary; byte-for-byte reproducibility is not claimed.';
        Inputs = @($inputFiles | ForEach-Object { [ordered]@{ Path = $_.Substring($root.Length + 1); SHA256 = Get-Sha256 $_ } });
        Artifacts = @($artifacts | ForEach-Object { [ordered]@{ Name = [IO.Path]::GetFileName($_); Bytes = (Get-Item -LiteralPath $_).Length; SHA256 = Get-Sha256 $_; Signature = (Get-AuthenticodeSignature -LiteralPath $_).Status.ToString() } })
    }
    $metadata = Join-Path $work 'build-info.json'
    $report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $metadata -Encoding UTF8
    foreach ($artifact in $artifacts) { Copy-Item -LiteralPath $artifact -Destination $dist -Force }
    $sums = @($report.Artifacts | ForEach-Object { $_.SHA256.ToLowerInvariant() + '  ' + $_.Name })
    [IO.File]::WriteAllLines((Join-Path $dist 'SHA256SUMS.txt'), [string[]]$sums, [Text.Encoding]::ASCII)
    Copy-Item -LiteralPath $metadata -Destination $dist -Force
    Write-Host ('Built desktop and CLI in ' + $dist)
    Write-Host ('Build metadata: ' + (Join-Path $dist 'build-info.json'))
} finally {
    $resolvedWork = [IO.Path]::GetFullPath($work)
    $expectedParent = [IO.Path]::GetFullPath($dist).TrimEnd('\') + '\'
    if ($resolvedWork.StartsWith($expectedParent, [StringComparison]::OrdinalIgnoreCase) -and (Split-Path -Leaf $resolvedWork).StartsWith('.build-')) { Remove-Item -LiteralPath $resolvedWork -Recurse -Force }
}
