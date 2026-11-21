using System;
using System.IO;
using System.Text;
using System.Reflection;
using System.Collections.Generic;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using CodexSwitch;

internal static class AccountServiceReviewTests
{
    static int checks, failures;
    static byte[] Auth(string user,string work) {
        string jwt="h."+Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"https://api.openai.com/auth\":{\"chatgpt_user_id\":\""+user+"\",\"chatgpt_account_id\":\""+work+"\"}}" )).TrimEnd('=').Replace('+','-').Replace('/','_')+".s";
        return Encoding.UTF8.GetBytes("{\"tokens\":{\"account_id\":\""+work+"\",\"id_token\":\""+jwt+"\",\"access_token\":\""+jwt+"\"}}");
    }
    static bool Failed(AccountService service) {
        var property=typeof(AccountService).GetProperty("CurrentReadFailed");
        return property!=null && (bool)property.GetValue(service,null);
    }
    static void Check(string label,Func<bool> test) {
        bool ok=false; try { ok=test(); } catch { }
        checks++; if(!ok) failures++;
        Console.WriteLine((ok?"PASS ":"FAIL ")+label);
    }
    internal static void Run() {
        string root=Path.Combine(Environment.CurrentDirectory,"tests",".tmp-review-"+Guid.NewGuid().ToString("N"));
        string accounts=Path.Combine(root,"accounts"),auth=Path.Combine(root,"auth.json"),archive=Path.Combine(accounts,"original_auth.json");
        Directory.CreateDirectory(accounts);
        byte[] a=Auth("a","workspace-a"),b=Auth("b","workspace-b");
        try {
            File.WriteAllBytes(archive,a); File.WriteAllBytes(Path.Combine(accounts,"other_auth.json"),b); File.WriteAllBytes(auth,a);
            using(var service=new AccountService(root)) {
                CatalogEntry selected=Array.Find(service.List(),delegate(CatalogEntry e){return e.Name=="other";});
                string selectedPath=Path.Combine(accounts,"other_auth.json");
                int selectionBackendCalls=0,selectionConfirmCalls=0,selectionQuotaCalls=0;
                service.SwitchBackend=delegate(string r,string n,byte[] expected,Func<string,string,bool> confirm,Action<byte[]> departure) {
                    selectionBackendCalls++;
                    return new OperationResult(true,"SyntheticSwitch","");
                };
                service.FetchQuota=delegate {selectionQuotaCalls++; throw new Exception("unexpected quota call");};
                Func<string,string,bool> approve=delegate {selectionConfirmCalls++;return true;};
                File.Delete(selectedPath);
                File.WriteAllBytes(selectedPath,Auth("replacement","workspace-c"));
                Check("菜单显示后同名存档换成另一身份时在关闭和额度前拒绝",delegate {
                    var result=service.Switch(selected.Name,selected.SnapshotId,approve);
                    return !result.Success && result.Code=="SelectionChanged" && selectionBackendCalls==0 && selectionConfirmCalls==0 && selectionQuotaCalls==0 && Convert.ToBase64String(File.ReadAllBytes(auth))==Convert.ToBase64String(a);
                });
                File.WriteAllBytes(auth,File.ReadAllBytes(selectedPath));
                Check("同名新身份即使已是当前登录仍须拒绝旧菜单选择",delegate {
                    var result=service.Switch(selected.Name,selected.SnapshotId,approve);
                    return !result.Success && result.Code=="SelectionChanged" && selectionBackendCalls==0;
                });
                File.WriteAllBytes(auth,a);
                File.WriteAllBytes(selectedPath,b);
                Check("未变化的菜单快照进入切换事务",delegate {
                    return service.Switch(selected.Name,selected.SnapshotId,approve).Success && selectionBackendCalls==1;
                });
                File.WriteAllBytes(selectedPath,Auth("replacement","workspace-c"));
                Check("直接按名字切换仍解析调用时的同名账号",delegate {
                    return service.Switch(selected.Name,approve).Success && selectionBackendCalls==2;
                });
                Check("快照绑定切换不能以缺失摘要退回按名字切换",delegate {
                    return !service.Switch(selected.Name,null,approve).Success && !service.Switch(selected.Name,"",approve).Success && selectionBackendCalls==2;
                });
                File.Delete(selectedPath);
                Check("菜单中的存档已删除时不进入切换事务",delegate {
                    return !service.Switch(selected.Name,selected.SnapshotId,approve).Success && selectionBackendCalls==2;
                });
                File.WriteAllBytes(selectedPath,b);
                using(var held=new FileStream(auth,FileMode.Open,FileAccess.ReadWrite,FileShare.None)) {
                    Check("活动凭据独占时仍返回其他已保存账号并报告识别失败",delegate { var list=service.List(); return list.Length==2 && Failed(service) && !Array.Exists(list,delegate(CatalogEntry e){return e.IsCurrent;}); });
                    Check("活动凭据独占时保存失败且不创建具名副本",delegate { return !service.Save("blocked").Success && !File.Exists(Path.Combine(accounts,"blocked_auth.json")); });
                    int calls=0;
                    service.SwitchBackend=delegate { calls++; return new OperationResult(true,"Unexpected",""); };
                    Check("活动凭据独占时切换在事务前停止",delegate { return !service.Switch("other",delegate {return true;}).Success && calls==0; });
                }
                File.WriteAllText(auth,"damaged credential file");
                Check("活动凭据损坏时返回保存条目且不伪造当前登录",delegate { var list=service.List(); return list.Length==2 && Failed(service) && !Array.Exists(list,delegate(CatalogEntry e){return e.IsCurrent;}); });
                File.Delete(auth);
                Check("真正缺失活动凭据时区分未登录并清除失败状态",delegate { return service.List().Length==2 && !Failed(service); });
                File.WriteAllBytes(auth,a);
                File.SetAttributes(archive,FileAttributes.ReadOnly);
                Check("改名删旧名失败时仅撤销本次创建的新副本",delegate { var result=service.Save("renamed"); return !result.Success && File.Exists(archive) && !File.Exists(Path.Combine(accounts,"renamed_auth.json")); });
                File.SetAttributes(archive,FileAttributes.Normal);
                Check("补偿后可重试改名且不产生重复身份",delegate { return service.Save("renamed").Success && !File.Exists(archive) && service.List().Length==2; });
                File.WriteAllText(auth,"{\"OPENAI_API_KEY\":\"synthetic-review\"}");
                service.Now=delegate {return new DateTimeOffset(2026,11,21,12,34,56,TimeSpan.Zero);};
                string unnamed="未命名账号 · "+service.Now().ToLocalTime().ToString("yyyyMMddHHmmss");
                Check("无邮箱默认名包含未命名账号和年月日时分秒",delegate { return service.Save(null).Success && Array.Exists(service.List(),delegate(CatalogEntry e){return e.IsCurrent && e.Name==unnamed;}); });
                File.WriteAllBytes(auth,a);
                using(var legacy=new LegacyOperations()) {
                    typeof(LegacyOperations).GetMethod("Initialize",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(legacy,null);
                    var runspace=(Runspace)typeof(LegacyOperations).GetField("runspace",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(legacy);
                    var desktop=new Dictionary<string,object> { {"Running",false} };
                    using(var ps=PowerShell.Create()) {
                        ps.Runspace=runspace;
                        ps.AddScript("param($state) $global:reviewState=$state; function Get-SwitchDesktopState { [pscustomobject]@{Known=$true;Processes=$(if($global:reviewState['Running']){@(42)}else{@()});LaunchId=$null} }",false).AddArgument(desktop).Invoke();
                    }
                    service.SwitchBackend=legacy.SwitchShared;
                    service.FetchQuota=delegate {
                        desktop["Running"]=true;
                        throw new IOException("虚构网络失败后桌面端重新启动");
                    };
                    Check("额度捕获期间桌面重新启动时写前重检并拒绝切换",delegate { var result=service.Switch("other",delegate {return true;}); return !result.Success && result.Code=="DesktopRunning" && Convert.ToBase64String(File.ReadAllBytes(auth))==Convert.ToBase64String(a); });
                }
            }
        } finally {
            if(File.Exists(archive)) File.SetAttributes(archive,FileAttributes.Normal);
            Directory.Delete(root,true);
        }
        Console.WriteLine("评审修复检查："+checks+" 项，失败 "+failures+" 项。");
        if(failures>0) Environment.Exit(1);
    }
}
