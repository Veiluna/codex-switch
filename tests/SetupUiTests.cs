using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

// The production InstallService is deliberately excluded from this executable.
namespace CodexSwitch.Setup
{
    [Flags] internal enum InstallComponents { Desktop = 1, Cli = 2, All = 3 }
    internal sealed class InstallService
    {
        internal string DirectoryPath { get; set; }
        internal bool HasInstalledLocation { get; set; }
        internal int Installs, Uninstalls;
        internal Exception Failure;
        internal ManualResetEvent Release;
        internal InstallComponents DefaultComponents = InstallComponents.All;
        internal InstallComponents Selected;
        internal string SelectedDirectory;
        internal InstallComponents GetDefaultComponents() { return DefaultComponents; }
        internal void Install(InstallComponents components, string directory) { SelectedDirectory = directory; Install(components); }
        internal void Install(InstallComponents components) { Selected = components; Install(); }
        internal void Install() { Interlocked.Increment(ref Installs); Work(); }
        internal void Uninstall() { Interlocked.Increment(ref Uninstalls); Work(); }
        private void Work() { if (Release != null) Release.WaitOne(8000); if (Failure != null) throw Failure; }
    }
    internal static class SetupUiTests
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr window, IntPtr dc, uint flags);
        [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr window, int index);
        private delegate bool EnumWindow(IntPtr window, IntPtr parameter);
        [DllImport("user32.dll")] private static extern bool EnumThreadWindows(uint thread, EnumWindow callback, IntPtr parameter);
        [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumWindow callback, IntPtr parameter);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder text, int length);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder text, int length);
        [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr window, uint command);
        [DllImport("user32.dll")] private static extern bool IsWindowEnabled(IntPtr window);
        [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out WindowRect rectangle);
        [StructLayout(LayoutKind.Sequential)] private struct WindowRect { internal int Left, Top, Right, Bottom; }
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
        private static void Mnemonic(Form form, char key)
        {
            typeof(Form).GetMethod("ProcessMnemonic", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Invoke(form, new object[] { key });
            Application.DoEvents();
        }
        private static InstallService Service() { return new InstallService { DirectoryPath = @"C:\Synthetic User\AppData\Local\Programs\Codex Switch" }; }
        private static void CheckSingleLine(TextBox path)
        {
            Check(!path.Multiline && path.ScrollBars == ScrollBars.None && !path.WordWrap, "路径框必须单行且不显示滚动条");
            Check((GetWindowLong(path.Handle,-16) & (0x00200000 | 0x00100000)) == 0, "实际 Edit HWND 仍带 WS_VSCROLL/WS_HSCROLL");
        }
        private static void DirectorySelection()
        {
            var service = Service();
            string selected = @"D:\自选目录\精确安装路径";
            int picks = 0;
            using (var form = new SetupForm(service,false,null,delegate(IWin32Window owner, string current) {
                Check(owner is SetupForm && owner.Handle != IntPtr.Zero, "选择器 owner 应为安装窗口");
                Check(current == (picks == 0 ? service.DirectoryPath : selected), "选择器未使用当前路径");
                return picks++ == 0 ? selected : null;
            }))
            {
                form.Show(); Application.DoEvents();
                var path = Find<TextBox>(form,"InstallDirectory");
                var browse = Find<Button>(form,"BrowseDirectory");
                Mnemonic(form,'b'); Check(picks == 1 && path.Text == selected, "浏览快捷键未打开选择器或未回填精确目录");
                browse.PerformClick(); Check(path.Text == selected, "取消选择覆盖了路径");
                Check(!Find<RichTextBox>(form,"Details").Text.Contains("已安装在"), "首次自选目录不应误报迁移");
                browse.Focus(); Mnemonic(form,'d'); Check(form.ActiveControl == path, "安装位置快捷键未聚焦路径框");
                path.Text = @"E:\直接输入 空格\安装";
                path.SelectAll(); Check(path.SelectedText == path.Text && path.ShortcutsEnabled, "输入的完整路径不能复制");
                path.Text = ""; Check(!Find<Button>(form,"ActionButton").Enabled, "空路径仍允许提交");
                path.Text = @"E:\直接输入 空格\安装";
                service.Failure = new IOException("模拟路径安装失败");
                Find<Button>(form,"ActionButton").PerformClick(); Pump(delegate { return form.OperationFinished; });
                Check(service.SelectedDirectory == path.Text && service.Selected == InstallComponents.All, "用户输入路径或组件未传给服务");
                Check(path.Enabled && !path.ReadOnly && browse.Enabled && Find<Button>(form,"ActionButton").Enabled, "失败未恢复目录和浏览，无法重试");
                service.Failure = null; path.Text = selected;
                Find<Button>(form,"ActionButton").PerformClick(); Pump(delegate { return form.OperationFinished; });
                Check(service.Installs == 2 && service.SelectedDirectory == selected && form.ResultCode == 0, "新路径重试未传递精确路径");
                form.Close();
            }
            using (var form = new SetupForm(Service(),false))
            {
                form.Show(); form.Scale(new SizeF(1.5f,1.5f)); Application.DoEvents();
                var path = Find<TextBox>(form,"InstallDirectory"); var browse = Find<Button>(form,"BrowseDirectory");
                CheckSingleLine(path);
                Check(path.Parent.ClientRectangle.Contains(path.Bounds) && browse.Parent.ClientRectangle.Contains(browse.Bounds) && path.Right <= browse.Left && browse.Width >= browse.PreferredSize.Width, "150% 路径行裁切或重叠：path=" + path.Bounds + "; browse=" + browse.Bounds + "; parent=" + browse.Parent.ClientRectangle + "; preferred=" + browse.PreferredSize);
                Capture(form,"install-directory-layout150"); form.Close();
            }
            var existing = Service(); existing.HasInstalledLocation = true;
            using (var form = new SetupForm(existing,false))
            {
                form.Show(); Application.DoEvents();
                var path = Find<TextBox>(form,"InstallDirectory"); var details = Find<RichTextBox>(form,"Details");
                Check(details.Text == "", "原地更新不应误报迁移");
                path.Text = existing.DirectoryPath.ToUpperInvariant() + "\\";
                Check(details.Text == "", "同一目录大小写和末尾分隔符不应误报迁移");
                path.Text = selected;
                Check(details.Text.Contains(existing.DirectoryPath) && details.Text.Contains("继续安装会将程序移到所选目录"), "已有安装更改目录时未明确提示迁移");
                Capture(form,"install-directory-migration"); form.Close();
            }
        }
        private static void NativePickerCancellation()
        {
            var service = Service(); service.DirectoryPath = evidence;
            using (var form = new SetupForm(service,false))
            using (var timer = new System.Windows.Forms.Timer { Interval = 100 })
            {
                form.Show(); Application.DoEvents();
                IntPtr dialog = IntPtr.Zero;
                Exception failure = null;
                DateTime foundAt = DateTime.MinValue;
                DateTime deadline = DateTime.UtcNow.AddSeconds(12);
                timer.Tick += delegate {
                    EnumThreadWindows(GetCurrentThreadId(),delegate(IntPtr window, IntPtr unused) {
                        var className = new StringBuilder(256); GetClassName(window,className,className.Capacity);
                        if (className.ToString() == "#32770") { dialog = window; return false; }
                        return true;
                    },IntPtr.Zero);
                    if (dialog == IntPtr.Zero && DateTime.UtcNow < deadline) return;
                    if (dialog != IntPtr.Zero && foundAt == DateTime.MinValue) { foundAt = DateTime.UtcNow; return; }
                    if (dialog != IntPtr.Zero && DateTime.UtcNow.Subtract(foundAt).TotalMilliseconds < 600) return;
                    timer.Stop();
                    try {
                        Check(dialog != IntPtr.Zero, "原生文件夹选择窗口未出现");
                        var title = new StringBuilder(256); GetWindowText(dialog,title,title.Capacity);
                        Check(title.ToString() == "选择安装目录", "原生文件夹选择窗口中文标题不正确");
                        Check(GetWindow(dialog,4) == form.Handle && !IsWindowEnabled(form.Handle), "原生选择窗口未设置安装窗为 modal owner");
                        bool directUi = false, breadcrumbs = false;
                        EnumChildWindows(dialog,delegate(IntPtr window, IntPtr unused) {
                            var name = new StringBuilder(256); GetClassName(window,name,name.Capacity);
                            directUi |= name.ToString() == "DirectUIHWND";
                            breadcrumbs |= name.ToString().Contains("Breadcrumb");
                            return true;
                        },IntPtr.Zero);
                        Check(directUi && breadcrumbs, "实际窗口不是带导航地址栏的现代 Windows 选择器");
                        WindowRect rectangle; Check(GetWindowRect(dialog,out rectangle), "无法读取原生窗口尺寸");
                        using (var bitmap = new Bitmap(rectangle.Right-rectangle.Left,rectangle.Bottom-rectangle.Top)) {
                            using (var graphics = Graphics.FromImage(bitmap)) { IntPtr dc = graphics.GetHdc(); try { Check(PrintWindow(dialog,dc,0), "原生选择窗口截取失败"); } finally { graphics.ReleaseHdc(dc); } }
                            bitmap.Save(Path.Combine(evidence,"native-folder-picker.png"));
                        }
                    }
                    catch (Exception error) { failure = error; }
                    finally { if (dialog != IntPtr.Zero) PostMessage(dialog,0x111,new IntPtr(2),IntPtr.Zero); }
                };
                timer.Start(); Find<Button>(form,"BrowseDirectory").PerformClick(); timer.Stop();
                if (failure != null) throw failure;
                Check(dialog != IntPtr.Zero && Find<TextBox>(form,"InstallDirectory").Text == service.DirectoryPath && service.Installs == 0, "真实选择器取消覆盖目录或执行安装");
                Check(IsWindowEnabled(form.Handle) && !Find<RichTextBox>(form,"Details").Text.Contains("无法打开"), "取消原生选择器未恢复 owner 或被当作失败");
                form.Close();
            }
        }
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
                    Check(!Find<TextBox>(form,"InstallDirectory").ReadOnly && Find<TextBox>(form,"InstallDirectory").Text == cancel.DirectoryPath, "安装目录应显示实际默认值且可直接编辑");
                    CheckSingleLine(Find<TextBox>(form,"InstallDirectory"));
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
                    Check(form.ActiveControl == Find<Button>(form,"BrowseDirectory"), "目录后 Tab 顺序应为浏览");
                    form.SelectNextControl(form.ActiveControl,true,true,true,true);
                    Check(form.ActiveControl == Find<Button>(form,"CancelButton"), "浏览后 Tab 顺序应为取消");
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
                    Check(Find<TextBox>(form,"InstallDirectory").ReadOnly && form.Controls.Find("BrowseDirectory",true).Length == 0, "卸载目录应只读且不可浏览改选");
                    CheckSingleLine(Find<TextBox>(form,"InstallDirectory"));
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
                    Check(!Find<TextBox>(form,"InstallDirectory").Enabled && !Find<Button>(form,"BrowseDirectory").Enabled, "安装期间仍可修改路径");
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
                    Check(path.ClientSize.Height >= path.Font.Height && path.Width > 300, "150% 单行目录不可读");
                    CheckSingleLine(path);
                    Capture(form,"uninstall-failure-layout150");
                    failure.Failure = null; action.PerformClick(); Pump(delegate { return form.OperationFinished; });
                    Check(failure.Uninstalls == 2 && form.ResultCode == 0, "显式卸载重试失败");
                    Capture(form,"uninstall-success"); Key(form,Keys.Enter); Check(!form.Visible,"完成按钮 Enter 无法退出");
                }
                DirectorySelection();
                NativePickerCancellation();
                using(var graphics = Graphics.FromHwnd(IntPtr.Zero)) Console.WriteLine("Actual display DPI: " + graphics.DpiX + ". The 150% image uses injected layout scaling, not a changed monitor DPI.");
                Console.WriteLine("Setup UI passed: " + checks + " assertions. Screenshots: " + evidence);
                return 0;
            }
            catch(Exception error) { Console.Error.WriteLine(error); return 1; }
        }
    }
}
