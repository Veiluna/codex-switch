using System;
using System.Drawing;
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

        internal SetupForm(InstallService service, bool uninstall, InstallComponents? components = null)
        {
            if (service == null) throw new ArgumentNullException("service");
            this.service = service; this.uninstall = uninstall; ResultCode = 3;
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
            body.RowStyles.Add(new RowStyle(SizeType.Absolute,24)); body.RowStyles.Add(new RowStyle(SizeType.Absolute,54));
            body.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            heading = new Label { Name = "Heading", Text = uninstall ? "卸载 Codex Switch" : "安装 Codex Switch", Font = new Font(Font.FontFamily,17F,FontStyle.Bold), ForeColor = Ink, Dock = DockStyle.Fill, Margin = Padding.Empty, UseMnemonic = false };
            introduction = new Label { Name = "Introduction", Text = uninstall ? "将移除桌面端、命令行及其快捷方式。\r\n已保存的账号会保留。" : "选择要为当前 Windows 用户安装的组件。", ForeColor = Muted, Dock = DockStyle.Fill, Margin = Padding.Empty, UseMnemonic = false };
            var location = new Label { Text = "安装位置", Dock = DockStyle.Fill, ForeColor = Ink, Margin = Padding.Empty };
            var path = new TextBox { Name = "InstallDirectory", AccessibleName = "安装位置", ReadOnly = true, Text = service.DirectoryPath, Multiline = true, WordWrap = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, BackColor = Surface, ForeColor = Muted, BorderStyle = BorderStyle.FixedSingle, Margin = new Padding(0,0,0,8), TabIndex = 2 };
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
            body.Controls.Add(location,0,2+offset); body.Controls.Add(path,0,3+offset); body.Controls.Add(details,0,4+offset);
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
            bool valid = SelectedComponents != 0;
            action.Enabled = valid;
            details.Text = valid ? "" : "请至少选择一个组件。";
        }
        private void StartOperation()
        {
            if (busy || (!uninstall && SelectedComponents == 0)) return;
            if (!uninstall) {
                completedComponents = SelectedComponents;
                desktopComponent.Enabled = false; cliComponent.Enabled = false;
            }
            busy = true; OperationFinished = false; action.Enabled = false; cancel.Enabled = false;
            AcceptButton = null; CancelButton = null; details.TabStop = false;
            heading.Text = uninstall ? "正在卸载…" : "正在安装…";
            introduction.Text = "请稍候，完成后此窗口会显示结果。"; details.Clear();
            var worker = new Thread(delegate()
            {
                Exception failure = null;
                try { if (uninstall) service.Uninstall(); else service.Install(completedComponents); }
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
                if (!uninstall) { desktopComponent.Enabled = true; cliComponent.Enabled = true; }
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
}
