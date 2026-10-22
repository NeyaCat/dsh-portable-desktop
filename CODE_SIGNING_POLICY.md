# Code signing policy

**Free code signing provided by [SignPath.io](https://about.signpath.io), certificate by [SignPath Foundation](https://signpath.org).**

- Project: DSH Portable Desktop (unofficial)
- Repository: <https://github.com/NeyaCat/dsh-portable-desktop>
- Signing service: SignPath.io, through SignPath Foundation

## What is signed

Only artifacts built from this repository by GitHub Actions are submitted for
signing. A release ZIP contains two executables compiled from this source tree:

| File | Source |
| --- | --- |
| `DeepSeek Harness.exe` | `desktop/Desktop.cs`, `DesktopPreferences.cs`, `LoadingSurface.cs`, `SkinTheme.cs`, `Updates.cs` |
| `Version Switch.exe` | `desktop/VersionSwitch.cs`, `Updates.cs`, `SkinTheme.cs` |

The bundled Node.js runtime and `@deepseek-ai/dsh` package are upstream
open-source components. They are included in the package but are **not** signed
by this project.

## Build and origin

- Builds run on GitHub-hosted runners from a commit on `main`.
- The signing request is submitted through the SignPath connector with origin
  verification, so every signed file is traceable to this repository, branch and
  commit.
- Every signing request is reviewed and manually approved by a maintainer before
  the certificate is applied.

## Roles

This is a single-maintainer project.

| Role | Member |
| --- | --- |
| Author / committer | [@NeyaCat](https://github.com/NeyaCat) |
| Reviewer | [@NeyaCat](https://github.com/NeyaCat) |
| Approver | [@NeyaCat](https://github.com/NeyaCat) |

Changes are committed by the maintainer, and the maintainer re-reads the diff
against the release notes before approving a signing request.

## Privacy

This program will not transfer any information to other networked systems unless
specifically requested by the user or the person installing or operating it. See
[PRIVACY.md](PRIVACY.md).

## Reports and revocation

If a file signed for this project violates this policy, report it to
<support@signpath.io> and open an issue in this repository.
