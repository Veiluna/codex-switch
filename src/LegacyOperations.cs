using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace CodexSwitch
{
    public sealed class OperationResult
    {
        public readonly bool Success;
        public readonly string Code;
        public readonly string Message;
        public readonly string TargetUserId;
        public readonly string TargetAccountId;
        public OperationResult(bool success, string code, string message)
            : this(success,code,message,"","") { }
        public OperationResult(bool success, string code, string message, string targetUserId, string targetAccountId)
        { Success = success; Code = code; Message = message; TargetUserId = targetUserId; TargetAccountId = targetAccountId; }
    }

    internal interface ILegacyOperations : IDisposable
    {
        OperationResult Save(string root, string name);
        OperationResult Switch(string root, string name, IWin32Window owner);
    }

    // Keep the already audited byte-preserving save/switch algorithms in one place.
    // No script files, working-directory dependencies, shell processes or account text interpolation.
    internal sealed class LegacyOperations : ILegacyOperations
    {
        private Runspace runspace;
        private readonly int ownerThread = Thread.CurrentThread.ManagedThreadId;
        private static readonly string[] SourceOrder = {
            "UiStyle.ps1", "AccountList.ps1", "Desktop.ps1", "AccountSwitch.ps1", "SaveAccount.ps1"
        };

        private void Initialize()
        {
            if (Thread.CurrentThread.ManagedThreadId != ownerThread ||
                Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
                throw new InvalidOperationException("账号操作需要在界面线程执行。");
            if (runspace != null) return;
            var created = RunspaceFactory.CreateRunspace();
            try
            {
                created.ApartmentState = ApartmentState.STA;
                created.ThreadOptions = PSThreadOptions.UseCurrentThread;
                created.Open();
                using (var ps = PowerShell.Create())
                {
                    ps.Runspace = created;
                    var source = new StringBuilder("$ErrorActionPreference = 'Stop'\r\n");
                    foreach (string file in SourceOrder) source.AppendLine(ReadEmbedded(file));
                    ps.AddScript(source.ToString(), false).Invoke();
                    if (ps.HadErrors) throw new InvalidOperationException("账号操作组件不可用。", ps.Streams.Error[0].Exception);
                }
                runspace = created;
            }
            catch { created.Dispose(); throw; }
        }

        private static string ReadEmbedded(string file)
        {
            using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("CodexSwitch." + file))
            {
                if (stream == null) throw new InvalidOperationException("缺少账号操作组件。");
                using (var reader = new StreamReader(stream, Encoding.UTF8, true))
                {
                    var text = new StringBuilder();
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        // These are the only dependency directives in the audited sources.
                        // Fail closed if a future embedded script adds an unrecognized dot-source.
                        if (line == ". (Join-Path $PSScriptRoot 'UiStyle.ps1')" &&
                            (file == "Desktop.ps1" || file == "SaveAccount.ps1")) continue;
                        if (line == ". (Join-Path $PSScriptRoot 'Desktop.ps1')" && file == "AccountSwitch.ps1") continue;
                        if (line.TrimStart().StartsWith(". ", StringComparison.Ordinal))
                            throw new InvalidOperationException("账号操作组件依赖未经核实。");
                        text.AppendLine(line);
                    }
                    return text.ToString();
                }
            }
        }

        public OperationResult Save(string root, string name)
        { return Execute("Save-SwitchAccount", root, name, null); }
        public OperationResult Switch(string root, string name, IWin32Window owner)
        { return Execute("Invoke-DesktopAccountSwitch", root, name, owner); }

        // 用委托传递两次确认和离开前保存，避免共享服务依赖窗口类型。
        // 两种入口均在调用期间持有同一个系统文件锁。
        internal OperationResult SwitchShared(string root, string name, byte[] expected,
            Func<string,string,bool> confirm, Action<byte[]> departure)
        {
            Initialize();
            using (var ps = PowerShell.Create())
            {
                ps.Runspace = runspace;
                ps.AddScript(@"param($root,$name,$expected,$approve,$departure)
$confirmation = { param($Owner,$Name,$Phase) $approve.Invoke($Phase,$Name) }.GetNewClosure()
Invoke-DesktopAccountSwitch -Root $root -Name $name -ConfirmAction $confirmation -AlreadyLocked -ExpectedTargetBytes $expected -BeforeDeparture $departure", false)
                    .AddArgument(root).AddArgument(name).AddArgument(expected).AddArgument(confirm).AddArgument(departure);
                Collection<PSObject> results = ps.Invoke();
                if (ps.HadErrors || results.Count != 1) return Failure("Switch");
                PSObject item = results[0];
                return new OperationResult((bool)item.Properties["Success"].Value,
                    (string)item.Properties["Code"].Value, (string)item.Properties["Message"].Value,
                    TextProperty(item,"TargetUserId"),TextProperty(item,"TargetAccountId"));
            }
        }

        private OperationResult Execute(string command, string root, string name, IWin32Window owner)
        {
            try
            {
                Initialize();
                using (var ps = PowerShell.Create())
                {
                    ps.Runspace = runspace;
                    ps.AddCommand(command).AddParameter("Root", root).AddParameter("Name", name);
                    if (command == "Invoke-DesktopAccountSwitch") ps.AddParameter("Owner", owner);
                    Collection<PSObject> results = ps.Invoke();
                    if (ps.HadErrors || results.Count != 1) return Failure(command);
                    PSObject item = results[0];
                    var success = item.Properties["Success"];
                    var message = item.Properties["Message"];
                    var code = item.Properties["Code"];
                    if (success == null || !(success.Value is bool) || message == null || !(message.Value is string))
                        return Failure(command);
                    return new OperationResult((bool)success.Value, code == null ? "" : code.Value as string ?? "", (string)message.Value,
                        TextProperty(item,"TargetUserId"),TextProperty(item,"TargetAccountId"));
                }
            }
            catch { return Failure(command); }
        }
        private static string TextProperty(PSObject item,string name)
        { return item.Properties[name]==null?"":item.Properties[name].Value as string??""; }
        private static OperationResult Failure(string command)
        {
            return new OperationResult(false, "Unavailable", command == "Save-SwitchAccount"
                ? "保存未完成，请检查账号目录权限或 Windows PowerShell 组件后重试。"
                : "切换未完成，请检查账号目录权限、桌面端状态或 Windows PowerShell 组件后重试。");
        }
        public void Dispose()
        {
            if (runspace != null) { runspace.Dispose(); runspace = null; }
        }
    }
}
