using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using Microsoft.Win32.SafeHandles;

namespace CodexSwitch
{
    internal sealed class AccountDetails
    {
        public string Name = "";
        public string AccountId = "未知";
        public string Plan = "未知";
        public string UserId = "未知";
        public string LoginType = "未知";
        public string SnapshotId = "";
        public string Email = "未知";
    }

    internal static class AccountStore
    {
        internal static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        internal static bool ValidName(string name)
        {
            return !String.IsNullOrWhiteSpace(name) && name == name.Trim() &&
                !Regex.IsMatch(name, "[<>:\"/\\\\|?*\\x00-\\x1f]|[. ]$|^\\.|^backup-|^(CON|PRN|AUX|NUL|COM[1-9¹²³]|LPT[1-9¹²³])(?:\\.|$)", RegexOptions.IgnoreCase);
        }
        internal static string Root(string root)
        {
            bool drive = root != null && Regex.IsMatch(root, "^[A-Za-z]:[\\\\/]") && root.IndexOf(':', 2) < 0;
            bool unc = root != null && Regex.IsMatch(root, @"^\\\\[^\\/?.:]+[\\/][^\\/:]+(?:[\\/]|$)") && root.IndexOf(':') < 0;
            if (!drive && !unc)
                throw new IOException("本机账号目录无效。");
            return Path.GetFullPath(root);
        }
        internal static Dictionary<string, object> Json(byte[] bytes)
        {
            string text = Utf8.GetString(bytes).TrimStart('\uFEFF');
            if (bytes.Length > 2*1024*1024 || !text.TrimStart().StartsWith("{",StringComparison.Ordinal)) throw new IOException("存档格式无效。");
            var value = new JavaScriptSerializer { MaxJsonLength=2*1024*1024, RecursionLimit=32 }.DeserializeObject(text) as Dictionary<string,object>;
            if (value==null) throw new IOException("存档格式无效。");
            return value;
        }
        internal static Dictionary<string, object> Map(Dictionary<string, object> value, string key)
        {
            object found;
            return value != null && value.TryGetValue(key, out found)
                ? found as Dictionary<string, object> : null;
        }

        internal static string Str(Dictionary<string, object> value, string key)
        {
            object found;
            return value != null && value.TryGetValue(key, out found) ? found as string : null;
        }
        internal static string Clean(string value)
        {
            if (String.IsNullOrWhiteSpace(value) || value != value.Trim() ||
                value.Length > 256 || Regex.IsMatch(value, "[\\x00-\\x1f\\x7f]"))
                return null;
            return value;
        }
        internal static Dictionary<string, object> Claims(string jwt)
        {
            if (String.IsNullOrEmpty(jwt)) return null;
            try
            {
                string[] parts = jwt.Split('.');
                if (parts.Length != 3 || parts[0].Length == 0 || parts[2].Length == 0) return null;
                string payload = parts[1].Replace('-', '+').Replace('_', '/');
                payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
                return Map(Json(Convert.FromBase64String(payload)), "https://api.openai.com/auth");
            }
            catch { return null; }
        }

        // The confirmation token is an opaque digest, never the credential JSON itself.
        internal static string Hash(byte[] bytes)
        {
            using (var sha = SHA256.Create())
                return Convert.ToBase64String(sha.ComputeHash(bytes));
        }

        internal static string Target(string accounts, string name)
        {
            if (!ValidName(name)) throw new IOException("账号名称无效。");
            string modern = Path.Combine(accounts, name + "_auth.json");
            string legacy = Path.Combine(accounts, name + ".json");
            bool hasModern = Native.Exists(modern);
            bool hasLegacy = Native.Exists(legacy);
            if (hasModern && hasLegacy)
                throw new IOException("存在两个同名存档，请先整理新旧后缀冲突；未执行删除。");
            if (!hasModern && !hasLegacy) throw new IOException("未找到所选账号存档。");
            return hasModern ? modern : legacy;
        }
        public static string[] List(string root)
        {
            string accounts = Path.Combine(Root(root), "accounts");
            if (!Directory.Exists(accounts)) return new string[0];
            using (var guards = new PathGuards(accounts))
            {
                var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string path in Directory.GetFiles(accounts, "*.json", SearchOption.TopDirectoryOnly))
                {
                    string name = Path.GetFileNameWithoutExtension(path);
                    if (name.EndsWith("_auth", StringComparison.OrdinalIgnoreCase))
                        name = name.Substring(0, name.Length - 5);
                    if (ValidName(name)) names.Add(name);
                }
                var result = new string[names.Count];
                names.CopyTo(result);
                return result;
            }
        }

        internal static byte[] ReadArchive(string root, string name)
        {
            string accounts = Path.Combine(Root(root), "accounts");
            using (var guards = new PathGuards(accounts))
            using (var handle = Native.Open(Target(accounts, name), false, false))
                return Native.Bytes(handle);
        }

        public static AccountDetails Read(string root, string name)
        {
            byte[] bytes = ReadArchive(root, name);
            return Describe(bytes, name);
        }

        internal static AccountDetails Describe(byte[] bytes, string name)
        {
            var details = new AccountDetails { Name = name, SnapshotId = Hash(bytes) };
            Dictionary<string, object> data;
            try { data = Json(bytes); }
            catch { return details; }

            var tokens = Map(data, "tokens");
            var idClaims = Claims(Str(tokens, "id_token"));
            var accessClaims = Claims(Str(tokens, "access_token"));
            string account = Clean(Str(tokens, "account_id"));
            string idAccount = Clean(Str(idClaims, "chatgpt_account_id"));
            string accessAccount = Clean(Str(accessClaims, "chatgpt_account_id"));
            if (account == null) account = idAccount ?? accessAccount;

            // Local JWT claims are metadata, not signature verification. Conflicts remain unknown.
            bool conflict = account != null &&
                ((idAccount != null && idAccount != account) ||
                 (accessAccount != null && accessAccount != account));
            string user = Clean(Str(idClaims, "chatgpt_user_id")) ?? Clean(Str(idClaims, "user_id"));
            string accessUser = Clean(Str(accessClaims, "chatgpt_user_id")) ?? Clean(Str(accessClaims, "user_id"));
            if (user != null && accessUser != null && user != accessUser) conflict = true;
            if (!conflict)
            {
                details.AccountId = account ?? "未知";
                details.UserId = user ?? accessUser ?? "未知";
                details.Plan = Clean(Str(idClaims, "chatgpt_plan_type")) ??
                    Clean(Str(accessClaims, "chatgpt_plan_type")) ?? "未知";
                details.Email = Email(Str(tokens, "id_token")) ?? Email(Str(tokens, "access_token")) ?? "未知";
            }
            string authMode = Str(data, "auth_mode");
            if (!String.IsNullOrEmpty(Str(data, "OPENAI_API_KEY"))) details.LoginType = "API key";
            else if ((authMode == null || authMode == "chatgpt") && tokens != null &&
                accessClaims != null && !String.IsNullOrEmpty(Str(tokens, "access_token")))
                details.LoginType = "ChatGPT";
            return details;
        }

        static string Email(string jwt)
        {
            try
            {
                string[] parts = jwt.Split('.');
                if (parts.Length != 3 || parts[0].Length == 0 || parts[2].Length == 0) return null;
                string value = parts[1].Replace('-', '+').Replace('_', '/');
                value = value.PadRight(value.Length + (4 - value.Length % 4) % 4, '=');
                var claims = Json(Convert.FromBase64String(value));
                return Clean(Str(claims, "email")) ?? Clean(Str(Map(claims, "https://api.openai.com/profile"), "email"));
            }
            catch { return null; }
        }
        public static void Delete(string root,string name) { Delete(root,name,null); }
        public static void Delete(string root, string name, string expectedSnapshotId)
        {
            string accounts = Path.Combine(Root(root), "accounts");
            if (!ValidName(name)) throw new IOException("账号名称无效。");
            DeleteLocked(accounts, name, expectedSnapshotId);
        }

        static void DeleteLocked(string accounts, string name, string expectedSnapshot)
        {
            using (var guards = new PathGuards(accounts))
            {
                string gatePath = Path.Combine(accounts, ".switch.lock");
                Native.AssertPlain(gatePath);
                using (var gate = Native.OpenLock(gatePath))
                using (var handle = Native.Open(Target(accounts, name), false, true))
                {
                    if (expectedSnapshot != null && expectedSnapshot != Hash(Native.Bytes(handle)))
                        throw new IOException("存档在确认后已变化，请重新查看并确认删除。");
                    Native.Disposition deletion = new Native.Disposition { DeleteFile = true };
                    uint dispositionSize = (uint)Marshal.SizeOf(typeof(Native.Disposition));
                    if (!Native.SetFileInformationByHandle(handle, Native.FileDispositionInfo, ref deletion, dispositionSize))
                        throw new IOException("删除失败，存档已保留，请检查文件访问权限。");
                    // .current is historical lookup only; retaining it preserves unknown formats and avoids multi-file partial deletion.
                }
            }
        }
        internal sealed class PathGuards : IDisposable
        {
            readonly List<SafeFileHandle> handles = new List<SafeFileHandle>();
            internal PathGuards(string directory)
            {
                try
                {
                    var paths = new List<string>();
                    string path = Path.GetFullPath(directory);
                    while (path != null)
                    {
                        paths.Add(path);
                        var parent = Directory.GetParent(path);
                        path = parent == null ? null : parent.FullName;
                    }
                    paths.Reverse();
                    foreach (string part in paths) handles.Add(Native.Open(part, true, false));
                }
                catch { Dispose(); throw; }
            }

            public void Dispose()
            {
                for (int i = handles.Count - 1; i >= 0; i--) handles[i].Dispose();
                handles.Clear();
            }
        }
        internal static class Native
        {
            const uint GenericRead = 0x80000000;
            const uint GenericWrite = 0x40000000;
            const uint DeleteAccess = 0x00010000;
            const uint ListDirectoryAccess = 0x00000001;
            const uint ShareReadWrite = 3;
            const uint OpenExisting = 3;
            const uint OpenAlways = 4;
            const uint OpenReparsePoint = 0x00200000;
            const uint BackupSemantics = 0x02000000;
            const uint ReparseAttribute = 0x400;
            const uint DirectoryAttribute = 0x10;
            internal const int FileDispositionInfo = 4;
            [StructLayout(LayoutKind.Sequential)] internal struct Info { public uint Attributes; public System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write; public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow; }
            [StructLayout(LayoutKind.Sequential)] internal struct Disposition { [MarshalAs(UnmanagedType.U1)] public bool DeleteFile; }
            [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern SafeFileHandle CreateFile(string path,uint access,uint share,IntPtr security,uint creation,uint flags,IntPtr template);
            [DllImport("kernel32.dll",SetLastError=true)] static extern bool GetFileInformationByHandle(SafeFileHandle handle,out Info info);
            [DllImport("kernel32.dll",SetLastError=true)] internal static extern bool SetFileInformationByHandle(SafeFileHandle handle,int kind,ref Disposition info,uint size);
            internal static void AssertPlain(string path)
            {
                if (Exists(path) &&
                    (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("不支持重解析点。");
            }

            // 仅“确实不存在”返回 false；拒绝访问等错误必须传递给调用方。
            internal static bool Exists(string path)
            {
                try { File.GetAttributes(path); return true; }
                catch (FileNotFoundException) { return false; }
                catch (DirectoryNotFoundException) { return false; }
            }

            internal static SafeFileHandle OpenLock(string path)
            {
                return OpenCore(path, GenericRead | GenericWrite, 0, OpenAlways, false);
            }

            internal static SafeFileHandle Open(string path, bool directory, bool delete)
            {
                // A zero-access metadata handle does not participate in all filesystem
                // sharing checks. Request the directory's minimal data-read right so
                // omitting FILE_SHARE_DELETE actually prevents rename/delete retargeting.
                // This needs list permission only, not generic read/security access.
                uint access = directory ? ListDirectoryAccess : GenericRead | (delete ? DeleteAccess : 0);
                uint share = directory ? ShareReadWrite : 0;
                return OpenCore(path, access, share, OpenExisting, directory);
            }

            static SafeFileHandle OpenCore(string path, uint access, uint share, uint creation, bool directory)
            {
                uint flags = OpenReparsePoint | (directory ? BackupSemantics : 0);
                var handle = CreateFile(path, access, share, IntPtr.Zero, creation, flags, IntPtr.Zero);
                if (handle.IsInvalid)
                {
                    handle.Dispose();
                    throw new IOException("账号目录正忙、不可读写或存档不存在。");
                }
                Info info;
                if (!GetFileInformationByHandle(handle, out info) ||
                    (info.Attributes & ReparseAttribute) != 0 ||
                    ((info.Attributes & DirectoryAttribute) != 0) != directory ||
                    (!directory && info.Links != 1))
                {
                    handle.Dispose();
                    throw new IOException("不支持重解析点、硬链接或特殊存档路径。");
                }
                return handle;
            }
            internal static byte[] Bytes(SafeFileHandle handle)
            {
                // FileStream owns a non-owning wrapper, so the validated native handle remains open for deletion.
                using (var wrapper = new SafeFileHandle(handle.DangerousGetHandle(), false))
                using (var stream = new FileStream(wrapper, FileAccess.Read))
                {
                    if (stream.Length > 2 * 1024 * 1024) throw new IOException("存档过大，未读取。");
                    byte[] bytes = new byte[(int)stream.Length];
                    int count = 0;
                    while (count < bytes.Length)
                    {
                        int read = stream.Read(bytes, count, bytes.Length - count);
                        if (read == 0) throw new IOException("读取存档未完成。");
                        count += read;
                    }
                    return bytes;
                }
            }
        }
    }
}
