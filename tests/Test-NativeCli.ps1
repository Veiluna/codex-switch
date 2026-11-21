param([switch]$ConsoleSmoke, [string]$ConsoleExecutable)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2
$project = Split-Path $PSScriptRoot -Parent
$temporary = Join-Path $PSScriptRoot ('.tmp-native-cli-' + [guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($temporary)
$utf8 = New-Object Text.UTF8Encoding($false)
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$checks = 0
function Assert-Cli([bool]$Ok, [string]$Label) {
    if (-not $Ok) { throw ('FAIL ' + $Label) }
    $script:checks++
    Write-Host ('PASS ' + $Label)
}
function Invoke-Cli([string]$Exe, [string[]]$Arguments, [int]$TimeoutMilliseconds = 20000) {
    $start = New-Object Diagnostics.ProcessStartInfo
    $start.FileName = $Exe
    # Test inputs intentionally contain no embedded quotes or trailing slashes.
    $start.Arguments = ($Arguments | ForEach-Object { '"' + $_ + '"' }) -join ' '
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.StandardOutputEncoding = $utf8
    $start.StandardErrorEncoding = $utf8
    $process = New-Object Diagnostics.Process
    $process.StartInfo = $start
    try {
        [void]$process.Start()
        # Leave stdin open: commands must never wait for pipeline confirmation.
        $outputTask = $process.StandardOutput.ReadToEndAsync()
        $errorTask = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit($TimeoutMilliseconds)) { $process.Kill(); throw 'CLI blocked with redirected input.' }
        return [pscustomobject]@{ Code = $process.ExitCode; Text = $outputTask.Result + $errorTask.Result }
    } finally { $process.Dispose() }
}
function Compile-Cli([string]$Exe, [string[]]$Sources, [string[]]$Extra) {
    $arguments = @('/nologo','/langversion:5','/codepage:65001','/target:exe',('/out:' + $Exe)) + $Extra + $Sources
    & $compiler $arguments
    if ($LASTEXITCODE -ne 0) { throw 'Native CLI test compilation failed.' }
}
try {
    $stub = Join-Path $temporary 'Stub.cs'
    [IO.File]::WriteAllText($stub, @'
using System;
using System.IO;
using System.Text;
namespace CodexSwitch {
    internal sealed class CatalogEntry {
        public string Name, AccountId="workspace-test",Plan="未知",LoginType="未知",Email="person@example.test",SnapshotId="snapshot",QuotaText="尚无额度记录",ResetHint="";
        public bool IsCurrent, IsSaved=true, HasDueExhausted;
    }
    internal sealed class OperationResult {
        public bool Success; public string Code,Message;
        public OperationResult(bool success,string code,string message) {Success=success;Code=code;Message=message;}
    }
    internal sealed class AccountService : IDisposable {
        public bool CurrentReadFailed {get; private set;}
        public AccountService(string root) {CurrentReadFailed=root.EndsWith("current-unreadable",StringComparison.Ordinal);}
        public CatalogEntry[] List() { return new[] {
            new CatalogEntry {Name="工作", IsCurrent=!CurrentReadFailed},
            new CatalogEntry {Name="study", QuotaText="主要窗口：上次剩余 0%", ResetHint="主要窗口：快照中的重置时间已到，可能已恢复，尚未重新查询。", HasDueExhausted=true},
            new CatalogEntry {Name="duplicate", AccountId="未知"}
        }; }
        public OperationResult Save(string name) { return new OperationResult(name!="busy","Busy",name=="busy"?"账号操作正忙":name==null?"默认邮箱保存":"已保存"); }
        public OperationResult Delete(string name,string snapshot) {return new OperationResult(snapshot=="snapshot","Deleted","已删除");}
        public OperationResult Switch(string target,Func<string,string,bool> confirm) {
            if(target=="fail")return new OperationResult(false,"Failed","未切换");
            if(target=="unknownphase")return new OperationResult(confirm("Other","未知确认"),"Unknown","未知阶段");
            bool close=confirm("Close","关闭 Codex？");
            bool force=target!="force" || confirm("Force","强制结束 Codex？");
            return new OperationResult(close&&force,"Switch",close&&force?"已切换":"未授权关闭");
        }
        public void Dispose() {}
    }
    internal static class Entry {static int Main(string[] args) {return CliProgram.Run(args);}}
}
'@, $utf8)
    $cliSource = Join-Path $project 'src\Cli\CliProgram.cs'
    $exe = Join-Path $temporary 'FakeCli.exe'
    Compile-Cli $exe @($stub,$cliSource) @()
    $result = Invoke-Cli $exe @('help')
    Assert-Cli ($result.Code -eq 0 -and $result.Text.Contains('--allow-close') -and $result.Text.Contains('--force-close')) 'help documents independent close permissions'
    Assert-Cli ($result.Text.Contains('codex-switch [--root DIR]') -and $result.Text.Contains('save [名称]')) 'help uses distributable CLI filename and documents optional save name'
    $result = Invoke-Cli $exe @('save')
    Assert-Cli ($result.Code -eq 0 -and $result.Text.Contains('默认邮箱保存')) 'save without a name delegates null to shared service'
    $result = Invoke-Cli $exe @()
    Assert-Cli ($result.Code -eq 0 -and $result.Text.Contains('工作')) 'no arguments with piped stdin lists without waiting'
    $result = Invoke-Cli $exe @('list')
    Assert-Cli ($result.Code -eq 0 -and $result.Text.Contains('已到点') -and $result.Text.Contains('未知')) 'list preserves unknown and due exhausted metadata'
    Assert-Cli ($result.Text.Contains('上次已用尽') -and $result.Text.Contains('快照中的重置时间已到')) 'list retains exhausted emphasis together with the existing reset hint'
    $result = Invoke-Cli $exe @('current')
    Assert-Cli ($result.Code -eq 0 -and $result.Text.Contains('工作') -and -not $result.Text.Contains('study')) 'current outputs actual service current only'
    $result = Invoke-Cli $exe @('show','study')
    Assert-Cli ($result.Code -eq 0 -and $result.Text.Contains('上次剩余 0%') -and $result.Text.Contains('已到点')) 'show keeps historical zero after reset time'
    Assert-Cli ($result.Text.Contains('上次已用尽') -and $result.Text.Contains('快照中的重置时间已到')) 'show retains exhausted emphasis together with the existing reset hint'
    $unreadableRoot = Join-Path $temporary 'current-unreadable'
    $result = Invoke-Cli $exe @('--root',$unreadableRoot,'list')
    Assert-Cli ($result.Text.Contains('无法识别当前登录') -and $result.Text.Contains('study') -and -not $result.Text.Contains('[当前登录]')) 'unreadable current login is explicit while saved catalog remains visible'
    $result = Invoke-Cli $exe @('--root',$unreadableRoot,'current')
    Assert-Cli ($result.Code -ne 0 -and $result.Text.Contains('无法识别当前登录')) 'current reports read failure instead of claiming logged out'
    $result = Invoke-Cli $exe @('show','absent')
    Assert-Cli ($result.Code -ne 0) 'missing show target fails'
    $result = Invoke-Cli $exe @('save','busy')
    Assert-Cli ($result.Code -ne 0) 'service busy produces nonzero exit'
    $result = Invoke-Cli $exe @('switch','study')
    Assert-Cli ($result.Code -ne 0) 'pipeline does not silently consent to closing'
    $result = Invoke-Cli $exe @('switch','study','--allow-close')
    Assert-Cli ($result.Code -eq 0) 'ordinary close flag authorizes ordinary closure'
    $result = Invoke-Cli $exe @('switch','force','--allow-close')
    Assert-Cli ($result.Code -ne 0) 'ordinary consent does not authorize force'
    $result = Invoke-Cli $exe @('switch','force','--force-close')
    Assert-Cli ($result.Code -ne 0) 'force flag does not silently authorize ordinary closure'
    $result = Invoke-Cli $exe @('switch','force','--allow-close','--force-close')
    Assert-Cli ($result.Code -eq 0) 'separate flags authorize both close phases'
    $result = Invoke-Cli $exe @('switch','unknownphase','--allow-close','--force-close')
    Assert-Cli ($result.Code -ne 0) 'unknown confirmation phase is never authorized'
    $result = Invoke-Cli $exe @('switch','fail','--allow-close','--force-close')
    Assert-Cli ($result.Code -ne 0) 'switch service failure produces nonzero exit'
    $result = Invoke-Cli $exe @('study','--allow-close')
    Assert-Cli ($result.Code -eq 0) 'saved alias shortcut uses shared switch'
    $result = Invoke-Cli $exe @('delete','study')
    Assert-Cli ($result.Code -ne 0) 'pipeline delete requires explicit yes'
    $result = Invoke-Cli $exe @('delete','study','--yes')
    Assert-Cli ($result.Code -eq 0) 'explicit delete passes selected snapshot'
    foreach ($case in @(@('quota'),@('refresh'),@('list','--refresh'),@('list','extra'),@('delete','study','--allow-close'),@('--root'),@('--root','relative','list'),@('--root','C:relative','list'),@('--root','\drive-relative','list'),@('switch','study','--yes'),@('--root',$temporary,'--root',$temporary,'list'))) {
        $result = Invoke-Cli $exe $case
        Assert-Cli ($result.Code -ne 0) ('invalid arguments rejected: ' + ($case -join ' '))
    }
    $result = Invoke-Cli $exe @('--root',$temporary,'list')
    Assert-Cli ($result.Code -eq 0) 'explicit absolute root accepted'

    # Compile a second executable with the actual shared service. Every credential
    # and root below is synthetic and isolated; no switch or network call is made.
    $entry = Join-Path $temporary 'Entry.cs'
    [IO.File]::WriteAllText($entry, 'using System; namespace CodexSwitch { internal static class Entry { [STAThread] static int Main(string[] args) { return CliProgram.Run(args); } } }', $utf8)
    $realExe = Join-Path $temporary 'RealCli.exe'
    $automation = Join-Path $env:WINDIR 'Microsoft.NET\assembly\GAC_MSIL\System.Management.Automation\v4.0_3.0.0.0__31bf3856ad364e35\System.Management.Automation.dll'
    $references = @('/r:System.Windows.Forms.dll','/r:System.Web.Extensions.dll',('/r:' + $automation))
    foreach ($file in @('UiStyle.ps1','Desktop.ps1','AccountList.ps1','AccountSwitch.ps1','SaveAccount.ps1')) {
        $references += '/resource:' + (Join-Path $project $file) + ',CodexSwitch.' + $file
    }
    $sources = @('AccountService.cs','AccountStore.cs','QuotaReader.cs','LegacyOperations.cs' | ForEach-Object { Join-Path (Join-Path $project 'src') $_ })
    Compile-Cli $realExe ($sources + @($entry,$cliSource)) $references
    $dataRoot = Join-Path $temporary '虚构账号 data'
    $accounts = Join-Path $dataRoot 'accounts'
    [void][IO.Directory]::CreateDirectory($accounts)
    function New-TestAuth([string]$User, [string]$Account, [string]$Refresh) {
        $claims = @{ email = 'person@example.test'; 'https://api.openai.com/auth' = @{ chatgpt_user_id = $User; chatgpt_account_id = $Account; chatgpt_plan_type = 'plus' } }
        $payload = [Convert]::ToBase64String($utf8.GetBytes(($claims | ConvertTo-Json -Compress -Depth 5))).TrimEnd('=').Replace('+','-').Replace('/','_')
        $token = 'h.' + $payload + '.s'
        return (@{ tokens = @{ account_id = $Account; id_token = $token; access_token = $token; refresh_token = $Refresh } } | ConvertTo-Json -Compress -Depth 5)
    }
    $old = New-TestAuth 'fake-user-a' 'fake-workspace' 'old-synthetic-secret'
    $latest = New-TestAuth 'fake-user-a' 'fake-workspace' 'new-synthetic-secret'
    $other = New-TestAuth 'fake-user-b' 'fake-workspace' 'other-synthetic-secret'
    $authPath = Join-Path $dataRoot 'auth.json'
    $alpha = Join-Path $accounts 'alpha_auth.json'
    $beta = Join-Path $accounts 'beta_auth.json'
    $marker = Join-Path $accounts '.current'
    [IO.File]::WriteAllText($alpha,$old,$utf8)
    [IO.File]::WriteAllText($beta,$other,$utf8)
    [IO.File]::WriteAllText($authPath,$latest,$utf8)
    [IO.File]::WriteAllText($marker,'beta',$utf8)
    $result = Invoke-Cli $realExe @('--root',$dataRoot,'current')
    Assert-Cli ($result.Code -eq 0 -and $result.Text.Contains('alpha') -and -not $result.Text.Contains('beta')) 'actual service ignores dishonest marker after token rotation'
    Assert-Cli (-not $result.Text.Contains('synthetic-secret') -and -not $result.Text.Contains('access_token') -and -not $result.Text.Contains('refresh_token') -and -not $result.Text.Contains('h.')) 'metadata output contains no synthetic credential values or JSON'
    $result = Invoke-Cli $realExe @('--root',$dataRoot,'save')
    Assert-Cli ($result.Code -eq 0 -and (Test-Path -LiteralPath $alpha) -and [IO.File]::ReadAllText($alpha) -eq $latest) 'unnamed save preserves a verified existing alias'
    $result = Invoke-Cli $realExe @('save','renamed','--root',$dataRoot)
    $renamed = Join-Path $accounts 'renamed_auth.json'
    Assert-Cli ($result.Code -eq 0 -and (Test-Path -LiteralPath $renamed) -and -not (Test-Path -LiteralPath $alpha) -and [IO.File]::ReadAllText($renamed) -eq $latest) 'actual service saves latest credentials and renames same identity'
    [IO.File]::WriteAllText($alpha,$old,$utf8)
    [IO.File]::WriteAllText($marker,'beta',$utf8)
    $result = Invoke-Cli $realExe @('--root',$dataRoot,'current')
    Assert-Cli ($result.Code -eq 0 -and $result.Text.Contains('多个') -and $result.Text.Contains('未保存')) 'actual duplicate identity stays independent instead of choosing an alias'
    $result = Invoke-Cli $realExe @('--root',$dataRoot,'save','ambiguous')
    Assert-Cli ($result.Code -ne 0 -and -not (Test-Path -LiteralPath (Join-Path $accounts 'ambiguous_auth.json'))) 'ambiguous identity cannot silently rename either duplicate'
    Remove-Item -LiteralPath $alpha -Force
    $result = Invoke-Cli $realExe @('--root',$dataRoot,'delete','renamed')
    Assert-Cli ($result.Code -ne 0 -and (Test-Path -LiteralPath $renamed)) 'redirected delete leaves real synthetic archive intact without yes'
    $gate = [IO.File]::Open((Join-Path $accounts '.switch.lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
    try { $result = Invoke-Cli $realExe @('--root',$dataRoot,'save','busy') }
    finally { $gate.Dispose() }
    Assert-Cli ($result.Code -ne 0 -and -not (Test-Path -LiteralPath (Join-Path $accounts 'busy_auth.json'))) 'actual shared lock yields nonzero CLI status'
    $result = Invoke-Cli $realExe @('--root',$dataRoot,'delete','renamed','--yes')
    Assert-Cli ($result.Code -eq 0 -and -not (Test-Path -LiteralPath $renamed) -and [IO.File]::ReadAllText($authPath) -eq $latest) 'actual delete preserves active credentials byte content'
    [IO.File]::WriteAllText($authPath,'{"OPENAI_API_KEY":"private-synthetic-api-secret"}',$utf8)
    $result = Invoke-Cli $realExe @('--root',$dataRoot,'current')
    Assert-Cli ($result.Code -eq 0 -and $result.Text.Contains('未知') -and $result.Text.Contains('API key') -and -not $result.Text.Contains('private-synthetic-api-secret')) 'unknown API identity stays visible with secret excluded'
    [IO.File]::WriteAllText($authPath,'not-json',$utf8)
    $result = Invoke-Cli $realExe @('--root',$dataRoot,'current')
    Assert-Cli ($result.Code -ne 0 -and $result.Text.Contains('无法识别当前登录')) 'actual corrupt current credentials produce an explicit current read failure'
    $result = Invoke-Cli $realExe @('--root',$dataRoot,'list')
    Assert-Cli ($result.Text.Contains('beta') -and $result.Text.Contains('无法识别当前登录') -and -not $result.Text.Contains('[当前登录]')) 'actual corrupt current credentials leave saved catalog available'
    $automaticRoot = Join-Path $temporary 'automatic-name'
    [void][IO.Directory]::CreateDirectory($automaticRoot)
    [IO.File]::WriteAllText((Join-Path $automaticRoot 'auth.json'),$latest,$utf8)
    $result = Invoke-Cli $realExe @('--root',$automaticRoot,'save')
    $automaticArchive = Join-Path $automaticRoot 'accounts\person@example.test_auth.json'
    Assert-Cli ($result.Code -eq 0 -and (Test-Path -LiteralPath $automaticArchive) -and [IO.File]::ReadAllText($automaticArchive) -eq $latest) 'unnamed save of an unsaved identity uses core email naming'
    if ($ConsoleSmoke) {
        if ([string]::IsNullOrWhiteSpace($ConsoleExecutable)) { $ConsoleExecutable = Join-Path $project 'dist\codex-switch.exe' }
        $ConsoleExecutable = [IO.Path]::GetFullPath($ConsoleExecutable)
        if (-not (Test-Path -LiteralPath $ConsoleExecutable -PathType Leaf)) { throw 'Build the actual CLI executable before running -ConsoleSmoke.' }
        $result = Invoke-Cli $ConsoleExecutable @('help')
        Assert-Cli ($result.Code -eq 0 -and $result.Text.Contains('codex-switch [--root DIR]') -and $result.Text.Contains('save [名称]')) 'built executable exposes corrected CLI usage'
        $consoleRoot = Join-Path $temporary 'console-only-fake-accounts'
        $consoleAccounts = Join-Path $consoleRoot 'accounts'
        [void][IO.Directory]::CreateDirectory($consoleAccounts)
        [IO.File]::WriteAllText((Join-Path $consoleAccounts 'alpha_auth.json'),$old,$utf8)
        [IO.File]::WriteAllText((Join-Path $consoleAccounts 'beta_auth.json'),$other,$utf8)
        $consoleSource = Join-Path $temporary 'ConsoleSmoke.cs'
        $consoleHelper = Join-Path $temporary 'ConsoleSmoke.exe'
        [IO.File]::WriteAllText($consoleSource, @'
using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

// This is a real Windows console integration test using injected input records.
// It sends only Down, Up and Escape; no account switch or real Codex shutdown occurs.
internal static class ConsoleSmoke {
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] struct Startup {
        public int Size; public string Reserved,Desktop,Title;
        public uint X,Y,Width,Height,Columns,Rows,Fill,Flags;
        public ushort Show,ReservedSize; public IntPtr ReservedBytes,Input,Output,Error;
    }
    [StructLayout(LayoutKind.Sequential)] struct ProcessInfo {public IntPtr Process,Thread; public uint Id,ThreadId;}
    [StructLayout(LayoutKind.Sequential)] struct Coord {public short X,Y;}
    [StructLayout(LayoutKind.Sequential)] struct Rect {public short Left,Top,Right,Bottom;}
    [StructLayout(LayoutKind.Sequential)] struct BufferInfo {public Coord Size,Cursor; public ushort Attributes; public Rect Window; public Coord Maximum;}
    [StructLayout(LayoutKind.Explicit,Size=20)] struct InputRecord {
        [FieldOffset(0)] public ushort Kind;
        [FieldOffset(4)] public int Down;
        [FieldOffset(8)] public ushort Repeat;
        [FieldOffset(10)] public ushort Key;
        [FieldOffset(12)] public ushort Scan;
        [FieldOffset(14)] public char Character;
        [FieldOffset(16)] public uint State;
    }
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool CreateProcess(string app,StringBuilder command,IntPtr processAttributes,IntPtr threadAttributes,bool inherit,uint flags,IntPtr environment,string directory,ref Startup startup,out ProcessInfo process);
    [DllImport("kernel32.dll",SetLastError=true)] static extern bool AttachConsole(uint process);
    [DllImport("kernel32.dll")] static extern bool FreeConsole();
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern IntPtr CreateFile(string name,uint access,uint share,IntPtr security,uint mode,uint flags,IntPtr template);
    [DllImport("kernel32.dll",SetLastError=true)] static extern bool GetConsoleScreenBufferInfo(IntPtr output,out BufferInfo info);
    [DllImport("kernel32.dll",EntryPoint="ReadConsoleOutputCharacterW",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool ReadCharacters(IntPtr output,[Out] char[] text,uint count,Coord origin,out uint read);
    [DllImport("kernel32.dll",EntryPoint="WriteConsoleInputW",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool WriteInput(IntPtr input,InputRecord[] records,uint count,out uint written);
    [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr handle,uint milliseconds);
    [DllImport("kernel32.dll")] static extern bool GetExitCodeProcess(IntPtr process,out uint code);
    [DllImport("kernel32.dll")] static extern bool TerminateProcess(IntPtr process,uint code);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    static void Check(bool value,string reason) {if(!value)throw new Win32Exception(Marshal.GetLastWin32Error(),reason);}
    static string Screen(IntPtr output) {
        BufferInfo info; Check(GetConsoleScreenBufferInfo(output,out info),"read buffer geometry");
        uint count=(uint)Math.Min(1000000,info.Size.X*(info.Cursor.Y+1));
        var text=new char[count]; uint read;
        Check(ReadCharacters(output,text,count,new Coord(),out read),"read console characters");
        return new string(text,0,(int)read);
    }
    static void WaitSelection(IntPtr output,string selected,string other) {
        DateTime deadline=DateTime.UtcNow.AddSeconds(10);
        while(DateTime.UtcNow<deadline) {
            string screen=Screen(output);
            if(screen.LastIndexOf("> "+selected,StringComparison.Ordinal)>screen.LastIndexOf("> "+other,StringComparison.Ordinal))return;
            Thread.Sleep(40);
        }
        throw new Exception("Timed out waiting for selected synthetic alias "+selected);
    }
    static void Key(IntPtr input,ushort key,char character) {
        var down=new InputRecord {Kind=1,Down=1,Repeat=1,Key=key,Character=character};
        var up=down;up.Down=0;uint written;
        Check(WriteInput(input,new[] {down,up},2,out written)&&written==2,"inject console key");
    }
    static int Main(string[] args) {
        ProcessInfo process=new ProcessInfo();IntPtr input=IntPtr.Zero,output=IntPtr.Zero;bool attached=false;
        try {
            // STARTF_USESHOWWINDOW + SW_HIDE + CREATE_NEW_CONSOLE: real hidden console,
            // with no redirected stdin/stdout handles in the executable under test.
            var startup=new Startup {Size=Marshal.SizeOf(typeof(Startup)),Flags=1,Show=0};
            var command=new StringBuilder("\""+args[0]+"\" --root \""+args[1]+"\"");
            Check(CreateProcess(args[0],command,IntPtr.Zero,IntPtr.Zero,false,0x10,IntPtr.Zero,Path.GetDirectoryName(args[0]),ref startup,out process),"create hidden console CLI");
            FreeConsole();
            DateTime deadline=DateTime.UtcNow.AddSeconds(10);
            while(!(attached=AttachConsole(process.Id)) && DateTime.UtcNow<deadline)Thread.Sleep(40);
            Check(attached,"attach isolated CLI console");
            input=CreateFile("CONIN$",0xC0000000,3,IntPtr.Zero,3,0,IntPtr.Zero);
            output=CreateFile("CONOUT$",0x80000000,3,IntPtr.Zero,3,0,IntPtr.Zero);
            Check(input!=new IntPtr(-1)&&output!=new IntPtr(-1),"open console buffers");
            string first=args.Length>3?args[3]:"alpha",second=args.Length>4?args[4]:"beta";
            WaitSelection(output,first,second);
            for(int i=5;i<args.Length;i++)if(!Screen(output).Contains(args[i]))throw new Exception("Required menu hint is missing");
            Key(input,0x28,'\0');WaitSelection(output,second,first);
            Key(input,0x26,'\0');WaitSelection(output,first,second);
            Key(input,0x1B,'\x1B');
            Check(WaitForSingleObject(process.Process,10000)==0,"Escape exits CLI");
            uint exit;Check(GetExitCodeProcess(process.Process,out exit)&&exit==0,"Escape exit status");
            File.WriteAllText(args[2],"PASS real hidden console: "+first+" -> Down "+second+" -> Up "+first+" -> Escape exit 0; system input records injected, not manual keyboard interaction.");
            return 0;
        } catch(Exception error) {File.WriteAllText(args[2],"FAIL "+error.GetType().Name+": "+error.Message);return 1;}
        finally {
            if(input!=IntPtr.Zero&&input!=new IntPtr(-1))CloseHandle(input);
            if(output!=IntPtr.Zero&&output!=new IntPtr(-1))CloseHandle(output);
            if(attached)FreeConsole();
            if(process.Process!=IntPtr.Zero) {
                if(WaitForSingleObject(process.Process,0)!=0)TerminateProcess(process.Process,1);
                CloseHandle(process.Process);
            }
            if(process.Thread!=IntPtr.Zero)CloseHandle(process.Thread);
        }
    }
}
'@, $utf8)
        Compile-Cli $consoleHelper @($consoleSource) @()
        $reportPath = Join-Path $temporary 'console-smoke.txt'
        $result = Invoke-Cli $consoleHelper @($ConsoleExecutable,$consoleRoot,$reportPath) 60000
        $report = if (Test-Path -LiteralPath $reportPath) { [IO.File]::ReadAllText($reportPath) } else { 'FAIL no console report' }
        Assert-Cli ($result.Code -eq 0 -and $report.StartsWith('PASS ')) $report
        Assert-Cli (-not (Test-Path -LiteralPath (Join-Path $consoleRoot 'auth.json')) -and [IO.File]::ReadAllText((Join-Path $consoleAccounts 'alpha_auth.json')) -eq $old -and [IO.File]::ReadAllText((Join-Path $consoleAccounts 'beta_auth.json')) -eq $other) 'real console navigation never switches or modifies synthetic credentials'
        $fakeReportPath = Join-Path $temporary 'console-synthetic-hints.txt'
        $result = Invoke-Cli $consoleHelper @($exe,$consoleRoot,$fakeReportPath,'工作','study','上次已用尽','快照中的重置时间已到') 60000
        $fakeReport = if (Test-Path -LiteralPath $fakeReportPath) { [IO.File]::ReadAllText($fakeReportPath) } else { 'FAIL no synthetic menu report' }
        Assert-Cli ($result.Code -eq 0 -and $fakeReport.StartsWith('PASS ')) ('synthetic service menu retains exhausted emphasis and reset hint; ' + $fakeReport)
        Write-Host ('Console executable SHA256: ' + (Get-FileHash -LiteralPath $ConsoleExecutable -Algorithm SHA256).Hash)
    }
    Write-Host ('PASS ' + $checks + ' native CLI isolated and actual shared-service checks (synthetic credentials only)')
} finally {
    $full = [IO.Path]::GetFullPath($temporary)
    $parent = [IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\') + '\'
    if ($full.StartsWith($parent,[StringComparison]::OrdinalIgnoreCase) -and (Split-Path -Leaf $full).StartsWith('.tmp-native-cli-')) {
        Remove-Item -LiteralPath $full -Recurse -Force
    }
}
