# GitHub Issues

规格和开发票据登记在 `Veiluna/codex-switch` 的 GitHub Issues。规格作为父议题，每张票作为子议题，阻塞关系通过 GitHub issue dependencies 记录。使用 `gh issue view` 核对票面，使用 `--body-file` 发布多行正文。关闭票据时附测试证据。外部 PR 作为请求入口的设置为关闭。

每次完成并验收一次修改，就同步源码，按递增版本创建 GitHub Release，附桌面 EXE、CLI EXE、自研安装器、npm 格式的 .tgz 及校验清单。核对远端标签、提交及附件后报告下载链接。公共 npm 注册表的发布按维护者授权和登录状态执行。
