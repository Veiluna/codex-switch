# GitHub Issues

规格和开发票据登记在 `Veiluna/codex-switch` 的 GitHub Issues。规格作为父议题，每张票作为子议题，阻塞关系通过 GitHub issue dependencies 记录。使用 `gh issue view` 核对票面，使用 `--body-file` 发布多行正文。关闭票据时附测试证据。外部 PR 作为请求入口的设置为关闭。

每次完成并验收一次修改，就同步源码，按递增版本创建 GitHub Release。标题与标签相同，例如 `v0.4.4`；唯一上传的附件为 `CodexSwitch-Setup-X.Y.Z-x64.exe`。变更说明写在发布正文。使用 `installer/Publish-Release.ps1 -ReleaseNotesFile <正文文件>` 创建或继续草稿，脚本会核对安装器名称、数量、大小和 SHA-256，再公开发布。核对远端标签、提交及下载链接后回写票据。
