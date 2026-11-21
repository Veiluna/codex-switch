using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Text;
using System.Threading;

namespace CodexSwitch
{
    public sealed class QuotaWindow
    {
        public string Label = "";
        public DateTimeOffset? ResetAtUtc;
        public bool Exhausted;
    }

    internal static class QuotaReader
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
            AddWindow(output,prefix+"主要窗口",AccountStore.Map(rate,"primary_window"));
            AddWindow(output,prefix+"次要窗口",AccountStore.Map(rate,"secondary_window"));
        }
        static void AddWindow(List<QuotaWindow> output,string label,Dictionary<string,object> map)
        {
            if(map==null) return;
            double? reset=Number(map,"reset_at"),used=Number(map,"used_percent");
            if(used.HasValue && (used.Value<0 || used.Value>100)) throw new IOException("比例无效。");
            if(reset.HasValue && (reset.Value<0 || reset.Value!=Math.Truncate(reset.Value))) throw new IOException("重置时间无效。");
            object flag;
            output.Add(new QuotaWindow { Label=label,
                ResetAtUtc=reset.HasValue?(DateTimeOffset?)new DateTimeOffset(1970,1,1,0,0,0,TimeSpan.Zero).AddSeconds(reset.Value):null,
                Exhausted=(used.HasValue && used.Value==100) || (map.TryGetValue("limit_reached",out flag) && flag is bool && (bool)flag) });
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

        static string Reset(Dictionary<string, object> window)
        {
            double? reset = Number(window, "reset_at");
            if (!reset.HasValue) return "未知";
            if (reset.Value < 0 || reset.Value != Math.Truncate(reset.Value))
                throw new IOException("重置时间无效。");
            try
            {
                return new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero)
                    .AddSeconds(reset.Value).ToLocalTime()
                    .ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture) + "（本地时间）";
            }
            catch (ArgumentOutOfRangeException) { throw new IOException("重置时间无效。"); }
        }

        static void Window(StringBuilder output, string label, Dictionary<string, object> window)
        {
            if (window == null)
            {
                output.AppendLine(label + "：剩余未知");
                return;
            }
            double? used = Number(window, "used_percent");
            double? seconds = Number(window, "limit_window_seconds");
            if (used.HasValue && (used.Value < 0 || used.Value > 100)) throw new IOException("比例无效。");
            if (seconds.HasValue && seconds.Value <= 0) throw new IOException("窗口无效。");
            output.Append(label).Append(seconds.HasValue
                ? "（" + Num(seconds.Value / 60) + " 分钟）" : "（时长未知）");
            output.AppendLine("：剩余 " + (used.HasValue ? Num(100 - used.Value) + "%" : "未知") +
                "（已用 " + (used.HasValue ? Num(used.Value) + "%" : "未知") + "）");
            output.AppendLine("重置：" + Reset(window));
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
            var data = AccountStore.Json(AccountStore.Utf8.GetBytes(json));
            CheckIdentity(data, "account_id", expectedAccount);
            CheckIdentity(data, "user_id", expectedUser);
            var output = new StringBuilder();
            output.AppendLine("来源：官方额度服务（本次查询）");
            output.AppendLine("套餐：" + (AccountStore.Clean(AccountStore.Str(data, "plan_type")) ?? "未知"));
            var rate = AccountStore.Map(data, "rate_limit");
            Window(output, "主要窗口", AccountStore.Map(rate, "primary_window"));
            Window(output, "次要窗口", AccountStore.Map(rate, "secondary_window"));
            Bool(output, rate, "allowed", "普通额度使用", "允许", "不允许");
            var credits = AccountStore.Map(data, "credits");
            string balance = AccountStore.Clean(AccountStore.Str(credits, "balance"));
            output.AppendLine("Credits 余额：" + (balance ?? "未知"));
            Bool(output, credits, "unlimited", "Credits 无限额", "是", "否");
            Bool(output, credits, "has_credits", "Credits 可用", "是", "否");
            object more;
            if (data.TryGetValue("additional_rate_limits", out more) && more is object[])
            {
                foreach (object entry in (object[])more)
                {
                    var extra = entry as Dictionary<string, object>;
                    if (extra == null) continue;
                    string label = AccountStore.Clean(AccountStore.Str(extra, "limit_name")) ??
                        AccountStore.Clean(AccountStore.Str(extra, "metered_feature")) ?? "附加额度";
                    var extraRate = AccountStore.Map(extra, "rate_limit");
                    Window(output, label + " / 主要窗口", AccountStore.Map(extraRate, "primary_window"));
                    Window(output, label + " / 次要窗口", AccountStore.Map(extraRate, "secondary_window"));
                }
            }
            output.Append("剩余比例按 100% − 已用比例计算；Credits 余额独立展示。");
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
