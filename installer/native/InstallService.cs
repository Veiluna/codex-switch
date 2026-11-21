using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Xml;
using Microsoft.Win32;

namespace CodexSwitch.Setup
{
    // Only the in-assembly test harness supplies this configuration. No command-line or
    // registry value is ever used as a filesystem deletion root.
    internal sealed class InstallConfiguration
    {
        internal string InstallDirectory;
        internal string ProgramsDirectory;
        internal string HelperDirectory;
        internal string UninstallKey;
        internal string EnvironmentKey;
        internal string LegacyKey;
        internal string MutexName;
        internal Version Version;
        internal Func<string, byte[]> LoadPayload;
        internal Action<string, string> CreateShortcut;
        internal Action<string> Fault;
        internal bool CheckMachineLegacy;
        internal bool BroadcastEnvironment;

        internal static InstallConfiguration Default()
        {
            Assembly assembly = Assembly.GetExecutingAssembly();
            return new InstallConfiguration {
                InstallDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "CodexSwitch"),
                ProgramsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Programs),
                HelperDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexSwitch", "Setup"),
                UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\CodexSwitch.Native",
                EnvironmentKey = "Environment",
                LegacyKey = @"Software\CodexSwitch\Installer",
                MutexName = @"Local\CodexSwitch.Setup." + WindowsIdentity.GetCurrent().User.Value,
                Version = assembly.GetName().Version,
                LoadPayload = delegate(string name) {
                    using (Stream input = assembly.GetManifestResourceStream(name)) {
                        if (input == null) throw new InvalidOperationException("安装包缺少程序资源，请重新下载安装程序。");
                        using (MemoryStream output = new MemoryStream()) { input.CopyTo(output); return output.ToArray(); }
                    }
                },
                CreateShortcut = MakeShortcut,
                CheckMachineLegacy = true,
                BroadcastEnvironment = true
            };
        }

        private static void MakeShortcut(string path, string target)
        {
            object shell = null;
            object shortcut = null;
            try {
                shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell", true));
                dynamic automation = shell;
                shortcut = automation.CreateShortcut(path);
                dynamic link = shortcut;
                link.TargetPath = target;
                link.WorkingDirectory = Path.GetDirectoryName(target);
                link.Description = "Codex 账号切换";
                link.Save();
            } finally {
                if (shortcut != null && Marshal.IsComObject(shortcut)) Marshal.FinalReleaseComObject(shortcut);
                if (shell != null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
            }
        }
    }

    internal sealed class InstallService
    {
        private const string Owner = "CodexSwitch.NativeInstaller.1";
        private const string StateName = ".codex-switch-install.xml";
        private static readonly string[] PayloadNames = { "CodexSwitch.exe", "codex-switch.exe", "uninstall.exe" };
        private static readonly string[] RegistrationNames = { "NativeInstallerOwner", "DisplayName", "DisplayVersion", "Publisher", "InstallLocation", "UninstallString", "QuietUninstallString", "DisplayIcon", "NoModify", "NoRepair", "EstimatedSize" };
        private readonly InstallConfiguration configuration;
        internal string DirectoryPath { get { return configuration.InstallDirectory; } }
        // A running uninstall helper is renamed to this unique cache filename. Only
        // this obsolete instance may be removed after it exits, never a new install.
        internal string DeferredCleanupPath { get; private set; }
        internal InstallService() : this(InstallConfiguration.Default()) { }
        internal InstallService(InstallConfiguration configuration)
        {
            if (configuration == null) throw new ArgumentNullException("configuration");
            this.configuration = configuration;
        }

        internal void Install() { Execute(false); }
        internal void Uninstall() { Execute(true); }

        private void Execute(bool uninstall)
        {
            bool acquired = false;
            try {
                ValidateRoot(DirectoryPath);
                ValidateRoot(configuration.ProgramsDirectory);
                ValidateRoot(configuration.HelperDirectory);
                using (Mutex mutex = new Mutex(false, configuration.MutexName)) {
                    try { acquired = mutex.WaitOne(0); } catch (AbandonedMutexException) { acquired = true; }
                    if (!acquired) throw new InvalidOperationException("另一个安装或卸载操作正在进行，请等待完成后重试。");
                    try { ExecuteLocked(uninstall); }
                    finally { mutex.ReleaseMutex(); }
                }
            } catch (Exception error) {
                if (error is InstallFailure) throw;
                throw new InstallFailure((uninstall ? "卸载" : "安装") + "未完成：" + Explain(error), error);
            }
        }

        private void ExecuteLocked(bool uninstall)
        {
            CheckLegacy();
            string statePath = Path.Combine(DirectoryPath, StateName);
            byte[] stateBytes = File.Exists(statePath) ? File.ReadAllBytes(statePath) : null;
            InstallState oldState = stateBytes == null ? null : InstallState.Read(stateBytes);
            RegistrySnapshot registration = RegistrySnapshot.Read(configuration.UninstallKey, RegistrationNames);
            object registeredOwner = registration.Value("NativeInstallerOwner");
            if (registration.Exists && !Object.Equals(registeredOwner, Owner))
                throw new InvalidOperationException("卸载登记不属于本安装程序，请先核对已有安装。");
            if (oldState == null && registration.Exists)
                throw new InvalidOperationException("安装归属记录缺失，已停止操作以保护现有程序，请保留文件并重新下载安装程序。");
            if (uninstall && oldState == null) return;
            if (!uninstall && oldState != null && oldState.Version.CompareTo(configuration.Version) > 0)
                throw new InvalidOperationException("已安装较新版本，请使用同版或更新的安装程序。");

            string shortcutDirectory = Path.Combine(configuration.ProgramsDirectory, "Codex Switch");
            string shortcutPath = Path.Combine(shortcutDirectory, "Codex Switch.lnk");
            ValidateRoot(shortcutDirectory);
            PathValue pathBefore = PathValue.Read(configuration.EnvironmentKey);
            InstallState next = uninstall ? null : new InstallState();
            Dictionary<string, byte[]> payload = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            if (!uninstall) {
                next.Version = configuration.Version;
                next.PathOwned = oldState != null && oldState.PathOwned;
                next.PathBefore = oldState != null ? oldState.PathBefore : pathBefore;
                next.PathAfter = oldState != null ? oldState.PathAfter : null;
                foreach (string name in PayloadNames) {
                    byte[] bytes = configuration.LoadPayload("Payload." + name);
                    if (bytes == null || bytes.Length == 0) throw new InvalidOperationException("安装包包含空程序资源，请重新下载。");
                    payload.Add(name, bytes);
                    next.Hashes.Add(name, Hash(bytes));
                }
            }

            bool installDirectoryExisted = Directory.Exists(DirectoryPath);
            bool shortcutDirectoryExisted = Directory.Exists(shortcutDirectory);
            bool helperDirectoryExisted = Directory.Exists(configuration.HelperDirectory);
            PathValue pathWritten = null;
            RegistrySnapshot registrationWritten = null;
            bool registryChanged = false;
            using (FileTransaction files = new FileTransaction()) {
                try {
                    foreach (string name in PayloadNames) {
                        string target = PayloadTarget(name);
                        bool runningHelper = uninstall && name == "uninstall.exe" && String.Equals(Assembly.GetExecutingAssembly().Location, target, StringComparison.OrdinalIgnoreCase);
                        files.Acquire(target, oldState == null ? null : oldState.Hashes[name], runningHelper);
                    }
                    files.Acquire(statePath, stateBytes == null ? null : Hash(stateBytes));
                    files.Acquire(shortcutPath, oldState == null ? null : oldState.ShortcutHash);

                    if (!uninstall) {
                        Directory.CreateDirectory(DirectoryPath);
                        Directory.CreateDirectory(shortcutDirectory);
                        Directory.CreateDirectory(configuration.HelperDirectory);
                        foreach (string name in PayloadNames) files.Replace(PayloadTarget(name), payload[name]);
                        // Build the shortcut beside its final name, preserving .lnk for WSH.
                        string stagedLink = Path.Combine(shortcutDirectory, ".cs-" + Guid.NewGuid().ToString("N") + ".lnk");
                        try {
                            configuration.CreateShortcut(stagedLink, Path.Combine(DirectoryPath, "CodexSwitch.exe"));
                            next.ShortcutHash = Hash(File.ReadAllBytes(stagedLink));
                            files.Replace(shortcutPath, File.ReadAllBytes(stagedLink));
                        } finally { if (File.Exists(stagedLink)) File.Delete(stagedLink); }
                        PathValue pathAfter = pathBefore;
                        if (!ContainsPath(pathBefore.Text, DirectoryPath)) {
                            pathAfter = pathBefore.Append(DirectoryPath);
                            next.PathOwned = true;
                            next.PathBefore = pathBefore;
                            next.PathAfter = pathAfter.Text;
                        }
                        files.Replace(statePath, next.Write());
                        Fault("files");
                        if (!pathBefore.Same(pathAfter)) { pathWritten = pathAfter; WritePathChecked(pathBefore, pathAfter); }
                        Fault("path");
                        registryChanged = true;
                        WriteRegistration(next, payload);
                        registrationWritten = RegistrySnapshot.Read(configuration.UninstallKey, RegistrationNames);
                        Fault("registration");
                    } else {
                        foreach (string name in PayloadNames) files.Remove(PayloadTarget(name));
                        files.Remove(shortcutPath);
                        files.Remove(statePath);
                        Fault("files");
                        PathValue after = RemoveOwnedPath(pathBefore, oldState);
                        if (!pathBefore.Same(after)) { pathWritten = after; WritePathChecked(pathBefore, after); }
                        Fault("path");
                        registryChanged = true;
                        DeleteRegistration();
                        registrationWritten = RegistrySnapshot.Read(configuration.UninstallKey, RegistrationNames);
                        Fault("registration");
                    }
                    files.Commit(configuration.Fault);
                    DeferredCleanupPath = files.DeferredCleanupPath;
                } catch (Exception failure) {
                    List<string> recovery = new List<string>();
                    if (registryChanged) {
                        try {
                            RegistrySnapshot current = RegistrySnapshot.Read(configuration.UninstallKey, RegistrationNames);
                            // An exception while writing registration still restores only the known fields.
                            if (registrationWritten == null || current.Same(registrationWritten)) registration.Restore(configuration.UninstallKey, RegistrationNames);
                            else recovery.Add("卸载登记已被其他程序更改");
                        } catch { recovery.Add("卸载登记恢复失败"); }
                    }
                    if (pathWritten != null) {
                        try {
                            if (PathValue.Read(configuration.EnvironmentKey).Same(pathWritten)) pathBefore.Write(configuration.EnvironmentKey);
                            else if (!PathValue.Read(configuration.EnvironmentKey).Same(pathBefore)) recovery.Add("PATH 已被其他程序更改");
                        } catch { recovery.Add("PATH 恢复失败"); }
                    }
                    try { files.Rollback(); } catch { recovery.Add("程序文件恢复失败，请保留安装目录中的 .cs-backup 文件"); }
                    if (!installDirectoryExisted) TryRemoveEmpty(DirectoryPath);
                    if (!shortcutDirectoryExisted) TryRemoveEmpty(shortcutDirectory);
                    if (!helperDirectoryExisted) TryRemoveEmpty(configuration.HelperDirectory);
                    if (recovery.Count != 0) throw new InvalidOperationException(Explain(failure) + "；" + String.Join("；", recovery.ToArray()) + "。请勿删除备份后重试。", failure);
                    throw;
                }
            }
            if (uninstall) { TryRemoveEmpty(DirectoryPath); TryRemoveEmpty(shortcutDirectory); TryRemoveEmpty(configuration.HelperDirectory); }
            if (configuration.BroadcastEnvironment) NotifyEnvironment();
        }

        private void Fault(string point) { if (configuration.Fault != null) configuration.Fault(point); }
        private string PayloadTarget(string name) { return Path.Combine(name == "uninstall.exe" ? configuration.HelperDirectory : DirectoryPath, name); }

        private static string Explain(Exception error)
        {
            if (error is PathTooLongException) return "安装路径过长，请缩短当前用户的配置路径后重试。";
            if (error is UnauthorizedAccessException) return "没有写入权限，或程序正在使用文件；请关闭 Codex Switch 和相关终端后重试。";
            if (error is IOException && !(error is InstallFileException)) return "文件正在使用或磁盘写入失败，请关闭 Codex Switch 和相关终端，检查磁盘后重试。" + error.Message;
            return error.Message;
        }

        private void CheckLegacy()
        {
            using (RegistryKey legacy = Registry.CurrentUser.OpenSubKey(configuration.LegacyKey)) {
                if (legacy != null && legacy.GetValue("InstallDirectory", null, RegistryValueOptions.DoNotExpandEnvironmentNames) != null) LegacyFound();
            }
            if (!configuration.CheckMachineLegacy) return;
            foreach (RegistryHive hive in new RegistryHive[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
                foreach (RegistryView view in new RegistryView[] { RegistryView.Registry32, RegistryView.Registry64 })
                    using (RegistryKey root = RegistryKey.OpenBaseKey(hive, view))
                    using (RegistryKey uninstall = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall")) {
                        if (uninstall == null) continue;
                        foreach (string name in uninstall.GetSubKeyNames())
                            using (RegistryKey item = uninstall.OpenSubKey(name)) {
                                if (item == null) continue;
                                string display = item.GetValue("DisplayName") as string;
                                object windowsInstaller = item.GetValue("WindowsInstaller");
                                if (String.Equals(display, "Codex Switch", StringComparison.OrdinalIgnoreCase) && windowsInstaller is int && (int)windowsInstaller == 1) LegacyFound();
                            }
                    }
        }

        private static void LegacyFound()
        {
            throw new InvalidOperationException("检测到旧版 MSI 安装。请先在 Windows 设置 → 应用 → 已安装的应用中卸载旧版 Codex Switch，再重新运行本安装程序。账号存档会保留；不要直接覆盖旧版目录。");
        }

        private void WriteRegistration(InstallState state, Dictionary<string, byte[]> payload)
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(configuration.UninstallKey)) {
                key.SetValue("NativeInstallerOwner", Owner, RegistryValueKind.String);
                key.SetValue("DisplayName", "Codex Switch", RegistryValueKind.String);
                key.SetValue("DisplayVersion", state.Version.ToString(3), RegistryValueKind.String);
                key.SetValue("Publisher", "Codex Switch contributors", RegistryValueKind.String);
                key.SetValue("InstallLocation", DirectoryPath, RegistryValueKind.String);
                string helper = "\"" + PayloadTarget("uninstall.exe") + "\"";
                key.SetValue("UninstallString", helper, RegistryValueKind.String);
                key.SetValue("QuietUninstallString", helper + " --quiet", RegistryValueKind.String);
                key.SetValue("DisplayIcon", "\"" + Path.Combine(DirectoryPath, "CodexSwitch.exe") + "\",0", RegistryValueKind.String);
                key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                int total = 0; foreach (byte[] bytes in payload.Values) total += bytes.Length;
                key.SetValue("EstimatedSize", (total + 1023) / 1024, RegistryValueKind.DWord);
            }
        }

        private void DeleteRegistration()
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(configuration.UninstallKey, true))
                if (key != null) foreach (string name in RegistrationNames) key.DeleteValue(name, false);
            TryDeleteEmptyKey(configuration.UninstallKey);
        }

        private void WritePathChecked(PathValue expected, PathValue next)
        {
            if (!PathValue.Read(configuration.EnvironmentKey).Same(expected)) throw new InvalidOperationException("用户 PATH 正在被其他程序修改，请稍后重试。");
            next.Write(configuration.EnvironmentKey);
        }

        private PathValue RemoveOwnedPath(PathValue current, InstallState state)
        {
            if (!state.PathOwned || !current.Exists) return current;
            if (String.Equals(current.Text, state.PathAfter, StringComparison.Ordinal) && current.Kind == state.PathBefore.EffectiveKind) return state.PathBefore;
            string[] entries = current.Text.Split(';');
            int count = 0;
            int index = -1;
            for (int i = 0; i < entries.Length; i++) if (SamePath(entries[i], DirectoryPath)) { count++; if (String.Equals(entries[i], DirectoryPath, StringComparison.Ordinal)) index = i; }
            // If the user duplicated or reformatted the entry, ownership is ambiguous: preserve it.
            if (count != 1 || index < 0) return current;
            List<string> kept = new List<string>(entries); kept.RemoveAt(index);
            return new PathValue(true, String.Join(";", kept.ToArray()), current.Kind);
        }

        private static bool ContainsPath(string value, string directory)
        {
            foreach (string entry in value.Split(';')) if (SamePath(entry, directory)) return true;
            return false;
        }

        private static bool SamePath(string entry, string directory)
        {
            string candidate = Environment.ExpandEnvironmentVariables(entry.Trim().Trim('"')).TrimEnd('\\', '/');
            try { return String.Equals(Path.GetFullPath(candidate), directory.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }

        internal static void ValidateRoot(string directory)
        {
            if (String.IsNullOrWhiteSpace(directory) || directory.Length > 190 || directory.IndexOfAny(new char[] { ';', '"', '\r', '\n' }) >= 0)
                throw new InvalidOperationException("安装路径无效或过长，请检查当前用户的文件夹路径。");
            string full = Path.GetFullPath(directory);
            if (!Path.IsPathRooted(directory) || full.StartsWith(@"\\", StringComparison.Ordinal) || !String.Equals(full.TrimEnd('\\'), directory.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) || full.Length < 4)
                throw new InvalidOperationException("安装路径必须是当前用户的本地绝对路径。");
            string scan = full;
            while (!String.IsNullOrEmpty(scan)) {
                if ((Directory.Exists(scan) || File.Exists(scan)) && (File.GetAttributes(scan) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("安装路径包含链接或重解析点，已停止操作以保护其他文件。");
                scan = Path.GetDirectoryName(scan);
            }
        }

        private static string Hash(byte[] bytes) { using (SHA256 hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", ""); }
        private static string Hash(Stream stream) { stream.Position = 0; using (SHA256 hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", ""); }
        private static void TryRemoveEmpty(string directory) { try { if (Directory.Exists(directory)) Directory.Delete(directory, false); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        private static void TryDeleteEmptyKey(string path)
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(path)) { if (key == null || key.ValueCount != 0 || key.SubKeyCount != 0) return; }
            Registry.CurrentUser.DeleteSubKey(path, false);
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr SendMessageTimeout(IntPtr window, uint message, UIntPtr wparam, string lparam, uint flags, uint timeout, out UIntPtr result);
        private static void NotifyEnvironment() { UIntPtr result; SendMessageTimeout(new IntPtr(0xffff), 0x1a, UIntPtr.Zero, "Environment", 2, 1000, out result); }

        internal sealed class InstallFailure : Exception { internal InstallFailure(string message, Exception inner) : base(message, inner) { } }
        private sealed class InstallFileException : IOException { internal InstallFileException(string message) : base(message) { } }

        private sealed class PathValue
        {
            internal bool Exists;
            internal string Text;
            internal RegistryValueKind Kind;
            internal RegistryValueKind EffectiveKind { get { return Exists ? Kind : RegistryValueKind.ExpandString; } }
            internal PathValue(bool exists, string text, RegistryValueKind kind) { Exists = exists; Text = text; Kind = kind; }
            internal static PathValue Read(string path)
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(path)) {
                    object value = key == null ? null : key.GetValue("Path", null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                    if (value == null) return new PathValue(false, "", RegistryValueKind.ExpandString);
                    RegistryValueKind kind = key.GetValueKind("Path");
                    if (!(value is string) || (kind != RegistryValueKind.String && kind != RegistryValueKind.ExpandString)) throw new InvalidOperationException("用户 PATH 的注册表类型异常，已停止操作以保护原值。");
                    return new PathValue(true, (string)value, kind);
                }
            }
            internal PathValue Append(string entry) { return new PathValue(true, Text + (Text.Length == 0 || Text.EndsWith(";", StringComparison.Ordinal) ? "" : ";") + entry, EffectiveKind); }
            internal bool Same(PathValue other) { return Exists == other.Exists && (!Exists || (Text == other.Text && Kind == other.Kind)); }
            internal void Write(string path)
            {
                if (Exists) { using (RegistryKey key = Registry.CurrentUser.CreateSubKey(path)) key.SetValue("Path", Text, Kind); }
                else { using (RegistryKey key = Registry.CurrentUser.OpenSubKey(path, true)) if (key != null) key.DeleteValue("Path", false); }
            }
        }

        private sealed class RegistrySnapshot
        {
            internal bool Exists;
            private readonly Dictionary<string, object> values = new Dictionary<string, object>();
            private readonly Dictionary<string, RegistryValueKind> kinds = new Dictionary<string, RegistryValueKind>();
            internal object Value(string name) { object value; return values.TryGetValue(name, out value) ? value : null; }
            internal static RegistrySnapshot Read(string path, string[] names)
            {
                RegistrySnapshot snapshot = new RegistrySnapshot();
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(path)) {
                    if (key == null) return snapshot;
                    snapshot.Exists = true;
                    foreach (string name in names) {
                        object value = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                        if (value != null) { snapshot.values.Add(name, value); snapshot.kinds.Add(name, key.GetValueKind(name)); }
                    }
                }
                return snapshot;
            }
            internal bool Same(RegistrySnapshot other)
            {
                if (Exists != other.Exists || values.Count != other.values.Count) return false;
                foreach (string name in values.Keys) if (!Object.Equals(Value(name), other.Value(name)) || kinds[name] != other.kinds[name]) return false;
                return true;
            }
            internal void Restore(string path, string[] names)
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(path)) {
                    foreach (string name in names) { if (values.ContainsKey(name)) key.SetValue(name, values[name], kinds[name]); else key.DeleteValue(name, false); }
                }
                if (!Exists) TryDeleteEmptyKey(path);
            }
        }

        private sealed class InstallState
        {
            internal Version Version;
            internal Dictionary<string, string> Hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            internal string ShortcutHash;
            internal bool PathOwned;
            internal PathValue PathBefore;
            internal string PathAfter;
            internal byte[] Write()
            {
                XmlDocument document = new XmlDocument();
                XmlElement root = document.CreateElement("installation"); document.AppendChild(root);
                root.SetAttribute("owner", Owner); root.SetAttribute("version", Version.ToString()); root.SetAttribute("shortcut", ShortcutHash);
                foreach (string name in PayloadNames) { XmlElement file = document.CreateElement("file"); file.SetAttribute("name", name); file.SetAttribute("sha256", Hashes[name]); root.AppendChild(file); }
                XmlElement path = document.CreateElement("path"); root.AppendChild(path);
                path.SetAttribute("owned", PathOwned ? "true" : "false"); path.SetAttribute("existed", PathBefore.Exists ? "true" : "false"); path.SetAttribute("kind", ((int)PathBefore.Kind).ToString());
                path.SetAttribute("before", PathBefore.Text); path.SetAttribute("after", PathAfter ?? "");
                using (MemoryStream output = new MemoryStream()) { document.Save(output); return output.ToArray(); }
            }
            internal static InstallState Read(byte[] bytes)
            {
                try {
                    if (bytes.Length > 1024 * 1024) throw new InvalidDataException();
                    XmlReaderSettings settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
                    XmlDocument document = new XmlDocument(); document.XmlResolver = null;
                    using (MemoryStream input = new MemoryStream(bytes)) using (XmlReader reader = XmlReader.Create(input, settings)) document.Load(reader);
                    XmlElement root = document.DocumentElement;
                    if (root == null || root.Name != "installation" || root.GetAttribute("owner") != Owner) throw new InvalidDataException();
                    InstallState state = new InstallState();
                    state.Version = new Version(root.GetAttribute("version"));
                    state.ShortcutHash = root.GetAttribute("shortcut"); ValidateHash(state.ShortcutHash);
                    foreach (XmlNode node in root.SelectNodes("file")) {
                        XmlElement file = (XmlElement)node; string name = file.GetAttribute("name"); string sha = file.GetAttribute("sha256");
                        if (Array.IndexOf(PayloadNames, name) < 0 || state.Hashes.ContainsKey(name)) throw new InvalidDataException();
                        ValidateHash(sha); state.Hashes.Add(name, sha);
                    }
                    if (state.Hashes.Count != PayloadNames.Length) throw new InvalidDataException();
                    XmlElement path = (XmlElement)root.SelectSingleNode("path");
                    if (path == null) throw new InvalidDataException();
                    state.PathOwned = Boolean.Parse(path.GetAttribute("owned"));
                    RegistryValueKind kind = (RegistryValueKind)Int32.Parse(path.GetAttribute("kind"));
                    if (kind != RegistryValueKind.String && kind != RegistryValueKind.ExpandString) throw new InvalidDataException();
                    state.PathBefore = new PathValue(Boolean.Parse(path.GetAttribute("existed")), path.GetAttribute("before"), kind);
                    state.PathAfter = path.GetAttribute("after");
                    return state;
                } catch (Exception error) { throw new InvalidOperationException("安装归属记录损坏，已停止操作以保护现有文件。请保留安装目录并重新下载安装程序。", error); }
            }
            private static void ValidateHash(string sha)
            {
                if (sha == null || sha.Length != 64) throw new InvalidDataException();
                foreach (char character in sha) if (!((character >= '0' && character <= '9') || (character >= 'A' && character <= 'F'))) throw new InvalidDataException();
            }
        }

        private sealed class FileTransaction : IDisposable
        {
            private sealed class Entry
            {
                internal string Target;
                internal string Backup;
                internal FileStream OldLock;
                internal FileStream NewLock;
                internal bool Changed;
                internal bool HadFile;
                internal bool RunningHelper;
            }
            private readonly List<Entry> entries = new List<Entry>();
            private bool completed;
            internal string DeferredCleanupPath { get; private set; }
            internal void Acquire(string target, string expectedHash) { Acquire(target, expectedHash, false); }
            internal void Acquire(string target, string expectedHash, bool runningHelper)
            {
                ValidateRoot(Path.GetDirectoryName(target));
                if (Directory.Exists(target)) throw new InstallFileException("目标位置存在不属于本程序的文件夹：" + Path.GetFileName(target));
                Entry entry = new Entry { Target = target, HadFile = File.Exists(target), RunningHelper = runningHelper };
                entries.Add(entry);
                if (!entry.HadFile) return;
                if ((File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0) throw new InstallFileException("目标文件是链接，已停止操作以保护其他文件。");
                if (expectedHash == null) throw new InstallFileException("目标文件不属于本安装程序，已停止操作：" + Path.GetFileName(target));
                try { entry.OldLock = new FileStream(target, FileMode.Open, runningHelper ? FileAccess.Read : FileAccess.ReadWrite, runningHelper ? FileShare.Read | FileShare.Delete : FileShare.Delete); }
                catch (IOException) { throw new InstallFileException("程序文件正在使用，请关闭 Codex Switch 和相关终端后重试。"); }
                if (Hash(entry.OldLock) != expectedHash) throw new InstallFileException("已安装文件已被修改，已停止操作以保护文件：" + Path.GetFileName(target));
            }
            private Entry Find(string target) { foreach (Entry entry in entries) if (entry.Target == target) return entry; throw new InvalidOperationException("未锁定的安装目标。"); }
            internal void Replace(string target, byte[] bytes)
            {
                Entry entry = Find(target);
                string staged = target + ".cs-new-" + Guid.NewGuid().ToString("N");
                try {
                    using (FileStream stream = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
                    // Hold the new image before making it visible, so another process cannot
                    // start it halfway through an operation and prevent a rollback.
                    entry.NewLock = new FileStream(staged, FileMode.Open, FileAccess.ReadWrite, FileShare.Delete);
                    MoveOld(entry);
                    File.Move(staged, target);
                    entry.Changed = true;
                } finally {
                    if (File.Exists(staged)) { if (entry.NewLock != null) { entry.NewLock.Dispose(); entry.NewLock = null; } File.Delete(staged); }
                }
            }
            internal void Remove(string target) { Entry entry = Find(target); MoveOld(entry); }
            private static void MoveOld(Entry entry)
            {
                if (!entry.HadFile) return;
                entry.Backup = entry.Target + ".cs-backup-" + Guid.NewGuid().ToString("N");
                File.Move(entry.Target, entry.Backup);
                entry.Changed = true;
            }
            internal void Commit(Action<string> fault)
            {
                // No destructive cleanup occurs until all replacements and registry writes succeeded.
                completed = true;
                foreach (Entry entry in entries) {
                    try { if (entry.OldLock != null) entry.OldLock.Dispose(); } catch (IOException) { } finally { entry.OldLock = null; }
                    try { if (entry.NewLock != null) entry.NewLock.Dispose(); } catch (IOException) { } finally { entry.NewLock = null; }
                    if (entry.Backup != null && File.Exists(entry.Backup)) {
                        if (entry.RunningHelper) DeferredCleanupPath = entry.Backup;
                        else {
                            // Cleanup is after the commit point. Keep this unique obsolete
                            // backup on failure; never undo the committed PATH/registration.
                            try { if (fault != null) fault("cleanup"); File.Delete(entry.Backup); }
                            catch (Exception) { }
                        }
                    }
                }
            }
            internal void Rollback()
            {
                if (completed) return;
                Exception problem = null;
                for (int i = entries.Count - 1; i >= 0; i--) {
                    Entry entry = entries[i];
                    try {
                        if (entry.Changed) {
                            if (entry.NewLock != null) { entry.NewLock.Dispose(); entry.NewLock = null; }
                            if (File.Exists(entry.Target)) File.Delete(entry.Target);
                            if (entry.Backup != null && File.Exists(entry.Backup)) File.Move(entry.Backup, entry.Target);
                        }
                    } catch (Exception error) { problem = error; }
                }
                completed = true;
                if (problem != null) throw problem;
            }
            public void Dispose()
            {
                foreach (Entry entry in entries) { if (entry.OldLock != null) entry.OldLock.Dispose(); if (entry.NewLock != null) entry.NewLock.Dispose(); }
            }
        }
    }
}
