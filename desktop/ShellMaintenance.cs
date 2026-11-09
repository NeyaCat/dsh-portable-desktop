using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows.Forms;

internal static class ShellMaintenance
{
    internal const string Journal = ".shell-update-journal";

    // Both windows hold shared access for their complete lifetime. The installer
    // takes exclusive access after the desktop exits, before changing any file.
    internal static FileStream Enter(string root)
    {
        string file = Path.Combine(root, ".shell-update.lock");
        FileStream lease;
        try { lease = new FileStream(file, FileMode.OpenOrCreate, FileAccess.Read, FileShare.Read); }
        catch (IOException error) {
            MessageBox.Show("无法打开桌面软件。请在更新结束后重试。\n" + error.Message, "桌面软件更新");
            return null;
        }
        catch (UnauthorizedAccessException error) {
            MessageBox.Show("请将便携包放到可写目录。\n" + error.Message, "桌面软件");
            return null;
        }
        if (!File.Exists(Path.Combine(root, Journal))) return lease;
        try { StartHelper(root, "", true); }
        finally { lease.Dispose(); }
        return null;
    }

    internal static void StartHelper(string root, string session, bool recover)
    {
        string script;
        using (var reader = new StreamReader(typeof(ShellMaintenance).Assembly.GetManifestResourceStream("Apply-ShellUpdate.ps1")))
            script = reader.ReadToEnd();
        string powershell = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
        var info = new ProcessStartInfo(powershell, "-NoProfile -NonInteractive -STA -EncodedCommand " +
            Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
        info.UseShellExecute = false;
        info.CreateNoWindow = true;
        info.WorkingDirectory = root;
        info.EnvironmentVariables["DSH_SHELL_SESSION"] = session;
        info.EnvironmentVariables["DSH_SHELL_RECOVER"] = recover ? "1" : "0";
        using (var current = Process.GetCurrentProcess()) info.EnvironmentVariables["DSH_SHELL_PARENT"] = current.Id.ToString();
        using (Process.Start(info)) {}
    }
}
