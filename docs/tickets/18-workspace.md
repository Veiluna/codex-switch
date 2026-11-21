# 票 18 · 同一用户跨工作空间保留桌面

## 行为与边界

同一 ChatGPT 用户、不同工作空间且桌面正在运行时，在统一锁内读取当前登录和目标存档，核对两个 token 的用户与空间声明。已知声明冲突即拒绝。只用邮箱、`login_owner_id` 或工作空间相同，不能进入保留进程的分支。JWT 声明仅作为本地元数据核对，不代表已验证签名或服务端成员资格。

保留进程分支先将离开凭据及额度保存到原身份对应的旧档案，随后重新对比当前登录与目标存档。发现并发变更即停止。没有已授权的原客户端控制通道时，保留 `auth.json` 和 `.current`，返回 `WorkspaceSelectionRequired`，`Success=false`，并携带锁内读取的 `TargetUserId` / `TargetAccountId`。提示用户在 Codex 自身的账号菜单选择目标工作空间并核对，回到工具后再依据重新读取的当前身份确认；不把准备动作报告为切换成功。

桌面原本关闭时继续使用已存在的原子文件事务直接切换。不同用户或用户身份未知时继续走原来的正常退出、二次强制退出确认与重新打开流程。所有分支共用 `.switch.lock`、目标快照核对、离开快照和文件 CAS 保护。

## 客户端契约证据

2026-11-21 仅使用隔离、无真实账号的 `CODEX_HOME` 运行帮助及 schema 导出：

- PATH 中的 CLI 为 `codex-cli 0.163.0`，其完整 experimental schema 没有 `accountSession/list` / `accountSession/switch`。
- 实际 MSIX 为 `OpenAI.Codex 26.1117.4957.0`；其 `app/resources/codex.exe` 为 `0.171.0-alpha.13`，含上述实验接口。`AccountSessionsResponse` 具有 `activeSessionId`、`userId`、`selectedWorkspaceAccountId`、`isActive` 与 `workspaces`；切换需要服务端发出的 `sessionId` 和目标 `accountId`。
- 内置 `app-server proxy --help` 确实公开连接运行中 control socket 的能力。但单有 CLI 接口不能证明连接的是 Windows 桌面已有的客户端。
- 父任务只读核对 MSIX `app/resources/app.asar` 的 `.vite/build/app-server-host.js`：`StdioTransport.connect` 仅在非 Windows 且 `CODEX_APP_SERVER_USE_LOCAL_DAEMON==1` 时接入 `app-server-control.sock`；Windows 本地模式启动私有 stdio 子进程。`.vite/build/early-bootstrap-916-2fd4cc5178eaa3b5.js` 的客户端动作通过其已有 connection 调用 `accountSession/switch`。因此另起 app-server 或默认 daemon proxy 不能作为原桌面切换已生效的证据。
- 父任务未找到可公开直达工作空间选择器的 deep link；`codex://launch` 是普通启动入口，内部 `/select-workspace` 路由不能直接推导为外部 URI。

Context7 查询的库为 `/openai/codex` 和 `/openai/codex/rust-v0.163.0`。最新主线文档描述实验性的工作空间切换协议，但版本必须以实际安装的客户端为准。官方 0.163.0 `AuthManager` 文档明确说明外部存储变更需要显式 `reload()`，不能将写 `auth.json` 等同于运行客户端切换。

官方来源：

- [app-server 协议与工作空间切换](https://github.com/openai/codex/blob/main/codex-rs/app-server/README.md)
- [0.163.0 的缓存与 reload 行为](https://github.com/openai/codex/blob/rust-v0.163.0/codex-rs/login/src/auth/manager.rs)

## 回归证据

先新增 `tests/Test-WorkspaceSwitch.ps1`，最初因缺少 `BeforeDeparture` 参数失败，再实现分支与桥接。

通过命令：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tests/Test-WorkspaceSwitch.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File tests/Test-AccountService.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File tests/Test-Switch.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File tests/Test-Desktop.ps1
git diff --check
```

独立工作空间用例 12 项，覆盖进程保持、无退出确认、凭据与标记不变、待确认元数据、锁、两类并发变更、冲突声明、不同用户退出重启及关闭时直接完成。共享服务原 36 项和评审原 15 项通过，新增 5 项覆盖旧档案名的额度归属、未确认状态、额度失败及模拟客户端选择后重读，另有 6 项活动身份能力回归。原切换和受控桌面进程测试均通过。

评审修复：默认文件事务即使没有调用方提供的摘要，也绑定锁内首次读取的 `targetBytes`。新增红测先复现正常退出过程中替换目标文件会安装新身份，再修复为 `ConcurrentChange` 且保留原活动登录。`CurrentIdentityVerified` 仅在列举当前项时从实际 `auth.json` 严格计算，不能继承代表存档的能力；API key、缺声明、冲突声明和 `None` / `unknown` 等占位值均不能用于确认用户与空间。

所有账号、token、桌面状态和额度均为隔离夹具。受控进程测试只操作其自行创建的假窗口。不连接真实 app-server，不读取私密登录材料，不切换或关闭真实 Codex，不发布版本。客户端内选择后的真实端到端切换未实测。
