using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;

internal sealed class ShellUpdateDialog : Form
{
    private const string Releases = "https://github.com/NeyaCat/dsh-portable-desktop/releases/latest";
    private readonly string current = FileVersionInfo.GetVersionInfo(Application.ExecutablePath).ProductVersion;
    private readonly Label status = new Label { AutoSize = false, Dock = DockStyle.Fill };
    private readonly Button action = new Button { AutoSize = true, Enabled = false, Text = "检查更新" };
    private readonly ProgressBar progress = new ProgressBar { Dock = DockStyle.Fill, Style = ProgressBarStyle.Marquee };
    private readonly WebClient client = new WebClient();
    private string version, url, digest, session;
    private bool working, closeRequested, staged, handedOff;

    internal ShellUpdateDialog(ProxyBridge bridge)
    {
        Text = "桌面软件更新";
        Font = SystemFonts.MessageBoxFont;
        ClientSize = new Size(450, 176);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = MaximizeBox = false;
        if (bridge != null) {
            var policy = bridge.Current;
            var proxy = policy.HttpsProxy != "" ? policy.HttpsProxy : policy.HttpProxy;
            if (proxy != "") client.Proxy = new WebProxy(proxy);
            else if (policy.Mode == "direct") client.Proxy = null;
        }
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 1, RowCount = 4 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 14));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(new Label { AutoSize = true, Text = "DSH Portable Desktop   ·   当前版本 " + current });
        layout.Controls.Add(status);
        layout.Controls.Add(progress);
        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        var close = new Button { Text = "关闭", AutoSize = true };
        close.Click += delegate { Close(); };
        buttons.Controls.Add(close); buttons.Controls.Add(action);
        layout.Controls.Add(buttons); Controls.Add(layout);
        CancelButton = close;
        Shown += async delegate { await Check(); };
        action.Click += async delegate {
            if (working) return;
            try {
                if (staged) {
                    ShellMaintenance.StartHelper(Updates.Root, Path.GetFileName(session), false);
                    handedOff = true;
                    DialogResult = DialogResult.OK;
                } else if (url == null) await Check();
                else if (url == "") Process.Start(new ProcessStartInfo(Releases) { UseShellExecute = true });
                else await Download();
            } catch (Exception error) { status.Text = error.Message; }
        };
        client.DownloadProgressChanged += delegate(object sender, DownloadProgressChangedEventArgs e) {
            if (!working || closeRequested) return;
            progress.Style = e.TotalBytesToReceive > 0 ? ProgressBarStyle.Continuous : ProgressBarStyle.Marquee;
            progress.Value = Math.Max(0, Math.Min(100, e.ProgressPercentage));
            status.Text = "正在下载桌面软件 " + version + "   " + (e.BytesReceived / 1048576.0).ToString("F1") + " MiB" +
                (e.TotalBytesToReceive > 0 ? " / " + (e.TotalBytesToReceive / 1048576.0).ToString("F1") + " MiB" : "");
        };
        FormClosing += delegate(object sender, FormClosingEventArgs e) {
            if (working && session != null) {
                e.Cancel = true; closeRequested = true; client.CancelAsync();
            }
        };
        FormClosed += delegate { client.Dispose(); if (!handedOff) Discard(); };
    }

    private async Task Check()
    {
        working = true; action.Enabled = false; progress.Visible = true;
        status.Text = "正在检查桌面软件的最新正式版…";
        try {
            var result = await Updates.Run("shell-release " + current);
            if (IsDisposed) return;
            version = Updates.Value(result, "version");
            if (!(bool)result["newer"]) {
                status.Text = "当前已是最新版本或更高版本。最新正式版为 " + version + "。";
                action.Text = "重新检查";
            } else {
                url = Updates.Value(result, "url"); digest = Updates.Value(result, "digest");
                status.Text = url == "" ? "发现桌面软件 " + version + "。此发布尚未提供可校验的桌面更新包。" :
                    "发现桌面软件 " + version + "。本次仅更新桌面组件，保留 DSH 版本、工作区和插件。";
                action.Text = url == "" ? "打开发布页" : "下载桌面更新";
            }
        } catch (Exception error) { if (!IsDisposed) { status.Text = "检查失败：" + error.Message; action.Text = "重试"; } }
        finally { working = false; if (!IsDisposed) { progress.Visible = false; action.Enabled = true; } }
    }

    private async Task Download()
    {
        working = true; action.Enabled = false;
        progress.Visible = true; progress.Style = ProgressBarStyle.Marquee;
        status.Text = "正在连接下载服务…";
        try {
            session = Path.Combine(Updates.Root, ".shell-update-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(session);
            string file = Path.Combine(session, "update.zip");
            RuntimeEnvironment.EnableTls();
            await client.DownloadFileTaskAsync(new Uri(url), file);
            status.Text = "正在验证 SHA-256 和桌面软件版本…";
            await Task.Run(() => Unpack(file, Path.Combine(session, "incoming"), digest, version));
            staged = true;
            status.Text = "桌面软件 " + version + " 已就绪。请保存当前工作并关闭 DSH 版本切换窗口，再重启安装。";
            action.Text = "重启并安装";
        } catch (Exception error) {
            status.Text = "下载或校验失败：" + error.Message; action.Text = "重新下载"; Discard();
        } finally {
            working = false; progress.Visible = false; action.Enabled = true;
            if (closeRequested) Close();
        }
    }

    private void Discard()
    {
        if (session == null) return;
        try { if (Directory.Exists(session)) Directory.Delete(session, true); }
        catch (IOException) {} catch (UnauthorizedAccessException) {}
        session = null;
    }

    internal static void Unpack(string file, string destination, string expected, string version)
    {
        using (var stream = File.OpenRead(file))
        using (var sha = SHA256.Create()) {
            var digest = "sha256:" + BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
            if (!String.Equals(expected, digest, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("更新包 SHA-256 不匹配");
            stream.Position = 0;
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Read)) {
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                long length = 0;
                foreach (var entry in zip.Entries) {
                    string name = entry.FullName.Replace('\\','/');
                    if (!Regex.IsMatch(name, @"\A(DeepSeek Harness\.exe|Version Switch\.exe|Microsoft\.Web\.WebView2\.(Core|WinForms)\.dll|WebView2Loader\.dll|desktop/[a-zA-Z0-9_-]+\.cjs|assets/[a-zA-Z0-9_-]+\.(ico|svg))\z") || !names.Add(name))
                        throw new InvalidDataException("无效的桌面更新文件：" + name);
                    length = checked(length + entry.Length);
                    if (length > 128L * 1024 * 1024) throw new InvalidDataException("更新包超过桌面组件大小上限");
                    var target = Path.Combine(destination, name);
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    entry.ExtractToFile(target);
                }
            }
        }
        foreach (var exe in new[] { "DeepSeek Harness.exe", "Version Switch.exe" })
            if (FileVersionInfo.GetVersionInfo(Path.Combine(destination,exe)).ProductVersion != version)
                throw new InvalidDataException("桌面程序版本与发布版本不一致");
    }
}
