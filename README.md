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
| 📦 | **便携** | 配置、会话、日志与 WebView2 数据都在 EXE 旁的 `data/`；整个文件夹搬走就是搬家 |
| 🔒 | **仅本机** | 界面只监听 `127.0.0.1` 的随机空闲端口；外部链接交给默认浏览器 |

## 🚀 快速开始

1. 从 [Releases](https://github.com/NeyaCat/dsh-portable-desktop/releases) 下载最新的 ZIP；
2. 解压到**可写**目录（不要放 `Program Files`）；
3. 双击 `DeepSeek Harness.exe`，运行数据会生成在 `./data`。

**运行要求**：Windows 10/11 x64、.NET Framework 4.6.2 或更高、Microsoft Edge WebView2 Runtime（Windows 11 和较新的 Windows 10 已内置）。

**卸载**：退出程序后直接删除整个文件夹即可 —— 不写注册表，不在系统目录留东西。

## 🧱 从源码构建

外壳是纯 C#，目标 .NET Framework 4.6.2，用 Windows 自带的编译器构建，**不需要 Visual Studio 或 SDK**。

```powershell
powershell -ExecutionPolicy Bypass -File .\desktop\Build.ps1
```

`desktop\Build.ps1` 会把 `desktop\*.cs` 编译成根目录的 `DeepSeek Harness.exe` 与 `Version Switch.exe`，把 WebView2 SDK 的 DLL 和 loader 复制到旁边，并把 `DesktopChrome.js`、`VersionSwitch.html` 作为程序集资源嵌入。

便携运行时（Node.js + `@deepseek-ai/dsh`）不随仓库提供。完整包（运行时 + 已编译外壳 + ZIP）由 `packaging/build-portable.ps1` 产出，发布流水线在 GitHub 托管的 Windows runner 上执行；Node.js 与 DSH 均从官方来源获取，并在上游提供校验和时进行校验。

```text
desktop/                 外壳与版本管理的 C# 源码
desktop/Build.ps1        用 Windows .NET 编译器构建两个可执行文件
desktop/vendor/webview2  Microsoft WebView2 SDK 1.0.2903.40 及其许可证
desktop/Test-*.ps1       手动集成测试（运行前请先关闭程序）
packaging/               从源码构建便携包与 ZIP
.github/workflows/       发布流水线
DeepSeek Harness.ico     传给编译器的程序图标
```

## 🧪 自测

```powershell
powershell -ExecutionPolicy Bypass -File .\desktop\Test-ProxyBridge.ps1   # 代理桥，无需关闭程序
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
