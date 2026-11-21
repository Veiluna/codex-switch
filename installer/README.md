# Windows 构建和安装

在 Windows x64 上运行 `build.cmd`。发布附件为 `dist/CodexSwitch-Setup-0.4.5-x64.exe`。桌面和命令行二进制、构建记录与校验清单留在本地供验收使用。Windows 原生程序使用系统 .NET Framework 4.8 C# 编译器和 Windows PowerShell 5.1。需要测试本地 npm 包时可加 `-IncludeNpm`。用户双击安装或使用静默安装均可离线完成。

安装器内嵌桌面端、CLI 和卸载入口。两种组件使用同一份本地账号核心，可以分别安装。桌面组件管理开始菜单快捷方式，CLI 组件管理用户 PATH。默认程序目录为 `%LOCALAPPDATA%\Programs\CodexSwitch`。图形安装器支持浏览或输入自定义本地目录。更改已有安装位置时同步更新所选组件、快捷方式、PATH 和卸载登记；后续升级和卸载通过登记与安装记录核对实际位置。构建记录位于 `dist/build-info.json`。

从 Windows「已安装的应用」卸载时，卸载程序自身仍在运行。修复后的安装器会在卸载事务内为这个文件改名，并保留在 `%LOCALAPPDATA%\CodexSwitch\Setup` 中。程序、快捷方式、PATH 和卸载登记均在返回成功前处理完毕。需要释放这部分空间时，等卸载窗口退出后，可手动删除该目录中对应的 `uninstall.exe.cs-backup-<GUID>` 文件。

## 验收

出包机需要启用 Microsoft Defender。构建脚本在嵌入 CLI、桌面端和卸载程序前扫描各文件，也会扫描最终安装器。独立运行 npm 打包脚本时，会检查暂存目录里的两个 EXE。只有扫描明确报告该文件无威胁且文件摘要保持一致，才会继续。遇到无法识别的扫描输出时，构建会停止。

```powershell
.\build.cmd
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tests\Test-NativeDistribution.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tests\Test-SetupEntry.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tests\Test-Installer.ps1
```

分发测试核对两个便携程序的共享核心、安装器的三个资源和最终附件摘要。入口测试启动正式安装器，检查帮助、无效参数与图形取消。生命周期测试将安装器服务的文件和注册表目标切换到隔离夹具，保留正式程序资源与快捷方式创建逻辑。

验收后将源码推送到 main，运行 `installer/Publish-Release.ps1 -ReleaseNotesFile <正文文件>`。发布脚本按构建版本构造安装器文件名，校验草稿中的单一附件与本地字节一致，再将其设为最新正式版。
