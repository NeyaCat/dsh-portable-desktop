# 默认插件集

本目录是便携版「首次启动推荐的插件」的唯一数据来源，采集自 2026-10-22 的当前环境
（DSH 0.2.1-rc.2）。`packaging/build-portable.ps1` 会把它原样打进发布包。

## 内容

| 路径 | 用途 |
| --- | --- |
| `plugins.json` | 插件目录：名称、版本、依赖声明、来源、分类、说明、介绍链接、是否必需、镜像地址 |
| `profile/package.json` | 默认 profile 的 `dsh.profile.bundles` 与依赖声明 |
| `profile/pnpm-lock.yaml` | 随包版本基线，包含 GitHub 插件的固定提交 |
| `profile/pnpm-workspace.yaml` | `nodeLinker: hoisted`，避免 Windows 符号链接 |
| `profile/cordis.patch.yml` | 默认模型、皮肤、权限与插件配置；安装器会移除未选插件的配置项 |
| `profile/local-packages/dsh-network-config-1.0.0.tgz` | 本地来源的 MIT 插件（作者 johnsonpanq-ctrl） |

## 插件列表

| 插件 | 版本 | 来源 | 分类 | 必需 |
| --- | --- | --- | --- | --- |
| `dshmarket` | 1.66.47 | npm | 必需 | 是 |
| `dsh-network-config` | 1.0.0 | 本地 tarball | 必需 | 是 |
| `dsh-http-proxy` | 0.1.3 | npm | 必需 | 是 |
| `dsh-dream-skin` | 10.21.2 | npm | 主题 | 否 |
| `@dsh-external/dsh-memory` | 0.5.0 | GitHub | 辅助 | 否 |
| `@nanmicoder/dsh-agent-teams` | 0.1.26 | npm | 辅助 | 否 |
| `@michengai/dsh-agency-agents` | 1.2.10 | npm | 辅助 | 否 |
| `dsh-notification` | 0.1.2 | npm | 辅助 | 否 |
| `dsh-better-sidebar` | 0.28.0 | npm | 布局 | 否 |
| `dsh-whale-widget` | 0.4.2 | npm | 布局 | 否 |
| `dsh-at-file` | 0.6.3 | npm | 功能 | 否 |
| `dsh-config-manager` | 0.1.79 | npm | 功能 | 否 |
| `dsh-openspec` | 0.1.0 | GitHub | 功能 | 否 |
| `dsh-workspace-acl-allow` | 0.1.1 | GitHub | 功能 | 否 |
| `@xbzbing/dsh-git-panel` | 1.8.2 | npm | 功能 | 否 |

`@deepseek-ai/dsh-base`、`@deepseek-ai/dsh-web-app`、`@deepseek-ai/dsh-experimental-auto-review`
由 DSH 本体提供，只出现在 `bundles` 里，不需要单独安装。

## 首次安装流程

启动器在环境体检通过后打开勾选窗口：

1. 选择安装后，插件市场、HTTP 代理、网络配置三项固定勾选，不能取消；选择「暂不安装」则本次不装任何插件；
2. 其余插件按「主题 / 辅助 / 布局 / 功能」分组，默认全选，可逐项取消；
3. 「详情」打开该插件的 GitHub 或 npm 页面；
4. 安装时先只安装 3 个必需插件，成功后再安装用户勾选的其他插件；
5. 未选插件的 `dependencies`、`dsh.profile.bundles` 和 `cordis.patch.yml` 配置会被移除。

安装器按成功安装的插件更新依赖与 bundle 清单，重试时保留已有配置；GitHub 插件仍使用固定提交。

## 网络与镜像

安装前会检测 `registry.npmjs.org`、`github.com`、`codeload.github.com`。三者都直连可用时
使用官方源；任一不可用则自动切换 `plugins.json` 中的镜像：

- npm：`https://registry.npmmirror.com/`
- GitHub / codeload：`https://gh-proxy.com/`

镜像模式下会同时改写锁文件中的 GitHub 与 npm 地址，并为 git 设置 URL 重写；直连安装失败时
启动器会自动用镜像重试一次。安装器的临时目录与 npm 缓存都放在当前 `DSH_HOME` 内。

## 更新

1. 修改 `profile/package.json`；
2. 在该目录执行 `pnpm install` 重新生成 `pnpm-lock.yaml`；
3. 更新 `plugins.json`（名称、版本、来源、分类、说明和链接），并更新本文表格；
4. 如插件增加/删除配置项，同步更新 `profile/cordis.patch.yml`。

## 首次安装兼容检查

上表是随包基线。首次安装时，安装器会按实际 DSH 版本为 npm 插件选择最新的兼容正式版本，而不是直接使用基线版本。查询失败时保留基线，并校验实际下载文件；版本查询和下载均继承便携环境的代理与镜像配置。

必需组件优先，然后逐个安装可选插件。安装后检查入口文件、宿主声明和 bundle 内部插件；不兼容的可选插件不进入最终依赖与 bundle 清单，原因记在 data/recommended-plugins.json 的 skipped 中。必需组件失败则明确报错。

对于 dsh-http-proxy 0.1.3 和 dsh-at-file 0.6.3，在已支持的 DSH 0.2.x 范围内应用版本限定的设置、客户端插槽与调用接口适配。原始代码保存在插件目录的 .portable-original 中，插件配置保留。不会授予兼容豁免或隐藏市场的真实状态。

例如 DSH 0.2.0-rc.2 会安装侧边栏 0.26.1，而不是要求新宿主的 0.28.0。GitHub 插件仍使用随包固定提交；工作区 ACL 0.1.1 要求 >=0.2.1-alpha.1，因此在旧宿主上跳过。
