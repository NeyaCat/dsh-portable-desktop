# 默认插件集

本目录是便携版「首次启动即带上的插件」的唯一数据来源，采集自 2026-10-22 的当前环境
（DSH 0.2.1-rc.2）。`packaging/build-portable.ps1` 会把它原样打进发布包。

## 内容

| 路径 | 用途 |
| --- | --- |
| `plugins.json` | 清单：插件名、已安装版本、依赖声明、来源 |
| `profile/package.json` | 默认 profile 的 `dsh.profile.bundles` 与依赖声明 |
| `profile/pnpm-lock.yaml` | 锁定到上面列出的版本 |
| `profile/pnpm-workspace.yaml` | `nodeLinker: hoisted`，避免 Windows 符号链接 |
| `profile/cordis.patch.yml` | 默认模型、皮肤、权限与插件配置 |
| `profile/local-packages/dsh-network-config-1.0.0.tgz` | 本地来源的 MIT 插件（作者 johnsonpanq-ctrl） |

## 插件列表

| 插件 | 版本 | 来源 |
| --- | --- | --- |
| `@dsh-external/dsh-memory` | 0.5.0 | GitHub |
| `@michengai/dsh-agency-agents` | 1.2.10 | npm |
| `@nanmicoder/dsh-agent-teams` | 0.1.26 | npm |
| `@xbzbing/dsh-git-panel` | 1.8.2 | npm |
| `dsh-at-file` | 0.6.3 | npm |
| `dsh-better-sidebar` | 0.28.0 | npm |
| `dsh-config-manager` | 0.1.79 | npm |
| `dsh-dream-skin` | 10.21.2 | npm |
| `dsh-http-proxy` | 0.1.3 | npm |
| `dsh-network-config` | 1.0.0 | 本地 tarball |
| `dsh-notification` | 0.1.2 | npm |
| `dsh-openspec` | 0.1.0 | GitHub |
| `dsh-whale-widget` | 0.4.2 | npm |
| `dsh-workspace-acl-allow` | 0.1.1 | GitHub |
| `dshmarket` | 1.66.47 | npm |

`@deepseek-ai/dsh-base`、`@deepseek-ai/dsh-web-app`、`@deepseek-ai/dsh-experimental-auto-review`
由 DSH 本体提供，只出现在 `bundles` 里，不需要单独安装。

## 安装方式

首次启动时（`DSH_HOME/profiles/web/node_modules` 不存在）：

1. 把 `profile/` 复制到 `DSH_HOME/profiles/web`；
2. 在 profile 目录执行
   `node\node.exe desktop\plugin-tools\node_modules\pnpm\bin\pnpm.cjs install --prod --frozen-lockfile`；
3. 安装完成后继续启动后端。

首次安装约下载 200 MB、落盘约 1.8 GB，需要联网；之后的更新由插件市场负责。

> 状态：安装钩子尚未接入启动器（`desktop/Desktop.cs`）。本目录已随构建进入发布包，
> 接入后即可在全新安装中生效。

## 更新

1. 修改 `profile/package.json`；
2. 在该目录执行 `pnpm install` 重新生成 `pnpm-lock.yaml`；
3. 重新生成 `plugins.json`（名称、版本、来源），并更新本文表格。
