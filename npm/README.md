# Codex Switch

Windows x64 原生 Codex 账号切换工具，需要 Windows PowerShell 5.1、.NET Framework 4.x 和 Node.js 18 或以上的 x64 版本。

请暂停使用 v0.4.0 包，本机 Microsoft Defender 已隔离其中两个 EXE。以下命令适用于后续通过验收的版本。请在下载目录运行，并将 `X.Y.Z` 替换为该版本号。

```powershell
npm install --global ".\veiluna-codex-switch-X.Y.Z.tgz"
codex-switch
codex-switch help
```

一次运行默认进入原生 CLI：

```powershell
npx --yes --package=".\veiluna-codex-switch-X.Y.Z.tgz" -- codex-switch
```

包内自带同版原生 CLI 和安装器，无 JavaScript 依赖，无 npm 安装生命周期脚本，不在安装时下载 EXE。npm 安装仅提供上述命令；需要桌面安装时显式运行：

```powershell
codex-switch-install
codex-switch-install install --quiet
codex-switch-install uninstall --quiet
```

无参数打开安装器 GUI；静默命令等待安装器完成并返回原始退出码。安装器与 CLI 均直接使用包内 EXE，转发全部参数并继承标准输入、标准输出和标准错误。包中不含账号、凭据、缓存或项目工作区文件。

也可一次运行安装器：

```powershell
npx --yes --package=".\veiluna-codex-switch-X.Y.Z.tgz" -- codex-switch-install
```

离线安装时，可在上述 `npm install` 命令中添加 `--offline`。
