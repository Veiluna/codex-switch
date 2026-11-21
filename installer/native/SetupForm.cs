using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace CodexSwitch.Setup
{
    internal sealed class SetupForm : Form
    {
        private readonly InstallService service;
        private readonly bool uninstall;
        private readonly Label heading;
        private readonly Label introduction;
        private readonly RichTextBox details;
        private readonly Button action;
        private readonly Button cancel;
        private readonly CheckBox desktopComponent;
        private readonly CheckBox cliComponent;
        private readonly TextBox path;
        private readonly Button browse;
        private readonly Func<IWin32Window, string, string> selectDirectory;
        private readonly string installedDirectory;
        private readonly bool hasInstalledLocation;
        private InstallComponents completedComponents;
        private bool busy;
        internal int ResultCode { get; private set; }
        internal bool OperationFinished { get; private set; }

        internal static int Run(InstallService service, bool uninstall, InstallComponents? components = null)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using (var form = new SetupForm(service, uninstall, components))
            {
                Application.Run(form);
                return form.ResultCode;
            }
        }

        internal SetupForm(InstallService service, bool uninstall, InstallComponents? components = null, Func<IWin32Window, string, string> selectDirectory = null)
        {
            if (service == null) throw new ArgumentNullException("service");
            this.service = service; this.uninstall = uninstall; ResultCode = 3;
            this.selectDirectory = selectDirectory ?? NativeFolderPicker.Select;
            installedDirectory = service.DirectoryPath; hasInstalledLocation = service.HasInstalledLocation;
            SuspendLayout();
            Text = "Codex Switch"; Name = "SetupForm";
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            Font = new Font("Microsoft YaHei UI", 10F);
            AutoScaleDimensions = new SizeF(96F,96F); AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(520, uninstall ? 360 : 436); MinimumSize = new Size(440, uninstall ? 360 : 436);
            StartPosition = FormStartPosition.CenterScreen;
            MaximizeBox = false; MinimizeBox = false;
            BackColor = Surface; ForeColor = Ink;
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, Margin = Padding.Empty };
            layout.RowStyles.Add(new RowStyle(SizeType.Percent,100)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute,72));
            var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = uninstall ? 5 : 7, Padding = new Padding(24,20,24,12), Margin = Padding.Empty };
            body.RowStyles.Add(new RowStyle(SizeType.Absolute,42)); body.RowStyles.Add(new RowStyle(SizeType.Absolute,48));
            if (!uninstall) { body.RowStyles.Add(new RowStyle(SizeType.Absolute,34)); body.RowStyles.Add(new RowStyle(SizeType.Absolute,34)); }
            body.RowStyles.Add(new RowStyle(SizeType.Absolute,24)); body.RowStyles.Add(new RowStyle(SizeType.Absolute,42));
            body.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            heading = new Label { Name = "Heading", Text = uninstall ? "卸载 Codex Switch" : "安装 Codex Switch", Font = new Font(Font.FontFamily,17F,FontStyle.Bold), ForeColor = Ink, Dock = DockStyle.Fill, Margin = Padding.Empty, UseMnemonic = false };
            introduction = new Label { Name = "Introduction", Text = uninstall ? "将移除桌面端、命令行及其快捷方式。\r\n已保存的账号会保留。" : "选择要为当前 Windows 用户安装的组件。", ForeColor = Muted, Dock = DockStyle.Fill, Margin = Padding.Empty, UseMnemonic = false };
            var location = new Label { Text = "安装位置(&D)", Dock = DockStyle.Fill, ForeColor = Ink, Margin = Padding.Empty, UseMnemonic = true };
            var directoryRow = new TableLayoutPanel { Name = "DirectoryRow", Dock = DockStyle.Fill, ColumnCount = uninstall ? 1 : 2, RowCount = 1, Margin = new Padding(0,0,0,8), TabIndex = 2 };
            directoryRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            path = new TextBox { Name = "InstallDirectory", AccessibleName = "安装位置", ReadOnly = uninstall, Text = installedDirectory, Multiline = false, WordWrap = false, ScrollBars = ScrollBars.None, Dock = DockStyle.Fill, BackColor = Surface, ForeColor = Ink, BorderStyle = BorderStyle.FixedSingle, Margin = new Padding(0,2,0,0), TabIndex = 0 };
            directoryRow.Controls.Add(path,0,0);
            if (!uninstall) {
                directoryRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
                browse = MakeButton("BrowseDirectory","浏览…(&B)",false);
                browse.AccessibleName = "浏览安装目录"; browse.TabIndex = 1; browse.AutoSize = true; browse.AutoSizeMode = AutoSizeMode.GrowAndShrink; browse.Margin = new Padding(8,0,0,0);
                browse.Click += delegate { BrowseDirectory(); };
                directoryRow.Controls.Add(browse,1,0);
                path.TextChanged += delegate { UpdateSelection(); };
            }
            details = new RichTextBox { Name = "Details", AccessibleName = "安装状态与详细原因", ReadOnly = true, BorderStyle = BorderStyle.None, DetectUrls = false, ScrollBars = RichTextBoxScrollBars.Vertical, WordWrap = true, Dock = DockStyle.Fill, BackColor = Surface, ForeColor = Muted, Margin = Padding.Empty, TabIndex = 1, TabStop = false };
            body.Controls.Add(heading,0,0); body.Controls.Add(introduction,0,1);
            int offset = uninstall ? 0 : 2;
            if (!uninstall) {
                InstallComponents selected = components ?? service.GetDefaultComponents();
                desktopComponent = new CheckBox { Name = "DesktopComponent", Text = "桌面端", AccessibleName = "桌面端", Checked = (selected & InstallComponents.Desktop) != 0, AutoSize = true, Dock = DockStyle.Fill, TabIndex = 0, UseVisualStyleBackColor = true, Margin = Padding.Empty };
                cliComponent = new CheckBox { Name = "CliComponent", Text = "命令行（CLI）", AccessibleName = "命令行（CLI）", Checked = (selected & InstallComponents.Cli) != 0, AutoSize = true, Dock = DockStyle.Fill, TabIndex = 1, UseVisualStyleBackColor = true, Margin = Padding.Empty };
                desktopComponent.CheckedChanged += delegate { UpdateSelection(); };
                cliComponent.CheckedChanged += delegate { UpdateSelection(); };
                body.Controls.Add(desktopComponent,0,2); body.Controls.Add(cliComponent,0,3);
            }
            body.Controls.Add(location,0,2+offset); body.Controls.Add(directoryRow,0,3+offset); body.Controls.Add(details,0,4+offset);
            var footer = new TableLayoutPanel { Name = "Actions", Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Padding = new Padding(24,18,24,18), Margin = Padding.Empty, BackColor = SystemInformation.HighContrast ? SystemColors.Control : Color.FromArgb(244,246,248), TabIndex = 1 };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100)); footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,92)); footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,92));
            footer.Paint += delegate(object sender, PaintEventArgs e) { using(var pen = new Pen(SystemInformation.HighContrast ? SystemColors.WindowText : Color.FromArgb(218,224,230))) e.Graphics.DrawLine(pen,0,0,footer.Width,0); };
            cancel = MakeButton("CancelButton","取消",false); cancel.TabIndex = 0; cancel.Margin = new Padding(0,0,8,0);
            action = MakeButton("ActionButton",uninstall ? "卸载" : "安装",true); action.TabIndex = 1; action.Margin = Padding.Empty;
            cancel.Click += delegate { if (!busy) Close(); };
            action.Click += delegate { StartOperation(); };
            footer.Controls.Add(cancel,1,0); footer.Controls.Add(action,2,0);
            layout.Controls.Add(body,0,0); layout.Controls.Add(footer,0,1); Controls.Add(layout);
            CancelButton = cancel; AcceptButton = uninstall ? cancel : action;
            Shown += delegate { cancel.Focus(); };
            FormClosing += delegate(object sender, FormClosingEventArgs e) { if (busy && e.CloseReason == CloseReason.UserClosing) e.Cancel = true; };
            UpdateSelection();
            ResumeLayout(true);
        }

        private InstallComponents SelectedComponents
        {
            get { return (desktopComponent.Checked ? InstallComponents.Desktop : 0) | (cliComponent.Checked ? InstallComponents.Cli : 0); }
        }
        private void UpdateSelection()
        {
            if (uninstall || busy) return;
            bool valid = SelectedComponents != 0 && !String.IsNullOrWhiteSpace(path.Text);
            action.Enabled = valid;
            details.Text = valid ? MigrationNotice() : SelectedComponents == 0 ? "请至少选择一个组件。" : "请输入安装目录或点击浏览选择文件夹。";
        }
        private string MigrationNotice()
        {
            if (!hasInstalledLocation) return "";
            try {
                string selected = Path.GetFullPath(path.Text).TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar);
                string installed = Path.GetFullPath(installedDirectory).TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar);
                return String.Equals(selected,installed,StringComparison.OrdinalIgnoreCase) ? "" : "已安装在 " + installedDirectory + "，继续安装会将程序移到所选目录。";
            }
            catch (ArgumentException) { return ""; }
            catch (NotSupportedException) { return ""; }
            catch (PathTooLongException) { return ""; }
        }
        private void BrowseDirectory()
        {
            if (uninstall || busy) return;
            try {
                string selected = selectDirectory(this,path.Text);
                if (selected != null) path.Text = selected;
                path.Focus(); path.SelectionStart = path.TextLength;
            }
            catch (Exception error) {
                details.Text = "无法打开文件夹选择窗口：" + error.Message;
                details.TabStop = true;
            }
        }
        private void StartOperation()
        {
            if (busy || (!uninstall && (SelectedComponents == 0 || String.IsNullOrWhiteSpace(path.Text)))) return;
            string installDirectory = path.Text;
            if (!uninstall) {
                completedComponents = SelectedComponents;
                desktopComponent.Enabled = false; cliComponent.Enabled = false;
                path.Enabled = false; browse.Enabled = false;
            }
            busy = true; OperationFinished = false; action.Enabled = false; cancel.Enabled = false;
            AcceptButton = null; CancelButton = null; details.TabStop = false;
            heading.Text = uninstall ? "正在卸载…" : "正在安装…";
            introduction.Text = "请稍候，完成后此窗口会显示结果。"; details.Clear();
            var worker = new Thread(delegate()
            {
                Exception failure = null;
                try { if (uninstall) service.Uninstall(); else service.Install(completedComponents,installDirectory); }
                catch(Exception error) { failure = error; }
                // User closing is blocked while busy; shutdown may have already disposed the window.
                if (!IsDisposed && IsHandleCreated)
                {
                    try { BeginInvoke((MethodInvoker)delegate { ShowResult(failure); }); }
                    catch(InvalidOperationException) { }
                }
            });
            worker.IsBackground = true; worker.SetApartmentState(ApartmentState.STA); worker.Start();
        }

        private void ShowResult(Exception failure)
        {
            busy = false; OperationFinished = true; cancel.Enabled = true;
            CancelButton = cancel; AcceptButton = cancel;
            if (failure == null)
            {
                ResultCode = 0; heading.Text = uninstall ? "卸载完成" : "安装完成";
                introduction.Text = uninstall ? "已保存的账号仍保留在本机。" : completedComponents == InstallComponents.Desktop ? "从开始菜单打开 Codex Switch。" : completedComponents == InstallComponents.Cli ? "在新开的终端输入 codex-switch。" : "从开始菜单打开 Codex Switch，\r\n或在新开的终端输入 codex-switch。";
                details.Clear(); action.Visible = false; ((TableLayoutPanel)cancel.Parent).SetColumn(cancel,2); cancel.Margin = Padding.Empty; cancel.Text = "完成";
            }
            else
            {
                ResultCode = 1; heading.Text = uninstall ? "卸载未完成" : "安装未完成";
                introduction.Text = "请查看以下原因，处理后可重试。";
                details.ForeColor = SystemInformation.HighContrast ? SystemColors.WindowText : Color.FromArgb(145,69,54);
                details.Text = failure.Message; details.SelectionStart = 0; details.TabStop = true;
                if (!uninstall) { desktopComponent.Enabled = true; cliComponent.Enabled = true; path.Enabled = true; browse.Enabled = true; }
                action.Enabled = true; action.Text = uninstall ? "重试卸载" : "重试安装"; cancel.Text = "关闭";
            }
            cancel.AccessibleName = cancel.Text; action.AccessibleName = action.Text; cancel.Focus();
        }

        private static Color Surface { get { return SystemInformation.HighContrast ? SystemColors.Window : Color.White; } }
        private static Color Ink { get { return SystemInformation.HighContrast ? SystemColors.WindowText : Color.FromArgb(32,42,52); } }
        private static Color Muted { get { return SystemInformation.HighContrast ? SystemColors.WindowText : Color.FromArgb(88,105,121); } }
        private Button MakeButton(string name, string text, bool primary)
        {
            Color fill = uninstall ? Color.FromArgb(145,69,54) : Color.FromArgb(25,77,118);
            var button = new Button { Name = name, Text = text, AccessibleName = text, Dock = DockStyle.Fill, MinimumSize = new Size(84,34), UseVisualStyleBackColor = false };
            if (SystemInformation.HighContrast) { button.FlatStyle = FlatStyle.System; button.UseVisualStyleBackColor = true; return button; }
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.MouseOverBackColor = primary ? ControlPaint.Light(fill,0.15F) : Color.FromArgb(237,242,246);
            button.FlatAppearance.MouseDownBackColor = primary ? ControlPaint.Dark(fill,0.15F) : Color.FromArgb(220,230,238);
            EventHandler style = delegate {
                button.BackColor = button.Enabled ? primary ? fill : Surface : Color.FromArgb(227,232,237);
                button.ForeColor = button.Enabled ? primary ? Color.White : Ink : Muted;
                button.FlatAppearance.BorderSize = button.Focused ? 2 : 1;
                button.FlatAppearance.BorderColor = button.Focused ? Ink : primary ? fill : Color.FromArgb(128,144,158);
            };
            button.GotFocus += style; button.LostFocus += style; button.EnabledChanged += style; style(button,EventArgs.Empty);
            return button;
        }
    }

    // Use the Windows common item dialog, not Framework 4.8's tree-only FolderBrowserDialog.
    internal static class NativeFolderPicker
    {
        private const uint PickFolders = 0x20, ForceFileSystem = 0x40, NoChangeDirectory = 0x8, DontAddToRecent = 0x02000000;
        internal static string Select(IWin32Window owner, string currentDirectory)
        {
            var dialog = (IFileOpenDialog)new FileOpenDialog();
            IShellItem initial = null, selected = null;
            IntPtr displayName = IntPtr.Zero;
            try
            {
                uint options; dialog.GetOptions(out options);
                dialog.SetOptions(options | PickFolders | ForceFileSystem | NoChangeDirectory | DontAddToRecent);
                dialog.SetTitle("选择安装目录"); dialog.SetOkButtonLabel("选择此文件夹"); dialog.SetFileNameLabel("文件夹");
                string folder = ExistingFolder(currentDirectory);
                if (folder != null)
                {
                    Guid shellItem = typeof(IShellItem).GUID;
                    if (SHCreateItemFromParsingName(folder,IntPtr.Zero,ref shellItem,out initial) >= 0) dialog.SetFolder(initial);
                }
                int result = dialog.Show(owner.Handle);
                if (result == unchecked((int)0x800704C7)) return null; // ERROR_CANCELLED is a normal dismissal.
                Marshal.ThrowExceptionForHR(result);
                dialog.GetResult(out selected);
                selected.GetDisplayName(0x80058000,out displayName); // SIGDN_FILESYSPATH
                return Marshal.PtrToStringUni(displayName);
            }
            finally
            {
                if (displayName != IntPtr.Zero) Marshal.FreeCoTaskMem(displayName);
                if (selected != null) Marshal.ReleaseComObject(selected);
                if (initial != null) Marshal.ReleaseComObject(initial);
                Marshal.ReleaseComObject(dialog);
            }
        }
        private static string ExistingFolder(string path)
        {
            try {
                if (String.IsNullOrWhiteSpace(path)) return null;
                string candidate = Path.GetFullPath(path);
                while (!Directory.Exists(candidate)) { candidate = Path.GetDirectoryName(candidate); if (String.IsNullOrEmpty(candidate)) return null; }
                return candidate;
            }
            catch (ArgumentException) { return null; }
            catch (NotSupportedException) { return null; }
            catch (PathTooLongException) { return null; }
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
        private static extern int SHCreateItemFromParsingName(string path, IntPtr bindContext, ref Guid iid, out IShellItem item);
        [ComImport, Guid("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7")]
        private class FileOpenDialog { }
        // COM declarations retain every preceding vtable slot, including inherited IModalWindow::Show.
        [ComImport, Guid("D57C7288-D4AD-4768-BE02-9D969532D960"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IFileOpenDialog
        {
            [PreserveSig] int Show(IntPtr owner);
            void SetFileTypes(uint count, IntPtr filters);
            void SetFileTypeIndex(uint index);
            void GetFileTypeIndex(out uint index);
            void Advise(IntPtr events, out uint cookie);
            void Unadvise(uint cookie);
            void SetOptions(uint options);
            void GetOptions(out uint options);
            void SetDefaultFolder(IShellItem item);
            void SetFolder(IShellItem item);
            void GetFolder(out IShellItem item);
            void GetCurrentSelection(out IShellItem item);
            void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
            void GetFileName(out IntPtr name);
            void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
            void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
            void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
            void GetResult(out IShellItem item);
            void AddPlace(IShellItem item, int placement);
            void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string extension);
            void Close(int result);
            void SetClientGuid(ref Guid guid);
            void ClearClientData();
            void SetFilter(IntPtr filter);
            void GetResults(out IntPtr items);
            void GetSelectedItems(out IntPtr items);
        }
        [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItem
        {
            void BindToHandler(IntPtr bindContext, ref Guid handler, ref Guid iid, out IntPtr result);
            void GetParent(out IShellItem parent);
            void GetDisplayName(uint format, out IntPtr name);
            void GetAttributes(uint mask, out uint attributes);
            void Compare(IShellItem other, uint hint, out int order);
        }
    }
}
