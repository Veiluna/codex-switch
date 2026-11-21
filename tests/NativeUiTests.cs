using System;
using System.Drawing;
using System.Threading;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace CodexSwitch
{
    internal static class NativeUiTests
    {
        private static int checks;
        [DllImport("user32.dll",EntryPoint="GetWindowLongW")] private static extern int GetWindowLong(IntPtr window,int index);
        [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr window,IntPtr device,int flags);
        [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr window,out RectangleEdges edges);
        [StructLayout(LayoutKind.Sequential)] private struct RectangleEdges { internal int Left,Top,Right,Bottom; }
        private static void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; }
        private static void CheckFullyVisible(RichTextBox text,string message)
        {
            var first=text.GetPositionFromCharIndex(0); var last=text.GetPositionFromCharIndex(Math.Max(0,text.TextLength-1));
            Check(first.Y>=0 && last.Y+text.Font.Height<=text.ClientSize.Height,message+" (first="+first+", last="+last+", height="+text.Height+", line="+text.Font.Height+")");
        }
        private static void Capture(Form form,string path)
        {
            form.Activate(); form.Refresh(); Application.DoEvents(); Thread.Sleep(150); Application.DoEvents();
            // PrintWindow renders this DPI-unaware process in its logical client coordinates.
            // Capture only the test window's own rendering, never pixels from another foreground app.
            RectangleEdges client; GetClientRect(form.Handle,out client);
            using(var image=new Bitmap(client.Right-client.Left,client.Bottom-client.Top))
            {
                using(var graphics=Graphics.FromImage(image))
                {
                    IntPtr device=graphics.GetHdc();
                    try { Check(PrintWindow(form.Handle,device,1),"窗口自身截图失败"); }
                    finally { graphics.ReleaseHdc(device); }
                }
                image.Save(path);
            }
        }
        private static string ArtifactPath(string path,string suffix)
        { return System.IO.Path.Combine(Environment.GetEnvironmentVariable("CODEX_SWITCH_UI_EXTRA_ARTIFACTS")??System.IO.Path.GetDirectoryName(path),System.IO.Path.GetFileNameWithoutExtension(path)+suffix+".png"); }
        private static void PumpUntil(Func<bool> predicate)
        {
            DateTime end = DateTime.UtcNow.AddSeconds(8);
            while (!predicate() && DateTime.UtcNow < end) { Application.DoEvents(); Thread.Sleep(10); }
            Check(predicate(), "等待界面结果超时");
        }
        private sealed class FakeService : IDesktopAccounts
        {
            public bool CurrentReadFailed { get; set; }
            internal CatalogEntry[] Entries;
            internal int ListCount, SwitchCount, SaveCount, DeleteCount;
            internal string Target, DeletedSnapshot;
            internal string SwitchMessage;
            internal bool FailList, DelaySwitch, ConfirmBoth, LastApproved;
            internal readonly ManualResetEvent Release = new ManualResetEvent(false);
            public CatalogEntry[] List() { ListCount++; if (FailList) throw new Exception("synthetic"); return Entries; }
            public OperationResult Save(string name) { SaveCount++; return new OperationResult(SaveCount > 1, "Saved", SaveCount > 1 ? "已保存" : "名称已存在"); }
            public OperationResult Delete(string name, string snapshot) { DeleteCount++; DeletedSnapshot = snapshot; return new OperationResult(true, "Deleted", "已删除"); }
            public OperationResult Switch(string name, Func<string,string,bool> confirm)
            {
                Check(Thread.CurrentThread.GetApartmentState() == ApartmentState.STA, "切换服务必须在STA线程");
                SwitchCount++; Target = name;
                if (DelaySwitch) Release.WaitOne(6000);
                LastApproved = !ConfirmBoth || (confirm("Close", name) && confirm("Force", name));
                return new OperationResult(LastApproved, LastApproved ? "Switched" : "Cancelled", SwitchMessage??(LastApproved ? "切换完成" : "已取消切换"));
            }
            public void Dispose() { Release.Dispose(); }
        }
        private static CatalogEntry Entry(string name, bool current, bool saved)
                { return new CatalogEntry { Name = name, IsCurrent = current, IsSaved = saved, SnapshotId = "snapshot-"+name, AccountId = "fixture-"+name, QuotaText = "5小时窗口：上次剩余 0%", Windows = new QuotaWindow[0] }; }
        [STAThread]
        private static int Main()
        {
            try
            {
                Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
                Control.CheckForIllegalCrossThreadCalls = true;
                string longName = new string('长', 96);
                var current = Entry("alpha", true, true);
                var beta = Entry("beta", false, true);
                beta.Email="research.demo@example.invalid"; beta.LoginType="ChatGPT"; beta.Plan="Plus"; beta.AccountId="workspace-demo-0000-1111-2222-333344445555";
                var unreadableFixture=new FakeService { Entries=new[]{beta}, CurrentReadFailed=true };
                using(var unreadable=new MainForm(unreadableFixture))
                {
                    unreadable.Show(); Application.DoEvents();
                    Check(unreadable.CurrentText=="无法识别当前登录","初启活动登录不可读时误报未登录");
                    Check(unreadable.AccountList.Items.Count==1 && unreadable.SelectedEntry.Name=="beta","活动登录不可读时已保存账号不可浏览");
                    Check(unreadable.Controls.Find("SwitchButton",true)[0].Enabled,"活动登录不可读时禁用了已保存目标");
                    Check(!unreadable.Controls.Find("SaveButton",true)[0].Enabled,"活动登录不可读时允许保存未知凭据");
                    unreadableFixture.CurrentReadFailed=false; unreadable.RefreshAccounts(null);
                    Check(unreadable.CurrentText=="未登录","刷新后未重新读取当前登录失败状态");
                    unreadable.Close(); Application.DoEvents();
                }
                using(var unreadableEmpty=new MainForm(new FakeService { Entries=new CatalogEntry[0], CurrentReadFailed=true }))
                {
                    unreadableEmpty.Show(); Application.DoEvents();
                    Check(unreadableEmpty.DetailName.Text=="无法识别当前登录","无存档时当前读取失败仍显示未登录引导");
                    Check(unreadableEmpty.FullDetailsText.Contains(unreadableEmpty.StatusText) && unreadableEmpty.Controls.Find("ViewFullDetailsButton",true)[0].Visible,"空态也必须能用完整详情读取操作状态");
                    unreadableEmpty.Close(); Application.DoEvents();
                }
                beta.CapturedAtUtc = DateTimeOffset.UtcNow.AddHours(-2); beta.SnapshotFailure = "最近更新失败（测试）";
                beta.Windows = new[] { new QuotaWindow { Label="5小时窗口", Exhausted=true, ResetAtUtc=DateTimeOffset.UtcNow.AddSeconds(30) }, new QuotaWindow { Label="周窗口", ResetAtUtc=DateTimeOffset.UtcNow.AddDays(1) } };
                var fake = new FakeService { Entries = new[] { current, beta, Entry(longName,false,true) } };
                using (var form = new MainForm(fake))
                {
                    form.Show(); Application.DoEvents();
                    foreach(string name in new[]{"DetailNameLabel","AccountDetails","QuotaText"})
                    {
                        var preview=(RichTextBox)form.Controls.Find(name,true)[0];
                        Check(preview.ScrollBars==RichTextBoxScrollBars.None,"主详情不得带独立滚动条："+name);
                        Check((GetWindowLong(preview.Handle,-16)&0x300000)==0,"主详情实际窗口仍带滚动条："+name);
                    }
                    var complete=form.Controls.Find("ViewFullDetailsButton",true);
                    Check(complete.Length==1 && complete[0].Visible && complete[0].TabStop,"完整详情入口必须可见且可通过键盘访问");
                    Check(form.AccountList.Items.Count == 3 && form.EntryAt(0).IsCurrent, "当前账号未置顶或重复");
                    form.SearchBox.Text="beta";
                    Check(form.AccountList.Items.Count == 2 && form.EntryAt(0).Name=="alpha", "搜索必须保留当前登录");
                    form.SearchBox.Text="alpha"; Check(form.AccountList.Items.Count==1, "当前匹配不能重复");
                    form.SearchBox.Text="none"; Check(form.AccountList.Items.Count==1 && form.CountText.Contains("没有匹配"), "零匹配仍需当前行");
                    form.SearchBox.Clear(); form.SelectEntry("beta");
                    Check(form.EntryAt(0).IsCurrent && !form.SelectedEntry.IsCurrent, "浏览高亮与当前登录混淆");
                    Check(fake.SwitchCount==0 && form.AcceptButton==null, "浏览或搜索不能切换");
                    Check(form.QuotaText.Text.Contains("最近更新失败") && form.QuotaText.Text.Contains("上次成功"), "快照成功时间与失败状态丢失");
                    int reads=fake.ListCount;
                    form.UpdateReminders(DateTimeOffset.UtcNow.AddMinutes(1));
                    Check(form.QuotaText.Text.Contains("上次已用尽") && form.QuotaText.Text.Contains("5小时窗口"), "耗尽到点提醒未显示");
                    Check(!form.QuotaText.Text.Contains("周窗口：已到点"), "不同窗口不应一并到点");
                    Check(fake.ListCount==reads, "到点提醒不得重复读磁盘");
                    Check(form.RefreshButton.Parent==form.SearchInput && form.RefreshButton.AccessibleName=="刷新账号列表", "刷新图标不在搜索框内或缺读屏名称");
                    form.RefreshButton.PerformClick(); Check(fake.ListCount==reads+1,"刷新图标未重读本机目录");
                    Check(form.Controls.Find("QuotaButton",true).Length==0, "不能保留手动额度刷新入口");
                    form.SearchBox.Focus(); Application.DoEvents(); Check(form.SearchInput.Padding.All==2, "聚焦边框不明显");
                    form.SelectEntry(longName); Check(form.DetailName.CompleteText==longName && form.FullDetailsText.Contains(longName), "长名称的完整详情被截断");
                    form.SelectEntry("beta"); form.SearchBox.Text="beta"; form.RefreshAccounts(null);
                    Check(form.SearchBox.Text=="beta" && form.SelectedEntry.Name=="beta", "刷新丢失搜索或浏览目标");
                    fake.FailList=true; form.RefreshAccounts(null);
                    Check(form.AccountList.Items.Count==2 && form.StatusText.Contains("失败"), "读取失败应保留列表并显示错误"); fake.FailList=false;
                    form.ConfirmDelete=delegate { return false; }; form.DeleteSelectedAccount(); Check(fake.DeleteCount==0,"取消删除调用了服务");
                    form.ConfirmDelete=delegate { return true; }; form.DeleteSelectedAccount(); Check(fake.DeletedSnapshot=="snapshot-beta","删除未使用展示快照身份");
                    fake.DelaySwitch=true; fake.ConfirmBoth=true; fake.SwitchMessage="已取消切换。"+new string('状',180)+"状态尾部"; int uiThread=Thread.CurrentThread.ManagedThreadId; int confirmations=0;
                    form.ConfirmSwitch=delegate(string phase,string target) { Check(Thread.CurrentThread.ManagedThreadId==uiThread,"确认未回UI线程"); confirmations++; return phase=="Close"; };
                    form.InvokeSelectedAccount(); PumpUntil(delegate { return fake.SwitchCount==1; });
                    bool pumped=false; form.BeginInvoke((MethodInvoker)delegate { pumped=true; }); PumpUntil(delegate { return pumped; });
                    Check(form.OperationBusy,"等待查询期间未保持忙碌"); fake.Release.Set(); PumpUntil(delegate { return !form.OperationBusy; });
                    Check(confirmations==2 && !fake.LastApproved && fake.SaveCount==0,"Close/Force取消或保存边界错误");
                    Check(form.StatusText==fake.SwitchMessage && form.FullDetailsText.Contains(fake.SwitchMessage),"长操作状态必须完整保留在可键盘访问的全文入口");
                    using(var save=new SaveDialog(fake))
                    {
                        save.Show(form); save.NameBox.Text="retry"; save.SaveButton.PerformClick(); Application.DoEvents();
                        Check(save.Visible && save.ErrorText.Text=="名称已存在" && save.NameInput.HasError,"保存错误不可见");
                        save.NameBox.Text="next"; Check(!save.NameInput.HasError,"修改名称未清理错误");
                        save.SaveButton.PerformClick(); Application.DoEvents(); Check(save.DialogResult==DialogResult.OK,"保存失败后不能重试");
                    }
                    foreach(string phase in new[]{"Close","Force"}) using(var dialog=new ConfirmSwitchDialog(phase,longName))
                    { Check(dialog.AcceptButton==dialog.CancelButton,"桌面确认必须默认取消"); Check(dialog.MessageText.Text.Contains(longName),"确认丢失完整目标"); Check(dialog.MessageText.Text.Contains(phase=="Force"?"未保存":"关闭"),"确认风险文案缺失"); }
                    using(var dialog=new ConfirmDeleteDialog(longName)) { Check(dialog.AcceptButton==dialog.CancelButton,"删除必须默认取消"); Check(dialog.MessageText.Text.Contains(longName),"删除确认丢失名称"); }
                    using(var save=new SaveDialog(fake,"fixture@example.invalid"))
                    {
                        Check(save.NameBox.Text=="fixture@example.invalid","邮箱未预填为可编辑名称");
                        int saveCount=fake.SaveCount; save.Show(form); save.Close(); Application.DoEvents(); Check(fake.SaveCount==saveCount,"取消保存调用了服务");
                    }
                    // Two quota windows and a normal workspace ID must be readable at the actual minimum window size.
                    beta.SnapshotFailure=null;
                    beta.QuotaText="快照时间："+beta.CapturedAtUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz")+Environment.NewLine+
                        QuotaReader.Format("{\"rate_limit\":{\"primary_window\":{\"used_percent\":64,\"limit_window_seconds\":18000,\"reset_at\":2000000000},\"secondary_window\":{\"used_percent\":28,\"limit_window_seconds\":604800,\"reset_at\":2000000000},\"allowed\":true},\"credits\":{\"balance\":\"12.50\",\"unlimited\":false,\"has_credits\":true}}",null).Replace("本次查询","历史快照");
                    form.SearchBox.Clear(); form.SelectEntry("beta"); form.RefreshAccounts("beta"); form.RefreshButton.PerformClick();
                    form.Size=form.MinimumSize; form.PerformLayout(); Application.DoEvents();
                    CheckFullyVisible((RichTextBox)form.Controls.Find("AccountDetails",true)[0],"最小窗口普通邮箱、套餐或账号 ID 被裁切");
                    CheckFullyVisible(form.QuotaText,"最小窗口普通额度或重置时间被裁切");
                    CheckFullyVisible(form.DetailName,"最小窗口普通账号名称被裁切");
                    var identityPreview=(DetailPreview)form.Controls.Find("AccountDetails",true)[0];
                    Check(!form.QuotaText.Text.EndsWith("…") && form.QuotaText.Text.Contains("剩余 36%") && form.QuotaText.Text.Contains("剩余 72%") && form.QuotaText.Text.Contains("Credits 余额：12.50") && form.QuotaText.Text.Contains("周窗口：") && identityPreview.Text.Replace("\r","")==identityPreview.CompleteText.Replace("\r",""),"最小窗口普通账号、真实格式余额或第二次重置不可直读");
                    form.SelectEntry("alpha");
                    CheckFullyVisible((RichTextBox)form.Controls.Find("AccountDetails",true)[0],"最小窗口当前账号身份行被裁切");
                    form.SelectEntry("beta");
                    foreach(int clientHeight in new[]{659,660,680})
                    {
                        form.ClientSize=new Size(980,clientHeight); form.PerformLayout(); Application.DoEvents();
                        Check(!form.QuotaText.Text.EndsWith("…") && form.QuotaText.Text.Contains("Credits 余额：12.50") && form.QuotaText.Text.Contains("周窗口："),"布局高度边界丢失普通额度："+clientHeight);
                        CheckFullyVisible(form.QuotaText,"布局高度边界裁切额度："+clientHeight);
                    }
                    string normalQuota=beta.QuotaText;
                    beta.QuotaText=normalQuota.Replace("Credits 余额：12.50","Credits 余额：未知").Replace("剩余 36%","剩余 0%");
                    beta.SnapshotFailure="最近更新失败（虚构）"; form.RefreshAccounts("beta");
                    Check(form.QuotaText.Text.Contains("最近更新失败") && form.QuotaText.Text.Contains("Credits 余额：未知") && form.QuotaText.Text.Contains("剩余 0%"),"额度摘要丢失失败、未知或真实零值");
                    Check(form.FullDetailsText.Contains("周窗口：") && form.FullDetailsText.Contains("Credits 无限额") && form.FullDetailsText.Contains("剩余比例按"),"完整详情丢失摘要之外的原始字段或重置语义");
                    beta.QuotaText=normalQuota; beta.SnapshotFailure=null; form.RefreshAccounts("beta");
                    form.Size=form.MinimumSize; form.PerformLayout(); Application.DoEvents();
                    using(var shortDialog=new FullDetailsDialog("短详情"))
                    {
                        shortDialog.Show(form); Application.DoEvents();
                        Check((GetWindowLong(shortDialog.DetailsText.Handle,-16)&0x300000)==0,"短详情模态不应显示滚动条");
                        Check(shortDialog.DetailsText.Focused,"完整详情打开后应聚焦可复制正文");
                        shortDialog.Close(); Application.DoEvents();
                    }
                    var expanded=Entry(new string('长',96),false,true); expanded.Email=new string('e',300)+"@example.invalid";
                    expanded.AccountId=new string('i',260); expanded.QuotaText=new string('额',500);
                    expanded.Windows=new QuotaWindow[30];
                    for(int index=0;index<expanded.Windows.Length;index++) expanded.Windows[index]=new QuotaWindow { Label="测试窗口 "+index,ResetAtUtc=DateTimeOffset.UtcNow.AddHours(index+1) };
                    string artifact=Environment.GetEnvironmentVariable("CODEX_SWITCH_UI_ARTIFACT");
                    if(!String.IsNullOrEmpty(artifact))
                    {
                        form.AccountList.Focus(); Capture(form,ArtifactPath(artifact,"-minimum"));
                        form.ClientSize=new Size(980,660); Capture(form,artifact);
                    }
                    fake.Entries=new[]{current,expanded}; form.RefreshAccounts(expanded.Name);
                    Check(form.DetailName.CompleteText==expanded.Name && form.DetailName.Text.Length>1 && form.DetailName.Text.EndsWith("…"),"长名称未用完整行摘要或丢失原文："+form.DetailName.Text+", size="+form.DetailName.ClientSize+", first="+form.DetailName.GetPositionFromCharIndex(0));
                    CheckFullyVisible(form.DetailName,"长名称摘要仍露出半行");
                    CheckFullyVisible(form.QuotaText,"长额度摘要仍露出半行");
                    if(!String.IsNullOrEmpty(artifact)) { form.AccountList.Focus(); Capture(form,ArtifactPath(artifact,"-long")); }
                    bool modalChecked=false;
                    form.BeginInvoke((MethodInvoker)delegate {
                        var modal=Application.OpenForms["FullAccountDetailsDialog"] as FullDetailsDialog;
                        Check(modal!=null && modal.Modal,"完整详情入口未打开模态窗口");
                        Check(modal.DetailsText.Text.Contains(expanded.Name) && modal.DetailsText.Text.Contains(expanded.Email) && modal.DetailsText.Text.Contains(expanded.AccountId) && modal.DetailsText.Text.Contains("测试窗口 29") && modal.DetailsText.Text.Contains(form.StatusText),"完整详情丢失长字段、尾部额度或操作状态");
                        Check(modal.DetailsText.ReadOnly && modal.DetailsText.ShortcutsEnabled,"完整详情必须支持选择复制");
                        Check(modal.DetailsText.Focused,"完整详情未把焦点移到正文");
                        Check((GetWindowLong(modal.DetailsText.Handle,-16)&0x200000)!=0,"超长模态未提供单一滚动区域");
                        modal.DetailsText.SelectAll(); Check(modal.DetailsText.SelectedText==modal.DetailsText.Text,"完整详情无法全选复制");
                        modal.DetailsText.Select(0,0); modal.DetailsText.ScrollToCaret();
                        if(!String.IsNullOrEmpty(artifact)) Capture(modal,ArtifactPath(artifact,"-full"));
                        modalChecked=true;
                        typeof(Form).GetMethod("ProcessDialogKey",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).Invoke(modal,new object[]{Keys.Escape});
                    });
                    ((LinkLabel)complete[0]).Select();
                    // Use the native link's Enter key path to exercise keyboard activation and Esc return.
                    typeof(LinkLabel).GetMethod("OnKeyDown",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).Invoke(complete[0],new object[]{new KeyEventArgs(Keys.Enter)}); Application.DoEvents();
                    Check(modalChecked && Application.OpenForms["FullAccountDetailsDialog"]==null,"Esc 未关闭完整详情");
                    Check(complete[0].Focused,"关闭完整详情后未恢复到入口焦点");
                    fake.Entries=new[]{current,beta}; form.RefreshAccounts("beta");
                    form.ClientSize=new Size(940,650); form.PerformLayout(); Application.DoEvents();
                    Check(form.RefreshButton.Width>=32 && form.RefreshButton.Height>=32,"图标命中区域不足");
                    var bounds=form.RefreshButton.RectangleToScreen(form.RefreshButton.ClientRectangle);
                    Check(form.RectangleToScreen(form.ClientRectangle).Contains(bounds),"刷新控件超出窗体");
                    Check(form.QuotaText.Height>=80,"最小窗口快照详情高度不足");
                    Check(form.Controls.Find("SwitchButton",true)[0].Parent.ClientRectangle.Contains(form.Controls.Find("SwitchButton",true)[0].Bounds),"主操作被裁切");
                    fake.Entries=new[]{Entry("当前登录（未保存）",true,false),beta}; form.RefreshAccounts(null);
                    Check(form.EntryAt(0).ToString()!=null && form.CurrentText.Contains("未保存"),"未知当前未明确展示");
                    fake.Entries=new[]{beta}; form.RefreshAccounts(null); Check(form.CurrentText=="未登录","登录缺失与未知当前未区分");
                    form.Close(); Application.DoEvents();
                }
                Console.WriteLine("PASS Native UI: "+checks+" checks"); return 0;
            }
            catch(Exception ex) { Console.Error.WriteLine("FAIL: "+ex); return 1; }
        }
    }
}
