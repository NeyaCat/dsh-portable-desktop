# DSH Portable Desktop (unofficial)

> **Unofficial project.** This repository is not affiliated with, endorsed by, or
> maintained by DeepSeek. "DeepSeek" and "DeepSeek Harness" are trademarks of their
> respective owners. The bundled DSH runtime and Node.js are third-party
> open-source components under the MIT license.

DSH Portable Desktop is a self-contained Windows x64 desktop shell for
[DeepSeek Harness](https://github.com/deepseek-ai/deepseek-harness) (DSH). It hosts
the local DSH web UI in a native WebView2 window and adds tray behaviour, a
close-to-tray dialog, skin-aware window chrome and an in-app version manager.
The DSH runtime and Node.js are fetched from their official upstream sources when
packaging a release; this repository contains the shell's own source.

## Features

- **Native window** — the DSH UI opens in its own window; no browser tab and no
  console window.
- **Single instance** — launching the EXE again restores and focuses the window
  that is already running.
- **Close to tray** — `×` offers "exit" or "minimize to tray"; the choice can be
  remembered.
- **Tray menu** — open the window, start a new session, open the version manager,
  or exit.
- **Skin-aware chrome** — a 28 px in-page title bar follows the active DSH skin,
  picks a light or dark icon by contrast and keeps native Windows 11 rounded
  corners and window animations.
- **Version manager** — `Version Switch.exe` lists and installs official DSH
  releases into a per-version folder. The running version is never overwritten;
  updates are offered in-app and applied on the next launch.
- **Portable** — configuration, sessions, logs and the WebView2 profile live under
  `data/` next to the executable. Move the folder, move the installation.
- **Local only** — the UI is served on `127.0.0.1` on a free port; external links
  open in the default browser.

## Getting started

1. Download the latest ZIP from [Releases](https://github.com/NeyaCat/dsh-portable-desktop/releases).
2. Extract it to a writable folder (not `Program Files`).
3. Run `DeepSeek Harness.exe`. State is created in `./data`.

Requirements: Windows 10/11 x64, .NET Framework 4.6.2 or later, and the Microsoft
Edge WebView2 Runtime (included with Windows 11 and current Windows 10 builds).

## Build from source

The shell is plain C# targeting .NET Framework 4.6.2 and is compiled with the
compiler that ships with Windows — no Visual Studio or SDK is required.

```powershell
powershell -ExecutionPolicy Bypass -File .\desktop\Build.ps1
```

`desktop\Build.ps1` compiles `desktop\*.cs` into `DeepSeek Harness.exe` and
`Version Switch.exe` in the repository root, copies the WebView2 SDK DLLs and the
loader next to them, and embeds `DesktopChrome.js` and `VersionSwitch.html` as
assembly resources.

The portable runtime (Node.js + `@deepseek-ai/dsh`) is downloaded from pinned
upstream versions when a release is packaged; it is not vendored here.

```text
desktop/                  C# sources for the shell and the version manager
desktop/Build.ps1         compiles both executables with the Windows .NET compiler
desktop/vendor/webview2   Microsoft WebView2 SDK 1.0.2903.40 with its license files
desktop/Test-*.ps1        manual integration tests (close the app first)
DeepSeek Harness.ico      application icon passed to the compiler
```

## Code signing policy

Free code signing provided by [SignPath.io](https://about.signpath.io), certificate by
[SignPath Foundation](https://signpath.org).

- Authors / committers: [@NeyaCat](https://github.com/NeyaCat)
- Reviewers: [@NeyaCat](https://github.com/NeyaCat)
- Approvers: [@NeyaCat](https://github.com/NeyaCat)

Releases are built by GitHub Actions from this repository and every signing
request is manually approved. See [CODE_SIGNING_POLICY.md](CODE_SIGNING_POLICY.md).

## Privacy

This program does not send telemetry or usage data to the maintainer. It contacts
other networked systems only when the user configures a model provider, uses
online features, or checks for DSH updates. See [PRIVACY.md](PRIVACY.md).

## License

MIT — see [LICENSE](LICENSE). Bundled third-party components keep their own
licenses.

## 中文说明

这是一个**非官方**的 DeepSeek Harness（DSH）Windows x64 桌面外壳：把本地 DSH Web
界面放进独立窗口，并加上系统托盘、关闭确认弹窗、跟随皮肤的标题栏和版本管理。
它不是 DeepSeek 官方项目，也不代表 DeepSeek。

- **用法**：解压 ZIP 到可写目录，双击 `DeepSeek Harness.exe`；`Version Switch.exe`
  用于查询和切换 DSH 发行版本。
- **数据**：`data/` 保存 DSH 配置、会话、日志和 WebView2 数据；`workspace/` 是推荐工作区。
- **卸载**：退出程序后删除整个文件夹即可，不写注册表、不留系统目录。
- **隐私**：不向维护者上报任何数据；只有你配置的模型服务、在线插件和更新检查会联网。
- **源码**：本仓库包含全部自研源码；DSH 与 Node.js 是按 MIT 许可分发的第三方组件。
