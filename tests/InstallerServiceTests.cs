using System;
using System.IO;
using System.Text;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using Microsoft.Win32;
using CodexSwitch.Setup;

internal static class InstallerServiceTests
{
    private static int assertions;
    private static string scratch;
    private static void Check(bool ok, string message) { assertions++; if (!ok) throw new Exception(message); }
    private static void Fails(Action operation, string contains)
    {
        try { operation(); } catch (Exception e) { Check(e.Message.Contains(contains), "错误信息不符：" + e.Message); return; }
        throw new Exception("预期失败：" + contains);
    }
    private static InstallConfiguration Configuration(string name)
    {
        string id = Guid.NewGuid().ToString("N");
        return new InstallConfiguration {
            InstallDirectory = Path.Combine(scratch, name + id, "Programs", "CodexSwitch"),
            ProgramsDirectory = Path.Combine(scratch, name + id, "StartMenu"),
            HelperDirectory = Path.Combine(scratch, name + id, "SetupCache"),
            UninstallKey = @"Software\CodexSwitch\InstallerServiceTests\" + id + @"\Uninstall",
            EnvironmentKey = @"Software\CodexSwitch\InstallerServiceTests\" + id + @"\Environment",
            LegacyKey = @"Software\CodexSwitch\InstallerServiceTests\" + id + @"\Legacy",
            MutexName = @"Local\CodexSwitch.Setup.Tests." + id,
            Version = new Version("0.4.0.0"),
            LoadPayload = delegate(string name2) { return Encoding.UTF8.GetBytes("fixture " + name2); },
            CreateShortcut = delegate(string path, string target) { File.WriteAllText(path, "fixture shortcut " + target); },
            CheckMachineLegacy = false,
            BroadcastEnvironment = false
        };
    }
    private static object Raw(InstallConfiguration c, string name) { using (RegistryKey k = Registry.CurrentUser.OpenSubKey(c.EnvironmentKey)) return k == null ? null : k.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames); }
    private static RegistryValueKind Kind(InstallConfiguration c) { using (RegistryKey k = Registry.CurrentUser.OpenSubKey(c.EnvironmentKey)) return k.GetValueKind("Path"); }
    private static void SetPath(InstallConfiguration c, string value, RegistryValueKind kind) { using (RegistryKey k = Registry.CurrentUser.CreateSubKey(c.EnvironmentKey)) k.SetValue("Path", value, kind); }
    private static string Product(InstallConfiguration c) { return Path.Combine(c.InstallDirectory, "CodexSwitch.exe"); }
    private static void CleanRegistry(InstallConfiguration c) { Registry.CurrentUser.DeleteSubKeyTree(c.UninstallKey.Substring(0, c.UninstallKey.LastIndexOf('\\')), false); }
    private static void Lifecycle()
    {
        InstallConfiguration c = Configuration("lifecycle");
        try {
            string before = @"%USERPROFILE%\bin;C:\unrelated;";
            SetPath(c, before, RegistryValueKind.ExpandString);
            InstallService s = new InstallService(c);
            s.Install();
            Check(File.ReadAllText(Product(c)) == "fixture Payload.CodexSwitch.exe", "载荷不一致");
            Check(File.Exists(Path.Combine(c.InstallDirectory, "codex-switch.exe")), "CLI 缺失");
            Check(File.Exists(Path.Combine(c.HelperDirectory, "uninstall.exe")), "卸载 helper 缺失");
            Check(File.Exists(Path.Combine(c.ProgramsDirectory, "Codex Switch", "Codex Switch.lnk")), "快捷方式缺失");
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(c.UninstallKey)) {
                Check(((string)k.GetValue("UninstallString")).StartsWith("\"" + Path.Combine(c.HelperDirectory, "uninstall.exe") + "\""), "卸载不指向独立 helper");
                Check(!((string)k.GetValue("UninstallString")).Contains("msiexec"), "自研卸载不能使用 MSI");
            }
            Check(Kind(c) == RegistryValueKind.ExpandString && ((string)Raw(c,"Path")).StartsWith(before), "展开型 PATH 被改写");
            string extra = Path.Combine(c.InstallDirectory, "auth.json");
            File.WriteAllText(extra, "account sentinel");
            s.Install();
            Check(((string)Raw(c, "Path")).Split(new string[] { c.InstallDirectory }, StringSplitOptions.None).Length == 2, "重复安装追加 PATH");
            c.Version = new Version("0.4.1.0");
            c.LoadPayload = delegate(string n) { return Encoding.UTF8.GetBytes("new " + n); };
            new InstallService(c).Install();
            Check(File.ReadAllText(Product(c)) == "new Payload.CodexSwitch.exe", "升级失败");
            c.Version = new Version("0.4.0.0");
            Fails(delegate { new InstallService(c).Install(); }, "较新版本");
            Check(File.ReadAllText(Product(c)) == "new Payload.CodexSwitch.exe", "降级扰动原版");
            new InstallService(c).Uninstall();
            Check(!File.Exists(Product(c)) && !File.Exists(Path.Combine(c.HelperDirectory, "uninstall.exe")), "卸载残留程序");
            Check(File.ReadAllText(extra) == "account sentinel", "卸载删除未知文件");
            Check((string)Raw(c, "Path") == before && Kind(c) == RegistryValueKind.ExpandString, "卸载未保留原始 PATH");
            Check(!Directory.Exists(Path.Combine(c.ProgramsDirectory, "Codex Switch")), "空快捷方式文件夹残留");
            new InstallService(c).Uninstall();
        } finally { CleanRegistry(c); }
    }
    private static void OwnershipAndPath()
    {
        InstallConfiguration c = Configuration("ownership");
        try {
            Directory.CreateDirectory(c.InstallDirectory);
            File.WriteAllText(Product(c), "foreign exe");
            Fails(delegate { new InstallService(c).Install(); }, "不属于");
            Check(File.ReadAllText(Product(c)) == "foreign exe", "覆盖外来程序");
            File.Delete(Product(c));
            string preexisting = "C:\\first;\"" + c.InstallDirectory + "\\\";C:\\last";
            SetPath(c, preexisting, RegistryValueKind.String);
            new InstallService(c).Install();
            new InstallService(c).Uninstall();
            Check((string)Raw(c,"Path") == preexisting && Kind(c) == RegistryValueKind.String, "删除原有 PATH 项");
            new InstallService(c).Install();
            File.WriteAllText(Product(c), "user replaced executable");
            Fails(delegate { new InstallService(c).Install(); }, "已被修改");
            Fails(delegate { new InstallService(c).Uninstall(); }, "已被修改");
            Check(File.ReadAllText(Product(c)) == "user replaced executable", "删除更改后的文件");
        } finally { CleanRegistry(c); }
        c = Configuration("path-edits");
        try {
            new InstallService(c).Install();
            Check(Raw(c,"Path") != null, "未创建 PATH");
            SetPath(c, "C:\\added;" + c.InstallDirectory + ";%OTHER%", RegistryValueKind.ExpandString);
            new InstallService(c).Uninstall();
            Check((string)Raw(c,"Path") == "C:\\added;%OTHER%" && Kind(c) == RegistryValueKind.ExpandString, "卸载损坏用户新增 PATH");
        } finally { CleanRegistry(c); }
        c = Configuration("absent-path");
        try { new InstallService(c).Install(); new InstallService(c).Uninstall(); Check(Raw(c,"Path") == null, "原先不存在的 PATH 未删除"); } finally { CleanRegistry(c); }
    }
    private static void RollbackAndLocks()
    {
        foreach (string point in new string[] { "files", "path", "registration" }) {
            InstallConfiguration c = Configuration("rollback");
            try {
                SetPath(c, "%RAW%", RegistryValueKind.ExpandString);
                c.Fault = delegate(string p) { if (p == point) throw new IOException("模拟失败"); };
                Fails(delegate { new InstallService(c).Install(); }, "模拟失败");
                Check(!File.Exists(Product(c)) && (string)Raw(c,"Path") == "%RAW%" && Kind(c) == RegistryValueKind.ExpandString, "新安装未回滚：" + point);
                c.Fault = null;
                new InstallService(c).Install();
                string previous = File.ReadAllText(Product(c));
                string pathBefore = (string)Raw(c,"Path");
                c.Version = new Version("0.4.1.0");
                c.LoadPayload = delegate(string n) { return Encoding.UTF8.GetBytes("changed " + n); };
                c.Fault = delegate(string p) { if (p == point) throw new IOException("模拟失败"); };
                Fails(delegate { new InstallService(c).Install(); }, "模拟失败");
                Check(File.ReadAllText(Product(c)) == previous && (string)Raw(c,"Path") == pathBefore, "升级未回滚：" + point);
                Fails(delegate { new InstallService(c).Uninstall(); }, "模拟失败");
                Check(File.ReadAllText(Product(c)) == previous && (string)Raw(c,"Path") == pathBefore, "卸载未回滚：" + point);
                c.Fault = null;
                using (FileStream locked = new FileStream(Product(c), FileMode.Open, FileAccess.Read, FileShare.Read)) {
                    Fails(delegate { new InstallService(c).Install(); }, "正在使用");
                    Fails(delegate { new InstallService(c).Uninstall(); }, "正在使用");
                    Check((string)Raw(c,"Path") == pathBefore, "锁失败扰动 PATH");
                }
                new InstallService(c).Uninstall();
            } finally { CleanRegistry(c); }
        }
    }
    private static void LegacyAndUnsafe()
    {
        InstallConfiguration c = Configuration("legacy");
        try {
            using (RegistryKey k = Registry.CurrentUser.CreateSubKey(c.LegacyKey)) k.SetValue("InstallDirectory", "old MSI");
            Fails(delegate { new InstallService(c).Install(); }, "旧版 MSI");
            Fails(delegate { new InstallService(c).Uninstall(); }, "旧版 MSI");
            Check(!Directory.Exists(c.InstallDirectory), "旧 MSI 拒绝时写入文件");
        } finally { CleanRegistry(c); }
        c = Configuration("long"); c.InstallDirectory = Path.Combine(scratch, new string('x', 190));
        Fails(delegate { new InstallService(c).Install(); }, "路径");
        c = Configuration("separator"); c.InstallDirectory += ";bad";
        Fails(delegate { new InstallService(c).Install(); }, "路径");
        c = Configuration("reparse");
        Directory.CreateDirectory(c.InstallDirectory);
        string link = Path.Combine(c.InstallDirectory, "CodexSwitch.exe");
        string sentinel = Path.Combine(scratch, "hardlink-sentinel"); File.WriteAllText(sentinel, "preserve");
        File.WriteAllText(link, "foreign");
        Fails(delegate { new InstallService(c).Install(); }, "不属于");
        Check(File.ReadAllText(sentinel) == "preserve", "未知文件受损");
        c = Configuration("junction");
        string outside = Path.Combine(scratch,"junction-target"); Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside,"sentinel.txt"),"preserve junction target");
        Directory.CreateDirectory(Path.GetDirectoryName(c.InstallDirectory));
        string cmd = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"cmd.exe");
        using (Process junction = Start(cmd,"/d /c mklink /J " + Quoted(c.InstallDirectory) + " " + Quoted(outside))) { junction.WaitForExit(); Check(junction.ExitCode == 0,"无法建立隔离 junction 夹具"); }
        try {
            Fails(delegate { new InstallService(c).Install(); }, "重解析点");
            Fails(delegate { new InstallService(c).Uninstall(); }, "重解析点");
            Check(File.ReadAllText(Path.Combine(outside,"sentinel.txt")) == "preserve junction target","链接目标被修改");
        } finally { Directory.Delete(c.InstallDirectory,false); }
        c = Configuration("malformed-state");
        try {
            new InstallService(c).Install();
            string state = Path.Combine(c.InstallDirectory,".codex-switch-install.xml");
            string good = File.ReadAllText(state);
            File.WriteAllText(state,good.Replace("name=\"CodexSwitch.exe\"","name=\"..\\outside.exe\""));
            Fails(delegate { new InstallService(c).Uninstall(); }, "归属记录损坏");
            Check(File.Exists(Product(c)),"恶意归属记录导致删除文件");
            File.WriteAllText(state,good);
            new InstallService(c).Uninstall();
        } finally { CleanRegistry(c); }
    }
    private static string Quoted(string value) { return "\"" + value + "\""; }
    private static string WorkerArguments(InstallConfiguration c)
    {
        return "--worker " + Quoted(c.InstallDirectory) + " " + Quoted(c.ProgramsDirectory) + " " + Quoted(c.HelperDirectory) + " " + Quoted(c.UninstallKey) + " " + Quoted(c.EnvironmentKey) + " " + Quoted(c.LegacyKey) + " " + Quoted(c.MutexName);
    }
    private static Process Start(string exe, string arguments)
    {
        return Process.Start(new ProcessStartInfo(exe, arguments) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true });
    }
    private static void RealImagesAndConcurrency()
    {
        InstallConfiguration c = Configuration("real-images");
        try {
            byte[] image = File.ReadAllBytes(Assembly.GetExecutingAssembly().Location);
            c.LoadPayload = delegate(string n) { return image; };
            new InstallService(c).Install();
            using (Process product = Start(Product(c), "--hold")) {
                Check(product.StandardOutput.ReadLine() == "ready", "运行夹具未启动");
                try {
                    Fails(delegate { new InstallService(c).Install(); }, "正在使用");
                    Fails(delegate { new InstallService(c).Uninstall(); }, "正在使用");
                    Check(File.Exists(Product(c)), "运行产品预检扰动现有安装");
                } finally { product.Kill(); product.WaitForExit(); }
            }
            ManualResetEvent held = new ManualResetEvent(false);
            ManualResetEvent release = new ManualResetEvent(false);
            Thread owner = new Thread(delegate() { using (Mutex mutex = new Mutex(false, c.MutexName)) { mutex.WaitOne(); held.Set(); release.WaitOne(); mutex.ReleaseMutex(); } });
            owner.Start(); held.WaitOne();
            try { Fails(delegate { new InstallService(c).Install(); }, "另一个"); Fails(delegate { new InstallService(c).Uninstall(); }, "另一个"); }
            finally { release.Set(); owner.Join(); held.Dispose(); release.Dispose(); }

            string retired;
            using (Process helper = Start(Path.Combine(c.HelperDirectory, "uninstall.exe"), WorkerArguments(c))) {
                retired = helper.StandardOutput.ReadLine();
                string errors = helper.StandardError.ReadToEnd();
                helper.WaitForExit();
                Check(helper.ExitCode == 0, "实进程卸载 helper 失败：" + errors);
            }
            Check(!File.Exists(Product(c)), "同步返回后产品仍在");
            Check(!File.Exists(Path.Combine(c.HelperDirectory, "uninstall.exe")), "旧 helper 未先退休");
            Check(retired != null && File.Exists(retired), "旧 helper 唯一路径缺失");
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(c.UninstallKey)) Check(key == null, "同步返回后 ARP 仍在");
            new InstallService(c).Install();
            string script = UninstallHost.CleanupScript(c, retired, Int32.MaxValue);
            string powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
            using (Process cleanup = Start(powershell, "-NoProfile -NonInteractive -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script)))) { string errors = cleanup.StandardError.ReadToEnd(); cleanup.WaitForExit(); Check(cleanup.ExitCode == 0, "旧缓存清理进程失败：" + errors); }
            Check(!File.Exists(retired), "旧缓存未清理");
            Check(File.Exists(Path.Combine(c.HelperDirectory,"uninstall.exe")), "延迟清理删除了快速重装的新 helper");
            Fails(delegate { UninstallHost.CleanupScript(c, Product(c), 1); }, "不属于");
            Fails(delegate { UninstallHost.CleanupScript(c, Path.Combine(c.HelperDirectory,"uninstall.exe.cs-backup-..\\outside"), 1); }, "不属于");
            new InstallService(c).Uninstall();
        } finally { CleanRegistry(c); }
    }
    private static void ShortcutAndRegistration()
    {
        InstallConfiguration c = Configuration("shortcut");
        try {
            string folder = Path.Combine(c.ProgramsDirectory,"Codex Switch"); Directory.CreateDirectory(folder);
            string link = Path.Combine(folder,"Codex Switch.lnk"); File.WriteAllText(link,"foreign shortcut");
            Fails(delegate { new InstallService(c).Install(); }, "不属于");
            Check(File.ReadAllText(link) == "foreign shortcut" && !File.Exists(Product(c)), "覆盖了既有快捷方式");
            File.Delete(link);
            using (RegistryKey k = Registry.CurrentUser.CreateSubKey(c.UninstallKey)) k.SetValue("DisplayName","foreign registration");
            Fails(delegate { new InstallService(c).Install(); }, "不属于");
            Registry.CurrentUser.DeleteSubKey(c.UninstallKey);
            new InstallService(c).Install();
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(c.UninstallKey,true)) k.SetValue("UnrelatedSetting","preserve");
            new InstallService(c).Uninstall();
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(c.UninstallKey)) Check((string)k.GetValue("UnrelatedSetting") == "preserve", "卸载删除未知注册表字段");
        } finally { CleanRegistry(c); }
    }
    private static void CleanupFailureAfterCommit()
    {
        InstallConfiguration c = Configuration("cleanup-failure");
        try {
            new InstallService(c).Install();
            c.Version = new Version("0.4.1.0");
            c.LoadPayload = delegate(string n) { return Encoding.UTF8.GetBytes("committed " + n); };
            c.Fault = delegate(string point) { if (point == "cleanup") throw new IOException("模拟提交后清理失败"); };
            new InstallService(c).Install();
            Check(File.ReadAllText(Product(c)) == "committed Payload.CodexSwitch.exe", "提交后清理错误回滚了程序");
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(c.UninstallKey)) Check((string)k.GetValue("DisplayVersion") == "0.4.1", "提交后清理错误回滚了 ARP");
            Check(((string)Raw(c,"Path")).Contains(c.InstallDirectory), "提交后清理错误回滚了 PATH");
            Check(Directory.GetFiles(c.InstallDirectory,"*.cs-backup-*").Length > 0, "清理失败没有留下唯一孤立备份");
            new InstallService(c).Uninstall();
            Check(!File.Exists(Product(c)) && Raw(c,"Path") == null, "提交后的卸载状态不一致");
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(c.UninstallKey)) Check(k == null, "提交后的卸载 ARP 不一致");
        } finally { CleanRegistry(c); }
    }
    public static int Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--hold") { Console.WriteLine("ready"); Thread.Sleep(30000); return 0; }
        if (args.Length == 8 && args[0] == "--worker") {
            try {
                InstallConfiguration c = new InstallConfiguration { InstallDirectory=args[1], ProgramsDirectory=args[2], HelperDirectory=args[3], UninstallKey=args[4], EnvironmentKey=args[5], LegacyKey=args[6], MutexName=args[7], CheckMachineLegacy=false, BroadcastEnvironment=false };
                InstallService service = new InstallService(c); service.Uninstall(); Console.WriteLine(service.DeferredCleanupPath); return 0;
            } catch (Exception e) { Console.Error.WriteLine(e); return 1; }
        }
        scratch = args[0];
        try { Lifecycle(); OwnershipAndPath(); RollbackAndLocks(); LegacyAndUnsafe(); RealImagesAndConcurrency(); ShortcutAndRegistration(); CleanupFailureAfterCommit(); Console.WriteLine("PASS " + assertions + " assertions"); return 0; }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }
}

namespace CodexSwitch.Setup
{
    // Compile-time stand-in: the helper's production GUI is provided by SetupForm.cs.
    internal static class SetupForm { internal static int Run(InstallService service, bool uninstall) { service.Uninstall(); return 0; } }
}
