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
    private static void RollbackPreservesIndependentFiles()
    {
        foreach (string scenario in new string[] { "before-publish", "after-publish", "after-remove", "new-after-publish" }) {
            InstallConfiguration c = Configuration("preserve-independent-" + scenario);
            try {
                bool firstInstall = scenario == "new-after-publish";
                if (!firstInstall) new InstallService(c).Install();
                string previous = firstInstall ? null : File.ReadAllText(Product(c));
                string independent = "independent file retained " + scenario;
                c.Version = new Version("0.4.1.0");
                c.LoadPayload = delegate(string n) { return Encoding.UTF8.GetBytes("replacement " + n); };
                bool callbackRan = false;
                c.Fault = delegate(string point) {
                    if (scenario == "before-publish" && point == "publish:CodexSwitch.exe") {
                        callbackRan = true;
                        File.WriteAllText(Product(c), independent);
                    } else if (scenario != "before-publish" && point == "files") {
                        callbackRan = true;
                        if (scenario == "after-publish" || firstInstall) File.Move(Product(c), Product(c) + ".published-fixture");
                        File.WriteAllText(Product(c), independent);
                        throw new IOException("模拟操作失败");
                    }
                };
                Fails(delegate {
                    if (scenario == "after-remove") new InstallService(c).Uninstall();
                    else new InstallService(c).Install();
                }, "程序文件恢复失败");
                Check(callbackRan, "未经过隔离回滚场景：" + scenario);
                Check(File.ReadAllText(Product(c)) == independent, "回滚改动独立文件：" + scenario);
                string[] backups = Directory.GetFiles(c.InstallDirectory, "CodexSwitch.exe.cs-backup-*");
                Check(firstInstall ? backups.Length == 0 : backups.Length == 1 && File.ReadAllText(backups[0]) == previous, "未保留原程序备份：" + scenario);
                if (scenario == "after-publish" || firstInstall)
                    Check(!File.Exists(Product(c) + ".published-fixture"), "未通过自有句柄回滚本次发布对象：" + scenario);
            } finally { CleanRegistry(c); }
        }
    }
    private static string Quoted(string value) { return "\"" + value + "\""; }
    private static void TransactionObjectOwnership()
    {
        foreach (string scenario in new string[] { "cleanup-backup", "rollback-backup", "move-old", "publish-new" }) {
            InstallConfiguration c = Configuration("object-ownership-" + scenario);
            try {
                new InstallService(c).Install();
                string previous = File.ReadAllText(Product(c));
                string independent = "independent ordinary file " + scenario;
                string independentPath = null;
                string relocated = Product(c) + ".relocated-fixture";
                bool callbackRan = false;
                c.Version = new Version("0.4.1.0");
                c.LoadPayload = delegate(string n) { return Encoding.UTF8.GetBytes("published " + n); };
                c.Fault = delegate(string point) {
                    if (callbackRan) return;
                    if ((scenario == "cleanup-backup" && point == "cleanup") || (scenario == "rollback-backup" && point == "files")) {
                        independentPath = Directory.GetFiles(c.InstallDirectory, "CodexSwitch.exe.cs-backup-*")[0];
                    } else if (scenario == "move-old" && point == "move:CodexSwitch.exe") {
                        independentPath = Product(c);
                    } else if (scenario == "publish-new" && point == "publish:CodexSwitch.exe") {
                        independentPath = Directory.GetFiles(c.InstallDirectory, "CodexSwitch.exe.cs-new-*")[0];
                    } else return;
                    callbackRan = true;
                    File.Move(independentPath, relocated);
                    File.WriteAllText(independentPath, independent);
                    if (scenario == "rollback-backup") throw new IOException("模拟操作失败");
                };
                if (scenario == "rollback-backup") Fails(delegate { new InstallService(c).Install(); }, "模拟操作失败");
                else if (scenario == "move-old") new InstallService(c).Uninstall();
                else new InstallService(c).Install();
                Check(callbackRan, "未经过对象归属隔离场景：" + scenario);
                Check(File.Exists(independentPath) && File.ReadAllText(independentPath) == independent, "按名字操作了独立文件：" + scenario);
                if (scenario == "rollback-backup") Check(File.ReadAllText(Product(c)) == previous, "未从已持有对象恢复原程序");
                else if (scenario != "move-old") Check(File.ReadAllText(Product(c)) == "published Payload.CodexSwitch.exe", "未发布本次持有的对象：" + scenario);
                Check(!File.Exists(relocated), "已持有对象未完成自身移动或清理：" + scenario);
            } finally { CleanRegistry(c); }
        }
    }
    private static void LeafOwnershipMetadataPolicy()
    {
        // Simulate handle metadata rather than constructing replacement links.
        Type transaction = typeof(InstallService).GetNestedType("FileTransaction", BindingFlags.NonPublic);
        MethodInfo policy = transaction.GetMethod("IsPlainOwnedFile", BindingFlags.Static | BindingFlags.NonPublic);
        Check(policy != null, "缺少持有句柄的普通文件归属检查");
        Check((bool)policy.Invoke(null, new object[] { (uint)FileAttributes.Normal, 1u }), "拒绝单链接普通文件");
        Check((bool)policy.Invoke(null, new object[] { (uint)FileAttributes.Archive, 1u }), "拒绝普通存档属性文件");
        Check(!(bool)policy.Invoke(null, new object[] { (uint)FileAttributes.ReparsePoint, 1u }), "接受了重解析点元数据");
        Check(!(bool)policy.Invoke(null, new object[] { (uint)FileAttributes.Directory, 1u }), "接受了目录元数据");
        Check(!(bool)policy.Invoke(null, new object[] { (uint)FileAttributes.Normal, 2u }), "接受了多链接元数据");
        Check(!(bool)policy.Invoke(null, new object[] { (uint)FileAttributes.Normal, 0u }), "接受了无有效链接的元数据");
    }
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
            Check(File.Exists(retired), "快速重装不应自动删除已退休缓存");
            Check(File.Exists(Path.Combine(c.HelperDirectory,"uninstall.exe")), "快速重装的新 helper 缺失");
            Check(typeof(UninstallHost).GetMethod("ScheduleCleanup", BindingFlags.Static | BindingFlags.NonPublic) == null, "卸载不能调度后台清理");
            Check(typeof(UninstallHost).GetMethod("CleanupScript", BindingFlags.Static | BindingFlags.NonPublic) == null, "卸载不能生成后台清理脚本");
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
    private static void Arguments()
    {
        bool uninstall, quiet, help; InstallComponents? components;
        Check(SetupProgram.TryParseArguments(new string[0], out uninstall, out quiet, out help, out components) && !uninstall && !quiet && components == null, "默认 GUI 参数错误");
        Check(SetupProgram.TryParseArguments(new string[] { "install", "--quiet", "--components", "cli" }, out uninstall, out quiet, out help, out components) && quiet && components == InstallComponents.Cli, "标准静默 CLI 选择无效");
        Check(SetupProgram.TryParseArguments(new string[] { "install", "--components", "desktop,cli", "--quiet" }, out uninstall, out quiet, out help, out components) && components == InstallComponents.All, "双选参数无效");
        foreach (string[] args in new string[][] {
            new string[] { "--quiet" }, new string[] { "--components", "cli" }, new string[] { "install", "--components" },
            new string[] { "install", "--components", "" }, new string[] { "uninstall", "--components", "cli" },
            new string[] { "install", "--quiet", "--quiet" }, new string[] { "install", "--components", "cli", "--components", "desktop" },
            new string[] { "install", "--components", "cli,cli" }, new string[] { "install", "--components", "unknown" },
            new string[] { "install", "--help" }, new string[] { "install", "--components=cli" }
        }) Check(!SetupProgram.TryParseArguments(args, out uninstall, out quiet, out help, out components), "歧义/错误参数被接受：" + String.Join(" ",args));
    }
    private static void ComponentOwnership()
    {
        foreach (InstallComponents selected in new InstallComponents[] { InstallComponents.Desktop, InstallComponents.Cli }) {
            InstallConfiguration c = Configuration("component-unowned");
            try {
                Directory.CreateDirectory(c.InstallDirectory);
                string foreign = Path.Combine(c.InstallDirectory, selected == InstallComponents.Desktop ? "codex-switch.exe" : "CodexSwitch.exe");
                File.WriteAllText(foreign,"foreign");
                new InstallService(c).Install(selected);
                Fails(delegate { new InstallService(c).Install(InstallComponents.All); }, "不属于");
                new InstallService(c).Uninstall();
                Check(File.ReadAllText(foreign) == "foreign", "未选组件外来文件被修改");
            } finally { CleanRegistry(c); }
        }
        InstallConfiguration preexisting = Configuration("component-path");
        try {
            SetPath(preexisting, preexisting.InstallDirectory + ";C:\\user", RegistryValueKind.String);
            new InstallService(preexisting).Install(InstallComponents.Cli);
            new InstallService(preexisting).Install(InstallComponents.Desktop);
            new InstallService(preexisting).Uninstall();
            Check((string)Raw(preexisting,"Path") == preexisting.InstallDirectory + ";C:\\user", "移除 CLI 删除用户原有 PATH");
        } finally { CleanRegistry(preexisting); }
    }
    private static void Components()
    {
        foreach (InstallComponents selected in new InstallComponents[] { InstallComponents.Desktop, InstallComponents.Cli, InstallComponents.All }) {
            InstallConfiguration c = Configuration("components");
            try {
                InstallService service = new InstallService(c);
                Check(service.GetDefaultComponents() == InstallComponents.All, "首次默认必须全选");
                service.Install(selected);
                Check(File.Exists(Product(c)) == ((selected & InstallComponents.Desktop) != 0), "桌面文件不匹配组件");
                Check(File.Exists(Path.Combine(c.InstallDirectory,"codex-switch.exe")) == ((selected & InstallComponents.Cli) != 0), "CLI 文件不匹配组件");
                Check(File.Exists(Path.Combine(c.ProgramsDirectory,"Codex Switch","Codex Switch.lnk")) == ((selected & InstallComponents.Desktop) != 0), "快捷方式不匹配组件");
                Check((Raw(c,"Path") != null) == ((selected & InstallComponents.Cli) != 0), "PATH 不匹配组件");
                Check(service.GetDefaultComponents() == selected, "已有安装默认未保持组件");
                service.Install();
                Check(service.GetDefaultComponents() == selected, "无显式组件升级改变组件");
                Fails(delegate { service.Install((InstallComponents)0); }, "至少选择");
                Fails(delegate { service.Install((InstallComponents)8); }, "有效组件");
                using (RegistryKey entry = Registry.CurrentUser.OpenSubKey(c.UninstallKey)) {
                    Check((string)entry.GetValue("InstalledComponents") == InstallService.ComponentText(selected), "登记未记录实际组件");
                    string icon = (string)entry.GetValue("DisplayIcon");
                    Check(icon.Contains(selected == InstallComponents.Cli ? Path.Combine(c.HelperDirectory,"uninstall.exe") : Product(c)), "图标登记指向未安装文件");
                }
                string savedState = File.ReadAllText(Path.Combine(c.InstallDirectory,".codex-switch-install.xml"));
                object savedPath = Raw(c,"Path");
                foreach (string point in new string[] { "files", "path", "registration" }) {
                    c.Fault = delegate(string p) { if (p == point) throw new IOException("组件失败"); };
                    Fails(delegate { service.Install(selected == InstallComponents.Cli ? InstallComponents.Desktop : InstallComponents.Cli); }, "组件失败");
                    Check(service.GetDefaultComponents() == selected && File.Exists(Product(c)) == ((selected & InstallComponents.Desktop) != 0), "组件变更未回滚");
                    Check(File.ReadAllText(Path.Combine(c.InstallDirectory,".codex-switch-install.xml")) == savedState && Object.Equals(Raw(c,"Path"),savedPath), "组件事务未恢复 state/PATH");
                    using (RegistryKey entry = Registry.CurrentUser.OpenSubKey(c.UninstallKey)) Check((string)entry.GetValue("InstalledComponents") == InstallService.ComponentText(selected), "组件登记未回滚");
                }
                c.Fault = null;
                service.Install(selected == InstallComponents.Cli ? InstallComponents.Desktop : InstallComponents.Cli);
                Check(File.Exists(Product(c)) == (selected == InstallComponents.Cli), "组件增删未生效");
                Check((Raw(c,"Path") == null) == (selected == InstallComponents.Cli), "移除 CLI 未清理 PATH");
                service.Uninstall();
                Check(!File.Exists(Product(c)) && Raw(c,"Path") == null, "组件卸载有残留");
            } finally { CleanRegistry(c); }
        }
        InstallConfiguration legacy = Configuration("legacy-state");
        try {
            legacy.Version = new Version("0.4.3.0");
            new InstallService(legacy).Install();
            string state = Path.Combine(legacy.InstallDirectory,".codex-switch-install.xml");
            var document = new System.Xml.XmlDocument(); document.Load(state); document.DocumentElement.RemoveAttribute("components"); document.Save(state);
            legacy.Version = new Version("0.4.4.0");
            new InstallService(legacy).Install();
            Check(new InstallService(legacy).GetDefaultComponents() == InstallComponents.All, "v0.4.3 旧记录升级丢失组件");
            new InstallService(legacy).Uninstall();
        } finally { CleanRegistry(legacy); }
    }
    public static int Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--hold") { Console.WriteLine("ready"); Thread.Sleep(30000); return 0; }
        if (args.Length == 8 && args[0] == "--worker") {
            try {
                InstallConfiguration c = new InstallConfiguration { InstallDirectory=args[1], ProgramsDirectory=args[2], HelperDirectory=args[3], UninstallKey=args[4], EnvironmentKey=args[5], LegacyKey=args[6], MutexName=args[7], CheckMachineLegacy=false, BroadcastEnvironment=false };
                InstallService service = new InstallService(c); service.Uninstall(); Console.WriteLine(service.RetainedHelperPath); return 0;
            } catch (Exception e) { Console.Error.WriteLine(e); return 1; }
        }
        scratch = args[0];
        try { Arguments(); Components(); ComponentOwnership(); LeafOwnershipMetadataPolicy(); Lifecycle(); OwnershipAndPath(); RollbackAndLocks(); RollbackPreservesIndependentFiles(); TransactionObjectOwnership(); LegacyAndUnsafe(); RealImagesAndConcurrency(); ShortcutAndRegistration(); CleanupFailureAfterCommit(); Console.WriteLine("PASS " + assertions + " assertions"); return 0; }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }
}

namespace CodexSwitch.Setup
{
    // Compile-time stand-in: the helper's production GUI is provided by SetupForm.cs.
    internal static class SetupForm { internal static int Run(InstallService service, bool uninstall, InstallComponents? components = null) { service.Uninstall(); return 0; } }
}
