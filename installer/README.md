# Windows 构建和安装

在 Windows x64 上运行 `build.cmd`，默认生成 0.4.0 的两个便携 EXE、自研 `installer.exe`、npm tarball 和 `SHA256SUMS.txt`。Windows 原生程序使用系统 .NET Framework 4.8 C# 编译器和 Windows PowerShell 5.1。构建 npm 格式的附件时使用 npm；仅构建原生发行物可加 `-SkipNpm`。用户下载后双击安装或使用静默安装均可离线完成。

安装器编译时将最终桌面端、CLI 和卸载入口嵌入自身。两个应用继续使用相同的本地账号核心。构建记录位于 `dist/build-info.json`。安装器按当前用户安装到 `%LOCALAPPDATA%\Programs\CodexSwitch`，管理开始菜单入口、用户 PATH 和 Windows 应用列表的卸载项。旧 MSI 的测试构建器保存在 `tests/legacy-msi`，仅用于历史兼容验证。

## 验收

```powershell
.\build.cmd
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tests\Test-NativeDistribution.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tests\Test-SetupEntry.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tests\Test-Installer.ps1
```

分发测试核对两个便携程序的共享核心、安装器的三个资源和最终附件摘要。入口测试启动正式安装器，检查帮助、无效参数与图形取消。生命周期测试将安装器服务的文件和注册表目标切换到隔离夹具，保留正式程序资源与快捷方式创建逻辑。
