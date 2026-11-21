# 原生账号存档与额度实现证据

0.3 从账号切换事务中采集原账号的额度，使用桌面端退出后读取的活动凭据。浏览详情和刷新名单只访问本地历史快照。下文记录的是最初接入额度读取时的接口依据及底层兼容测试。

核实日期：2026-11-21。编译目标：C# 5 / .NET Framework 4.8，额外引用 `System.Web.Extensions.dll`。

## 官方来源与路线选择

Context7 `/openai/codex` 核实了 app-server `initialize`、`account/read`、`account/rateLimits/read`、额度字段和 proxy 的语义。隔离空 `CODEX_HOME` 下运行本机 `codex-cli 0.163.0 app-server generate-ts --experimental`，确认该版本 `account/read` 尚无服务端 `userId`；只凭共享 workspace 的 account ID 不能把已有 app-server 的额度安全归属给某个存档。

官方 [app-server 文档](https://developers.openai.com/codex/app-server/) 将 `chatgptAuthTokens` 限定为已拥有完整登录生命周期的 host。本工具不接管 refresh token 生命周期，因此没有使用这个实验登录入口，也没有创建第二个可刷新真实 token 的 app-server。

按已核实的官方 CLI 后端源码，针对所选存档直接执行一次固定的官方 HTTPS GET：

- [官方默认配置](https://github.com/openai/codex/blob/main/codex-rs/config/defaults.toml)：`chatgpt_base_url` 的默认值为 `https://chatgpt.com/backend-api/`。
- [官方后端 client](https://github.com/openai/codex/blob/main/codex-rs/backend-client/src/client.rs)：Bearer 身份头、`ChatGPT-Account-Id`、raw `rate_limit.primary_window/secondary_window` 与 `credits` 的映射。
- [官方 usage 请求](https://github.com/openai/codex/blob/main/codex-rs/backend-client/src/client/rate_limit_resets.rs)：GET `/wham/usage`，默认不发送 reserve 的 opt-in header。该读取不消费重置 credits。
- [官方生成的 raw 响应模型](https://github.com/openai/codex/blob/main/codex-rs/codex-backend-openapi-models/src/models/rate_limit_status_payload.rs)：`plan_type`、可缺省/null 的 `rate_limit`、`credits`、`additional_rate_limits`。

代码不读取自定义 origin 配置或环境变量。选中档案中的 access token 只用于 `https://chatgpt.com/backend-api/wham/usage`，不跟随重定向、使用 TLS 1.2 和系统证书验证，不使用 refresh token、不写回登录、不创建子进程或临时凭据、不启动 MCP/analytics。活跃和非活跃存档走相同的专属 bearer 请求；若 access/id JWT 明确冲突则拒绝，响应 account/user 身份若存在也需匹配。401/403 提示切换重登后重新保存，不自动刷新。

此 URL 属于官方 CLI 内部后端接口，并非稳定的公共 API 承诺；服务端变更、过期 token、网络故障都明确显示未知。没有使用真实账号作在线验收。

Context7 `/dotnet/docs` 和 Microsoft 官方文档核实了证书验证、[禁重定向](https://learn.microsoft.com/en-us/dotnet/api/system.net.httpwebrequest.allowautoredirect?view=netframework-4.8)、[请求超时](https://learn.microsoft.com/en-us/dotnet/api/system.net.httpwebrequest.timeout?view=netframework-4.8)、[handle 文件信息](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-getfileinformationbyhandle)及 [handle 文件处置](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-setfileinformationbyhandle)。响应流亦受 Abort 计时器和 1 MiB 上限保护；`Timeout`/`ReadWriteTimeout` 均为 12 秒。

## 数据与删除语义

`AccountStore.Read` 仅返回名称、登录类型、account/user ID、套餐和确认用 SHA-256 摘要；JWT 信息是本机 metadata，不能宣称签名认证。缺失/冲突字段保持未知。`QuotaReader` 显示 `100 - used_percent` 的剩余比例，同时注明已用比例；按真实窗口秒数展示分钟，不把 primary 固定叫作“五小时”，重置 epoch 转成本地时间并显示 UTC offset。Credits 余额独立展示，不推断币种、余额、可恢复性。

删除使用与旧保存/切换相同的 `.switch.lock`，固定并持有祖先目录 handle，拒绝 reparse point、硬链接、设备路径和 ADS，支持盘符绝对路径及标准 UNC（UNC 需底层文件系统支持 handle 信息/处置）。新旧后缀同时存在则完整拒绝；仅对唯一所选档案的已验证 handle 执行单次删除，因此没有双文件部分删除。三参数 Delete 重载检查用户确认时的文件内容摘要；目标有变化则停止。当前 `auth.json`、备份、其他别名均不修改。

`.current` 始终作为历史提示保留，包括指向已删别名的旧标记；不把它当身份或归属证明，不猜测未知格式、不联动修改而形成部分失败。现有切换实现已在使用 marker 前比较实际登录身份。

## 验证

先编写 `tests/NativeAccountsTests.cs` 并运行，确认缺实现时红灯；补实现后运行 `powershell -NoProfile -ExecutionPolicy Bypass -File tests/Test-NativeAccounts.ps1`。

早期沙箱下 32 项合成检查通过：旧/新文件列举、隐藏备份、元数据未知值、路径边界、后缀冲突无部分删除、过期确认摘要、只删除别名、marker 保留、忙锁、目录 retarget 被拒绝、只读失败保留、无额外别名长度限制、剩余量 0/100/小数/未知、raw 窗口与本地重置时间、不同 account/user 与异常身份拒绝、固定 endpoint/GET/headers/禁止跳转/TLS/超时。

初次沙箱测试的硬链接 fixture 创建被 Win32 error 5 拒绝，且当时目录改名断言也可能被沙箱本身的限制误判为通过。随后父任务在获自动审批的真实 Windows 环境重新运行，硬链接断言通过，但发现零访问权限的目录 metadata handle 不能阻止目录改名。本次保留该回归测试并修复：目录改用 `FILE_LIST_DIRECTORY`（0x1，最小目录数据读取权限），保持 `FILE_SHARE_READ | FILE_SHARE_WRITE`、不授予 `FILE_SHARE_DELETE`，从祖先到目标持有并验证 handle。没有请求 `GENERIC_READ` 所含的额外权限，也没有获取管理员权限改变 ACL。

Context7 `/websites/learn_microsoft_en-us_windows_win32_api` 与 [Microsoft CreateFileW](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-createfilew) 资料确认零权限仅查询 metadata、属性访问不受共享标志同等约束，省略 delete sharing 可阻止删除/改名；[文件权限常量](https://learn.microsoft.com/en-us/windows/win32/fileio/file-access-rights-constants) 明确 `FILE_LIST_DIRECTORY` 为 0x1。查询返回的 [CreateDirectory2 官方示例](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-createdirectory2a) 亦使用目录列表权限且省略 delete sharing 来防止目录被删除后重定向。

修复后在 `require_escalated` 的真实 Windows 进程中重新运行合成账号测试，37 项全部通过：硬链接两项断言、被持有目录不能改名均实际通过，没有 SKIP；另补祖先目录改名受阻、普通子文件访问不受妨碍、释放 handle 后目录可正常改名的回归检查。所有测试仅用临时虚构账号，没有读取真实账号、调用真实额度服务或输出凭据。
