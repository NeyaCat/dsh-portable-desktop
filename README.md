<div align="center">

# 🐋 DSH Portable Desktop

**非官方**的 [DeepSeek Harness](https://github.com/deepseek-ai/deepseek-harness) Windows x64 桌面外壳

把本地 DSH Web 界面装进独立窗口，附带托盘、皮肤标题栏、版本管理与插件安装代理桥。

[![License](https://img.shields.io/badge/License-MIT-2563eb?style=flat-square)](LICENSE)
![Platform](https://img.shields.io/badge/Platform-Windows%20x64-0078d4?style=flat-square)
![.NET](https://img.shields.io/badge/.NET%20Framework-4.6.2-512bd4?style=flat-square)
![WebView2](https://img.shields.io/badge/WebView2-Runtime-2ea44f?style=flat-square)

</div>

---

> [!WARNING]
> **本项目为非官方项目。** 与 DeepSeek 无隶属、无背书、无维护关系。「DeepSeek」「DeepSeek Harness」为各自权利人的商标。随包分发的 DSH 运行时与 Node.js 是遵循 MIT 许可的第三方开源组件。

DSH Portable Desktop 是一个自带运行环境的 Windows x64 桌面外壳：把 DSH 的本地 Web 界面托管在原生 WebView2 窗口里，并补上托盘行为、关闭确认弹窗、跟随皮肤的窗口标题栏和内置版本管理。DSH 运行时与 Node.js 在打包发布版时从官方上游获取，本仓库只包含外壳自身的源码。

## ✨ 功能

| | 能力 | 说明 |
|---|---|---|
| 🪟 | **原生窗口** | DSH 界面在自己的窗口里打开，没有浏览器标签页，也没有黑框控制台 |
| 🔂 | **单实例** | 再次双击 EXE 会还原并聚焦已在运行的窗口，不会开第二份 |
| 📥 | **关闭到托盘** | 点 `×` 时可选「退出」或「最小化到托盘」，选择可记住 |
| 🧭 | **托盘菜单** | 打开窗口、新建会话、打开版本管理、退出 |
| 🎨 | **皮肤感知标题栏** | 28 px 页内标题栏跟随当前 DSH 皮肤，按对比度自动选深浅图标，保留 Windows 11 圆角与窗口动画 |
| 🔀 | **版本管理** | `Version Switch.exe` 列出并安装官方 DSH 发行版，运行中的版本永不被覆盖；更新在应用内提示，下次启动生效 |
| 🌐 | **安装代理桥** | 读取 `dsh-network-config` 的网络模式，把代理同时交给后端进程与每个 profile 的 `.npmrc`，并让 git 的 GitHub SSH 地址改写为 HTTPS；`direct` 模式会清除这些设置 |
| 🧹 | **残留锁自愈** | 启动时清理被中断的安装留下的 `.plugin-manager/run.json` 与 `package.json.lock`（PID 已退出且记录超过 2 分钟才动） |
| 🩺 | **首次启动体检** | 检查系统版本、数据目录、Node.js、DSH、Git、pnpm 与 WebView2；可自动下载便携 Git/pnpm，WebView2 缺失时可自动安装或引导安装 |
| 🧩 | **推荐插件选择** | 首次启动按「主题 / 辅助 / 布局 / 功能」分组展示 15 个插件，可勾选安装并打开介绍页；插件市场、HTTP 代理、网络配置必须安装成功，才能进入软件 |
| 📦 | **便携** | 配置、会话、日志、WebView2 数据、Node.js、DSH、Git、pnpm 都在 EXE 旁；发布包可选内置 WebView2 固定版本，去掉对系统运行时的依赖 |
| 🔒 | **仅本机** | 界面只监听 `127.0.0.1` 的随机空闲端口；外部链接交给默认浏览器 |

## 🚀 快速开始

1. 从 [Releases](https://github.com/NeyaCat/dsh-portable-desktop/releases) 下载最新的 ZIP；
2. 解压到**可写**目录（不要放 `Program Files`）；
3. 双击 `DeepSeek Harness.exe`，运行数据会生成在 `./data`。

**运行要求**：Windows 10/11 x64、.NET Framework 4.6.2 或更高。Node.js、DSH、Git、pnpm 随包提供；WebView2 优先使用包内 `webview2/` 固定版本，未内置时使用系统 Evergreen Runtime，缺失会询问是否自动安装。

首次启动会执行一次环境体检，并把结果写入 `data/environment-check.json`。Git/pnpm 缺失时会询问是否自动补全；选择“否”后不再自动询问，可随时运行 `DeepSeek Harness.exe --repair-environment` 修复，或用 `--verify-environment` 生成 `data/environment-check.txt` 检查报告。

首次启动会查询官方 latest 推荐 DSH 版本；新包默认内置打包时的最新推荐版，若已有更新则先验证并准备新版，再安装插件。已有版本选择和用户环境保持原样。

体检通过后会打开推荐插件选择窗口：插件市场、HTTP 代理、网络配置三项必装，窗口不能关闭、不能用 Esc 跳过；其余插件默认勾选、可逐项取消，也可选择「仅安装必需组件」。三个组件校验成功后才进入软件，失败会提示重试。已有 profile 缺少组件时，只补装必需组件并保留原插件与配置。直连 npm/GitHub 不可用时自动使用镜像。

**卸载**：退出程序后直接删除整个文件夹即可 —— 不写注册表，不在系统目录留东西。

## 🧱 从源码构建

外壳是纯 C#，目标 .NET Framework 4.6.2，用 Windows 自带的编译器构建，**不需要 Visual Studio 或 SDK**。

```powershell
powershell -ExecutionPolicy Bypass -File .\desktop\Build.ps1
```

`desktop\Build.ps1` 会把 `desktop\*.cs` 编译成根目录的 `DeepSeek Harness.exe` 与 `Version Switch.exe`，把 WebView2 SDK 的 DLL 和 loader 复制到旁边，并把 `DesktopChrome.js`、`VersionSwitch.html` 作为程序集资源嵌入。

便携运行时（Node.js + `@deepseek-ai/dsh`）不随仓库提供。完整包（运行时 + Git + pnpm + 已编译外壳 + ZIP）由 `packaging/build-portable.ps1` 产出，发布流水线在 GitHub 托管的 Windows runner 上执行；Node.js、DSH 与 MinGit 均从官方来源获取，并在上游提供校验和时进行校验。

```powershell
# 官方发布包：额外内置 WebView2 固定版本，完全不依赖系统 WebView2（约增加 670 MB）
powershell -ExecutionPolicy Bypass -File .\packaging\build-portable.ps1 -BundleWebView2
```

```text
desktop/                 外壳与版本管理的 C# 源码
desktop/Build.ps1        用 Windows .NET 编译器构建两个可执行文件
desktop/vendor/webview2  Microsoft WebView2 SDK 1.0.2903.40 及其许可证
desktop/Test-*.ps1       手动集成测试（运行前请先关闭程序）
packaging/               从源码构建便携包与 ZIP
.github/workflows/       发布流水线
assets/dsh-app.ico       主程序鲸鱼图标（编译时嵌入）
assets/version-switch.ico  版本切换工具的独立图标（编译时嵌入）
assets/version-switch.svg  版本切换图标的矢量资源
```

## 🧪 自测

```powershell
powershell -ExecutionPolicy Bypass -File .\desktop\Test-ProxyBridge.ps1   # 代理桥，无需关闭程序
.\DeepSeek Harness.exe --verify-environment                                # 环境体检，结果写入 data\environment-check.txt
.\DeepSeek Harness.exe --repair-environment                                # 补全便携 Git/pnpm
.\DeepSeek Harness.exe --preview-plugin-picker                             # 预览首次启动插件选择窗口
powershell -ExecutionPolicy Bypass -File .\desktop\Test-Desktop.ps1       # 窗口与进程生命周期，需先关闭程序
```

## 🔐 代码签名策略

免费代码签名由 [SignPath.io](https://about.signpath.io) 提供，证书由 [SignPath Foundation](https://signpath.org) 签发。

- 作者 / 提交者：[@NeyaCat](https://github.com/NeyaCat)
- 审核者：[@NeyaCat](https://github.com/NeyaCat)
- 批准者：[@NeyaCat](https://github.com/NeyaCat)

发布版本由 GitHub Actions 从本仓库构建，每一次签名请求都需人工批准。详见 [CODE_SIGNING_POLICY.md](CODE_SIGNING_POLICY.md)。

## 🛡️ 隐私

本程序不向维护者上报遥测或使用数据。只有在你自己配置了模型服务、使用联网功能或检查 DSH 更新时，它才会访问其他网络服务。详见 [PRIVACY.md](PRIVACY.md)。

## 📜 许可证

MIT —— 见 [LICENSE](LICENSE)。随包分发的第三方组件各自保留其许可。

### 标准版、完整版与手写更新日志

默认构建标准版（使用系统 WebView2，缺失时首次启动可安装）：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\packaging\build-portable.ps1 -Version 0.2.0 -Flavor standard -ReleaseNotesFile .\release-notes\0.2.0.md
```

完整版使用 `-Flavor full`，内置 WebView2。原有 `-BundleWebView2` 参数仍兼容。
输出文件名分别包含 `standard` 或 `full`，两种包可以并存。包内包含 `CHANGELOG.md`（指定日志时）和 `build-info.json`。
每次构建使用新目录；已有输出不会覆盖。

GitHub Actions 会构建两个版本。将手写 Markdown 日志保存为 `release-notes/版本号.md`，
与该版本代码一起提交后推送对应 `v版本号` 标签。该文件将作为 Release 正文并放入两个包；未提供时自动生成提交日志。
本地便携发布工具独立存放于 Git 忽略目录，不随仓库发布。

### 安装进度、退出清理与版本清单恢复点

推荐插件安装窗口提供节点日志、单插件进度和总体进度。未知下载大小时显示活动进度，不伪造百分比。启动期间识别到不兼容的推荐插件，会暂停该插件并保留配置，有限重试；详情记录在 `data/desktop.log`。

正常退出后，独立清理进程回收 npm/pnpm 下载缓存、临时文件、浏览器磁盘缓存和已完成事务的临时代码；保留会话、配置、凭据、浏览器登录状态、安装好的插件和待切换制品。清理统计写入 `data/cache-cleanup.json`。退出到托盘不会触发清理。

新恢复点只记录 DSH 版本、插件版本和启用状态，不复制完整环境。切换时检查兼容性与更新，复用相同版本的插件，获取所需替换版本；保留当前业务数据和插件配置。只有插件代码会临时保留一份，以支持启动失败恢复，健康确认后释放。下载使用便携环境中的代理或镜像。旧格式恢复点及旧环境文件保留，用于兼容既有待切换事务，不会自动删除。
