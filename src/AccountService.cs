using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;
using Microsoft.Win32.SafeHandles;

namespace CodexSwitch
{
    public sealed class CatalogEntry
    {
        public string Name = "", AccountId = "未知", Plan = "未知", LoginType = "未知", Email = "未知";
        public string SnapshotId = "", QuotaText = "尚无额度快照。", ResetHint = "", SnapshotFailure = "";
        public bool IsCurrent, IsSaved, HasDueExhausted;
        public DateTimeOffset? CapturedAtUtc;
        public DateTimeOffset? FailedAtUtc;
        public QuotaWindow[] Windows = new QuotaWindow[0];
        internal byte[] Bytes;
        internal AccountDetails Details;
    }

    // 两个入口共用此服务；列举本机存档不启动进程或发送凭据。
    public sealed class AccountService : IDisposable
    {
        readonly string root, accounts;
        internal Func<byte[],string> FetchQuota;
        internal Func<DateTimeOffset> Now = delegate { return DateTimeOffset.UtcNow; };
        internal Func<string,string,byte[],Func<string,string,bool>,Action<byte[]>,OperationResult> SwitchBackend;
        bool disposed;
        public bool CurrentReadFailed { get; private set; }

        public AccountService(string root)
        {
            this.root = AccountStore.Root(root);
            accounts = Path.Combine(this.root,"accounts");
            FetchQuota = QuotaReader.FetchForSnapshot;
            SwitchBackend = delegate(string r,string n,byte[] expected,Func<string,string,bool> confirm,Action<byte[]> departure)
            {
                using (var legacy = new LegacyOperations()) return legacy.SwitchShared(r,n,expected,confirm,departure);
            };
        }

        sealed class Gate : IDisposable
        {
            AccountStore.PathGuards guards;
            SafeFileHandle handle;
            internal Gate(string root,string accounts)
            {
                try
                {
                    AssertAncestors(root);
                    Directory.CreateDirectory(root);
                    using (var parent = new AccountStore.PathGuards(root))
                    {
                        AccountStore.Native.AssertPlain(accounts);
                        Directory.CreateDirectory(accounts);
                        guards = new AccountStore.PathGuards(accounts);
                    }
                    handle = AccountStore.Native.OpenLock(Path.Combine(accounts,".switch.lock"));
                }
                catch { Dispose(); throw; }
            }
            public void Dispose() { if(handle!=null) handle.Dispose(); if(guards!=null) guards.Dispose(); }
        }

        static void AssertAncestors(string path)
        {
            string cursor=Path.GetFullPath(path);
            while(cursor!=null)
            {
                AccountStore.Native.AssertPlain(cursor);
                var parent=Directory.GetParent(cursor);
                cursor=parent==null?null:parent.FullName;
            }
        }
        Gate Lock() { if(disposed) throw new ObjectDisposedException("AccountService"); return new Gate(root,accounts); }
        static byte[] Read(string path)
        {
            using(var file=AccountStore.Native.Open(path,false,false)) return AccountStore.Native.Bytes(file);
        }
        static bool Equal(byte[] a,byte[] b)
        {
            if(a==null || b==null) return a==b;
            if(a.Length!=b.Length) return false;
            for(int i=0;i<a.Length;i++) if(a[i]!=b[i]) return false;
            return true;
        }
        static bool Known(string value) { return !String.IsNullOrEmpty(value) && value!="未知"; }
        static string Identity(AccountDetails d)
        {
            return d.LoginType=="ChatGPT" && Known(d.UserId) && Known(d.AccountId)
                ? "user:"+d.UserId.Length.ToString(CultureInfo.InvariantCulture)+":"+d.UserId+":"+d.AccountId : null;
        }
        static bool Matches(CatalogEntry left,CatalogEntry right)
        {
            string id=Identity(left.Details);
            return Equal(left.Bytes,right.Bytes) || (id!=null && id==Identity(right.Details));
        }
        static CatalogEntry Entry(byte[] bytes,string name,bool saved)
        {
            AccountDetails d=AccountStore.Describe(bytes,name);
            return new CatalogEntry { Name=name, IsSaved=saved, Bytes=bytes, Details=d,
                SnapshotId=d.SnapshotId, AccountId=d.AccountId, Email=d.Email, Plan=d.Plan, LoginType=d.LoginType };
        }
        List<CatalogEntry> Archives()
        {
            var result=new List<CatalogEntry>();
            foreach(string name in AccountStore.List(root)) result.Add(Entry(AccountStore.ReadArchive(root,name),name,true));
            return result;
        }
        string Marker()
        {
            string path=Path.Combine(accounts,".current");
            if(!File.Exists(path)) return null;
            string value=AccountStore.Utf8.GetString(Read(path)).TrimStart('\uFEFF').Trim();
            return AccountStore.ValidName(value)?value:null;
        }
        CatalogEntry Resolve(List<CatalogEntry> all,CatalogEntry active)
        {
            var matches=all.FindAll(delegate(CatalogEntry e) { return Matches(e,active); });
            if(matches.Count==1) return matches[0];
            if(matches.Count>1)
            {
                string marker=Marker();
                CatalogEntry selected=matches.Find(delegate(CatalogEntry e) { return String.Equals(e.Name,marker,StringComparison.OrdinalIgnoreCase); });
                if(selected!=null) return selected;
                active.Name="当前登录（多个存档匹配）";
            }
            return active;
        }

        public CatalogEntry[] List()
        {
            using(Lock())
            {
                CurrentReadFailed=false;
                List<CatalogEntry> all=Archives();
                string path=Path.Combine(root,"auth.json");
                try
                {
                    byte[] bytes=OptionalRead(path);
                    if(bytes!=null)
                    {
                        AccountStore.Json(bytes);
                        CatalogEntry active=Entry(bytes,"当前登录（未保存）",false);
                        CatalogEntry representative=Resolve(all,active);
                        if(representative.IsSaved)
                        {
                            all.Remove(representative);
                            // 轮换后展示活动登录元数据，删除确认仍核对存档摘要。
                            representative.AccountId=active.AccountId; representative.Email=active.Email;
                            representative.Plan=active.Plan; representative.LoginType=active.LoginType;
                        }
                        representative.IsCurrent=true;
                        all.Insert(0,representative);
                    }
                }
                catch { CurrentReadFailed=true; }
                foreach(CatalogEntry entry in all) LoadSnapshot(entry);
                return all.ToArray();
            }
        }

        static OperationResult Result(bool ok,string code,string text) { return new OperationResult(ok,code,text); }
        static OperationResult Failed() { return Result(false,"Unavailable","操作未完成：账号目录正忙、存档已变化或文件不可读写，请刷新后重试。"); }
        public OperationResult Save(string name)
        {
            if(name!=null && (!AccountStore.ValidName(name) || name.EndsWith("_auth",StringComparison.OrdinalIgnoreCase)))
                return Result(false,"InvalidName","名称无效，请使用普通名称，勿使用路径字符、备份前缀或 _auth 后缀。");
            try
            {
                using(Lock())
                {
                    string auth=Path.Combine(root,"auth.json");
                    byte[] bytes=OptionalRead(auth);
                    if(bytes==null) return Result(false,"MissingAuth","没有可保存的当前登录，请先登录 Codex。");
                    AccountStore.Json(bytes);
                    CatalogEntry active=Entry(bytes,"",false);
                    List<CatalogEntry> all=Archives();
                    CatalogEntry existing=Resolve(all,active);
                    if(name==null) name=existing.IsSaved?existing.Name:AutomaticName(active);
                    CatalogEntry collision=all.Find(delegate(CatalogEntry e) { return String.Equals(e.Name,name,StringComparison.OrdinalIgnoreCase); });
                    if(collision!=null && (!existing.IsSaved || !String.Equals(collision.Name,existing.Name,StringComparison.OrdinalIgnoreCase)))
                        return Result(false,"NameExists","同名账号已经存在，请换一个名称。");
                    if(!existing.IsSaved && all.Exists(delegate(CatalogEntry e) { return Matches(e,active); }))
                        return Result(false,"AmbiguousIdentity","多个存档匹配当前登录，请先整理重复存档，再保存名称。");
                    Persist(active,existing.IsSaved?existing:null,name);
                    if(!Equal(Read(auth),bytes)) return Result(false,"ConcurrentChange","保存期间当前登录已改变，已保留原快照，请刷新后重试。");
                    Atomic(Path.Combine(accounts,".current"),AccountStore.Utf8.GetBytes(name),OptionalRead(Path.Combine(accounts,".current")),false);
                    return Result(true,"Saved","已保存当前登录为“"+name+"”。");
                }
            }
            catch { return Failed(); }
        }

        void Persist(CatalogEntry active,CatalogEntry existing,string name)
        {
            string destination=existing!=null && String.Equals(existing.Name,name,StringComparison.OrdinalIgnoreCase)
                ? AccountStore.Target(accounts,existing.Name) : Path.Combine(accounts,name+"_auth.json");
            if(existing==null || !String.Equals(existing.Name,name,StringComparison.OrdinalIgnoreCase))
            {
                if(AccountStore.Native.Exists(destination) || AccountStore.Native.Exists(Path.Combine(accounts,name+".json"))) throw new IOException("名称冲突。");
                Atomic(destination,active.Bytes,null,true);
                if(existing!=null)
                {
                    try { DeleteArchive(existing.Name,existing.SnapshotId); }
                    catch
                    {
                        // 只移除仍为本次字节快照的新副本；外部替换后的同名记录必须保留。
                        try { DeleteArchive(name,active.SnapshotId); } catch { }
                        throw;
                    }
                }
            }
            else if(!Equal(existing.Bytes,active.Bytes)) Atomic(destination,active.Bytes,existing.Bytes,true);
        }

        void DeleteArchive(string name,string expected)
        {
            using(var handle=AccountStore.Native.Open(AccountStore.Target(accounts,name),false,true))
            {
                if(expected!=AccountStore.Hash(AccountStore.Native.Bytes(handle))) throw new IOException("存档已变化。");
                var deletion=new AccountStore.Native.Disposition { DeleteFile=true };
                if(!AccountStore.Native.SetFileInformationByHandle(handle,AccountStore.Native.FileDispositionInfo,ref deletion,
                    (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(AccountStore.Native.Disposition)))) throw new IOException("无法删除存档。");
            }
        }
        public OperationResult Delete(string name,string snapshotId)
        {
            if(!AccountStore.ValidName(name) || String.IsNullOrEmpty(snapshotId)) return Result(false,"InvalidSelection","请重新选择已保存账号。");
            try { using(Lock()) { DeleteArchive(name,snapshotId); return Result(true,"Deleted","已删除本地账号“"+name+"”，当前登录已保留。"); } }
            catch { return Failed(); }
        }

        public OperationResult Switch(string target,Func<string,string,bool> confirm)
        {
            return SwitchCore(target,null,confirm);
        }

        public OperationResult Switch(string target,string snapshotId,Func<string,string,bool> confirm)
        {
            if(String.IsNullOrEmpty(snapshotId)) return Result(false,"InvalidSelection","请刷新账号列表后重新选择。");
            return SwitchCore(target,snapshotId,confirm);
        }

        OperationResult SwitchCore(string target,string snapshotId,Func<string,string,bool> confirm)
        {
            if(!AccountStore.ValidName(target)) return Result(false,"InvalidName","请选择已保存的目标账号。");
            try
            {
                using(Lock())
                {
                    CatalogEntry selected=Entry(AccountStore.ReadArchive(root,target),target,true);
                    // A menu selection refers to the archive that was displayed, not a reused alias.
                    // Check under the shared lock before desktop closure, quota capture or writes.
                    if(snapshotId!=null && snapshotId!=selected.SnapshotId)
                        return Result(false,"SelectionChanged","所选账号存档已变化，请刷新账号列表后重新选择。");
                    AccountStore.Json(selected.Bytes);
                    string auth=Path.Combine(root,"auth.json");
                    byte[] current=OptionalRead(auth);
                    if(current!=null)
                    {
                        AccountStore.Json(current);
                        if(Matches(selected,Entry(current,"",false))) return Result(true,"AlreadyCurrent","当前已登录所选账号。");
                    }
                    bool quotaFailed=false;
                    OperationResult result=SwitchBackend(root,target,selected.Bytes,confirm??delegate { return false; },delegate(byte[] latest)
                    {
                        CatalogEntry active=Entry(latest,"",false);
                        AccountStore.Json(latest);
                        CatalogEntry existing=Resolve(Archives(),active);
                        string name=existing.IsSaved?existing.Name:AutomaticName(active);
                        // 必须在替换活动凭据之前保存；保存失败即中止切换。
                        Persist(active,existing.IsSaved?existing:null,name);
                        quotaFailed=!Capture(active);
                    });
                    if(result.Success && quotaFailed) return Result(true,result.Code,result.Message+" 离开账号的额度捕获失败，最近成功快照已保留。");
                    return result;
                }
            }
            catch { return Failed(); }
        }

        string AutomaticName(CatalogEntry active)
        {
            string name=Known(active.Email)?active.Email:Unnamed();
            // 工作区后缀仅用于命名，不能作为身份归属依据。
            if(Known(active.Email) && Known(active.AccountId))
            {
                bool sameEmail=Archives().Exists(delegate(CatalogEntry e) { return e.Email==active.Email && !Matches(e,active); });
                if(sameEmail) name+=" · "+Short(active.AccountId);
            }
            name=System.Text.RegularExpressions.Regex.Replace(name,"[<>:\"/\\\\|?*\\x00-\\x1f]","_").Trim().TrimEnd('.');
            if(!AccountStore.ValidName(name)) name=Unnamed();
            if(name.Length>120) name=name.Substring(0,120);
            string candidate=name;
            int count=0;
            while(AccountStore.Native.Exists(Path.Combine(accounts,candidate+"_auth.json")) || AccountStore.Native.Exists(Path.Combine(accounts,candidate+".json")))
                candidate=name+" "+AccountStore.Hash(active.Bytes).Replace('/','_').Replace('+','-').Substring(0,8)+(++count==1?"":"-"+count.ToString(CultureInfo.InvariantCulture));
            return candidate;
        }

        string Unnamed() { return "未命名账号 · "+Now().ToLocalTime().ToString("yyyyMMddHHmmss",CultureInfo.InvariantCulture); }
        static string Short(string value) { return AccountStore.Hash(AccountStore.Utf8.GetBytes(value)).Replace('/','_').Replace('+','-').Substring(0,8); }
        static byte[] OptionalRead(string path) { return AccountStore.Native.Exists(path)?Read(path):null; }
        // 对比、替换，再核对实际被替换的字节；保留外部竞争版本的凭据备份。
        void Atomic(string path,byte[] bytes,byte[] expected,bool credential)
        {
            AccountStore.Native.AssertPlain(path);
            string temp=Path.Combine(accounts,".service-"+Guid.NewGuid().ToString("N")+".tmp");
            string backup=Path.Combine(accounts,(credential?"backup-":".recovery-")+Guid.NewGuid().ToString("N")+(credential?"_auth.json":".tmp"));
            try
            {
                using(var stream=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None))
                { stream.Write(bytes,0,bytes.Length); stream.Flush(true); }
                if(!Equal(OptionalRead(path),expected)) throw new IOException("并发变更。");
                if(expected==null) File.Move(temp,path);
                else
                {
                    File.Replace(temp,path,backup);
                    if(!Equal(Read(backup),expected))
                    {
                        // 仅在目标仍为本次写入版本时，恢复实际捕获的外部竞争版本。
                        if(Equal(OptionalRead(path),bytes))
                        {
                            string recovery=Path.Combine(accounts,(credential?"backup-":".recovery-")+Guid.NewGuid().ToString("N")+(credential?"_auth.json":".tmp"));
                            File.Replace(backup,path,recovery);
                        }
                        throw new IOException("并发变更。");
                    }
                    if(!credential) File.Delete(backup);
                }
            }
            finally { if(File.Exists(temp)) File.Delete(temp); }
        }

        string SnapshotPath(CatalogEntry entry)
        {
            string key=Identity(entry.Details)??"bytes:"+AccountStore.Hash(entry.Bytes);
            string hash=AccountStore.Hash(AccountStore.Utf8.GetBytes(key)).Replace('/','_').Replace('+','-').TrimEnd('=');
            return Path.Combine(accounts,".quota-"+hash+".snapshot");
        }
        Dictionary<string,object> Snapshot(CatalogEntry entry)
        {
            string path=SnapshotPath(entry);
            if(!File.Exists(path)) return new Dictionary<string,object>();
            return AccountStore.Json(Read(path));
        }
        bool Capture(CatalogEntry entry)
        {
            Dictionary<string,object> snapshot;
            try { snapshot=Snapshot(entry); }
            catch { snapshot=new Dictionary<string,object>(); }
            bool success=false;
            try
            {
                string json=FetchQuota(entry.Bytes);
                string text=QuotaReader.Format(json,Known(entry.AccountId)?entry.AccountId:null,Known(entry.Details.UserId)?entry.Details.UserId:null);
                QuotaWindow[] windows=QuotaReader.Windows(json);
                snapshot["captured_at"]=Now().ToString("o",CultureInfo.InvariantCulture);
                snapshot["quota_text"]=text;
                var saved=new List<object>();
                foreach(var w in windows) saved.Add(new Dictionary<string,object> {
                    {"label",w.Label},{"reset_at",w.ResetAtUtc.HasValue?(object)w.ResetAtUtc.Value.ToString("o",CultureInfo.InvariantCulture):null},{"exhausted",w.Exhausted} });
                snapshot["windows"]=saved;
                snapshot.Remove("failure"); snapshot.Remove("failed_at");
                success=true;
            }
            catch
            {
                snapshot["failure"]="最近一次额度捕获失败或超时；保留最近成功快照，当前额度未知。";
                snapshot["failed_at"]=Now().ToString("o",CultureInfo.InvariantCulture);
            }
            try
            {
                string path=SnapshotPath(entry);
                Atomic(path,AccountStore.Utf8.GetBytes(new JavaScriptSerializer().Serialize(snapshot)),OptionalRead(path),false);
            }
            catch { return false; }
            return success;
        }

        static DateTimeOffset? Date(string value)
        {
            DateTimeOffset parsed;
            return DateTimeOffset.TryParseExact(value,"o",CultureInfo.InvariantCulture,DateTimeStyles.None,out parsed)?(DateTimeOffset?)parsed:null;
        }
        void LoadSnapshot(CatalogEntry entry)
        {
            try
            {
                var snapshot=Snapshot(entry);
                entry.CapturedAtUtc=Date(AccountStore.Str(snapshot,"captured_at"));
                entry.FailedAtUtc=Date(AccountStore.Str(snapshot,"failed_at"));
                entry.SnapshotFailure=AccountStore.Str(snapshot,"failure")??"";
                if(entry.CapturedAtUtc.HasValue)
                    entry.QuotaText="快照时间："+entry.CapturedAtUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz",CultureInfo.InvariantCulture)+Environment.NewLine+
                        (AccountStore.Str(snapshot,"quota_text")??"额度未知。").Replace("官方额度服务（本次查询）","官方额度服务（历史快照）");
                object raw;
                var windows=new List<QuotaWindow>();
                if(snapshot.TryGetValue("windows",out raw) && raw is object[])
                    foreach(object value in (object[])raw)
                    {
                        var map=value as Dictionary<string,object>;
                        object exhausted;
                        windows.Add(new QuotaWindow { Label=AccountStore.Clean(AccountStore.Str(map,"label"))??"额度窗口",
                            ResetAtUtc=Date(AccountStore.Str(map,"reset_at")), Exhausted=map!=null && map.TryGetValue("exhausted",out exhausted) && exhausted is bool && (bool)exhausted });
                    }
                entry.Windows=windows.ToArray();
                var hints=new List<string>();
                foreach(var window in entry.Windows)
                    if(window.ResetAtUtc.HasValue && window.ResetAtUtc.Value<=Now())
                    {
                        if(window.Exhausted) entry.HasDueExhausted=true;
                        hints.Add(window.Label+"：快照中的重置时间已到，"+(window.Exhausted?"可能已恢复，":"")+"尚未重新查询。");
                    }
                entry.ResetHint=String.Join(Environment.NewLine,hints.ToArray());
                if(entry.SnapshotFailure.Length>0) entry.QuotaText+=Environment.NewLine+
                    (entry.FailedAtUtc.HasValue?"捕获尝试："+entry.FailedAtUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz",CultureInfo.InvariantCulture)+Environment.NewLine:"")+
                    entry.SnapshotFailure;
            }
            catch { entry.SnapshotFailure="额度快照无法读取，当前额度未知。"; entry.QuotaText=entry.SnapshotFailure; }
        }
        public void Dispose() { disposed=true; }
    }
}
