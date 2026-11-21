using System;
using System.IO;
using System.Text;
using System.Net;
using System.Runtime.InteropServices;
using CodexSwitch;

internal static class NativeAccountsTests
{
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    static extern bool CreateHardLink(string link, string existing, IntPtr security);
    static int checks;
    static void Check(bool value, string label) { Console.WriteLine((value?"PASS ":"FAIL ")+label); if (!value) throw new Exception(label); checks++; }
    static void Fails(Action action, string label) { bool failed = false; try { action(); } catch { failed = true; } Check(failed, label); }
    static string Jwt(string json) { return "header." + Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+','-').Replace('/','_') + ".signature"; }
    static void Main()
    {
        string root = Path.Combine(Path.GetTempPath(), "codex-switch-native-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "accounts"));
        try {
            string a = Path.Combine(root,"accounts","alpha_auth.json");
            string claims = "{\"https://api.openai.com/auth\":{\"chatgpt_account_id\":\"account-a\",\"chatgpt_user_id\":\"user-a\",\"chatgpt_plan_type\":\"plus\"}}";
            string auth = "{\"tokens\":{\"account_id\":\"account-a\",\"id_token\":\""+Jwt(claims)+"\",\"access_token\":\""+Jwt(claims)+"\",\"refresh_token\":\"NEVER-RETURN-THIS\"}}";
            File.WriteAllText(a,auth); File.WriteAllText(Path.Combine(root,"auth.json"),auth);
            File.WriteAllText(Path.Combine(root,"accounts","backup-hidden_auth.json"),auth);
            File.WriteAllText(Path.Combine(root,"accounts","beta.json"),"{\"OPENAI_API_KEY\":\"secret\"}");
            Check(AccountStore.List(root).Length==2,"list legacy and modern; hide backup");
            AccountDetails detail=AccountStore.Read(root,"alpha");
            Check(detail.AccountId=="account-a" && detail.UserId=="user-a" && detail.Plan=="plus","metadata");
            Check(!detail.SnapshotId.Contains("NEVER"),"opaque snapshot");
            Check(AccountStore.Read(root,"beta").Plan=="未知","api key has no guessed plan");
            File.WriteAllText(Path.Combine(root,"accounts","future.json"), "{\"auth_mode\":\"future-provider\",\"tokens\":{\"account_id\":\"unknown\",\"access_token\":\"synthetic-secret\"}}");
            Check(AccountStore.Read(root,"future").LoginType=="未知" && QuotaReader.Read(root,"future").Contains("不支持"), "unknown login type not guessed or queried");
            File.Delete(Path.Combine(root,"accounts","future.json"));
            Fails(delegate { AccountStore.Delete(root,"../alpha"); },"traversal");
            File.WriteAllText(Path.Combine(root,"accounts","alpha.json"),auth);
            Fails(delegate { AccountStore.Delete(root,"alpha"); },"suffix conflict rejects before delete");
            Check(File.Exists(a),"conflict no partial delete");
            File.Delete(Path.Combine(root,"accounts","alpha.json"));
            File.AppendAllText(a," ");
            Fails(delegate { AccountStore.Delete(root,"alpha",detail.SnapshotId); },"stale delete confirmation");
            AccountStore.Delete(root,"alpha",AccountStore.Read(root,"alpha").SnapshotId);
            Check(!File.Exists(a) && File.Exists(Path.Combine(root,"auth.json")),"delete only alias");
            Check(File.Exists(Path.Combine(root,"accounts","backup-hidden_auth.json")),"preserve backups");
            File.WriteAllText(a,auth);
            string marker=Path.Combine(root,"accounts",".current"); File.WriteAllText(marker,"alpha");
            AccountStore.Delete(root,"alpha");
            Check(File.ReadAllText(marker)=="alpha", "historical marker retained without assuming ownership");
            File.WriteAllText(a,auth);
            string hardlink=Path.Combine(root,"accounts","linked.json");
            bool hardlinkCreated=CreateHardLink(hardlink,a,IntPtr.Zero);
            if(hardlinkCreated) {
                Fails(delegate { AccountStore.Delete(root,"linked"); },"refuse hardlink deletion");
                Check(File.Exists(hardlink) && File.ReadAllText(a)==auth,"hardlink failure preserves both names");
                File.Delete(hardlink);
            } else {
                int error=Marshal.GetLastWin32Error();
                if(error!=5 && error!=1314) throw new Exception("Hardlink setup failed: "+error);
                Console.WriteLine("SKIP hardlink fixture: Windows/sandbox denied creation ("+error+")");
            }
            using(var held=new FileStream(Path.Combine(root,"accounts",".switch.lock"),FileMode.Open,FileAccess.ReadWrite,FileShare.None))
                Fails(delegate { AccountStore.Delete(root,"alpha"); },"busy shared switch lock");
            string guardedAccounts = Path.Combine(root, "accounts");
            string movedAccounts = Path.Combine(root, "moved");
            using(var paths=new AccountStore.PathGuards(guardedAccounts)) {
                Fails(delegate { Directory.Move(guardedAccounts,movedAccounts); },"held directory cannot be retargeted");
                Fails(delegate { Directory.Move(root,root+"-moved"); },"held ancestor cannot be retargeted");
                string child = Path.Combine(guardedAccounts, "ordinary-child.tmp");
                File.WriteAllText(child, "synthetic");
                Check(File.ReadAllText(child)=="synthetic", "directory guards preserve ordinary child access");
                File.Delete(child);
            }
            Directory.Move(guardedAccounts,movedAccounts);
            Check(Directory.Exists(movedAccounts), "directory rename succeeds after guard release");
            Directory.Move(movedAccounts,guardedAccounts);
            File.SetAttributes(a,FileAttributes.ReadOnly);
            Fails(delegate { AccountStore.Delete(root,"alpha"); },"readonly delete failure");
            Check(File.Exists(a),"readonly failure preserved archive"); File.SetAttributes(a,FileAttributes.Normal);
            string longName=new string('a',181);
            Check(AccountStore.ValidName(longName),"no artificial alias length cap");
            string usage="{\"account_id\":\"account-a\",\"plan_type\":\"plus\",\"rate_limit\":{\"primary_window\":{\"used_percent\":25,\"limit_window_seconds\":18000,\"reset_at\":2000000000}},\"credits\":{\"has_credits\":false,\"unlimited\":false,\"balance\":null}}";
            string text=QuotaReader.Format(usage,"account-a");
            Check(text.Contains("剩余 75%") && text.Contains("已用 25%") && text.Contains("300 分钟") && text.Contains("余额：未知") && text.Contains("本地时间"),"remaining percentage/window/local reset, unknown balance");
            Check(QuotaReader.Format(usage.Replace("25,","0,"),"account-a").Contains("剩余 100%"),"zero used leaves 100");
            Check(QuotaReader.Format(usage.Replace("25,","100,"),"account-a").Contains("剩余 0%"),"100 used leaves zero");
            Check(QuotaReader.Format(usage.Replace("25,","12.34,"),"account-a").Contains("剩余 87.66%"),"fractional remaining");
            Fails(delegate { QuotaReader.Format(usage,"account-b"); },"foreign backend account");
            Fails(delegate { QuotaReader.Format(usage.Replace("\"plan_type\"", "\"user_id\":\"user-b\",\"plan_type\""), "account-a", "user-a"); },"foreign user in shared workspace");
            Fails(delegate { QuotaReader.Format(usage.Replace("\"account-a\"", "14"), "account-a"); },"malformed backend identity rejected");
            Check(QuotaReader.Format("{\"rate_limit\":{\"primary_window\":{\"used_percent\":null}}}","account-a").Contains("未知"),"null not zero");
            Fails(delegate { QuotaReader.Format("{\"rate_limit\":{\"primary_window\":{\"used_percent\":-3}}}","account-a"); },"negative percentage invalid");
            var request=QuotaReader.CreateRequest("synthetic-access-token","synthetic-account");
            Check(request.RequestUri.AbsoluteUri=="https://chatgpt.com/backend-api/wham/usage" && request.Method=="GET" && !request.AllowAutoRedirect,"fixed HTTPS GET with redirects blocked");
            Check(request.Headers["Authorization"]=="Bearer synthetic-access-token" && request.Headers["ChatGPT-Account-Id"]=="synthetic-account","selected credentials only");
            Check(ServicePointManager.SecurityProtocol==SecurityProtocolType.Tls12 && request.Timeout==12000 && request.ReadWriteTimeout==12000,"TLS 1.2 and timeouts");
            request.Abort();
            Fails(delegate { AccountStore.Root(@"\\?\C:\unsafe"); },"device paths rejected");
            Fails(delegate { AccountStore.Root(@"C:\safe:stream"); },"ADS rejected");
            Check(AccountStore.Root(@"\\server\share\codex").StartsWith(@"\\server\share"),"standard UNC accepted");
            Console.WriteLine("PASS " + checks + " native account/quota checks (synthetic credentials only)");
        } finally { Directory.Delete(root,true); }
    }
}
