using System;
using System.IO;
using System.Windows.Forms;

namespace CodexSwitch
{
    internal static class Program
    {
        [STAThread]
        internal static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            try
            {
                string root = Environment.GetEnvironmentVariable("CODEX_HOME");
                if (String.IsNullOrWhiteSpace(root)) root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
                // Root selection is explicit and never interpreted as PowerShell or command text.
                if (args.Length == 2 && args[0] == "--root") root = args[1];
                else if (args.Length != 0) throw new ArgumentException();
                if (!Path.IsPathRooted(root)) throw new ArgumentException();
                root = Path.GetFullPath(root);
                using (var form = new MainForm(root)) Application.Run(form);
                return 0;
            }
            catch
            {
                MessageBox.Show("无法打开账号切换窗口，请检查本机目录设置和 Windows 组件。", "Codex 账号切换", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return 1;
            }
        }
    }
}
