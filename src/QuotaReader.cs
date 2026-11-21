using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace CodexSwitch
{
    public sealed class QuotaWindow
    {
        public string Label = "";
        public string Role = "", Prefix = "";
        public double? LimitWindowSeconds, UsedPercent;
        public DateTimeOffset? ResetAtUtc;
        public bool Exhausted;
    }

    public static class QuotaReader
    {
        internal const string UsageEndpoint = "https://chatgpt.com/backend-api/wham/usage";
        internal const int TimeoutMilliseconds = 12000;
        const int MaximumResponseBytes = 1024 * 1024;

        // No refresh token, CLI subprocess, temporary credential file, telemetry or MCP is involved.
        // Only the selected archive's bearer is sent, to this fixed official HTTPS endpoint.
        public static string Read(string root, string name)
        {
            try
            {
                byte[] bytes = AccountStore.ReadArchive(root, name);
                var data = AccountStore.Json(bytes);
                var tokens = AccountStore.Map(data, "tokens");
                if (!String.IsNullOrEmpty(AccountStore.Str(data, "OPENAI_API_KEY")))
                    return "API key 登录：此接口不提供 API 余额，请在 OpenAI 平台查看。";
                string authMode = AccountStore.Str(data, "auth_mode");
                if (authMode != null && authMode != "chatgpt")
                    return "额度未知：不支持此存档的登录类型。";
                string access = AccountStore.Str(tokens, "access_token");
                string account = AccountStore.Clean(AccountStore.Str(tokens, "account_id"));
                if (String.IsNullOrWhiteSpace(access) || account == null)
                    return "额度未知：存档缺少可用的 ChatGPT 登录信息。";
                if (access.Length > 65536 || access.IndexOf('\r') >= 0 || access.IndexOf('\n') >= 0)
                    return "额度未知：登录信息格式无效。";
                var id = AccountStore.Claims(AccountStore.Str(tokens, "id_token"));
                var claims = AccountStore.Claims(access);
                if (claims == null)
                    return "额度未知：存档缺少可识别的 ChatGPT access token，请重新登录并保存。";
                if (Conflicts(id, claims, account))
                    return "额度未知：存档身份信息冲突，请重新保存登录。";
                string result = Fetch(access, account);
                // A selection can be deleted/replaced while the network request is running. Never label that result as its replacement.
                byte[] current = AccountStore.ReadArchive(root, name);
                if (!Equal(bytes, current)) return "额度未知：查询期间存档已变化，请重新查询。";
                string user = AccountStore.Clean(AccountStore.Str(id, "chatgpt_user_id")) ??
                    AccountStore.Clean(AccountStore.Str(id, "user_id")) ??
                    AccountStore.Clean(AccountStore.Str(claims, "chatgpt_user_id")) ??
                    AccountStore.Clean(AccountStore.Str(claims, "user_id"));
                return Format(result, account, user);
            }
            catch (WebException error)
            {
                var response = error.Response as HttpWebResponse;
                if (response != null)
                {
                    int status = (int)response.StatusCode;
                    response.Dispose();
                    if (status == 401 || status == 403)
                        return "额度未知：登录已过期或无权查询。请先切换到该账号并在 Codex 重新登录，再保存当前登录。";
                    if (status == 429) return "额度未知：查询暂时受限，请稍后重试。";
                }
                if (error.Status == WebExceptionStatus.Timeout || error.Status == WebExceptionStatus.RequestCanceled)
                    return "额度查询超时，请检查网络后重试。";
                return "额度未知：无法连接官方额度服务，请检查网络后重试。";
            }
            catch { return "额度未知：无法读取所选存档或官方返回了不支持的数据。"; }
        }

        static bool Equal(byte[] left, byte[] right)
        {
            if (left.Length != right.Length) return false;
            for (int i = 0; i < left.Length; i++)
                if (left[i] != right[i]) return false;
            return true;
        }

        internal static string FetchForSnapshot(byte[] bytes)
        {
            var data=AccountStore.Json(bytes);
            var tokens=AccountStore.Map(data,"tokens");
            AccountDetails identity=AccountStore.Describe(bytes,"");
            if(identity.LoginType!="ChatGPT" || identity.AccountId=="未知" || identity.UserId=="未知")
                throw new IOException("无法可靠确定额度所属用户与工作区。");
            string access=AccountStore.Str(tokens,"access_token");
            if(String.IsNullOrWhiteSpace(access) || access.Length>65536 || access.IndexOf('\r')>=0 || access.IndexOf('\n')>=0)
                throw new IOException("登录信息无效。");
            return Fetch(access,identity.AccountId);
        }

        internal static QuotaWindow[] Windows(string json)
        {
            var data=AccountStore.Json(AccountStore.Utf8.GetBytes(json));
            var result=new List<QuotaWindow>();
            AddWindows(result,"",AccountStore.Map(data,"rate_limit"));
            object extras;
            if(data.TryGetValue("additional_rate_limits",out extras) && extras is object[])
                foreach(object value in (object[])extras)
                {
                    var map=value as Dictionary<string,object>;
                    if(map==null) continue;
                    string label=AccountStore.Clean(AccountStore.Str(map,"limit_name")) ??
                        AccountStore.Clean(AccountStore.Str(map,"metered_feature")) ?? "附加额度";
                    AddWindows(result,label+" / ",AccountStore.Map(map,"rate_limit"));
                }
            return result.ToArray();
        }
        static void AddWindows(List<QuotaWindow> output,string prefix,Dictionary<string,object> rate)
        {
            var group=new List<QuotaWindow>();
            AddWindow(group,prefix,"primary",AccountStore.Map(rate,"primary_window"));
            AddWindow(group,prefix,"secondary",AccountStore.Map(rate,"secondary_window"));
            output.AddRange(Order(group.ToArray()));
        }
        static void AddWindow(List<QuotaWindow> output,string prefix,string role,Dictionary<string,object> map)
        {
            if(map==null) return;
            double? reset=Number(map,"reset_at"),used=Number(map,"used_percent"),seconds=Number(map,"limit_window_seconds");
            if(used.HasValue && (used.Value<0 || used.Value>100)) throw new IOException("比例无效。");
            if(seconds.HasValue && seconds.Value<=0) throw new IOException("窗口无效。");
            if(reset.HasValue && (reset.Value<0 || reset.Value!=Math.Truncate(reset.Value))) throw new IOException("重置时间无效。");
            object flag;
            var window=new QuotaWindow { Prefix=prefix, Role=role, LimitWindowSeconds=seconds, UsedPercent=used,
                ResetAtUtc=reset.HasValue?(DateTimeOffset?)new DateTimeOffset(1970,1,1,0,0,0,TimeSpan.Zero).AddSeconds(reset.Value):null,
                Exhausted=(used.HasValue && used.Value==100) || (map.TryGetValue("limit_reached",out flag) && flag is bool && (bool)flag) };
            window.Label=WindowLabel(window);
            output.Add(window);
        }

        internal static QuotaWindow[] Order(QuotaWindow[] windows)
        {
            // Keep each service/additional quota group independent; roles never identify periods.
            var result=new List<QuotaWindow>();
            var prefixes=new List<string>();
            foreach(var window in windows) if(!prefixes.Contains(window.Prefix)) prefixes.Add(window.Prefix);
            foreach(string prefix in prefixes)
                for(int rank=0;rank<3;rank++)
                    foreach(var window in windows)
                        if(window.Prefix==prefix && Rank(window)==rank) result.Add(window);
            return result.ToArray();
        }
        static int Rank(QuotaWindow window) { return window.LimitWindowSeconds==604800?0:window.LimitWindowSeconds==18000?1:2; }
        internal static string WindowLabel(QuotaWindow window)
        {
            string period=window.LimitWindowSeconds==604800?"周额度":window.LimitWindowSeconds==18000?"5 小时额度":
                window.LimitWindowSeconds.HasValue?Num(window.LimitWindowSeconds.Value/60)+" 分钟额度":
                window.Role=="primary"?"主要窗口（时长未知）":window.Role=="secondary"?"次要窗口（时长未知）":
                String.IsNullOrEmpty(window.Label) || window.Label.EndsWith("周额度",StringComparison.Ordinal) || window.Label.EndsWith("5 小时额度",StringComparison.Ordinal)
                    ?"额度窗口（时长未知）":window.Label;
            return window.Prefix+period;
        }
        public static string FormatCountdown(DateTimeOffset? resetAtUtc,DateTimeOffset now)
        {
            if(!resetAtUtc.HasValue) return "未知";
            long ticks=(resetAtUtc.Value-now).Ticks;
            // Integer ceiling avoids floating point loss immediately after a minute boundary.
            long minutes=ticks<=0?0:(ticks-1)/TimeSpan.TicksPerMinute+1;
            return String.Format(CultureInfo.InvariantCulture,"{0:00}天{1:00}小时{2:00}分钟",
                minutes/(24*60),minutes/60%24,minutes%60);
        }
        public static string FormatWindowSummary(QuotaWindow window,DateTimeOffset now)
        {
            if(window==null) return "额度未知。";
            return WindowLabel(window)+"：剩余 "+(window.UsedPercent.HasValue?Num(100-window.UsedPercent.Value)+"%":"未知")+
                " · 重置倒计时 "+FormatCountdown(window.ResetAtUtc,now);
        }

        static bool Conflicts(Dictionary<string, object> id, Dictionary<string, object> access, string account)
        {
            string idAccount = AccountStore.Clean(AccountStore.Str(id, "chatgpt_account_id"));
            string accessAccount = AccountStore.Clean(AccountStore.Str(access, "chatgpt_account_id"));
            string idUser = AccountStore.Clean(AccountStore.Str(id, "chatgpt_user_id")) ??
                AccountStore.Clean(AccountStore.Str(id, "user_id"));
            string accessUser = AccountStore.Clean(AccountStore.Str(access, "chatgpt_user_id")) ??
                AccountStore.Clean(AccountStore.Str(access, "user_id"));
            return (idAccount != null && idAccount != account) ||
                (accessAccount != null && accessAccount != account) ||
                (idUser != null && accessUser != null && idUser != accessUser);
        }

        internal static HttpWebRequest CreateRequest(string access, string account)
        {
            // The executable targets Framework 4.8 but may inherit older runtime defaults.
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            var request = (HttpWebRequest)WebRequest.Create(UsageEndpoint);
            request.Method = "GET";
            request.AllowAutoRedirect = false;
            request.Timeout = TimeoutMilliseconds;
            request.ReadWriteTimeout = TimeoutMilliseconds;
            request.KeepAlive = false;
            request.UseDefaultCredentials = false;
            request.Accept = "application/json";
            request.UserAgent = "codex-switch/1.0";
            request.Headers[HttpRequestHeader.Authorization] = "Bearer " + access;
            request.Headers["ChatGPT-Account-Id"] = account;
            request.Headers[HttpRequestHeader.CacheControl] = "no-store";
            request.ServerCertificateValidationCallback = delegate(object sender,
                System.Security.Cryptography.X509Certificates.X509Certificate certificate,
                System.Security.Cryptography.X509Certificates.X509Chain chain, SslPolicyErrors errors)
            {
                return errors == SslPolicyErrors.None;
            };
            return request;
        }
        static string Fetch(string access, string account)
        {
            var request = CreateRequest(access, account);
            // Abort also bounds a slow or endless response body, beyond per-read timeouts.
            using (var timer = new Timer(delegate { try { request.Abort(); } catch { } },
                null, TimeoutMilliseconds, Timeout.Infinite))
            {
                try
                {
                    using (var response = (HttpWebResponse)request.GetResponse())
                    {
                        if (response.StatusCode != HttpStatusCode.OK)
                            throw new IOException("官方服务未返回额度数据。");
                        if (response.ResponseUri.AbsoluteUri != UsageEndpoint)
                            throw new IOException("拒绝重定向。");
                        using (var stream = response.GetResponseStream())
                        using (var buffer = new MemoryStream())
                        {
                            byte[] chunk = new byte[4096];
                            int count;
                            while ((count = stream.Read(chunk, 0, chunk.Length)) > 0)
                            {
                                if (buffer.Length + count > MaximumResponseBytes)
                                    throw new IOException("额度响应过大。");
                                buffer.Write(chunk, 0, count);
                            }
                            return AccountStore.Utf8.GetString(buffer.ToArray());
                        }
                    }
                }
                finally { request.Abort(); }
            }
        }
        static double? Number(Dictionary<string, object> map, string key)
        {
            object value;
            if (map == null || !map.TryGetValue(key, out value) || value == null) return null;
            if (!(value is int) && !(value is long) && !(value is double) && !(value is decimal))
                throw new IOException("未知数值格式。");
            double result = Convert.ToDouble(value, CultureInfo.InvariantCulture);
            if (Double.IsNaN(result) || Double.IsInfinity(result)) throw new IOException("数值无效。");
            return result;
        }

        static string Num(double value) { return value.ToString("0.##", CultureInfo.InvariantCulture); }

        static void Window(StringBuilder output,QuotaWindow window,DateTimeOffset now)
        {
            output.AppendLine(FormatWindowSummary(window,now));
            output.AppendLine("窗口时长："+(window.LimitWindowSeconds.HasValue?Num(window.LimitWindowSeconds.Value/60)+" 分钟":"未知")+
                "；已用 "+(window.UsedPercent.HasValue?Num(window.UsedPercent.Value)+"%":"未知"));
            output.AppendLine("重置："+(window.ResetAtUtc.HasValue?window.ResetAtUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz",CultureInfo.InvariantCulture)+"（本地时间）":"未知"));
        }

        static void Bool(StringBuilder output, Dictionary<string, object> map,
            string key, string label, string yes, string no)
        {
            object value;
            if (map != null && map.TryGetValue(key, out value) && value is bool)
                output.AppendLine(label + "：" + ((bool)value ? yes : no));
        }
        internal static string Format(string json, string expectedAccount)
        {
            return Format(json, expectedAccount, null);
        }

        internal static string Format(string json, string expectedAccount, string expectedUser)
        {
            return Format(json,expectedAccount,expectedUser,DateTimeOffset.UtcNow);
        }

        internal static string Format(string json,string expectedAccount,string expectedUser,DateTimeOffset now)
        {
            var data = AccountStore.Json(AccountStore.Utf8.GetBytes(json));
            CheckIdentity(data, "account_id", expectedAccount);
            CheckIdentity(data, "user_id", expectedUser);
            var output = new StringBuilder();
            output.AppendLine("来源：官方额度服务（本次查询）");
            output.AppendLine("套餐：" + (AccountStore.Clean(AccountStore.Str(data, "plan_type")) ?? "未知"));
            var rate = AccountStore.Map(data, "rate_limit");
            QuotaWindow[] windows=Windows(json);
            if(windows.Length==0) output.AppendLine("额度窗口：剩余未知；重置倒计时 未知");
            foreach(var window in windows) Window(output,window,now);
            Bool(output, rate, "allowed", "普通额度使用", "允许", "不允许");
            var credits = AccountStore.Map(data, "credits");
            string balance = AccountStore.Clean(AccountStore.Str(credits, "balance"));
            output.AppendLine("Credits 余额：" + (balance ?? "未知"));
            Bool(output, credits, "unlimited", "Credits 无限额", "是", "否");
            Bool(output, credits, "has_credits", "Credits 可用", "是", "否");
            output.Append("剩余比例按 100% − 已用比例计算；Credits 余额独立展示。");
            return output.ToString();
        }

        // Persist only fields that are displayed, rather than unknown response fields.
        internal static Dictionary<string,object> SnapshotData(string json)
        {
            var data=AccountStore.Json(AccountStore.Utf8.GetBytes(json));
            var saved=Select(data,"plan_type");
            saved["rate_limit"]=SnapshotRate(AccountStore.Map(data,"rate_limit"));
            saved["credits"]=Select(AccountStore.Map(data,"credits"),"balance","unlimited","has_credits");
            object raw;
            var extras=new List<object>();
            if(data.TryGetValue("additional_rate_limits",out raw) && raw is object[])
                foreach(object value in (object[])raw)
                {
                    var map=value as Dictionary<string,object>;
                    if(map==null) continue;
                    var extra=Select(map,"limit_name","metered_feature");
                    extra["rate_limit"]=SnapshotRate(AccountStore.Map(map,"rate_limit"));
                    extras.Add(extra);
                }
            saved["additional_rate_limits"]=extras;
            return saved;
        }
        static Dictionary<string,object> SnapshotRate(Dictionary<string,object> rate)
        {
            var result=Select(rate,"allowed");
            foreach(string key in new[] {"primary_window","secondary_window"})
            {
                var window=AccountStore.Map(rate,key);
                if(window!=null) result[key]=Select(window,"used_percent","limit_window_seconds","reset_at","limit_reached");
            }
            return result;
        }
        static Dictionary<string,object> Select(Dictionary<string,object> map,params string[] keys)
        {
            var result=new Dictionary<string,object>();
            if(map!=null) foreach(string key in keys) {object value; if(map.TryGetValue(key,out value)) result[key]=value;}
            return result;
        }

        internal static string CreditsSummary(string text,bool hasWindows)
        {
            Match credits=Regex.Match(text??"",@"(?m)^Credits 余额：[^\r\n]*");
            return (hasWindows?"":"额度未知；")+(credits.Success?credits.Value:"Credits 余额：未知");
        }

        internal static void RecoverLegacyWindow(QuotaWindow window,string originalLabel,string text)
        {
            string primary="主要窗口",secondary="次要窗口";
            if(originalLabel.EndsWith(primary,StringComparison.Ordinal)) {window.Role="primary"; window.Prefix=originalLabel.Substring(0,originalLabel.Length-primary.Length);}
            else if(originalLabel.EndsWith(secondary,StringComparison.Ordinal)) {window.Role="secondary"; window.Prefix=originalLabel.Substring(0,originalLabel.Length-secondary.Length);}
            // Match the old role's exact line, never the display position after sorting.
            MatchCollection lines=Regex.Matches(text??"",@"(?m)^"+Regex.Escape(originalLabel)+@"(?:（(?<minutes>[0-9]+(?:\.[0-9]+)?) 分钟）|（时长未知）)?：[^\r\n]*");
            if(lines.Count==1)
            {
                double minutes;
                if(!window.LimitWindowSeconds.HasValue && Double.TryParse(lines[0].Groups["minutes"].Value,NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture,out minutes) && minutes>0)
                    window.LimitWindowSeconds=minutes*60;
                Match used=Regex.Match(lines[0].Value,@"已用 (?<used>[0-9]+(?:\.[0-9]+)?)%");
                double percent;
                if(!window.UsedPercent.HasValue && used.Success && Double.TryParse(used.Groups["used"].Value,NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture,out percent) && percent>=0 && percent<=100)
                    window.UsedPercent=percent;
            }
            window.Label=WindowLabel(window);
        }

        internal static string FormatLegacySnapshot(string text,QuotaWindow[] windows,DateTimeOffset now)
        {
            var output=new StringBuilder();
            foreach(var window in windows) Window(output,window,now);
            // Remove recognized old/new quota blocks before regenerating them from observations.
            var labels=new List<string> {@"[^\r\n]*?(?:主要窗口|次要窗口)(?:（[^\r\n]*?）)?"};
            foreach(var window in windows) labels.Add(Regex.Escape(WindowLabel(window)));
            string metadata=Regex.Replace(text??"",@"(?m)^(?:"+String.Join("|",labels.ToArray())+@")：[^\r\n]*(?:\r?\n窗口时长：[^\r\n]*)?(?:\r?\n重置：[^\r\n]*)?\r?\n?","");
            output.Append(metadata);
            return output.ToString();
        }

        static void CheckIdentity(Dictionary<string, object> data, string field, string expected)
        {
            object raw;
            if (!data.TryGetValue(field, out raw) || raw == null) return;
            string returned = AccountStore.Clean(raw as string);
            if (returned == null || (expected != null && returned != expected))
                throw new IOException("官方额度归属不匹配。");
        }
    }
}
