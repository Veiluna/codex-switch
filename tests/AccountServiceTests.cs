using System;
using System.IO;
using System.Text;
using System.Reflection;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using CodexSwitch;

internal static class AccountServiceTests
{
    static int checks;
    static void Check(bool ok,string name) { Console.WriteLine((ok?"PASS ":"FAIL ")+name); if(!ok) Environment.Exit(1); checks++; }
    static string Jwt(string user,string account) { return "h."+Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"email\":\"person@example.test\",\"https://api.openai.com/auth\":{\"chatgpt_user_id\":\""+user+"\",\"chatgpt_account_id\":\""+account+"\",\"chatgpt_plan_type\":\"plus\"}}" )).TrimEnd('=').Replace('+','-').Replace('/','_')+".s"; }
    static byte[] Auth(string user,string account,string token) { return Encoding.UTF8.GetBytes("{\"tokens\":{\"account_id\":\""+account+"\",\"id_token\":\""+Jwt(user,account)+"\",\"access_token\":\""+Jwt(user,account)+"\",\"refresh_token\":\""+token+"\"}}"); }
    [STAThread]
    static void Main()
    {
        string root=Path.Combine(Environment.CurrentDirectory,"tests",".tmp-service-data-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root,"accounts"));
        string auth=Path.Combine(root,"auth.json"),a=Path.Combine(root,"accounts","alpha_auth.json"),b=Path.Combine(root,"accounts","beta_auth.json"),marker=Path.Combine(root,"accounts",".current");
        byte[] old=Auth("user-a","work-a","old"),latest=Auth("user-a","work-a","latest"),other=Auth("user-b","work-a","other");
        try {
            File.WriteAllBytes(a,old); File.WriteAllBytes(b,other); File.WriteAllBytes(auth,latest); File.WriteAllText(marker,"beta");
            using(var service=new AccountService(root)) {
                CatalogEntry[] list=service.List();
                Check(list[0].Name=="alpha" && list[0].IsCurrent && list[0].Email=="person@example.test","rotated credentials match user and workspace, dishonest marker ignored");
                OperationResult save=service.Save("renamed"); Console.WriteLine(save.Code+" "+save.Message);
                Check(save.Success && !File.Exists(a),"save renames original instead of duplicating identity");
                Check(File.ReadAllBytes(Path.Combine(root,"accounts","renamed_auth.json")).Length==latest.Length,"save latest bytes");
                Check(!service.Save("beta").Success && File.ReadAllText(b).Contains("other"),"name collision never overwrites another user");
                File.WriteAllBytes(a,old); File.WriteAllText(marker,"beta");
                list=service.List(); Check(list[0].IsCurrent && !list[0].IsSaved && list[0].Name.Contains("多个"),"ambiguous duplicate identity shown as independent current");
                File.WriteAllText(marker,"alpha"); Check(service.List()[0].Name=="alpha","verified marker resolves duplicates only after identity match");
                File.Delete(a); File.WriteAllText(marker,"beta");
                using(var gate=new FileStream(Path.Combine(root,"accounts",".switch.lock"),FileMode.Open,FileAccess.ReadWrite,FileShare.None))
                    Check(!service.Save("busy").Success,"cross client lock rejects overlapping save");
                string before=service.List()[0].SnapshotId;
                File.WriteAllBytes(Path.Combine(root,"accounts","renamed_auth.json"),old);
                Check(!service.Delete("renamed",before).Success,"stale delete confirmation rejected");
                Check(service.Delete("renamed",service.List()[0].SnapshotId).Success && File.Exists(auth),"delete alias leaves active credentials");
                File.WriteAllText(auth,"{\"OPENAI_API_KEY\":\"synthetic\"}");
                Check(service.List()[0].IsCurrent && !service.List()[0].IsSaved,"unknown identity remains visible current");
                File.WriteAllBytes(auth,latest); File.WriteAllBytes(a,old); File.WriteAllText(marker,"alpha");
                DateTimeOffset now=new DateTimeOffset(2026,11,20,10,0,0,TimeSpan.Zero);
                service.Now=delegate { return now; };
                string usage="{\"account_id\":\"work-a\",\"user_id\":\"user-a\",\"rate_limit\":{\"primary_window\":{\"used_percent\":100,\"reset_at\":1795168801},\"secondary_window\":{\"used_percent\":25,\"reset_at\":1795168802}},\"credits\":{\"balance\":null}}";
                int fetched=0;
                service.FetchQuota=delegate(byte[] bytes) {
                    fetched++;
                    Check(File.ReadAllText(a).Contains("flushed"),"latest flushed credentials archived before quota request");
                    Check(File.ReadAllText(auth).Contains("flushed"),"quota capture occurs before target auth replacement");
                    return usage;
                };
                service.SwitchBackend=delegate(string r,string n,byte[] expected,Func<string,string,bool> confirm,Action<byte[]> departure) {
                    Check(confirm("Close",n),"caller confirms before desktop action");
                    File.WriteAllBytes(auth,Auth("user-a","work-a","flushed"));
                    departure(File.ReadAllBytes(auth));
                    File.WriteAllBytes(auth,expected); File.WriteAllText(marker,n);
                    return new OperationResult(true,"Switched","switched");
                };
                Check(service.Switch("beta",delegate { return true; }).Success && fetched==1,"switch captures one departure snapshot");
                list=service.List(); CatalogEntry alpha=Array.Find(list,delegate(CatalogEntry e){ return e.Name=="alpha"; });
                Check(alpha.CapturedAtUtc==now && alpha.Windows.Length==2 && alpha.QuotaText.Contains("余额：未知"),"snapshot stores actual capture time, both windows and unknown credits");
                now=now.AddSeconds(3); alpha=Array.Find(service.List(),delegate(CatalogEntry e){ return e.Name=="alpha"; });
                Check(alpha.HasDueExhausted && !alpha.Windows[1].Exhausted && alpha.ResetHint.Contains("主要窗口") && alpha.ResetHint.Contains("次要窗口") && alpha.QuotaText.Contains("剩余 0%"),"every due window prompts, including non-exhausted window, without inventing renewed balance");
                File.WriteAllBytes(auth,latest); File.WriteAllText(marker,"alpha");
                service.FetchQuota=delegate { throw new IOException("simulated timeout"); };
                Check(service.Switch("beta",delegate { return true; }).Success,"quota timeout does not block switch");
                alpha=Array.Find(service.List(),delegate(CatalogEntry e){ return e.Name=="alpha"; });
                Check(alpha.CapturedAtUtc==now.AddSeconds(-3) && alpha.FailedAtUtc==now && alpha.SnapshotFailure.Length>0 && alpha.Windows.Length==2,"failed quota capture preserves last successful snapshot and records attempt");
                File.WriteAllBytes(auth,latest); File.WriteAllText(marker,"alpha");
                Check(service.Save("alpha-renamed").Success,"rename saved snapshot owner");
                CatalogEntry renamed=Array.Find(service.List(),delegate(CatalogEntry e){ return e.Name=="alpha-renamed"; });
                Check(renamed.CapturedAtUtc==alpha.CapturedAtUtc && renamed.Windows.Length==2,"renaming keeps identity bound snapshot");
                Check(service.Save("alpha").Success,"restore test alias name");
                alpha=Array.Find(service.List(),delegate(CatalogEntry e){ return e.Name=="alpha"; });
                Check(service.Delete("alpha",alpha.SnapshotId).Success,"delete snapshot owner alias");
                File.WriteAllBytes(a,Auth("user-c","work-a","new-owner"));
                alpha=Array.Find(service.List(),delegate(CatalogEntry e){ return e.Name=="alpha"; });
                Check(!alpha.CapturedAtUtc.HasValue && !alpha.HasDueExhausted,"delete and recreate same alias for another identity never inherits snapshot");
                File.WriteAllBytes(auth,Auth("new-user","new-work","unsaved"));
                service.SwitchBackend=delegate(string r,string n,byte[] expected,Func<string,string,bool> confirm,Action<byte[]> departure) {
                    departure(File.ReadAllBytes(auth)); File.WriteAllBytes(auth,expected); return new OperationResult(true,"Switched","switched");
                };
                Check(service.Switch("beta",delegate { return true; }).Success,"unsaved departure can switch after automatic credential save");
                Check(Array.Exists(service.List(),delegate(CatalogEntry e){ return e.Email=="person@example.test" && e.AccountId=="new-work" && e.Name.Contains("person@example.test"); }),"automatic name uses email and workspace when email already exists");
                using(var legacy=new LegacyOperations()) {
                    typeof(LegacyOperations).GetMethod("Initialize",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(legacy,null);
                    var runspace=(Runspace)typeof(LegacyOperations).GetField("runspace",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(legacy);
                    using(var ps=PowerShell.Create()) {
                        ps.Runspace=runspace;
                        ps.AddScript("function Get-SwitchDesktopState { [pscustomobject]@{ Known=$true; Processes=@(); LaunchId=$null } }",false).Invoke();
                        Check(!ps.HadErrors,"simulated desktop setup does not inspect real desktop");
                    }
                    bool departed=false;
                    File.WriteAllBytes(auth,latest); File.WriteAllBytes(a,old);
                    service.SwitchBackend=delegate(string r,string n,byte[] expected,Func<string,string,bool> confirm,Action<byte[]> departure) {
                        return legacy.SwitchShared(r,n,expected,confirm,delegate(byte[] bytes) { departed=true; departure(bytes); });
                    };
                    OperationResult switched=service.Switch("beta",delegate { return false; });
                    Console.WriteLine("Bridge: "+switched.Code+" "+switched.Message);
                    Check(switched.Success && departed && File.ReadAllText(auth).Contains("other"),"real embedded bridge runs pre-departure save under shared lock before atomic switch");
                    using(var ps=PowerShell.Create()) {
                        ps.Runspace=runspace;
                        ps.AddScript(@"param($auth,$bytes)
$global:fixtureAuth=$auth; $global:fixtureBytes=$bytes; $global:fixtureRunning=$true
function Get-SwitchDesktopState { [pscustomobject]@{ Known=$true; Processes=$(if($global:fixtureRunning){@([pscustomobject]@{Id=42})}else{@()}); LaunchId='fixture' } }
function Invoke-SwitchDesktopProcessAction { param($Snapshot,[switch]$Force) if($Force){[IO.File]::WriteAllBytes($global:fixtureAuth,$global:fixtureBytes); $global:fixtureRunning=$false}; return $true }
function Wait-SwitchDesktopClosed { param($GetState,$TimeoutMilliseconds) return (-not $global:fixtureRunning) }
function Start-SwitchDesktop { param($Snapshot) return $true }",false).AddArgument(auth).AddArgument(Auth("user-a","work-a","flushed-force")).Invoke();
                        Check(!ps.HadErrors,"simulated forced desktop exit setup");
                    }
                    File.WriteAllBytes(auth,latest); File.WriteAllText(marker,"alpha");
                    int calls=0; departed=false;
                    OperationResult cancelled=service.Switch("beta",delegate(string phase,string target){ calls++; return false; });
                    Check(!cancelled.Success && calls==1 && !departed && File.ReadAllText(auth).Contains("latest"),"cancel first close approval leaves auth and archive untouched");
                    calls=0;
                    OperationResult forceCancelled=service.Switch("beta",delegate(string phase,string target){ calls++; return phase=="Close"; });
                    Check(!forceCancelled.Success && calls==2 && !departed && File.ReadAllText(auth).Contains("latest"),"force close requires a separate second approval");
                    calls=0;
                    OperationResult forceSwitched=service.Switch("beta",delegate(string phase,string target){ calls++; return true; });
                    Check(forceSwitched.Success && calls==2 && departed && File.ReadAllText(a).Contains("flushed-force"),"force exit saves final flushed credentials only after both approvals");
                    File.WriteAllBytes(auth,latest); File.WriteAllText(marker,"alpha"); File.SetAttributes(a,FileAttributes.ReadOnly);
                    int quotaCalls=0; service.FetchQuota=delegate { quotaCalls++; return usage; };
                    OperationResult failedSave=service.Switch("beta",delegate { return true; });
                    File.SetAttributes(a,FileAttributes.Normal);
                    Check(!failedSave.Success && quotaCalls==0 && File.ReadAllText(auth).Contains("latest"),"failed departure credential save stops before quota and target auth change");
                    File.WriteAllBytes(auth,latest); File.WriteAllText(marker,"alpha");
                    service.FetchQuota=delegate { File.WriteAllBytes(b,Auth("user-d","work-d","changed-target")); return usage; };
                    OperationResult changedTarget=service.Switch("beta",delegate { return true; });
                    Check(!changedTarget.Success && changedTarget.Code=="ConcurrentChange" && File.ReadAllText(auth).Contains("latest"),"target changed during quota capture is rejected before active auth replacement");
                    File.WriteAllBytes(b,other);
                    service.FetchQuota=delegate { File.WriteAllBytes(auth,Auth("user-external","work-external","external")); return usage; };
                    OperationResult changedAuth=service.Switch("beta",delegate { return true; });
                    Check(!changedAuth.Success && File.ReadAllText(auth).Contains("external"),"external auth change during quota capture is preserved");
                    File.WriteAllBytes(auth,other);
                    departed=false;
                    Check(service.Switch("beta",delegate { throw new Exception("unnecessary approval"); }).Success && !departed,"retry selecting actual current identity is idempotent and does not fetch quota");
                }
            }
            Console.WriteLine("PASS "+checks+" shared service checks (synthetic credentials only)");
            AccountServiceReviewTests.Run();
        } finally { Directory.Delete(root,true); }
    }
}
