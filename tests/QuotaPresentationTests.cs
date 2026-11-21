using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Web.Script.Serialization;
using CodexSwitch;

internal static class QuotaPresentationTests
{
    static int checks;
    static void Check(bool ok,string label) { if(!ok) throw new Exception("FAIL "+label); checks++; Console.WriteLine("PASS "+label); }
    static string Window(int seconds,int used,long reset) { return "{\"used_percent\":"+used+",\"limit_window_seconds\":"+seconds+",\"reset_at\":"+reset+"}"; }
    static byte[] Auth()
    {
        string claims="{\"email\":\"person@example.test\",\"https://api.openai.com/auth\":{\"chatgpt_user_id\":\"fake-user\",\"chatgpt_account_id\":\"fake-workspace\"}}";
        string token="h."+Convert.ToBase64String(Encoding.UTF8.GetBytes(claims)).TrimEnd('=').Replace('+','-').Replace('/','_')+".s";
        return Encoding.UTF8.GetBytes("{\"tokens\":{\"account_id\":\"fake-workspace\",\"id_token\":\""+token+"\",\"access_token\":\""+token+"\"}}");
    }
    static void Main()
    {
        DateTimeOffset now=new DateTimeOffset(2026,11,21,0,0,0,TimeSpan.Zero);
        long reset=(long)(now.AddHours(25).AddMinutes(1)-new DateTimeOffset(1970,1,1,0,0,0,TimeSpan.Zero)).TotalSeconds;
        string usage="{\"rate_limit\":{\"primary_window\":"+Window(18000,25,reset)+",\"secondary_window\":"+Window(604800,100,reset)+"},\"additional_rate_limits\":[{\"limit_name\":\"额外\",\"rate_limit\":{\"primary_window\":"+Window(18000,0,reset)+",\"secondary_window\":"+Window(604800,10,reset)+"}}]}";
        QuotaWindow[] windows=QuotaReader.Windows(usage);
        Check(windows.Length==4 && windows[0].LimitWindowSeconds==604800 && windows[0].UsedPercent==100 && windows[1].LimitWindowSeconds==18000 && windows[1].UsedPercent==25,"real durations put weekly before five-hour without swapping values");
        Check(windows[2].Label.Contains("额外") && windows[2].LimitWindowSeconds==604800 && windows[3].UsedPercent==0,"additional quotas keep their own sorted group and real zero");
        Check(QuotaReader.FormatCountdown(now.AddHours(25).AddMinutes(1),now)=="01天01小时01分钟","countdown breaks total minutes into days, hours and minutes");
        Check(QuotaReader.FormatCountdown(now.AddDays(1).AddSeconds(-1),now)=="01天00小时00分钟","rounding carries into the next day");
        Check(QuotaReader.FormatCountdown(now.AddSeconds(1),now)=="00天00小时01分钟" && QuotaReader.FormatCountdown(now.AddMinutes(1).AddTicks(1),now)=="00天00小时02分钟","partial minutes round up");
        Check(QuotaReader.FormatCountdown(now,now)=="00天00小时00分钟" && QuotaReader.FormatCountdown(now.AddSeconds(-1),now)=="00天00小时00分钟" && QuotaReader.FormatCountdown(null,now)=="未知","due and absent reset stay distinct");
        Check(QuotaReader.FormatCountdown(now.ToOffset(TimeSpan.FromHours(8)).AddMinutes(1),now)=="00天00小时01分钟","offsets compare the same instant");
        var unknown=QuotaReader.Windows("{\"rate_limit\":{\"primary_window\":{\"used_percent\":0},\"secondary_window\":{\"reset_at\":null}}}");
        Check(unknown.Length==2 && !unknown[0].LimitWindowSeconds.HasValue && !unknown[1].UsedPercent.HasValue && !unknown[0].Label.Contains("周") && !unknown[0].Label.Contains("5 小时"),"missing duration and used values remain unknown");
        var single=QuotaReader.Windows("{\"rate_limit\":{\"secondary_window\":"+Window(604800,0,reset)+"}}");
        Check(single.Length==1 && single[0].Label=="周额度" && single[0].UsedPercent==0,"a lone secondary weekly period is recognized without invented companion windows");
        var arbitrary=QuotaReader.Windows("{\"rate_limit\":{\"primary_window\":"+Window(3600,50,reset)+"}}");
        Check(arbitrary[0].Label=="60 分钟额度" && arbitrary[0].LimitWindowSeconds==3600,"nonstandard durations are preserved rather than relabeled as week or five-hour");
        string text=QuotaReader.Format(usage,null,null,now);
        Check(text.IndexOf("周额度",StringComparison.Ordinal)<text.IndexOf("5 小时额度",StringComparison.Ordinal) && text.Contains("01天01小时01分钟") && text.Contains("剩余 0%"),"full details use the same period order and dynamic countdown");
        string root=Path.Combine(Environment.CurrentDirectory,"tests",".tmp-quota-presentation-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root,"accounts"));
        try
        {
            File.WriteAllBytes(Path.Combine(root,"accounts","fake_auth.json"),Auth());
            using(var service=new AccountService(root))
            {
                service.Now=delegate {return now;}; service.FetchQuota=delegate {return usage;};
                CatalogEntry entry=service.List()[0];
                bool captured=(bool)typeof(AccountService).GetMethod("Capture",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(service,new object[]{entry});
                Check(captured && service.List()[0].QuotaText.Contains("01天01小时01分钟"),"captured snapshot renders a current countdown");
                now=now.AddMinutes(2);
                entry=service.List()[0];
                Check(entry.QuotaText.Contains("01天00小时59分钟") && !entry.QuotaText.Contains("01天01小时01分钟") && entry.Windows[0].UsedPercent==100,"loading snapshot recalculates countdown without changing observed usage");
                Check(AccountService.FormatQuotaDetails(entry,now.AddMinutes(2)).Contains("01天00小时57分钟"),"open details recalculate countdown without rereading the catalog");
                Check(entry.QuotaSummary=="Credits 余额：未知","shared summary preserves independent unknown credits");
                string capturedJson=File.ReadAllText(Directory.GetFiles(Path.Combine(root,"accounts"),".quota-*.snapshot")[0]);
                Check(capturedJson.Contains("limit_window_seconds") && capturedJson.Contains("quota_data") && !capturedJson.Contains("access_token"),"new snapshot stores exact periods and display-only data");
                string path=Directory.GetFiles(Path.Combine(root,"accounts"),".quota-*.snapshot")[0];
                var serializer=new JavaScriptSerializer();
                var legacy=new Dictionary<string,object> { {"captured_at",now.ToString("o",CultureInfo.InvariantCulture)},
                    {"quota_text","来源：官方额度服务（本次查询）\n主要窗口（300 分钟）：剩余 75%（已用 25%）\n重置：未知\n次要窗口（10080 分钟）：剩余 0%（已用 100%）\n重置：未知\nCredits 余额：未知"},
                    {"windows",new object[] {new Dictionary<string,object>{{"label","主要窗口"},{"reset_at",now.AddMinutes(1).ToString("o",CultureInfo.InvariantCulture)},{"exhausted",false}},new Dictionary<string,object>{{"label","次要窗口"},{"reset_at",now.AddMinutes(2).ToString("o",CultureInfo.InvariantCulture)},{"exhausted",true}}}} };
                File.WriteAllText(path,serializer.Serialize(legacy),Encoding.UTF8);
                entry=service.List()[0];
                Check(entry.Windows[0].LimitWindowSeconds==604800 && entry.Windows[0].Exhausted && entry.Windows[0].ResetAtUtc==now.AddMinutes(2) && entry.Windows[1].LimitWindowSeconds==18000 && entry.Windows[1].ResetAtUtc==now.AddMinutes(1),"legacy text duration matches original role and preserves the correct reset");
                Check(entry.QuotaText.IndexOf("周额度",StringComparison.Ordinal)<entry.QuotaText.IndexOf("5 小时额度",StringComparison.Ordinal) && entry.QuotaText.Contains("00天00小时02分钟"),"legacy details rebuild the same sorted live presentation");
                legacy["quota_text"]="旧额度数据，无时长";
                File.WriteAllText(path,serializer.Serialize(legacy),Encoding.UTF8);
                entry=service.List()[0];
                Check(!entry.Windows[0].LimitWindowSeconds.HasValue && !entry.Windows[1].LimitWindowSeconds.HasValue && !entry.Windows[0].Label.Contains("周"),"legacy windows without exact duration never become guessed periods");
                legacy["quota_text"]="主要窗口额外（10080 分钟）：剩余 0%（已用 100%）\n次要窗口（10080 分钟）：剩余 0%（已用 100%）\n次要窗口（300 分钟）：剩余 75%（已用 25%）";
                File.WriteAllText(path,serializer.Serialize(legacy),Encoding.UTF8);
                entry=service.List()[0];
                Check(!entry.Windows[0].LimitWindowSeconds.HasValue && !entry.Windows[1].LimitWindowSeconds.HasValue,"partial label and ambiguous duplicate legacy lines do not establish a period");
                legacy["quota_text"]="原文已丢失";
                legacy["windows"]=new object[] {new Dictionary<string,object>{{"label","次要窗口"},{"limit_window_seconds",604800},{"used_percent",100},{"reset_at",now.AddMinutes(2).ToString("o",CultureInfo.InvariantCulture)},{"exhausted",true}},new Dictionary<string,object>{{"label","主要窗口"},{"limit_window_seconds",18000},{"used_percent",25},{"reset_at",now.AddMinutes(1).ToString("o",CultureInfo.InvariantCulture)},{"exhausted",false}}};
                File.WriteAllText(path,serializer.Serialize(legacy),Encoding.UTF8);
                entry=service.List()[0];
                Check(entry.Windows[0].LimitWindowSeconds==604800 && entry.Windows[0].UsedPercent==100 && entry.Windows[0].ResetAtUtc==now.AddMinutes(2),"saved exact durations recover periods even when legacy array order is reversed and text is absent");
                legacy["quota_text"]="周额度：剩余 0% · 重置倒计时 999:99\n窗口时长：10080 分钟；已用 100%\n重置：未知\nCredits 余额：未知";
                File.WriteAllText(path,serializer.Serialize(legacy),Encoding.UTF8);
                entry=service.List()[0];
                Check(entry.QuotaText.Contains("00天00小时02分钟") && !entry.QuotaText.Contains("999:99"),"duration-only snapshots also replace persisted countdowns with current values");
                now=now.AddHours(2);
                entry=service.List()[0];
                Check(entry.HasDueExhausted && entry.QuotaText.Contains("00天00小时00分钟") && entry.ResetHint.Contains("尚未重新查询"),"due exhausted history retains warnings and zero countdown");
            }
        }
        finally { Directory.Delete(root,true); }
        Console.WriteLine("PASS "+checks+" quota presentation checks (synthetic credentials only)");
    }
}
