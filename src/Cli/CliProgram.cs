using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace CodexSwitch
{
    internal static class CliProgram
    {
        // Exit codes: 0 completed, 1 operation failed/cancelled, 2 invalid invocation.
        internal static int Run(string[] args)
        {
            try
            {
                try { Console.OutputEncoding = new UTF8Encoding(false); } catch { }
                Options options = Parse(args ?? new string[0]);
                if (options.Help) { Help(); return 0; }
                using (var service = new AccountService(options.Root))
                {
                    if (options.Command == "")
                    {
                        CatalogEntry[] entries = service.List();
                        WarnCurrentRead(service);
                        return Interactive() ? Menu(service, entries, options) : List(entries, service.CurrentReadFailed);
                    }
                    if (options.Command == "list")
                    {
                        CatalogEntry[] entries = service.List();
                        WarnCurrentRead(service);
                        return List(entries, service.CurrentReadFailed);
                    }
                    if (options.Command == "current") return ShowCurrent(service);
                    if (options.Command == "show")
                        return options.Target == null ? ShowCurrent(service) : Show(Saved(service.List(), options.Target));
                    if (options.Command == "save") return Result(service.Save(options.Target));
                    if (options.Command == "delete")
                    {
                        CatalogEntry entry = Saved(service.List(), options.Target);
                        if (entry == null) return Missing();
                        Show(entry);
                        if (!options.Yes && !Ask("删除这个已保存账号？当前登录文件会保留。"))
                            return Error("未删除。交互确认或 --yes 后可删除。", 1);
                        return Result(service.Delete(entry.Name, entry.SnapshotId));
                    }
                    // Unknown words are shortcut aliases only when present in the saved catalog.
                    if (options.Command == "alias" && Saved(service.List(), options.Target) == null)
                        return Error("未知命令或已保存账号。运行 help 查看用法。", 2);
                    return Switch(service, options.Target, options);
                }
            }
            catch (ArgumentException) { return Error("参数无效。运行 help 查看用法。", 2); }
            catch { return Error("操作未完成，请检查本机账号目录、文件访问权限或其他客户端是否正在操作。", 1); }
        }

        sealed class Options
        {
            internal string Root, Command = "", Target;
            internal bool Help, AllowClose, ForceClose, Yes;
        }

        static Options Parse(string[] args)
        {
            var result = new Options();
            var positional = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                if (String.IsNullOrWhiteSpace(arg)) throw new ArgumentException();
                if (!arg.StartsWith("-", StringComparison.Ordinal)) { positional.Add(arg); continue; }
                if (!seen.Add(arg)) throw new ArgumentException();
                switch (arg)
                {
                    case "--root":
                        if (++i >= args.Length || String.IsNullOrWhiteSpace(args[i])) throw new ArgumentException();
                        result.Root = args[i]; break;
                    case "--allow-close": result.AllowClose = true; break;
                    case "--force-close": result.ForceClose = true; break;
                    case "--yes": result.Yes = true; break;
                    case "--help": case "-h": result.Help = true; break;
                    default: throw new ArgumentException();
                }
            }
            if (positional.Count > 0)
            {
                result.Command = positional[0].ToLowerInvariant();
                if (result.Command == "help") result.Help = true;
                switch (result.Command)
                {
                    case "help": case "list": case "current":
                        if (positional.Count != 1) throw new ArgumentException(); break;
                    case "show": case "save":
                        if (positional.Count > 2) throw new ArgumentException();
                        if (positional.Count == 2) result.Target = positional[1]; break;
                    case "switch": case "delete":
                        if (positional.Count != 2) throw new ArgumentException();
                        result.Target = positional[1]; break;
                    default:
                        if (positional.Count != 1) throw new ArgumentException();
                        result.Target = positional[0]; result.Command = "alias"; break;
                }
            }
            bool switching = result.Command == "switch" || result.Command == "alias";
            if ((!switching && (result.AllowClose || result.ForceClose)) ||
                (result.Yes && result.Command != "delete") ||
                (result.Help && (result.AllowClose || result.ForceClose || result.Yes))) throw new ArgumentException();
            if (result.Root == null) result.Root = Environment.GetEnvironmentVariable("CODEX_HOME");
            if (String.IsNullOrWhiteSpace(result.Root))
                result.Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
            // A relative root can accidentally select the caller's working directory.
            string drive = Path.GetPathRoot(result.Root);
            if (!Path.IsPathRooted(result.Root) || String.IsNullOrEmpty(drive) ||
                (drive.Length < 3 && !drive.StartsWith("\\\\", StringComparison.Ordinal))) throw new ArgumentException();
            result.Root = Path.GetFullPath(result.Root);
            return result;
        }

        static void Help()
        {
            Console.WriteLine("Codex Switch 原生命令行\n用法：codex-switch [--root DIR] <命令>\n" +
                "  help                     显示帮助\n" +
                "  list                     查看账号与本机额度快照\n" +
                "  current                  查看实际当前登录\n" +
                "  show [名称]              查看已保存账号详情；省略名称查看当前登录\n" +
                "  save [名称]              保存当前登录；省略名称使用已有别名或默认邮箱名称\n" +
                "  switch 名称              切换到已保存账号\n" +
                "  名称                     按已保存别名快捷切换\n" +
                "  delete 名称 [--yes]      删除存档（保留当前登录文件）\n" +
                "切换：--allow-close 同意普通关闭；--force-close 单独同意强制结束。\n" +
                "两阶段分别确认；--force-close 不代替 --allow-close，--yes 仅用于删除。\n" +
                "无参数时用 ↑/↓ 选择，Enter 切换，Esc 退出；管道中仅列出账号。\n" +
                "--root 必须为绝对目录；默认使用 CODEX_HOME 或用户目录下 .codex。\n" +
                "额度仅显示离开账号时保存的历史快照；到点不代表额度已经恢复。\n" +
                "退出码：0 成功，1 操作失败或取消，2 参数错误。");
        }

        static int List(CatalogEntry[] entries, bool currentReadFailed)
        {
            if (entries.Length == 0) { Console.WriteLine(currentReadFailed ? "尚无已保存账号。" : "未登录，尚无已保存账号。"); return 0; }
            foreach (CatalogEntry entry in entries)
            {
                Console.WriteLine((entry.IsCurrent ? "* " : "  ") + Safe(entry.Name) +
                    (entry.IsCurrent ? " [当前登录]" : "") + (!entry.IsSaved ? " [未保存]" : "") +
                    " | " + Value(entry.Email) + " | " + Value(entry.Plan));
                foreach(QuotaWindow window in entry.Windows)
                    Console.WriteLine("    "+Safe(QuotaReader.FormatWindowSummary(window,DateTimeOffset.UtcNow)));
                Console.WriteLine("    "+Value(entry.QuotaSummary));
                string reset = ResetStatus(entry);
                if (reset.Length > 0) Console.WriteLine("    " + reset);
            }
            Console.WriteLine("额度为历史快照；未知字段保持未知。使用 show 名称 查看详情。");
            return 0;
        }

        static CatalogEntry Current(CatalogEntry[] entries)
        {
            foreach (CatalogEntry entry in entries) if (entry.IsCurrent) return entry;
            return null;
        }

        static void WarnCurrentRead(AccountService service)
        {
            if (service.CurrentReadFailed) Console.Error.WriteLine("无法识别当前登录：活动登录文件无法读取或格式无效，已保存账号仍可查看。");
        }

        static int ShowCurrent(AccountService service)
        {
            CatalogEntry current = Current(service.List());
            if (service.CurrentReadFailed)
            {
                WarnCurrentRead(service);
                return 1;
            }
            return Show(current);
        }

        static string ResetStatus(CatalogEntry entry)
        {
            string hint = Safe(entry.ResetHint);
            if (entry.HasDueExhausted && hint.IndexOf("上次已用尽", StringComparison.Ordinal) < 0)
                return "上次已用尽 · 已到点" + (hint.Length == 0 ? "" : "；" + hint);
            return hint;
        }

        static CatalogEntry Saved(CatalogEntry[] entries, string name)
        {
            CatalogEntry found = null;
            foreach (CatalogEntry entry in entries)
                if (entry.IsSaved && String.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    if (found != null) return null;
                    found = entry;
                }
            return found;
        }

        static int Show(CatalogEntry entry)
        {
            if (entry == null) return Missing();
            Console.WriteLine("名称：" + Value(entry.Name));
            Console.WriteLine("状态：" + (entry.IsCurrent ? "当前登录" : "已保存") + (!entry.IsSaved ? " · 未保存" : ""));
            Console.WriteLine("邮箱：" + Value(entry.Email));
            Console.WriteLine("账号 ID：" + Value(entry.AccountId));
            Console.WriteLine("套餐：" + Value(entry.Plan));
            Console.WriteLine("登录类型：" + Value(entry.LoginType));
            Console.WriteLine("额度快照：\n" + Value(AccountService.FormatQuotaDetails(entry,DateTimeOffset.UtcNow)));
            string reset = ResetStatus(entry);
            if (reset.Length > 0) Console.WriteLine(reset);
            return 0;
        }

        static int Switch(AccountService service, string target, Options options)
        {
            return Result(service.Switch(target, Confirmation(options)));
        }

        static int Switch(AccountService service, CatalogEntry selected, Options options)
        {
            return Result(service.Switch(selected.Name, selected.SnapshotId, Confirmation(options)));
        }

        static Func<string, string, bool> Confirmation(Options options)
        {
            return delegate(string phase, string prompt)
            {
                if (phase == "Close") return options.AllowClose || Ask(prompt);
                if (phase == "Force") return options.ForceClose || Ask(prompt);
                return false;
            };
        }

        static int Menu(AccountService service, CatalogEntry[] entries, Options options)
        {
            var choices = new List<CatalogEntry>();
            foreach (CatalogEntry entry in entries) if (entry.IsSaved) choices.Add(entry);
            if (choices.Count == 0) return List(entries, service.CurrentReadFailed);
            int selected = 0;
            while (true)
            {
                Console.WriteLine("\n↑/↓ 选择账号，Enter 切换，Esc 退出");
                for (int i = 0; i < choices.Count; i++)
                {
                    Console.WriteLine((selected == i ? "> " : "  ") + Safe(choices[i].Name) +
                        (choices[i].IsCurrent ? " [当前登录]" : "") +
                        (ResetStatus(choices[i]).Length == 0 ? "" : " · " + ResetStatus(choices[i])));
                    foreach(QuotaWindow window in choices[i].Windows)
                        Console.WriteLine("    "+Safe(QuotaReader.FormatWindowSummary(window,DateTimeOffset.UtcNow)));
                }
                ConsoleKey key = Console.ReadKey(true).Key;
                if (key == ConsoleKey.Escape) return 0;
                if (key == ConsoleKey.UpArrow) selected = (selected + choices.Count - 1) % choices.Count;
                else if (key == ConsoleKey.DownArrow) selected = (selected + 1) % choices.Count;
                else if (key == ConsoleKey.Enter) return Switch(service, choices[selected], options);
            }
        }

        static bool Ask(string prompt)
        {
            if (!Interactive()) return false;
            Console.WriteLine(Safe(prompt) + " [y/N，Esc 取消]");
            ConsoleKey key = Console.ReadKey(true).Key;
            Console.WriteLine();
            return key == ConsoleKey.Y;
        }

        [DllImport("kernel32.dll")] static extern IntPtr GetStdHandle(int kind);
        [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool GetConsoleMode(IntPtr handle, out uint mode);

        static bool Interactive()
        {
            uint mode;
            return !Console.IsInputRedirected && !Console.IsOutputRedirected &&
                GetConsoleMode(GetStdHandle(-10), out mode) && GetConsoleMode(GetStdHandle(-11), out mode);
        }

        static string Value(string value) { return String.IsNullOrWhiteSpace(value) ? "未知" : Safe(value); }
        static string Safe(string value)
        {
            if (value == null) return "";
            var text = new StringBuilder(value.Length);
            foreach (char c in value) text.Append(Char.IsControl(c) && c != '\n' ? ' ' : c);
            return text.ToString();
        }
        static int Result(OperationResult result)
        {
            if (result == null) return Error("操作未完成。", 1);
            if (!result.Success) return Error(Value(result.Message), 1);
            Console.WriteLine(Value(result.Message)); return 0;
        }
        static int Missing() { return Error("未找到所选已保存账号或当前登录。", 1); }
        static int Error(string message, int code) { Console.Error.WriteLine(message); return code; }
    }
}
