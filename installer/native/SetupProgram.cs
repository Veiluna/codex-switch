using System;
using System.IO;
using System.Windows.Forms;

namespace CodexSwitch.Setup
{
    internal static class SetupProgram
    {
        private static string Usage { get { return Path.GetFileName(Application.ExecutablePath) + " [install [--quiet] [--components desktop|cli|desktop,cli] | uninstall [--quiet]]"; } }
        internal static bool TryParseArguments(string[] args, out bool uninstall, out bool quiet, out bool help, out InstallComponents? components)
        {
            uninstall = false; quiet = false; help = false; components = null;
            if (args.Length == 0) return true;
            if (args.Length == 1 && (args[0] == "--help" || args[0] == "-h")) { help = true; return true; }
            uninstall = args[0] == "uninstall";
            if (!uninstall && args[0] != "install") return false;
            for (int i = 1; i < args.Length; i++) {
                if (args[i] == "--quiet" && !quiet) quiet = true;
                else if (args[i] == "--components" && !uninstall && !components.HasValue && i + 1 < args.Length) {
                    try { components = InstallService.ParseComponents(args[++i]); }
                    catch (InvalidOperationException) { return false; }
                } else return false;
            }
            return true;
        }
        [STAThread]
        internal static int Main(string[] args)
        {
            bool uninstall, quiet, help;
            InstallComponents? components;
            if (!TryParseArguments(args, out uninstall, out quiet, out help, out components)) {
                Console.Error.WriteLine("参数无效。用法：" + Usage);
                return 2;
            }
            if (help) { Console.WriteLine(Usage); return 0; }
            try
            {
                var service = new InstallService();
                if (!quiet)
                    return SetupForm.Run(service, uninstall, components);
                if (uninstall) service.Uninstall(); else if (components.HasValue) service.Install(components.Value); else service.Install();
                Console.WriteLine(uninstall ? "Codex Switch 已卸载。" : "Codex Switch 已安装。");
                return 0;
            }
            catch (Exception ex)
            {
                if (quiet) Console.Error.WriteLine(ex.Message);
                else MessageBox.Show(ex.Message, "Codex Switch 安装程序", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }
    }
}
