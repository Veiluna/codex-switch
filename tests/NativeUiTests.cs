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
        [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
        [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr window, out ScreenRect rect);
        [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr window, ref Point point);
        [StructLayout(LayoutKind.Sequential)] private struct ScreenRect { internal int Left,Top,Right,Bottom; }
        private static void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; }
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
                return new OperationResult(LastApproved, LastApproved ? "Switched" : "Cancelled", LastApproved ? "切换完成" : "已取消切换");
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
                    unreadableEmpty.Close(); Application.DoEvents();
                }
                beta.CapturedAtUtc = DateTimeOffset.UtcNow.AddHours(-2); beta.SnapshotFailure = "最近更新失败（测试）";
                beta.Windows = new[] { new QuotaWindow { Label="5小时窗口", Exhausted=true, ResetAtUtc=DateTimeOffset.UtcNow.AddSeconds(30) }, new QuotaWindow { Label="周窗口", ResetAtUtc=DateTimeOffset.UtcNow.AddDays(1) } };
                var fake = new FakeService { Entries = new[] { current, beta, Entry(longName,false,true) } };
                using (var form = new MainForm(fake))
                {
                    form.Show(); Application.DoEvents();
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
                    form.SelectEntry(longName); Check(form.DetailName.Text==longName, "长名称详情被截断");
                    form.SelectEntry("beta"); form.SearchBox.Text="beta"; form.RefreshAccounts(null);
                    Check(form.SearchBox.Text=="beta" && form.SelectedEntry.Name=="beta", "刷新丢失搜索或浏览目标");
                    fake.FailList=true; form.RefreshAccounts(null);
                    Check(form.AccountList.Items.Count==2 && form.StatusText.Contains("失败"), "读取失败应保留列表并显示错误"); fake.FailList=false;
                    form.ConfirmDelete=delegate { return false; }; form.DeleteSelectedAccount(); Check(fake.DeleteCount==0,"取消删除调用了服务");
                    form.ConfirmDelete=delegate { return true; }; form.DeleteSelectedAccount(); Check(fake.DeletedSnapshot=="snapshot-beta","删除未使用展示快照身份");
                    fake.DelaySwitch=true; fake.ConfirmBoth=true; int uiThread=Thread.CurrentThread.ManagedThreadId; int confirmations=0;
                    form.ConfirmSwitch=delegate(string phase,string target) { Check(Thread.CurrentThread.ManagedThreadId==uiThread,"确认未回UI线程"); confirmations++; return phase=="Close"; };
                    form.InvokeSelectedAccount(); PumpUntil(delegate { return fake.SwitchCount==1; });
                    bool pumped=false; form.BeginInvoke((MethodInvoker)delegate { pumped=true; }); PumpUntil(delegate { return pumped; });
                    Check(form.OperationBusy,"等待查询期间未保持忙碌"); fake.Release.Set(); PumpUntil(delegate { return !form.OperationBusy; });
                    Check(confirmations==2 && !fake.LastApproved && fake.SaveCount==0,"Close/Force取消或保存边界错误");
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
                    form.ClientSize=new Size(940,650); form.PerformLayout(); Application.DoEvents();
                    Check(form.RefreshButton.Width>=32 && form.RefreshButton.Height>=32,"图标命中区域不足");
                    var bounds=form.RefreshButton.RectangleToScreen(form.RefreshButton.ClientRectangle);
                    Check(form.RectangleToScreen(form.ClientRectangle).Contains(bounds),"刷新控件超出窗体");
                    Check(form.QuotaText.Height>=80,"最小窗口快照详情高度不足");
                    Check(form.Controls.Find("SwitchButton",true)[0].Parent.ClientRectangle.Contains(form.Controls.Find("SwitchButton",true)[0].Bounds),"主操作被裁切");
                    string artifact=Environment.GetEnvironmentVariable("CODEX_SWITCH_UI_ARTIFACT");
                    if(!String.IsNullOrEmpty(artifact))
                    {
                        form.SearchBox.Clear(); form.SelectEntry("beta"); form.UpdateReminders(DateTimeOffset.UtcNow.AddMinutes(1));
                        form.Activate(); form.Refresh(); Application.DoEvents(); Thread.Sleep(200); Application.DoEvents();
                        IntPtr previous=SetThreadDpiAwarenessContext(new IntPtr(-2));
                        try
                        {
                            ScreenRect screen; GetClientRect(form.Handle,out screen); Point origin=Point.Empty; ClientToScreen(form.Handle,ref origin);
                            using(var image=new Bitmap(screen.Right-screen.Left,screen.Bottom-screen.Top))
                            {
                                using(var graphics=Graphics.FromImage(image)) graphics.CopyFromScreen(origin,Point.Empty,image.Size);
                                image.Save(artifact);
                            }
                        }
                        finally { SetThreadDpiAwarenessContext(previous); }
                    }
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
