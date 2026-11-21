using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Forms;

namespace CodexSwitch.Setup
{
    // Installed separately from the product so CLI callers wait for the actual
    // uninstall. The only deferred work is deleting this already-retired image.
    internal static class UninstallHost
    {
        [STAThread]
        private static int Main(string[] args)
        {
            bool quiet = args.Length == 1 && args[0] == "--quiet";
            if (args.Length != 0 && !quiet) return 2;
            InstallConfiguration configuration = InstallConfiguration.Default();
            InstallService service = new InstallService(configuration);
            int result;
            try {
                if (quiet) { service.Uninstall(); result = 0; }
                else result = SetupForm.Run(service, true);
            } catch (Exception error) {
                if (!quiet) MessageBox.Show(error.Message, "Codex Switch 卸载未完成", MessageBoxButtons.OK, MessageBoxIcon.Error);
                result = 1;
            }
            if (result == 0 && service.DeferredCleanupPath != null) {
                // Cleanup launch failure does not misreport the completed product uninstall.
                // A uniquely named obsolete cache file may remain for manual removal.
                try { ScheduleCleanup(configuration, service.DeferredCleanupPath); } catch { }
            }
            return result;
        }

        internal static void ScheduleCleanup(InstallConfiguration configuration, string retiredPath)
        {
            string script = CleanupScript(configuration, retiredPath, Process.GetCurrentProcess().Id);
            string powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
            ProcessStartInfo start = new ProcessStartInfo(powershell);
            start.Arguments = "-NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            start.UseShellExecute = false;
            start.CreateNoWindow = true;
            start.WindowStyle = ProcessWindowStyle.Hidden;
            using (Process cleanup = Process.Start(start)) { }
        }

        // Only an in-memory path returned by this operation is accepted. There is no
        // cleanup command-line switch, registry deletion root, wildcard, or recursion.
        internal static string CleanupScript(InstallConfiguration configuration, string retiredPath, int parentId)
        {
            InstallService.ValidateRoot(configuration.HelperDirectory);
            string prefix = Path.Combine(configuration.HelperDirectory, "uninstall.exe.cs-backup-");
            Guid id;
            if (retiredPath == null || !retiredPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || retiredPath.Length != prefix.Length + 32 || !Guid.TryParseExact(retiredPath.Substring(prefix.Length), "N", out id))
                throw new InvalidOperationException("卸载缓存路径不属于本次操作。");
            if ((File.GetAttributes(retiredPath) & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("卸载缓存不能是链接。");
            string sha;
            using (SHA256 hash = SHA256.Create())
            using (FileStream input = new FileStream(retiredPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
                sha = BitConverter.ToString(hash.ComputeHash(input)).Replace("-", "");
            string file = Quote(retiredPath);
            string directory = Quote(configuration.HelperDirectory);
            return "$ErrorActionPreference='Stop';try { " +
                "$parent=$null;try{$parent=[Diagnostics.Process]::GetProcessById(" + parentId + ")}catch{};" +
                "if($null -ne $parent){if(-not $parent.WaitForExit(120000)){exit}};" +
                "$file=" + file + ";$directory=" + directory + ";" +
                "if(([IO.File]::GetAttributes($directory) -band [IO.FileAttributes]::ReparsePoint) -ne 0){exit};" +
                "if(([IO.File]::GetAttributes($file) -band [IO.FileAttributes]::ReparsePoint) -ne 0){exit};" +
                "$input=[IO.File]::Open($file,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Delete);" +
                "$hash=[Security.Cryptography.SHA256]::Create();try{$actual=[BitConverter]::ToString($hash.ComputeHash($input)).Replace('-','');" +
                "if($actual -ceq '" + sha + "'){[IO.File]::Delete($file)}}finally{$input.Dispose();$hash.Dispose()};" +
                "try{[IO.Directory]::Delete($directory,$false)}catch{}" +
                "}catch{};exit 0";
        }

        private static string Quote(string value) { return "'" + value.Replace("'", "''") + "'"; }
    }
}
