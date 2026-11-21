using System;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace CodexSwitch
{
    internal static class NativeStyle
    {
        internal static Color Surface { get { return SystemInformation.HighContrast ? SystemColors.Window : Color.White; } }
        internal static Color Sidebar { get { return SystemInformation.HighContrast ? SystemColors.Window : Color.FromArgb(240, 242, 245); } }
        internal static Color Ink { get { return SystemInformation.HighContrast ? SystemColors.WindowText : Color.FromArgb(32, 42, 52); } }
        internal static Color Muted { get { return SystemInformation.HighContrast ? SystemColors.WindowText : Color.FromArgb(88, 105, 121); } }
        internal static Color Primary { get { return SystemInformation.HighContrast ? SystemColors.Highlight : Color.FromArgb(25, 77, 118); } }
        internal static Color Error { get { return SystemInformation.HighContrast ? SystemColors.WindowText : Color.FromArgb(145, 69, 54); } }
        internal static Color Border { get { return SystemInformation.HighContrast ? SystemColors.WindowText : Color.FromArgb(128, 144, 158); } }
        internal static Button Button(string name, string text, bool primary)
        {
            var button = new Button { Name = name, Text = text, AccessibleName = text, Size = new Size(140, 40),
                Margin = new Padding(0, 0, 10, 0), UseVisualStyleBackColor = false };
            if (SystemInformation.HighContrast) { button.FlatStyle = FlatStyle.System; button.UseVisualStyleBackColor = true; }
            else
            {
                button.FlatStyle = FlatStyle.Flat;
                button.FlatAppearance.BorderColor = primary ? Primary : Border;
                button.FlatAppearance.MouseOverBackColor = primary ? Color.FromArgb(36, 94, 139) : Color.FromArgb(237, 242, 246);
                button.FlatAppearance.MouseDownBackColor = primary ? Color.FromArgb(16, 57, 87) : Color.FromArgb(220, 230, 238);
            }
            EventHandler update = delegate {
                if (SystemInformation.HighContrast) return;
                button.BackColor = button.Enabled ? primary ? Primary : Surface : Color.FromArgb(227, 232, 237);
                button.ForeColor = button.Enabled ? primary ? Color.White : Ink : Color.FromArgb(88, 105, 121);
                button.FlatAppearance.BorderSize = button.Focused ? 2 : 1;
                button.FlatAppearance.BorderColor = button.Focused ? primary ? Ink : Primary : primary ? Primary : Border;
            };
            button.GotFocus += update; button.LostFocus += update; button.EnabledChanged += update;
            update(button, EventArgs.Empty);
            return button;
        }
        internal static RichTextBox ReadOnlyText(string name)
        {
            // RichEdit owns scrollbar visibility. Changing TextBox.ScrollBars during handle creation
            // can recreate the very handle being created and fail in the real Application.Run loop.
            return new RichTextBox { Name = name, AccessibleName = name, ReadOnly = true, Multiline = true,
                BorderStyle = BorderStyle.None, BackColor = Surface, ForeColor = Muted, WordWrap = true,
                DetectUrls = false, ScrollBars = RichTextBoxScrollBars.Vertical,
                Dock = DockStyle.Fill, Margin = new Padding(0, 2, 0, 8) };
        }
        internal static Label Label(string text, int size, bool bold)
        {
            return new Label { Text = text, UseMnemonic = false, Dock = DockStyle.Fill, Margin = Padding.Empty,
                ForeColor = bold ? Ink : Muted, Font = new Font("Microsoft YaHei UI", size, bold ? FontStyle.Bold : FontStyle.Regular) };
        }
    }

    internal sealed class InputFrame : Panel
    {
        private readonly TextBox input;
        private readonly Panel inside;
        internal void ReserveAdornment(Button button)
        {
            EventHandler arrange = delegate {
                int size = Math.Max(32, ClientSize.Height - 6);
                inside.Padding = new Padding(7, Math.Max(5, (ClientSize.Height - input.PreferredHeight) / 2 - Padding.Top), size + 9, 3);
                button.SetBounds(Math.Max(0, ClientSize.Width - size - 3), 3, size, size);
                button.BringToFront();
            };
            Resize += arrange; arrange(this, EventArgs.Empty);
        }
        private bool hasError;
        internal bool HasError { get { return hasError; } set { hasError = value; UpdateStyle(); } }
        internal InputFrame(TextBox input)
        {
            this.input = input; Name = input.Name + "Frame"; TabStop = false; AccessibleRole = AccessibleRole.None;
            Dock = DockStyle.Top; Height = 36; Margin = new Padding(0, 0, 0, 4);
            inside = new Panel { Dock = DockStyle.Fill, Padding = new Padding(7, 5, 7, 5), TabStop = false, AccessibleRole = AccessibleRole.None };
            input.BorderStyle = BorderStyle.None; input.Dock = DockStyle.Fill; input.Margin = Padding.Empty;
            inside.Controls.Add(input); Controls.Add(inside);
            input.GotFocus += delegate { UpdateStyle(); }; input.LostFocus += delegate { UpdateStyle(); };
            input.EnabledChanged += delegate { UpdateStyle(); }; input.SystemColorsChanged += delegate { UpdateStyle(); };
            inside.Click += delegate { input.Focus(); }; Click += delegate { input.Focus(); };
            UpdateStyle();
        }
        private void UpdateStyle()
        {
            BackColor = hasError ? NativeStyle.Error : input.Focused ? NativeStyle.Primary : NativeStyle.Border;
            Padding = new Padding(input.Focused || hasError ? 2 : 1);
            inside.BackColor = input.BackColor = input.Enabled ? NativeStyle.Surface : SystemColors.Control;
            input.ForeColor = input.Enabled ? NativeStyle.Ink : SystemColors.GrayText;
        }
    }

    internal interface IDesktopAccounts : IDisposable
    {
        bool CurrentReadFailed { get; }
        CatalogEntry[] List();
        OperationResult Save(string name);
        OperationResult Delete(string name, string snapshotId);
        OperationResult Switch(string target,string snapshotId,Func<string,string,bool> confirm);
    }
    internal sealed class DesktopAccounts : IDesktopAccounts
    {
        private readonly AccountService service;
        internal DesktopAccounts(string root) { service = new AccountService(root); }
        public bool CurrentReadFailed { get { return service.CurrentReadFailed; } }
        public CatalogEntry[] List() { return service.List(); }
        public OperationResult Save(string name) { return service.Save(name); }
        public OperationResult Delete(string name, string snapshotId) { return service.Delete(name, snapshotId); }
        public OperationResult Switch(string target,string snapshotId,Func<string,string,bool> confirm) { return service.Switch(target,snapshotId,confirm); }
        public void Dispose() { service.Dispose(); }
    }
    internal sealed class RefreshGlyphButton : Button
    {
        internal RefreshGlyphButton()
        {
            Name="RefreshAccountsButton"; Text="刷新账号列表"; AccessibleName="刷新账号列表"; AccessibleRole=AccessibleRole.PushButton;
            Size=new Size(34,34); FlatStyle=FlatStyle.Flat; FlatAppearance.BorderSize=0;
            BackColor=NativeStyle.Surface; ForeColor=NativeStyle.Primary; TabIndex=1;
            SystemColorsChanged+=delegate { BackColor=NativeStyle.Surface; ForeColor=NativeStyle.Primary; Invalidate(); };
            SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer,true);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            // Retain a native window caption for out-of-process MSAA/UIA proxies while drawing only the glyph.
            using(var background=new SolidBrush(NativeStyle.Surface)) e.Graphics.FillRectangle(background,ClientRectangle);
            var color=Enabled ? (SystemInformation.HighContrast ? SystemColors.WindowText : NativeStyle.Primary) : SystemColors.GrayText;
            e.Graphics.SmoothingMode=System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            float scale=DeviceDpiFactor(e.Graphics); float size=14*scale;
            var circle=new RectangleF((Width-size)/2,(Height-size)/2,size,size);
            using(var pen=new Pen(color,Math.Max(1.6f,1.6f*scale)))
            {
                e.Graphics.DrawArc(pen,circle,35,290);
                float x=circle.Right-1*scale,y=circle.Top+3*scale;
                e.Graphics.DrawLine(pen,x,y,x-5*scale,y); e.Graphics.DrawLine(pen,x,y,x,y-5*scale);
            }
            if(Focused) ControlPaint.DrawFocusRectangle(e.Graphics,new Rectangle(2,2,Width-4,Height-4),color,BackColor);
        }
        private static float DeviceDpiFactor(Graphics graphics) { return graphics.DpiX/96f; }
    }
    internal sealed class AccountRow
    {
        internal CatalogEntry Entry;
        internal string Reminder;
        internal string DisplaySignature;
        internal AccountRow(CatalogEntry entry) { Entry=entry; DisplaySignature=Signature(); }
        internal string Signature() { return Entry.Name+"\n"+Entry.Email+"\n"+Entry.IsCurrent+"\n"+Entry.IsSaved+"\n"+Reminder; }
        public override string ToString() { return Entry.Name + (Entry.IsCurrent ? " · 当前登录" : "") + (!Entry.IsSaved ? " · 未保存" : "") + (String.IsNullOrEmpty(Reminder)?"":" · "+Reminder); }
    }
    // Label's default DrawText padding grows with the font size. Detail headings use the
    // same one-pixel text origin as the borderless RichEdit previews instead.
    internal sealed class DetailLabel : Label
    {
        protected override void OnPaint(PaintEventArgs e)
        {
            TextRenderer.DrawText(e.Graphics,Text,Font,new Rectangle(1,Padding.Top,Math.Max(0,Width-1),Height-Padding.Vertical),
                ForeColor,TextFormatFlags.NoPadding|TextFormatFlags.NoPrefix|TextFormatFlags.WordBreak|(AutoEllipsis?TextFormatFlags.EndEllipsis:0));
        }
        internal static Label Create(string text,int size,bool bold)
        {
            return new DetailLabel { Text=text, Dock=DockStyle.Fill, Margin=Padding.Empty,
                ForeColor=bold?NativeStyle.Ink:NativeStyle.Muted,
                Font=new Font("Microsoft YaHei UI",size,bold?FontStyle.Bold:FontStyle.Regular) };
        }
    }
    internal sealed class DetailPreview : RichTextBox
    {
        private string completeText="";
        private string previewText="";
        internal string CompleteText { get { return completeText; } set { SetContent(value,value); } }
        internal void SetContent(string complete,string preview)
        {
            complete=complete??""; preview=(preview??"").Replace("\r\n","\n"); if(completeText==complete && previewText==preview) return;
            completeText=complete; previewText=preview; RefreshPreview();
        }
        protected override void OnSizeChanged(EventArgs e) { base.OnSizeChanged(e); RefreshPreview(); }
        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); RefreshPreview(); }
        private void RefreshPreview()
        {
            if(!IsHandleCreated || ClientSize.Height<=0) { if(Text!=previewText) Text=previewText; return; }
            if(Text==previewText && Fits(this)) return;
            string visible=previewText;
            // Measure in a separate RichEdit; probing truncation must never rewrite the visible preview.
            using(var measure=new RichTextBox { Size=ClientSize, Font=Font, BorderStyle=BorderStyle.None,
                WordWrap=true, ScrollBars=RichTextBoxScrollBars.None, Text=previewText })
            {
                var handle=measure.Handle;
                if(!Fits(measure))
                {
                    int low=0,high=previewText.Length;
                    while(low<high)
                    {
                        int middle=(low+high+1)/2; measure.Text=previewText.Substring(0,middle).TrimEnd()+"…";
                        if(Fits(measure)) low=middle; else high=middle-1;
                    }
                    visible=previewText.Substring(0,low).TrimEnd()+"…";
                }
            }
            if(Text==visible) return;
            int start=SelectionStart,length=SelectionLength;
            string old=Text; int prefix=0,suffix=0;
            while(prefix<old.Length && prefix<visible.Length && old[prefix]==visible[prefix]) prefix++;
            while(suffix<old.Length-prefix && suffix<visible.Length-prefix && old[old.Length-1-suffix]==visible[visible.Length-1-suffix]) suffix++;
            Select(prefix,old.Length-prefix-suffix); SelectedText=visible.Substring(prefix,visible.Length-prefix-suffix);
            int delta=visible.Length-old.Length;
            if(start>=old.Length-suffix) start+=delta;
            Select(Math.Min(start,TextLength),Math.Min(length,Math.Max(0,TextLength-start)));
        }
        private static bool Fits(RichTextBox text)
        {
            if(text.TextLength==0) return true;
            int lineHeight=text.Font.Height,next=text.GetFirstCharIndexFromLine(1);
            if(next>0) lineHeight=Math.Max(lineHeight,text.GetPositionFromCharIndex(next).Y-text.GetPositionFromCharIndex(0).Y);
            return text.GetPositionFromCharIndex(text.TextLength-1).Y+lineHeight<=text.ClientSize.Height;
        }
        internal DetailPreview(string name)
        {
            Name=name; ReadOnly=true; Multiline=true; BorderStyle=BorderStyle.None; BackColor=NativeStyle.Surface;
            ForeColor=NativeStyle.Muted; WordWrap=true; DetectUrls=false; ScrollBars=RichTextBoxScrollBars.None;
            Dock=DockStyle.Fill; Margin=new Padding(0,2,0,8);
        }
    }
    internal sealed class MainForm : Form
    {
        private readonly IDesktopAccounts service;
        private readonly Button switchButton, saveButton, deleteButton;
        private readonly DetailPreview basicDetails;
        private readonly Label countLabel, statusLabel, currentLabel;
        private readonly ToolTip toolTip=new ToolTip();
        private readonly System.Windows.Forms.Timer reminderTimer=new System.Windows.Forms.Timer();
        private CatalogEntry[] entries=new CatalogEntry[0];
        private bool loading, busy, closing, readFailed, currentReadFailed;
        private string pendingWorkspaceUser,pendingWorkspaceAccount;
        private DateTimeOffset reminderNow=DateTimeOffset.UtcNow;
        internal readonly TextBox SearchBox;
        internal readonly InputFrame SearchInput;
        internal readonly Button RefreshButton;
        internal readonly ListBox AccountList;
        internal readonly DetailPreview DetailName, QuotaText;
        internal Func<string,bool> ConfirmDelete;
        internal Func<string,string,bool> ConfirmSwitch;
        internal bool OperationBusy { get { return busy; } }
        internal string StatusText { get { return statusLabel.Text; } }
        internal string CountText { get { return countLabel.Text; } }
        internal string CurrentText { get { return currentLabel.Text; } }
        internal string FullDetailsText { get { return DetailName.CompleteText+Environment.NewLine+Environment.NewLine+basicDetails.CompleteText+Environment.NewLine+Environment.NewLine+"上次离开时的额度"+Environment.NewLine+QuotaText.CompleteText+(String.IsNullOrEmpty(StatusText)?"":Environment.NewLine+Environment.NewLine+"操作状态"+Environment.NewLine+StatusText); } }
        internal CatalogEntry SelectedEntry { get { var row=AccountList.SelectedItem as AccountRow; return row==null?null:row.Entry; } }
        internal CatalogEntry EntryAt(int index) { return ((AccountRow)AccountList.Items[index]).Entry; }
        internal void SelectEntry(string name)
        {
            for(int i=0;i<AccountList.Items.Count;i++) if(EntryAt(i).Name==name) { AccountList.SelectedIndex=i; return; }
        }
        internal MainForm(string root) : this(new DesktopAccounts(root)) { }
        internal MainForm(IDesktopAccounts service)
        {
            this.service=service;
            Name="CodexSwitchWindow"; Text="Codex 账号切换"; ShowIcon=false; StartPosition=FormStartPosition.CenterScreen;
            ClientSize=new Size(980,660); MinimumSize=new Size(940,650); Font=new Font("Microsoft YaHei UI",10);
            ForeColor=NativeStyle.Ink; BackColor=NativeStyle.Surface; AutoScaleMode=AutoScaleMode.Font;
            var layout=new TableLayoutPanel { Dock=DockStyle.Fill, ColumnCount=2, RowCount=1, Margin=Padding.Empty };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,280)); layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent,100)); Controls.Add(layout);
            var sidebar=new TableLayoutPanel { Dock=DockStyle.Fill, ColumnCount=1, RowCount=8, BackColor=NativeStyle.Sidebar, Padding=new Padding(22,26,22,22), Margin=Padding.Empty };
            foreach(int height in new[]{48,36,26,46,42}) sidebar.RowStyles.Add(new RowStyle(SizeType.Absolute,height));
            sidebar.RowStyles.Add(new RowStyle(SizeType.Percent,100)); sidebar.RowStyles.Add(new RowStyle(SizeType.Absolute,14)); sidebar.RowStyles.Add(new RowStyle(SizeType.Absolute,44));
            sidebar.Controls.Add(NativeStyle.Label("Codex Switch",15,true),0,0);
            currentLabel=NativeStyle.Label("",10,true); currentLabel.Name="CurrentLoginLabel"; currentLabel.AutoEllipsis=true;
            sidebar.Controls.Add(currentLabel,0,1); sidebar.Controls.Add(NativeStyle.Label("搜索账号",9,false),0,2);
            SearchBox=new TextBox { Name="SearchBox", AccessibleName="搜索账号名称或邮箱", TabIndex=0 };
            SearchInput=new InputFrame(SearchBox); SearchInput.Height=40;
            RefreshButton=new RefreshGlyphButton(); SearchInput.Controls.Add(RefreshButton);
            SearchInput.ReserveAdornment(RefreshButton); toolTip.SetToolTip(RefreshButton,"刷新账号列表（仅重读本机状态）");
            sidebar.Controls.Add(SearchInput,0,3);
            countLabel=NativeStyle.Label("已保存的账号",9,false); countLabel.Name="CountLabel"; countLabel.AutoEllipsis=true; sidebar.Controls.Add(countLabel,0,4);
            AccountList=new ListBox { Name="AccountList", AccessibleName="账号列表，当前登录始终置顶", Dock=DockStyle.Fill, BorderStyle=BorderStyle.None,
                BackColor=NativeStyle.Sidebar, ForeColor=NativeStyle.Ink, DrawMode=DrawMode.OwnerDrawFixed, ItemHeight=66, IntegralHeight=false, TabIndex=2 };
            AccountList.DrawItem+=DrawAccount; sidebar.Controls.Add(AccountList,0,5);
            saveButton=NativeStyle.Button("SaveButton","保存当前登录",false); saveButton.Dock=DockStyle.Fill; saveButton.Margin=Padding.Empty; sidebar.Controls.Add(saveButton,0,7);
            layout.Controls.Add(sidebar,0,0);
            var detail=new TableLayoutPanel { Dock=DockStyle.Fill, ColumnCount=1, RowCount=10, Margin=Padding.Empty, Padding=new Padding(30,24,30,16) };
            foreach(int height in new[]{48,30,20,64,128,30}) detail.RowStyles.Add(new RowStyle(SizeType.Absolute,height));
            detail.RowStyles.Add(new RowStyle(SizeType.Percent,100)); detail.RowStyles.Add(new RowStyle(SizeType.Absolute,30)); detail.RowStyles.Add(new RowStyle(SizeType.Absolute,46)); detail.RowStyles.Add(new RowStyle(SizeType.Absolute,60));
            detail.Controls.Add(DetailLabel.Create("切换账号",23,true),0,0); detail.Controls.Add(DetailLabel.Create("浏览账号后，明确点击切换或在列表按 Enter。",10,false),0,1);
            detail.Controls.Add(DetailLabel.Create("正在查看",9,false),0,2);
            DetailName=new DetailPreview("DetailNameLabel"); DetailName.AccessibleName="正在查看的账号名称，可查看完整详情"; DetailName.Font=new Font("Microsoft YaHei UI",18,FontStyle.Bold); DetailName.ForeColor=NativeStyle.Ink; detail.Controls.Add(DetailName,0,3);
            basicDetails=new DetailPreview("AccountDetails"); basicDetails.AccessibleName="账号身份信息，可查看完整详情"; detail.Controls.Add(basicDetails,0,4);
            detail.Controls.Add(DetailLabel.Create("上次离开时的额度",11,true),0,5);
            QuotaText=new DetailPreview("QuotaText"); QuotaText.AccessibleName="额度快照、成功时间、失败状态及各窗口到点提醒，可查看完整详情"; detail.Controls.Add(QuotaText,0,6);
            var complete=new LinkLabel { Name="ViewFullDetailsButton", Text="查看完整详情", AccessibleName="查看完整详情，可选择并复制全部内容",
                AutoSize=true, UseCompatibleTextRendering=true, Margin=Padding.Empty, TabStop=true, TabIndex=6, LinkColor=NativeStyle.Primary, ActiveLinkColor=NativeStyle.Primary };
            complete.LinkClicked+=delegate { using(var dialog=new FullDetailsDialog(FullDetailsText)) dialog.ShowDialog(this); };
            detail.Controls.Add(complete,0,7);
            var actions=new FlowLayoutPanel { Dock=DockStyle.Fill, Margin=new Padding(1,0,0,0), WrapContents=false };
            switchButton=NativeStyle.Button("SwitchButton","切换到此账号",true); switchButton.Width=180;
            deleteButton=NativeStyle.Button("DeleteButton","删除账号",false); deleteButton.Width=112;
            actions.Controls.Add(switchButton); actions.Controls.Add(deleteButton); detail.Controls.Add(actions,0,8);
            statusLabel=DetailLabel.Create("",10,false); statusLabel.Name="StatusLabel"; statusLabel.AccessibleName="操作状态"; statusLabel.Padding=new Padding(0,6,0,6); statusLabel.AutoEllipsis=true; detail.Controls.Add(statusLabel,0,9); layout.Controls.Add(detail,1,0);
            bool compactDetails=false;
            Resize+=delegate {
                bool compact=ClientSize.Height<660; if(compact==compactDetails) return; compactDetails=compact;
                detail.SuspendLayout();
                int[] heights=compact?new[]{0,0,0,64,128,26,0,24,44,46}:new[]{48,30,20,64,128,30,0,30,46,60};
                for(int row=0;row<heights.Length;row++) if(row!=6) detail.RowStyles[row].Height=heights[row];
                detail.GetControlFromPosition(0,0).Visible=detail.GetControlFromPosition(0,1).Visible=detail.GetControlFromPosition(0,2).Visible=!compact;
                detail.Padding=compact?new Padding(30,18,30,12):new Padding(30,24,30,16);
                detail.ResumeLayout();
            };
            SearchBox.TextChanged+=delegate { if(!loading) RenderAccounts(null); };
            AccountList.SelectedIndexChanged+=delegate { if(!loading) UpdateDetail(); };
            AccountList.KeyDown+=delegate(object sender,KeyEventArgs e) { if(e.KeyCode==Keys.Enter) { e.SuppressKeyPress=true; InvokeSelectedAccount(); } };
            RefreshButton.Click+=delegate {
                if(pendingWorkspaceUser!=null) RefreshPendingWorkspace();
                else { RefreshAccounts(null); if(!readFailed && !currentReadFailed) SetStatus("已刷新本机账号列表。"); }
            };
            switchButton.Click+=delegate { InvokeSelectedAccount(); }; saveButton.Click+=delegate { SaveCurrentAccount(); }; deleteButton.Click+=delegate { DeleteSelectedAccount(); };
            ConfirmDelete=delegate(string target) { using(var dialog=new ConfirmDeleteDialog(target)) return dialog.ShowDialog(this)==DialogResult.OK; };
            ConfirmSwitch=delegate(string phase,string target) { using(var dialog=new ConfirmSwitchDialog(phase,target)) return dialog.ShowDialog(this)==DialogResult.OK; };
            reminderTimer.Interval=1000; reminderTimer.Tick+=delegate { UpdateReminders(DateTimeOffset.UtcNow); }; reminderTimer.Start();
            Activated+=delegate { UpdateReminders(DateTimeOffset.UtcNow); RefreshPendingWorkspace(); };
            Shown+=delegate { AccountList.Focus(); };
            FormClosing+=delegate(object sender,FormClosingEventArgs e) { if(busy) { e.Cancel=true; SetStatus("操作进行中，请等待结果后关闭窗口。"); } else closing=true; };
            RefreshAccounts(null);
        }
        private void DrawAccount(object sender,DrawItemEventArgs e)
        {
            if(e.Index<0 || e.Index>=AccountList.Items.Count) return;
            var row=(AccountRow)AccountList.Items[e.Index]; bool selected=(e.State&DrawItemState.Selected)!=0;
            Color background=selected ? (SystemInformation.HighContrast?SystemColors.Highlight:Color.FromArgb(220,232,241)):NativeStyle.Sidebar;
            Color foreground=selected&&SystemInformation.HighContrast?SystemColors.HighlightText:NativeStyle.Ink;
            using(var brush=new SolidBrush(background)) e.Graphics.FillRectangle(brush,e.Bounds);
            if(row.Entry.IsCurrent) using(var brush=new SolidBrush(SystemInformation.HighContrast?foreground:NativeStyle.Primary)) e.Graphics.FillRectangle(brush,e.Bounds.X,e.Bounds.Y+9,3,e.Bounds.Height-18);
            int inset=10; var nameBounds=new Rectangle(e.Bounds.X+inset,e.Bounds.Y+6,Math.Max(0,e.Bounds.Width-20),25);
            TextRenderer.DrawText(e.Graphics,row.Entry.Name,e.Font,nameBounds,foreground,TextFormatFlags.SingleLine|TextFormatFlags.EndEllipsis|TextFormatFlags.NoPrefix);
            string secondary=row.Entry.IsCurrent?"当前登录"+(row.Entry.IsSaved?"":" · 未保存"):
                String.IsNullOrWhiteSpace(row.Entry.Email) || row.Entry.Email=="未知"?"":row.Entry.Email;
            if(!String.IsNullOrEmpty(row.Reminder)) secondary=String.IsNullOrEmpty(secondary)?row.Reminder:secondary+" · "+row.Reminder;
            var subBounds=new Rectangle(e.Bounds.X+inset,e.Bounds.Y+33,Math.Max(0,e.Bounds.Width-20),25);
            using(var font=new Font(Font.FontFamily,8.5f,row.Reminder!=null&&row.Reminder.Contains("耗尽")?FontStyle.Bold:FontStyle.Regular))
                TextRenderer.DrawText(e.Graphics,secondary??"",font,subBounds,foreground,TextFormatFlags.SingleLine|TextFormatFlags.EndEllipsis|TextFormatFlags.NoPrefix);
            e.DrawFocusRectangle();
        }
        private static string Known(string value) { return String.IsNullOrWhiteSpace(value)?"未知":value; }
        private static bool IsKnownIdentity(string value) { return !String.IsNullOrWhiteSpace(value) && value!="未知"; }
        private void RefreshPendingWorkspace()
        {
            if(busy || pendingWorkspaceUser==null) return;
            RefreshAccounts(null);
            if(readFailed || currentReadFailed) return;
            foreach(var entry in entries)
                if(entry.IsCurrent && entry.CurrentIdentityVerified && entry.LoginType=="ChatGPT" && entry.UserId==pendingWorkspaceUser && entry.AccountId==pendingWorkspaceAccount)
                {
                    pendingWorkspaceUser=pendingWorkspaceAccount=null;
                    SetStatus("已从本机登录信息确认目标用户和工作区。"); return;
                }
            SetStatus("仍未确认目标工作区（ID："+pendingWorkspaceAccount+"）。请在 Codex 的账号菜单中选择该工作区，然后回到此窗口自动核对。");
        }
        private static string Identity(CatalogEntry entry) { return (entry.IsCurrent?"current:":"saved:")+entry.Name; }
        internal void RefreshAccounts(string selected)
        {
            try
            {
                entries=service.List()??new CatalogEntry[0]; readFailed=false; currentReadFailed=service.CurrentReadFailed;
                if(currentReadFailed) SetStatus("无法识别当前登录，已保存的账号仍可浏览。请检查当前登录状态后刷新。");
            }
            catch { readFailed=true; SetStatus("列表刷新失败，保留上次显示。无法识别当前登录，请检查目录权限后重试。"); }
            reminderNow=DateTimeOffset.UtcNow; RenderAccounts(selected);
        }
        private void RenderAccounts(string selected)
        {
            string old=selected!=null?null:SelectedEntry==null?null:Identity(SelectedEntry);
            string query=SearchBox.Text.Trim(); int saved=0,matched=0; CatalogEntry current=null;
            foreach(var entry in entries) { if(entry.IsCurrent && current==null) current=entry; if(entry.IsSaved) saved++; }
            currentLabel.Text=readFailed || currentReadFailed?"无法识别当前登录":current==null?"未登录":current.IsSaved?"当前登录已置顶":"当前登录 · 未保存";
            var visible=new System.Collections.Generic.List<CatalogEntry>();
            if(current!=null) visible.Add(current);
            foreach(var entry in entries)
            {
                bool match=query.Length==0 || (entry.Name??"").IndexOf(query,StringComparison.OrdinalIgnoreCase)>=0 || (entry.Email??"").IndexOf(query,StringComparison.OrdinalIgnoreCase)>=0;
                if(entry.IsSaved && match) matched++;
                if(!entry.IsCurrent && match) visible.Add(entry);
            }
            bool rebuild=visible.Count!=AccountList.Items.Count;
            if(!rebuild) for(int index=0;index<visible.Count;index++) if(Identity(visible[index])!=Identity(EntryAt(index))) { rebuild=true; break; }
            loading=true; if(rebuild) AccountList.BeginUpdate();
            try
            {
                if(rebuild)
                {
                    AccountList.Items.Clear(); foreach(var entry in visible) AccountList.Items.Add(new AccountRow(entry));
                }
                else for(int index=0;index<visible.Count;index++) ((AccountRow)AccountList.Items[index]).Entry=visible[index];
                int selection=-1;
                for(int i=0;i<AccountList.Items.Count;i++) if(selected!=null?EntryAt(i).Name==selected:Identity(EntryAt(i))==old) { selection=i; break; }
                if(selection<0 && AccountList.Items.Count>0) selection=0; AccountList.SelectedIndex=selection;
                countLabel.Text=query.Length==0?"已保存 "+saved+" 个账号":matched==0?"没有匹配的已保存账号":"匹配 "+matched+" / 已保存 "+saved;
                UpdateRowReminders();
            }
            finally { loading=false; if(rebuild) AccountList.EndUpdate(); UpdateDetail(); }
        }
        private static string DueText(CatalogEntry entry,DateTimeOffset now)
        {
            int due=0; bool exhausted=false;
            if(entry.Windows!=null) foreach(var window in entry.Windows) if(window.ResetAtUtc.HasValue && now>=window.ResetAtUtc.Value) { due++; exhausted|=window.Exhausted; }
            if(due==0) return null;
            return exhausted?"上次耗尽 · 已到点":due==1?"窗口已到点":due+" 个窗口已到点";
        }
        private void UpdateRowReminders()
        {
            for(int index=0;index<AccountList.Items.Count;index++)
            {
                var row=(AccountRow)AccountList.Items[index]; string reminder=DueText(row.Entry,reminderNow);
                row.Reminder=reminder;
                string signature=row.Signature(); if(signature==row.DisplaySignature) continue;
                row.DisplaySignature=signature;
                if(AccountList.IsHandleCreated) AccountList.Invalidate(AccountList.GetItemRectangle(index));
            }
        }
        internal void UpdateReminders(DateTimeOffset now)
        {
            reminderNow=now; UpdateRowReminders(); UpdateQuotaText();
        }
        private void UpdateDetail()
        {
            var selected=SelectedEntry; UpdateActions();
            if(selected==null)
            {
                DetailName.CompleteText=readFailed?"暂时无法读取账号":currentReadFailed?"无法识别当前登录":entries.Length==0?"还没有已保存的账号":"没有匹配的账号";
                basicDetails.CompleteText=readFailed?"请检查本机账号目录权限后刷新。":currentReadFailed?"当前登录暂时无法读取。请检查当前登录状态后刷新。":"先登录 Codex，再使用左侧“保存当前登录”添加账号。";
                QuotaText.CompleteText="尚无可显示的额度快照。"; return;
            }
            DetailName.CompleteText=selected.Name; toolTip.SetToolTip(DetailName,selected.Name);
            basicDetails.CompleteText=(selected.IsCurrent?"当前登录"+(selected.IsSaved?"":" · 未保存")+Environment.NewLine:"")+"邮箱："+Known(selected.Email)+Environment.NewLine+"登录类型："+Known(selected.LoginType)+Environment.NewLine+"套餐："+Known(selected.Plan)+Environment.NewLine+"账号 ID："+Known(selected.AccountId);
            UpdateQuotaText();
        }
        private void UpdateQuotaText()
        {
            var selected=SelectedEntry; if(selected==null) return;
            var text=new System.Text.StringBuilder();
            var summary=new System.Text.StringBuilder();
            string captured=selected.CapturedAtUtc.HasValue?"上次成功："+selected.CapturedAtUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz"):"尚无成功记录。切离这个账号时会尝试保存。";
            text.AppendLine(captured); summary.AppendLine(captured);
            text.AppendLine(AccountService.FormatQuotaDetails(selected,reminderNow));
            if(selected.Windows!=null) foreach(var window in QuotaReader.Order(selected.Windows))
            {
                string line=QuotaReader.FormatWindowSummary(window,reminderNow);
                bool due=window.ResetAtUtc.HasValue && reminderNow>=window.ResetAtUtc.Value;
                if(due) line+=" · "+(window.Exhausted?"上次已用尽 · 已到点":"已到点");
                summary.AppendLine(line);
                if(due) text.AppendLine(QuotaReader.WindowLabel(window)+"："+(window.Exhausted?"上次已用尽 · 已到点":"已到点"));
            }
            if(!String.IsNullOrWhiteSpace(selected.QuotaSummary)) summary.AppendLine(selected.QuotaSummary);
            if(selected.Windows!=null && selected.Windows.Length>0) text.Append("到点仅为时间提醒，剩余额度以新快照为准。");
            if(!String.IsNullOrEmpty(selected.SnapshotFailure) && summary.ToString().IndexOf(selected.SnapshotFailure,StringComparison.Ordinal)<0) summary.Insert(0,selected.SnapshotFailure+Environment.NewLine);
            QuotaText.SetContent(text.ToString(),summary.ToString().TrimEnd());
        }
        private void SetStatus(string value) { statusLabel.Text=value; statusLabel.BackColor=String.IsNullOrEmpty(value)?NativeStyle.Surface:NativeStyle.Sidebar; toolTip.SetToolTip(statusLabel,value); }
        private void SetResultStatus(string value) { SetStatus(value+(readFailed?" 列表刷新失败，当前显示可能已过时，请刷新重试。":currentReadFailed?" 无法识别当前登录，已保存的账号仍可浏览。":"")); }
        private void UpdateActions()
        {
            var selected=SelectedEntry;
            switchButton.Enabled=!busy && selected!=null && selected.IsSaved && !selected.IsCurrent;
            deleteButton.Enabled=!busy && selected!=null && selected.IsSaved;
            bool hasCurrent=false; foreach(var entry in entries) hasCurrent|=entry.IsCurrent;
            saveButton.Enabled=!busy && hasCurrent && !readFailed && !currentReadFailed;
            SearchBox.Enabled=AccountList.Enabled=RefreshButton.Enabled=!busy;
        }
        private void SetBusy(bool value) { busy=value; UpdateActions(); }
        internal void InvokeSelectedAccount()
        {
            var selected=SelectedEntry; if(busy || selected==null || !selected.IsSaved || selected.IsCurrent || !IsHandleCreated) return;
            pendingWorkspaceUser=pendingWorkspaceAccount=null;
            string target=selected.Name,snapshot=selected.SnapshotId; SetBusy(true); SetStatus("正在切换并保存离开时快照，请稍候…");
            var worker=new Thread(delegate()
            {
                OperationResult result; bool forcePrompted=false;
                try
                {
                    result=service.Switch(target,snapshot,delegate(string phase,string account)
                    {
                        if(closing || IsDisposed || !IsHandleCreated) return false;
                        if(account!=target) return false;
                        if(phase=="Close") return true;
                        if(phase!="Force" || forcePrompted) return false;
                        forcePrompted=true;
                        return (bool)Invoke(new Func<bool>(delegate { return ConfirmSwitch("Force",account); }));
                    });
                }
                catch { result=new OperationResult(false,"Unavailable","切换未完成，请检查本机账号目录和桌面端状态后重试。"); }
                try
                {
                    if(closing || IsDisposed || !IsHandleCreated) return;
                    BeginInvoke((MethodInvoker)delegate {
                        SetBusy(false); RefreshAccounts(target); SetResultStatus(result.Message);
                        if(result.Code=="WorkspaceSelectionRequired" && IsKnownIdentity(result.TargetUserId) && IsKnownIdentity(result.TargetAccountId))
                        {
                            pendingWorkspaceUser=result.TargetUserId; pendingWorkspaceAccount=result.TargetAccountId;
                            SetResultStatus(result.Message+" 完成后回到此窗口，将自动核对当前用户和工作区。");
                        }
                    });
                }
                catch(InvalidOperationException) { }
            });
            worker.IsBackground=true; worker.SetApartmentState(ApartmentState.STA); worker.Start();
        }
        private void SaveCurrentAccount()
        {
            if(busy || !saveButton.Enabled) return; SetBusy(true);
            try
            {
                string initialName="";
                foreach(var entry in entries) if(entry.IsCurrent && !String.IsNullOrWhiteSpace(entry.Email) && entry.Email!="未知") { initialName=entry.Email; break; }
                using(var dialog=new SaveDialog(service,initialName))
                {
                    if(dialog.ShowDialog(this)==DialogResult.OK) { RefreshAccounts(dialog.SavedName); SetResultStatus(dialog.ResultMessage); }
                    else SetStatus("已取消保存。");
                }
            }
            finally { SetBusy(false); }
        }
        internal void DeleteSelectedAccount()
        {
            var selected=SelectedEntry; if(busy || selected==null || !selected.IsSaved) return;
            string target=selected.Name,snapshot=selected.SnapshotId; SetBusy(true);
            try
            {
                if(!ConfirmDelete(target)) { SetStatus("已取消删除。"); return; }
                var result=service.Delete(target,snapshot); RefreshAccounts(null); SetResultStatus(result.Message);
            }
            catch { SetStatus("删除未完成，存档可能已改变或目录不可写。请刷新列表后重试。"); }
            finally { SetBusy(false); }
        }
        protected override void Dispose(bool disposing)
        {
            if(disposing) { closing=true; reminderTimer.Stop(); reminderTimer.Dispose(); toolTip.Dispose(); service.Dispose(); }
            base.Dispose(disposing);
        }
    }
    internal sealed class FullDetailsDialog : Form
    {
        internal readonly RichTextBox DetailsText;
        internal FullDetailsDialog(string details)
        {
            Name="FullAccountDetailsDialog"; Text="完整账号详情"; AccessibleName=Text; ShowIcon=false; ShowInTaskbar=false;
            StartPosition=FormStartPosition.CenterParent; MinimizeBox=MaximizeBox=false;
            Font=new Font("Microsoft YaHei UI",10); AutoScaleMode=AutoScaleMode.Font;
            ClientSize=new Size(680,520); MinimumSize=new Size(480,360); BackColor=NativeStyle.Surface;
            var layout=new TableLayoutPanel { Dock=DockStyle.Fill, Padding=new Padding(24), ColumnCount=1, RowCount=3 };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute,44)); layout.RowStyles.Add(new RowStyle(SizeType.Percent,100)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute,44));
            layout.Controls.Add(DetailLabel.Create("完整账号详情",15,true),0,0);
            DetailsText=NativeStyle.ReadOnlyText("CompleteAccountDetails"); DetailsText.AccessibleName="完整账号详情，可按 Ctrl+A 全选、Ctrl+C 复制";
            DetailsText.Text=details; DetailsText.TabIndex=0; layout.Controls.Add(DetailsText,0,1);
            var close=NativeStyle.Button("CloseFullDetailsButton","关闭",false); close.DialogResult=DialogResult.Cancel; close.TabIndex=1;
            close.Anchor=AnchorStyles.Left|AnchorStyles.Bottom; layout.Controls.Add(close,0,2);
            Controls.Add(layout); CancelButton=close;
            Shown+=delegate { DetailsText.Select(0,0); DetailsText.Focus(); };
        }
    }
    internal class NoticeDialog : Form
    {
        internal readonly RichTextBox MessageText;
        protected readonly Button CancelAction;
        protected readonly FlowLayoutPanel Actions;
        internal NoticeDialog(string title,string message)
        {
            Text="Codex 账号切换"; ShowIcon=false; ShowInTaskbar=false; FormBorderStyle=FormBorderStyle.FixedDialog;
            MaximizeBox=MinimizeBox=false; StartPosition=FormStartPosition.CenterParent; Font=new Font("Microsoft YaHei UI",10);
            BackColor=NativeStyle.Surface; ForeColor=NativeStyle.Ink; AutoScaleMode=AutoScaleMode.Font; ClientSize=new Size(560,320);
            var layout=new TableLayoutPanel { Dock=DockStyle.Fill, Padding=new Padding(24), ColumnCount=1, RowCount=3 };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute,50)); layout.RowStyles.Add(new RowStyle(SizeType.Percent,100)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute,42));
            layout.Controls.Add(NativeStyle.Label(title,15,true),0,0);
            MessageText=NativeStyle.ReadOnlyText("NoticeMessage"); MessageText.Text=message; MessageText.TabIndex=1; layout.Controls.Add(MessageText,0,1);
            Actions=new FlowLayoutPanel { Dock=DockStyle.Fill, FlowDirection=FlowDirection.RightToLeft, Margin=Padding.Empty };
            CancelAction=NativeStyle.Button("NoticeClose","知道了",true); CancelAction.Width=96; CancelAction.DialogResult=DialogResult.Cancel; CancelAction.TabIndex=0; Actions.Controls.Add(CancelAction);
            layout.Controls.Add(Actions,0,2); Controls.Add(layout); CancelButton=AcceptButton=CancelAction; ActiveControl=CancelAction;
            Shown+=delegate { CancelAction.Select(); };
        }
        protected void AddApproval(string name,string text)
        {
            CancelAction.Name="ConfirmCancel"; CancelAction.Text=CancelAction.AccessibleName="取消";
            var approve=NativeStyle.Button(name,text,false); approve.Width=150; approve.DialogResult=DialogResult.OK; approve.TabIndex=1;
            Actions.Controls.Add(approve); Actions.Controls.SetChildIndex(approve,0); AcceptButton=CancelButton=CancelAction;
        }
    }
    internal sealed class ConfirmDeleteDialog : NoticeDialog
    {
        internal ConfirmDeleteDialog(string name) : base("删除这个已保存的账号？","目标账号为“"+name+"”。"+Environment.NewLine+"此操作删除已保存的本地存档，不会退出当前登录。")
        { Name="DeleteAccountDialog"; AddApproval("DeleteConfirm","删除账号"); }
    }
    internal sealed class ConfirmSwitchDialog : NoticeDialog
    {
        internal ConfirmSwitchDialog(string phase,string target) : base(phase=="Force"?"强制结束桌面端？":"关闭桌面端后切换？",
            "目标账号为“"+target+"”。"+Environment.NewLine+(phase=="Force"?"桌面端仍未退出。强制结束可能丢失未保存的内容，请先确认工作已保存。选择取消将停止本次切换。":"需要先关闭正在运行的 Codex 桌面端。请先保存工作，关闭后才能继续切换。选择取消将停止本次切换。"))
        { Name=phase=="Force"?"ForceDesktopDialog":"CloseDesktopDialog"; AddApproval("Approve"+phase,phase=="Force"?"强制结束并继续":"关闭并继续"); }
    }
    internal sealed class SaveDialog : Form
    {
        internal readonly TextBox NameBox;
        internal readonly InputFrame NameInput;
        internal readonly Label ErrorText;
        internal readonly Button SaveButton;
        internal string SavedName { get; private set; }
        internal string ResultMessage { get; private set; }
        internal SaveDialog(IDesktopAccounts service) : this(service, "") { }
        internal SaveDialog(IDesktopAccounts service,string initialName)
        {
            Name="SaveAccountDialog"; Text="保存当前登录"; ShowIcon=false; ShowInTaskbar=false; FormBorderStyle=FormBorderStyle.FixedDialog;
            MaximizeBox=MinimizeBox=false; ClientSize=new Size(480,320); StartPosition=FormStartPosition.CenterParent;
            Font=new Font("Microsoft YaHei UI",10); AutoScaleMode=AutoScaleMode.Font; BackColor=NativeStyle.Surface; ForeColor=NativeStyle.Ink;
            var layout=new TableLayoutPanel { Dock=DockStyle.Fill, ColumnCount=1, RowCount=6, Padding=new Padding(24) };
            foreach(int height in new[]{44,44,26,42}) layout.RowStyles.Add(new RowStyle(SizeType.Absolute,height));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent,100)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute,42));
            layout.Controls.Add(NativeStyle.Label("给当前登录起个名称",15,true),0,0); layout.Controls.Add(NativeStyle.Label("保存当前登录，之后可从左侧列表切换。",10,false),0,1); layout.Controls.Add(NativeStyle.Label("账号名称",9,false),0,2);
            NameBox=new TextBox { Name="AccountNameBox", AccessibleName="账号名称", MaxLength=100, TabIndex=0 }; NameInput=new InputFrame(NameBox); layout.Controls.Add(NameInput,0,3);
            NameBox.Text=initialName??"";
            ErrorText=NativeStyle.Label("",10,false); ErrorText.Name="SaveErrorLabel"; ErrorText.ForeColor=NativeStyle.Error; layout.Controls.Add(ErrorText,0,4);
            var actions=new FlowLayoutPanel { Dock=DockStyle.Fill, FlowDirection=FlowDirection.RightToLeft, Margin=Padding.Empty };
            SaveButton=NativeStyle.Button("ConfirmSaveButton","保存账号",true); SaveButton.Width=112; SaveButton.TabIndex=1;
            var cancel=NativeStyle.Button("CancelSaveButton","取消",false); cancel.Width=88; cancel.DialogResult=DialogResult.Cancel; cancel.TabIndex=2;
            actions.Controls.Add(SaveButton); actions.Controls.Add(cancel); layout.Controls.Add(actions,0,5); Controls.Add(layout);
            AcceptButton=SaveButton; CancelButton=cancel; ActiveControl=NameBox;
            NameBox.TextChanged+=delegate { ErrorText.Text=""; NameInput.HasError=false; };
            SaveButton.Click+=delegate
            {
                SaveButton.Enabled=false;
                try
                {
                    var result=service.Save(NameBox.Text);
                    if(result.Success) { SavedName=NameBox.Text; ResultMessage=result.Message; DialogResult=DialogResult.OK; Close(); }
                    else { ErrorText.Text=result.Message; NameInput.HasError=true; NameBox.Focus(); }
                }
                catch { ErrorText.Text="保存未完成，请检查账号目录权限后重试。"; NameInput.HasError=true; NameBox.Focus(); }
                finally { SaveButton.Enabled=true; }
            };
            Shown+=delegate { NameBox.Focus(); };
        }
    }
}
