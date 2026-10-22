# Privacy policy

DSH Portable Desktop (unofficial) does not collect telemetry, analytics or crash
reports, and sends no usage data to the maintainer.

## What is stored locally

All state stays inside the application folder and disappears when you delete the
folder:

| Path | Content |
| --- | --- |
| `data/` | DSH home: configuration, sessions, credentials you enter, plugins |
| `data/desktop.log` | window and lifecycle log (login URLs are redacted) |
| `data/desktop-preferences.json` | window preferences and the last close actions |
| `data/desktop-webview/` | Microsoft WebView2 browser profile |
| `workspace/` | optional working folder |
| `versions/` | DSH versions downloaded by the version manager |

## Network access

The application contacts the network only for:

1. **Update checks.** On launch it asks `https://registry.npmjs.org` for the
   published `@deepseek-ai/dsh` versions, and downloads a version when you accept
   an update or use `Version Switch.exe`. The request carries no account data.
2. **Your own services.** Model providers, online plugins and other services you
   configure are contacted directly by DSH with your credentials.
3. **Links you open.** External links are passed to your default browser.

## Third-party components

- DeepSeek Harness — MIT — <https://github.com/deepseek-ai/deepseek-harness>
- Node.js — MIT — <https://nodejs.org>
- Microsoft Edge WebView2 — Microsoft — <https://learn.microsoft.com/microsoft-edge/webview2/>

Their own terms and privacy policies apply to their network requests.

## Contact

Open an issue: <https://github.com/NeyaCat/dsh-portable-desktop/issues>
