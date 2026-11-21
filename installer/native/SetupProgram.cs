using System;
using System.Windows.Forms;

namespace CodexSwitch.Setup
{
    internal static class SetupProgram
    {
        [STAThread]
        internal static int Main(string[] args)
        {
            bool quiet = args.Length == 2 && args[1] == "--quiet";
            bool install = args.Length == 0 || args[0] == "install";
            bool uninstall = args.Length > 0 && args[0] == "uninstall";
            if (args.Length == 1 && (args[0] == "--help" || args[0] == "-h"))
            {
                Console.WriteLine("installer.exe [install|uninstall] [--quiet]");
                return 0;
            }
            if ((!install && !uninstall) || args.Length > 2 || (args.Length == 2 && !quiet))
            {
                Console.Error.WriteLine("参数无效。用法：installer.exe [install|uninstall] [--quiet]");
                return 2;
            }
            try
            {
                var service = new InstallService();
                if (!quiet)
                    return SetupForm.Run(service, uninstall);
                if (uninstall) service.Uninstall(); else service.Install();
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
