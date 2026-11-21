# Windows 构建和安装

在 Windows x64 上运行 `build.cmd`。默认版本为 0.3.0，也可传入 `-Version`。构建先用系统 .NET Framework 的 C# 5 编译器生成共享核心，其中嵌入五个兼容脚本资源；随后将完全相同的核心嵌入桌面和命令行 EXE。MakeCab 和 Windows Installer COM 将两个正式程序打入 MSI。源码、依赖、产物及 SHA-256 记录在 `dist/build-info.json`。

MSI 面向当前用户，默认安装在 `%LOCALAPPDATA%\Programs\CodexSwitch`，创建开始菜单快捷方式，并将安装目录追加到用户 PATH。重新打开终端后可以直接输入 `codex-switch`。升级采用原产品家族的 UpgradeCode 和递增版本。卸载移除已安装的两个程序、快捷方式及对应 PATH 条目，保留其他用户数据。

```powershell
.\build.cmd
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tests\Test-NativeDistribution.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tests\Test-Installer.ps1
```

分发测试核对源码、两个主程序内的共享核心、五个脚本资源与 MSI 内两个文件的 SHA-256。安装测试使用独立测试产品和临时环境变量，实际安装旧版夹具、升级到双程序版、尝试降级再卸载，同时核对用户原有数据。PATH 条目的追加与卸载遵循 [Windows Installer Environment 表](https://learn.microsoft.com/en-us/windows/win32/msi/environment-table)。
