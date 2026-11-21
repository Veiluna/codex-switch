using System;
using System.Windows.Forms;

namespace CodexSwitch.Setup
{
    // Installed separately from the product so CLI callers wait for the actual
    // uninstall. Its retired cache image is retained; no background cleanup runs.
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
            return result;
        }
    }
}
