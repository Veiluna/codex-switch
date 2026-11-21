using System;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;

// The production InstallService is deliberately excluded from this executable.
namespace CodexSwitch.Setup
{
    [Flags] internal enum InstallComponents { Desktop = 1, Cli = 2, All = 3 }
    internal sealed class InstallService
    {
        internal string DirectoryPath { get; set; }
        internal int Installs, Uninstalls;
        internal Exception Failure;
        internal ManualResetEvent Release;
        internal InstallComponents DefaultComponents = InstallComponents.All;
        internal InstallComponents Selected;
        internal InstallComponents GetDefaultComponents() { return DefaultComponents; }
        internal void Install(InstallComponents components) { Selected = components; Install(); }
        internal void Install() { Interlocked.Increment(ref Installs); Work(); }
        internal void Uninstall() { Interlocked.Increment(ref Uninstalls); Work(); }
        private void Work() { if (Release != null) Release.WaitOne(8000); if (Failure != null) throw Failure; }
    }
    internal static class SetupUiTests
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr window, IntPtr dc, uint flags);
        private static int checks;
        private static string evidence;
        private static void Check(bool result, string message) { if (!result) throw new Exception(message); checks++; }
        private static void Pump(Func<bool> ready)
        {
            DateTime end = DateTime.UtcNow.AddSeconds(10);
            while (!ready() && DateTime.UtcNow < end) { Application.DoEvents(); Thread.Sleep(10); }
            Check(ready(), "等待 UI 状态超时");
        }
        private static T Find<T>(Form form, string name) where T : Control { var found = form.Controls.Find(name,true); if(found.Length == 0) throw new Exception("Missing " + name + " on " + form.Name + "; children " + form.Controls.Count); return (T)found[0]; }
        private static void Capture(Form form, string name)
        {
            Application.DoEvents();
            using (var bitmap = new Bitmap(form.Width, form.Height))
            {
                using(var graphics = Graphics.FromImage(bitmap)) { IntPtr dc = graphics.GetHdc(); try { Check(PrintWindow(form.Handle,dc,0), "PrintWindow 窗口截取失败"); } finally { graphics.ReleaseHdc(dc); } }
                bitmap.Save(Path.Combine(evidence, name + ".png"));
            }
        }
        private static void Key(Form form, Keys key)
        {
            typeof(Form).GetMethod("ProcessDialogKey", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Invoke(form, new object[] { key });
            Application.DoEvents();
        }
        private static InstallService Service() { return new InstallService { DirectoryPath = @"C:\Synthetic User\AppData\Local\Programs\Codex Switch" }; }
        [STAThread]
        private static int Main(string[] args)
        {
            try
            {
                evidence = args[0]; Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
                Control.CheckForIllegalCrossThreadCalls = true;
                if(args.Length > 1)
                {
                var runFixture = Service();
                EventHandler cancelRun = null;
                cancelRun = delegate {
                    if(Application.OpenForms.Count == 0) return;
                    Application.Idle -= cancelRun;
                    Find<Button>(Application.OpenForms[0],"CancelButton").PerformClick();
                };
                Application.Idle += cancelRun;
                Check(SetupForm.Run(runFixture,false) == 3 && runFixture.Installs == 0, "真实消息循环取消返回值错误");
                    Console.WriteLine("Setup UI message-loop cancellation passed.");
                    return 0;
                }
                var cancel = Service();
                using (var form = new SetupForm(cancel, false))
                {
                    form.Show(); Application.DoEvents();
                    Check(Find<TextBox>(form,"InstallDirectory").ReadOnly && Find<TextBox>(form,"InstallDirectory").Text == cancel.DirectoryPath, "实际目录必须可复制且只读");
                    var desktop = Find<CheckBox>(form,"DesktopComponent");
                    var cli = Find<CheckBox>(form,"CliComponent");
                    Check(desktop.Checked && cli.Checked && desktop.TabStop && cli.TabStop, "首次组件必须全选且键盘可达");
                    desktop.Checked = false; cli.Checked = false;
                    Check(!Find<Button>(form,"ActionButton").Enabled && Find<RichTextBox>(form,"Details").Text.Contains("至少选择"), "空选未阻止提交并解释原因");
                    cli.Checked = true;
                    Check(Find<Button>(form,"ActionButton").Enabled, "选择组件后未恢复提交");
                    desktop.Checked = true;
                    desktop.Focus(); form.SelectNextControl(desktop,true,true,true,true);
                    Check(form.ActiveControl == cli, "桌面组件后 Tab 未到 CLI");
                    form.SelectNextControl(cli,true,true,true,true);
                    Check(form.ActiveControl == Find<TextBox>(form,"InstallDirectory"), "CLI 组件后 Tab 未到路径");
                    var path = Find<TextBox>(form,"InstallDirectory");
                    path.SelectAll(); Check(path.SelectedText == cancel.DirectoryPath && path.ShortcutsEnabled,"目录不可完整选择复制");
                    path.Focus(); form.SelectNextControl(path,true,true,true,true);
                    Check(form.ActiveControl == Find<Button>(form,"CancelButton"), "目录后 Tab 顺序应为取消");
                    form.SelectNextControl(form.ActiveControl,true,true,true,true);
                    Check(form.ActiveControl == Find<Button>(form,"ActionButton"), "取消后 Tab 顺序应为安装");
                    Capture(form,"install-ready");
                    form.Size = form.MinimumSize; Application.DoEvents();
                    Check(Find<Button>(form,"ActionButton").Parent.ClientRectangle.Contains(Find<Button>(form,"ActionButton").Bounds), "最小尺寸安装按钮越界");
                    Capture(form,"install-minimum"); Key(form,Keys.Escape);
                    Check(!form.Visible && cancel.Installs == 0 && form.ResultCode == 3, "Esc 取消产生了安装副作用");
                }
                var uninstall = Service();
                using (var form = new SetupForm(uninstall,true))
                {
                    form.Show(); Application.DoEvents();
                    Check(form.Controls.Find("DesktopComponent",true).Length == 0 && form.Controls.Find("CliComponent",true).Length == 0, "卸载不得显示组件选择");
                    Check(form.ActiveControl == Find<Button>(form,"CancelButton"), "卸载初始焦点必须取消");
                    Capture(form,"uninstall-ready"); Key(form,Keys.Enter);
                    Check(!form.Visible && uninstall.Uninstalls == 0, "卸载默认 Enter 不得执行");
                }
                var existing = Service(); existing.DefaultComponents = InstallComponents.Cli;
                using (var form = new SetupForm(existing,false)) {
                    form.Show(); Application.DoEvents();
                    Check(!Find<CheckBox>(form,"DesktopComponent").Checked && Find<CheckBox>(form,"CliComponent").Checked, "GUI 未保持已有组件");
                    Find<Button>(form,"ActionButton").PerformClick(); Pump(delegate { return form.OperationFinished; });
                    Check(existing.Selected == InstallComponents.Cli && !Find<Label>(form,"Introduction").Text.Contains("开始菜单"), "CLI 单选结果文案不准确");
                    form.Close();
                }
                var busy = Service(); busy.Release = new ManualResetEvent(false);
                using (var form = new SetupForm(busy,false))
                {
                    form.Show(); Find<Button>(form,"ActionButton").PerformClick();
                    Pump(delegate { return busy.Installs == 1; });
                    Find<Button>(form,"ActionButton").PerformClick(); form.Close(); Key(form,Keys.Escape);
                    Check(form.Visible && !Find<Button>(form,"CancelButton").Enabled && busy.Installs == 1, "执行期间重复提交或关闭窗口");
                    Capture(form,"install-busy"); busy.Release.Set(); Pump(delegate { return form.OperationFinished; });
                    Check(form.ResultCode == 0 && Find<Label>(form,"Heading").Text == "安装完成", "安装结果不可见");
                    Capture(form,"install-success"); Key(form,Keys.Escape); Check(!form.Visible,"成功后 Esc 无法退出");
                }
                busy.Release.Dispose();
                var failure = Service(); failure.Failure = new InvalidOperationException("检测到旧 MSI。请打开 Windows 设置 → 应用 → 已安装的应用，先卸载旧版 Codex Switch，再重试。\r\n" + new string('长',400));
                failure.DirectoryPath = @"C:\Synthetic User\" + new string('路',100) + @"\Codex Switch";
                using (var form = new SetupForm(failure,true))
                {
                    form.Show(); Find<Button>(form,"ActionButton").PerformClick(); Pump(delegate { return form.OperationFinished; });
                    Check(form.ResultCode == 1 && Find<RichTextBox>(form,"Details").Text.Replace("\r\n","\n") == failure.Failure.Message.Replace("\r\n","\n"), "具体失败原因丢失");
                    Check(form.ActiveControl == Find<Button>(form,"CancelButton"), "失败时默认焦点不得重试卸载");
                    Capture(form,"uninstall-failure-long");
                    var details = Find<RichTextBox>(form,"Details"); details.Select(details.Text.Length,0); details.ScrollToCaret(); Application.DoEvents();
                    Check(details.GetPositionFromCharIndex(details.Text.Length-1).Y < details.ClientSize.Height,"长错误不能滚动到末尾");
                    details.Select(0,0); details.ScrollToCaret();
                    form.Scale(new SizeF(1.5f,1.5f)); Application.DoEvents();
                    var action = Find<Button>(form,"ActionButton"); var close = Find<Button>(form,"CancelButton"); var path = Find<TextBox>(form,"InstallDirectory");
                    Check(action.Visible && action.Parent.ClientRectangle.Contains(action.Bounds) && close.Parent.ClientRectangle.Contains(close.Bounds), "150% 布局按钮越界");
                    Check(path.ClientSize.Height >= path.Font.Height * 2 && path.Width > 300, "150% 长目录不可读");
                    Capture(form,"uninstall-failure-layout150");
                    failure.Failure = null; action.PerformClick(); Pump(delegate { return form.OperationFinished; });
                    Check(failure.Uninstalls == 2 && form.ResultCode == 0, "显式卸载重试失败");
                    Capture(form,"uninstall-success"); Key(form,Keys.Enter); Check(!form.Visible,"完成按钮 Enter 无法退出");
                }
                using(var graphics = Graphics.FromHwnd(IntPtr.Zero)) Console.WriteLine("Actual display DPI: " + graphics.DpiX + ". The 150% image uses injected layout scaling, not a changed monitor DPI.");
                Console.WriteLine("Setup UI passed: " + checks + " assertions. Screenshots: " + evidence);
                return 0;
            }
            catch(Exception error) { Console.Error.WriteLine(error); return 1; }
        }
    }
}
