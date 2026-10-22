using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

// First-run environment gate. Node.js, DSH, Git, pnpm and (optionally) a fixed
// WebView2 runtime ship next to the EXE. Only Windows and the .NET Framework
// cannot be bundled; everything else is detected, repaired or installed before
// the main window is created.
internal static class EnvironmentCheck
{
    private const int NetFramework462Release = 394254;

    internal static bool EnsureReady()
    {
        if (IntPtr.Size != 8)
        {
            Warn("需要 64 位 Windows 10/11。\r\n当前系统不是 64 位，无法运行本程序。");
            return false;
        }
        if (RuntimeEnvironment.WindowsMajorVersion < 10)
        {
            Warn("需要 Windows 10 或更高版本。");
            return false;
        }
        string error;
        if (!DataWritable(out error))
        {
            Warn("数据目录不可写：" + error +
                "\r\n请把便携包放到可写目录（例如用户目录），不要放在 Program Files 下。");
            return false;
        }
        if (!RuntimeEnvironment.NodeAvailable || !RuntimeEnvironment.DshAvailable)
        {
            Warn("安装包不完整：缺少内置的 Node.js 或 DSH 运行时。\r\n" +
                "请重新解压完整的便携包，不要只复制 EXE 文件。");
            return false;
        }
        if (!NetFrameworkReady())
        {
            Warn("需要 .NET Framework 4.6.2 或更高版本。\r\n\r\n" +
                "Windows 10 1607 及更高版本、Windows 11 已内置；\r\n" +
                "较旧系统请从微软官网安装后重新启动：\r\n" +
                "https://dotnet.microsoft.com/download/dotnet-framework/net462");
            return false;
        }

        bool fullCheck = !RuntimeEnvironment.FullCheckCompleted();
        string nodeVersion = "";
        string dshVersion = "";
        if (!RuntimeEnvironment.TryNodeVersion(out nodeVersion))
        {
            DialogResult answer = MessageBox.Show(
                "内置的 Node.js 无法启动，通常是因为缺少 Microsoft Visual C++ 运行库。\r\n\r\n" +
                "是否现在自动下载并安装（安装程序会请求管理员权限）？",
                "DSH Desktop", MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button1);
            bool installed = false;
            if (answer == DialogResult.Yes)
            {
                using (EnvironmentProgress progress = new EnvironmentProgress("正在修复运行环境…"))
                {
                    progress.Show();
                    Application.DoEvents();
                    installed = RuntimeEnvironment.InstallVcRedist(progress.SetText);
                }
            }
            if (!installed || !RuntimeEnvironment.TryNodeVersion(out nodeVersion))
            {
                Warn("Node.js 仍无法启动。\r\n\r\n" +
                    "请手动安装 Microsoft Visual C++ 2015-2022 运行库（x64）后重试：\r\n" +
                    "https://aka.ms/vs/17/release/vc_redist.x64.exe");
                return false;
            }
        }
        if (fullCheck && !RuntimeEnvironment.TryDshVersion(out dshVersion))
        {
            Warn("内置的 DSH 运行时无法启动。\r\n\r\n" +
                "安装包可能不完整（缺少 node_modules 或文件被安全软件删除）。\r\n" +
                "请重新解压完整的便携包后再启动。");
            return false;
        }

        string gitVersionText;
        bool git = RuntimeEnvironment.TryGitVersion(out gitVersionText);
        bool pnpm = RuntimeEnvironment.PnpmAvailable;
        List<string> missing = new List<string>();
        if (!git) missing.Add("• Git：安装 GitHub 上的插件时需要（约 40 MB）");
        if (!pnpm) missing.Add("• pnpm 工具链：插件市场安装依赖时需要（约 20 MB）");
        bool attempted = false;
        bool repaired = true;
        bool declinedOptional = false;
        if (missing.Count > 0)
        {
            if (RuntimeEnvironment.OptionalRepairDeclined())
            {
                declinedOptional = true;
                RuntimeEnvironment.Log("Environment: optional components missing and repair was declined earlier");
            }
            else
            {
                string message = "检测到以下可选组件缺失：\r\n\r\n" +
                    String.Join("\r\n", missing.ToArray()) +
                    "\r\n\r\n是否现在自动下载并安装到便携文件夹？\r\n" +
                    "选择“否”后不再自动询问；仍可运行 DeepSeek Harness.exe --repair-environment 手动修复。";
                if (MessageBox.Show(message, "DSH 首次启动", MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question, MessageBoxDefaultButton.Button1) == DialogResult.Yes)
                {
                    attempted = true;
                    using (EnvironmentProgress progress = new EnvironmentProgress("正在修复便携运行环境…"))
                    {
                        progress.Show();
                        Application.DoEvents();
                        if (!git) git = RuntimeEnvironment.EnsureBundledGit(progress.SetText);
                        if (!pnpm) pnpm = RuntimeEnvironment.EnsurePnpm(progress.SetText);
                    }
                    repaired = git && pnpm;
                }
                else
                {
                    declinedOptional = true;
                }
            }
        }

        if (!RuntimeEnvironment.WebView2Available)
        {
            DialogResult answer = MessageBox.Show(
                "缺少 Microsoft Edge WebView2 Runtime，DSH 无法显示界面。\r\n\r\n" +
                "是否现在自动下载并安装（安装程序会请求管理员权限）？",
                "DSH Desktop", MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button1);
            bool installed = false;
            if (answer == DialogResult.Yes)
            {
                using (EnvironmentProgress progress = new EnvironmentProgress("正在安装 WebView2 Runtime…"))
                {
                    progress.Show();
                    Application.DoEvents();
                    installed = RuntimeEnvironment.InstallWebView2(progress.SetText);
                }
            }
            if (!installed)
            {
                try { Process.Start(new ProcessStartInfo(RuntimeEnvironment.WebView2Page) { UseShellExecute = true }); }
                catch (Exception) { }
                Warn("WebView2 Runtime 自动安装没有完成。\r\n\r\n" +
                    "请手动安装后再启动：\r\n" + RuntimeEnvironment.WebView2Page);
                return false;
            }
        }
        else
        {
            RuntimeEnvironment.GrantFixedWebView2Access();
        }

        if (attempted && !repaired)
            MessageBox.Show(
                "部分可选组件未能自动安装。DSH 仍可启动，但插件安装可能失败。\r\n" +
                "请检查网络或代理后重启重试，也可以在 data\\desktop.log 查看原因。",
                "DSH Desktop", MessageBoxButtons.OK, MessageBoxIcon.Warning);

        long free = RuntimeEnvironment.FreeDiskBytes();
        if (fullCheck && free > 0 && free < 1024L * 1024 * 1024)
            MessageBox.Show(
                "当前磁盘剩余空间不足 1 GB。\r\n" +
                "DSH 会话、更新和插件需要额外空间，建议先清理磁盘。",
                "DSH Desktop", MessageBoxButtons.OK, MessageBoxIcon.Warning);

        RuntimeEnvironment.WriteReport(true, true, nodeVersion, git, pnpm, RuntimeEnvironment.WebView2Mode, declinedOptional);
        RuntimeEnvironment.Log("Environment: ready (os=" + RuntimeEnvironment.WindowsVersionText +
            ", webview2=" + RuntimeEnvironment.WebView2Mode +
            ", git=" + git + ", pnpm=" + pnpm + ", fullCheck=" + fullCheck + ")");
        return true;
    }

    // Headless validation used by packaging and tests. Optional components are
    // included: a complete portable package must contain Git and pnpm as well.
    internal static int Verify()
    {
        string nodeVersion;
        bool node = RuntimeEnvironment.TryNodeVersion(out nodeVersion);
        string dshVersion;
        bool dsh = RuntimeEnvironment.TryDshVersion(out dshVersion);
        string gitVersionText;
        bool git = RuntimeEnvironment.TryGitVersion(out gitVersionText);
        bool pnpm = RuntimeEnvironment.PnpmAvailable;
        bool web = RuntimeEnvironment.WebView2Available;
        bool netfx = NetFrameworkReady();
        bool windows = RuntimeEnvironment.WindowsMajorVersion >= 10 && IntPtr.Size == 8;
        string writableError;
        bool writable = DataWritable(out writableError);
        bool ok = windows && node && dsh && git && pnpm && web && netfx && writable;

        StringBuilder text = new StringBuilder();
        text.AppendLine("DSH portable environment check");
        text.AppendLine("root: " + RuntimeEnvironment.Root);
        text.AppendLine("os: " + (windows ? "PASS " + RuntimeEnvironment.WindowsVersionText + " x64" : "FAIL " + RuntimeEnvironment.WindowsVersionText));
        text.AppendLine("node: " + (node ? "PASS " + nodeVersion : "FAIL"));
        text.AppendLine("dsh: " + (dsh ? "PASS " + dshVersion : "FAIL"));
        text.AppendLine("git: " + (git ? "PASS " + RuntimeEnvironment.GitVersionReported : "FAIL"));
        text.AppendLine("pnpm: " + (pnpm ? "PASS" : "FAIL"));
        text.AppendLine("webview2: " + (web ? "PASS " + RuntimeEnvironment.WebView2Mode + " " + RuntimeEnvironment.WebView2Version : "FAIL"));
        text.AppendLine("netfx: " + (netfx ? "PASS" : "FAIL"));
        text.AppendLine("data: " + (writable ? "PASS" : "FAIL"));
        text.AppendLine("freeBytes: " + RuntimeEnvironment.FreeDiskBytes());
        text.AppendLine(ok ? "RESULT: PASS" : "RESULT: FAIL");
        try
        {
            Directory.CreateDirectory(RuntimeEnvironment.DataRoot);
            File.WriteAllText(Path.Combine(RuntimeEnvironment.DataRoot, "environment-check.txt"),
                text.ToString(), new UTF8Encoding(false));
        }
        catch (Exception) { }
        RuntimeEnvironment.WriteReport(ok, true, nodeVersion, git, pnpm, RuntimeEnvironment.WebView2Mode);
        return ok ? 0 : 1;
    }

    // Non-interactive repair used for testing and for the documented recovery
    // path. It never installs machine-wide components.
    internal static int RepairBundled()
    {
        string writableError;
        if (DataWritable(out writableError))
        {
            RuntimeEnvironment.EnsureBundledGit(null);
            RuntimeEnvironment.EnsurePnpm(null);
        }
        return Verify();
    }

    private static bool DataWritable(out string error)
    {
        error = "";
        try
        {
            Directory.CreateDirectory(RuntimeEnvironment.DataRoot);
            string probe = Path.Combine(RuntimeEnvironment.DataRoot, ".write-test-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return true;
        }
        catch (Exception exception)
        {
            error = exception.Message;
            return false;
        }
    }

    private static bool NetFrameworkReady()
    {
        try
        {
            using (RegistryKey root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            using (RegistryKey key = root.OpenSubKey(@"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full"))
            {
                if (key == null) return true;
                object release = key.GetValue("Release");
                if (release == null) return true;
                return Convert.ToInt32(release) >= NetFramework462Release;
            }
        }
        catch (Exception) { return true; }
    }

    private static void Warn(string message)
    {
        MessageBox.Show(message, "DSH Desktop", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }
}

internal sealed class EnvironmentProgress : Form
{
    private readonly Label label = new Label();
    private readonly ProgressBar bar = new ProgressBar();

    internal EnvironmentProgress(string title)
    {
        Text = title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        ControlBox = false;
        ShowInTaskbar = false;
        TopMost = true;
        ClientSize = new Size(480, 130);
        label.AutoSize = false;
        label.AutoEllipsis = true;
        label.SetBounds(20, 22, 440, 48);
        label.Text = title;
        bar.SetBounds(20, 78, 440, 18);
        bar.Style = ProgressBarStyle.Marquee;
        bar.MarqueeAnimationSpeed = 30;
        Controls.Add(label);
        Controls.Add(bar);
    }

    internal void SetText(string text)
    {
        if (IsDisposed) return;
        label.Text = text;
        label.Update();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) bar.MarqueeAnimationSpeed = 0;
        base.Dispose(disposing);
    }
}
