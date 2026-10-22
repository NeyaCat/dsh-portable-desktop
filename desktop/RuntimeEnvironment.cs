using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;

// Shared by the desktop shell and Version Switch. Everything the app can own
// lives next to the EXE; only Windows itself and the WebView2 runtime can fall
// back to machine-wide installations.
internal static class RuntimeEnvironment
{
    internal static readonly string Root = AppDomain.CurrentDomain.BaseDirectory;

    internal const string NodeVersion = "v24.19.0";
    internal const string GitVersion = "2.56.0.2";
    private const string GitSha256 = "DA35E72AA21C005A5A0D298CFBAE110BC1609A815730EA0DDE84B01A1B3CD3BE";
    private const string GitUrl = "https://github.com/git-for-windows/git/releases/download/v2.56.0.windows.2/MinGit-2.56.0.2-64-bit.zip";
    private const string VcRedistUrl = "https://aka.ms/vs/17/release/vc_redist.x64.exe";
    private const string WebView2Client = @"{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}";
    private const string WebView2Bootstrapper = "https://go.microsoft.com/fwlink/p/?LinkId=2124703";
    internal const string WebView2Page = "https://developer.microsoft.com/microsoft-edge/webview2/";

    // Environment.OSVersion is compatibility-shimmed to 6.2 for executables
    // without a Windows 10 manifest. RtlGetVersion returns the real build.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OsVersionInfo
    {
        public uint Size;
        public uint Major;
        public uint Minor;
        public uint Build;
        public uint Platform;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string ServicePack;
        public ushort ServicePackMajor;
        public ushort ServicePackMinor;
        public ushort SuiteMask;
        public byte ProductType;
        public byte Reserved;
    }
    [DllImport("ntdll.dll", CharSet = CharSet.Unicode)]
    private static extern int RtlGetVersion(ref OsVersionInfo info);

    internal static int WindowsMajorVersion
    {
        get
        {
            try
            {
                OsVersionInfo info = new OsVersionInfo();
                info.Size = (uint)Marshal.SizeOf(typeof(OsVersionInfo));
                if (RtlGetVersion(ref info) == 0) return (int)info.Major;
            }
            catch (Exception) { }
            return Environment.OSVersion.Version.Major;
        }
    }

    internal static string WindowsVersionText
    {
        get
        {
            try
            {
                OsVersionInfo info = new OsVersionInfo();
                info.Size = (uint)Marshal.SizeOf(typeof(OsVersionInfo));
                if (RtlGetVersion(ref info) == 0)
                    return info.Major + "." + info.Minor + "." + info.Build;
            }
            catch (Exception) { }
            return Environment.OSVersion.Version.ToString();
        }
    }

    internal static string DataRoot { get { return Path.Combine(Root, "data"); } }
    internal static string NodeExe { get { return Path.Combine(Root, "node", "node.exe"); } }
    internal static string NpmCli { get { return Path.Combine(Root, "node", "node_modules", "npm", "bin", "npm-cli.js"); } }
    internal static string DshEntry { get { return Path.Combine(Root, "node_modules", "@deepseek-ai", "dsh", "lib", "bin.js"); } }
    internal static string GitExe { get { return Path.Combine(Root, "git", "cmd", "git.exe"); } }
    internal static string PnpmCmd { get { return Path.Combine(Root, "desktop", "plugin-tools", "node_modules", ".bin", "pnpm.cmd"); } }
    internal static string PluginTools { get { return Path.Combine(Root, "desktop", "plugin-tools"); } }
    internal static string ReportPath { get { return Path.Combine(DataRoot, "environment-check.json"); } }


    internal static bool NodeAvailable { get { return File.Exists(NodeExe); } }
    internal static bool DshAvailable { get { return File.Exists(DshEntry); } }
    internal static bool GitAvailable { get { return File.Exists(GitExe); } }
    internal static bool PnpmAvailable { get { return File.Exists(PnpmCmd); } }

    internal static bool FullCheckCompleted()
    {
        try
        {
            if (!File.Exists(ReportPath)) return false;
            string text = File.ReadAllText(ReportPath, Encoding.UTF8);
            return ReportFlag(text, "ready") && ReportFlag(text, "fullCheck");
        }
        catch (Exception) { return false; }
    }

    internal static bool OptionalRepairDeclined()
    {
        try
        {
            if (!File.Exists(ReportPath)) return false;
            return ReportFlag(File.ReadAllText(ReportPath, Encoding.UTF8), "optionalDeclined");
        }
        catch (Exception) { return false; }
    }

    private static bool ReportFlag(string text, string name)
    {
        string key = "\"" + name + "\":";
        int index = text.IndexOf(key, StringComparison.Ordinal);
        if (index < 0) return false;
        index += key.Length;
        while (index < text.Length && (text[index] == ' ' || text[index] == '\t')) index++;
        return index + 4 <= text.Length && String.CompareOrdinal(text, index, "true", 0, 4) == 0;
    }

    // A local fixed-version runtime makes the package independent of the
    // machine-wide Evergreen installation. Microsoft requires the AppContainer
    // SIDs to be able to read the folder on Windows 10 (fixed runtime 120+).
    internal static string BrowserExecutableFolder
    {
        get
        {
            string custom = Environment.GetEnvironmentVariable("WEBVIEW2_BROWSER_EXECUTABLE_FOLDER");
            if (!String.IsNullOrEmpty(custom) && File.Exists(Path.Combine(custom, "msedgewebview2.exe")))
                return custom;
            string home = Path.Combine(Root, "webview2");
            if (File.Exists(Path.Combine(home, "msedgewebview2.exe"))) return home;
            try
            {
                string[] folders = Directory.GetDirectories(home);
                Array.Sort(folders, StringComparer.OrdinalIgnoreCase);
                foreach (string folder in folders)
                    if (File.Exists(Path.Combine(folder, "msedgewebview2.exe"))) return folder;
            }
            catch (Exception) { }
            return null;
        }
    }

    internal static bool FixedWebView2Available { get { return BrowserExecutableFolder != null; } }

    // Prefer the bundled fixed runtime, but never let a damaged copy prevent
    // startup when the machine still has a working Evergreen runtime.
    internal static async Task<CoreWebView2Environment> CreateWebView2EnvironmentAsync(string userDataFolder)
    {
        string browser = BrowserExecutableFolder;
        if (browser == null)
            return await CoreWebView2Environment.CreateAsync(null, userDataFolder);
        GrantFixedWebView2Access();
        string failure = null;
        try
        {
            return await CoreWebView2Environment.CreateAsync(browser, userDataFolder);
        }
        catch (Exception error)
        {
            if (!EvergreenWebView2Installed()) throw;
            failure = error.Message;
        }
        Log("Environment: fixed WebView2 failed, falling back to Evergreen: " + failure);
        return await CoreWebView2Environment.CreateAsync(null, userDataFolder);
    }

    internal static bool EvergreenWebView2Installed()
    {
        foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using (RegistryKey root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
                using (RegistryKey key = root.OpenSubKey(@"SOFTWARE\Microsoft\EdgeUpdate\Clients\" + WebView2Client))
                {
                    if (key != null && !String.IsNullOrEmpty(key.GetValue("pv") as string)) return true;
                }
            }
            catch (Exception) { }
        }
        try
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\EdgeUpdate\Clients\" + WebView2Client))
            {
                if (key != null && !String.IsNullOrEmpty(key.GetValue("pv") as string)) return true;
            }
        }
        catch (Exception) { }
        return false;
    }

    internal static bool WebView2Available { get { return FixedWebView2Available || EvergreenWebView2Installed(); } }

    internal static string WebView2Mode
    {
        get
        {
            if (FixedWebView2Available) return "fixed";
            if (EvergreenWebView2Installed()) return "evergreen";
            return "missing";
        }
    }

    internal static string WebView2Version
    {
        get
        {
            string folder = BrowserExecutableFolder;
            if (folder != null)
            {
                try { return FileVersionInfo.GetVersionInfo(Path.Combine(folder, "msedgewebview2.exe")).FileVersion; }
                catch (Exception) { }
                return Path.GetFileName(folder);
            }
            foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using (RegistryKey root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
                    using (RegistryKey key = root.OpenSubKey(@"SOFTWARE\Microsoft\EdgeUpdate\Clients\" + WebView2Client))
                    {
                        string value = key == null ? null : key.GetValue("pv") as string;
                        if (!String.IsNullOrEmpty(value)) return value;
                    }
                }
                catch (Exception) { }
            }
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\EdgeUpdate\Clients\" + WebView2Client))
                {
                    string value = key == null ? null : key.GetValue("pv") as string;
                    if (!String.IsNullOrEmpty(value)) return value;
                }
            }
            catch (Exception) { }
            return "";
        }
    }

    internal static string GitVersionReported
    {
        get
        {
            if (!GitAvailable) return "";
            try { return Capture(GitExe, "--version", Root, 10000).Trim(); }
            catch (Exception) { return ""; }
        }
    }

    internal static void GrantFixedWebView2Access()
    {
        string folder = BrowserExecutableFolder;
        if (folder == null) return;
        string marker = Path.Combine(folder, ".dsh-portable-acl");
        if (File.Exists(marker)) return;
        try
        {
            string icacls = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "icacls.exe");
            ProcessStartInfo info = new ProcessStartInfo(icacls);
            info.Arguments = "\"" + folder.TrimEnd('\\') + "\" /grant *S-1-15-2-2:(OI)(CI)(RX) *S-1-15-2-1:(OI)(CI)(RX) /C /Q";
            info.UseShellExecute = false;
            info.CreateNoWindow = true;
            info.RedirectStandardOutput = true;
            info.RedirectStandardError = true;
            using (Process process = new Process { StartInfo = info })
            {
                process.Start();
                process.StandardOutput.ReadToEnd();
                process.StandardError.ReadToEnd();
                if (!process.WaitForExit(30000)) { try { process.Kill(); } catch (Exception) { } return; }
                if (process.ExitCode == 0)
                    File.WriteAllText(marker, "fixed WebView2 AppContainer grants applied", new UTF8Encoding(false));
                else
                    Log("Environment: fixed WebView2 ACL grant exited with code " + process.ExitCode);
            }
        }
        catch (Exception error) { Log("Environment: fixed WebView2 ACL grant failed: " + error.Message); }
    }

    internal static bool TryNodeVersion(out string version)
    {
        version = "";
        try
        {
            version = Capture(NodeExe, "--version", Root, 15000).Trim();
            return version.StartsWith("v", StringComparison.Ordinal);
        }
        catch (Exception) { return false; }
    }

    internal static bool TryDshVersion(out string version)
    {
        version = "";
        try
        {
            version = Capture(NodeExe, "\"" + DshEntry + "\" --version", Root, 30000).Trim();
            return version.Length > 0;
        }
        catch (Exception) { return false; }
    }

    internal static bool EnsureBundledGit(Action<string> progress)
    {
        if (GitAvailable)
        {
            string installed;
            if (TryGitVersion(out installed)) return true;
            Log("Environment: bundled Git is present but not runnable, reinstalling");
        }
        string repair = Path.Combine(DataRoot, ".repair");
        string zip = Path.Combine(repair, "MinGit-" + GitVersion + ".zip");
        string stage = Path.Combine(repair, "git-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(repair);
            if (progress != null) progress("正在下载便携 Git（约 40 MB）…");
            Download(GitUrl, zip, GitSha256, progress);
            if (progress != null) progress("正在解压便携 Git…");
            ZipFile.ExtractToDirectory(zip, stage);
            string target = Path.Combine(Root, "git");
            if (Directory.Exists(target)) Directory.Delete(target, true);
            Directory.Move(stage, target);
            if (!GitAvailable) throw new InvalidOperationException("MinGit archive did not contain cmd\\git.exe");
            Log("Environment: bundled Git " + GitVersion + " installed");
            return true;
        }
        catch (Exception error)
        {
            Log("Environment: Git install failed: " + error.Message);
            if (progress != null) progress("Git 安装失败：" + error.Message);
            return false;
        }
        finally
        {
            try { if (File.Exists(zip)) File.Delete(zip); } catch (Exception) { }
            try { if (Directory.Exists(stage)) Directory.Delete(stage, true); } catch (Exception) { }
        }
    }

    internal static bool EnsurePnpm(Action<string> progress)
    {
        if (PnpmAvailable) return true;
        if (!File.Exists(Path.Combine(PluginTools, "package.json")) || !File.Exists(NpmCli)) return false;
        if (progress != null) progress("正在安装 pnpm 工具链（约 20 MB）…");
        try
        {
            int code = Run(NodeExe,
                "\"" + NpmCli + "\" ci --omit=dev --no-audit --no-fund",
                PluginTools, 20 * 60 * 1000, "plugins-toolchain");
            if (code != 0 || !PnpmAvailable)
            {
                Log("Environment: pnpm install failed with exit code " + code);
                return false;
            }
            Log("Environment: pnpm toolchain repaired");
            return true;
        }
        catch (Exception error)
        {
            Log("Environment: pnpm install failed: " + error.Message);
            return false;
        }
    }

    internal static bool InstallVcRedist(Action<string> progress)
    {
        string file = Path.Combine(RepairDirectory(), "vc_redist.x64.exe");
        try
        {
            if (progress != null) progress("正在下载 Microsoft Visual C++ 运行库…");
            Download(VcRedistUrl, file, null, progress);
            if (!IsMicrosoftSigned(file)) throw new InvalidOperationException("下载的安装程序签名无效");
            if (progress != null) progress("正在安装 Microsoft Visual C++ 运行库…");
            int code = RunElevated(file, "/install /quiet /norestart", 20 * 60 * 1000);
            if (code != 0 && code != 1638 && code != 3010)
                throw new InvalidOperationException("安装程序退出代码 " + code);
            string version;
            return TryNodeVersion(out version);
        }
        catch (Exception error)
        {
            Log("Environment: Visual C++ install failed: " + error.Message);
            if (progress != null) progress("Visual C++ 运行库安装失败：" + error.Message);
            return false;
        }
        finally
        {
            try { if (File.Exists(file)) File.Delete(file); } catch (Exception) { }
        }
    }

    internal static bool InstallWebView2(Action<string> progress)
    {
        string file = Path.Combine(RepairDirectory(), "MicrosoftEdgeWebView2Setup.exe");
        try
        {
            if (progress != null) progress("正在下载 Microsoft Edge WebView2 Runtime…");
            Download(WebView2Bootstrapper, file, null, progress);
            if (!IsMicrosoftSigned(file)) throw new InvalidOperationException("下载的安装程序签名无效");
            if (progress != null) progress("正在安装 Microsoft Edge WebView2 Runtime…");
            int code = RunElevated(file, "/silent /install", 20 * 60 * 1000);
            if (code != 0 && code != 3010) throw new InvalidOperationException("安装程序退出代码 " + code);
            return EvergreenWebView2Installed();
        }
        catch (Exception error)
        {
            Log("Environment: WebView2 install failed: " + error.Message);
            if (progress != null) progress("WebView2 安装失败：" + error.Message);
            return false;
        }
        finally
        {
            try { if (File.Exists(file)) File.Delete(file); } catch (Exception) { }
        }
    }

    internal static long FreeDiskBytes()
    {
        try { return new DriveInfo(Path.GetPathRoot(Root)).AvailableFreeSpace; }
        catch (Exception) { return 0; }
    }

    internal static bool NetworkNeedsMirror(string proxy, Action<string> log)
    {
        bool npm = CanReach("https://registry.npmjs.org/", 6000, proxy);
        bool github = CanReach("https://github.com/", 6000, proxy);
        bool codeload = CanReach("https://codeload.github.com/", 6000, proxy);
        if (log != null)
            log("Plugin network: npm=" + (npm ? "direct" : "unreachable") +
                " github=" + (github ? "direct" : "unreachable") +
                " codeload=" + (codeload ? "direct" : "unreachable") +
                " -> " + (npm && github && codeload ? "direct" : "mirror"));
        return !(npm && github && codeload);
    }

    internal static bool CanReach(string url, int timeoutMs, string proxy)
    {
        try
        {
            EnableTls();
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = "HEAD";
            request.Timeout = timeoutMs;
            request.ReadWriteTimeout = timeoutMs;
            request.AllowAutoRedirect = true;
            request.UserAgent = "DSH-Portable";
            if (!String.IsNullOrEmpty(proxy)) request.Proxy = new WebProxy(proxy);
            using (HttpWebResponse response = (HttpWebResponse)request.GetResponse()) return true;
        }
        catch (WebException error)
        {
            // A 403/404 still proves the host is reachable; only transport
            // failures (DNS, TLS, timeout) count as a blocked connection.
            if (error.Response != null)
            {
                try { error.Response.Close(); } catch (Exception) { }
                return true;
            }
            return false;
        }
        catch (Exception) { return false; }
    }

    private static void EnableTls()
    {
        try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; }
        catch (Exception) { }
        try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)12288; }
        catch (Exception) { }
    }

    internal static bool TryGitVersion(out string version)
    {
        version = "";
        try
        {
            version = Capture(GitExe, "--version", Root, 10000).Trim();
            return version.IndexOf("git version", StringComparison.OrdinalIgnoreCase) >= 0;
        }
        catch (Exception) { return false; }
    }

    internal static void WriteReport(bool ready, bool fullCheck, string node, bool git, bool pnpm, string webviewMode)
    {
        WriteReport(ready, fullCheck, node, git, pnpm, webviewMode, false);
    }

    internal static void WriteReport(bool ready, bool fullCheck, string node, bool git, bool pnpm, string webviewMode, bool optionalDeclined)
    {
        try
        {
            Directory.CreateDirectory(DataRoot);
            StringBuilder json = new StringBuilder();
            json.Append("{\n");
            json.Append("  \"checked\": \"").Append(Escape(DateTime.UtcNow.ToString("o"))).Append("\",\n");
            json.Append("  \"ready\": ").Append(ready ? "true" : "false").Append(",\n");
            json.Append("  \"fullCheck\": ").Append(fullCheck ? "true" : "false").Append(",\n");
            json.Append("  \"optionalDeclined\": ").Append(optionalDeclined ? "true" : "false").Append(",\n");
            json.Append("  \"node\": \"").Append(Escape(node)).Append("\",\n");
            json.Append("  \"git\": ").Append(git ? "true" : "false").Append(",\n");
            json.Append("  \"pnpm\": ").Append(pnpm ? "true" : "false").Append(",\n");
            json.Append("  \"webview2\": \"").Append(Escape(webviewMode)).Append("\",\n");
            json.Append("  \"webview2Version\": \"").Append(Escape(WebView2Version)).Append("\",\n");
            json.Append("  \"freeBytes\": ").Append(FreeDiskBytes()).Append(",\n");
            json.Append("  \"root\": \"").Append(Escape(Root)).Append("\"\n");
            json.Append("}\n");
            File.WriteAllText(ReportPath, json.ToString(), new UTF8Encoding(false));
        }
        catch (Exception error) { Log("Environment: report write failed: " + error.Message); }
    }

    internal static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(DataRoot);
            File.AppendAllText(Path.Combine(DataRoot, "desktop.log"),
                DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss") + " " + message + "\r\n",
                new UTF8Encoding(false));
        }
        catch (Exception) { }
    }

    private static string RepairDirectory()
    {
        string directory = Path.Combine(DataRoot, ".repair");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void Download(string url, string destination, string sha256, Action<string> progress)
    {
        string partial = destination + ".download";
        try
        {
            EnableTls();
            if (File.Exists(partial)) File.Delete(partial);
            using (WebClient client = new WebClient())
            {
                client.Headers[HttpRequestHeader.UserAgent] = "DSH-Portable/" + NodeVersion;
                client.DownloadProgressChanged += delegate(object sender, DownloadProgressChangedEventArgs e)
                {
                    if (progress != null) progress("正在下载 " + Path.GetFileName(destination) + "… " + e.ProgressPercentage + "%");
                };
                System.Threading.Tasks.Task task = client.DownloadFileTaskAsync(new Uri(url), partial);
                while (!task.IsCompleted)
                {
                    Application.DoEvents();
                    Thread.Sleep(40);
                }
                if (task.IsFaulted)
                    throw task.Exception.GetBaseException();
            }
            if (!String.IsNullOrEmpty(sha256))
            {
                string actual;
                using (SHA256 hash = SHA256.Create())
                using (FileStream stream = File.OpenRead(partial))
                    actual = BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "");
                if (!String.Equals(actual, sha256, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(partial);
                    throw new InvalidOperationException("SHA-256 校验失败");
                }
            }
            if (File.Exists(destination)) File.Delete(destination);
            File.Move(partial, destination);
        }
        catch (Exception)
        {
            try { if (File.Exists(partial)) File.Delete(partial); } catch (Exception) { }
            throw;
        }
    }

    private static int RunElevated(string file, string arguments, int timeout)
    {
        try
        {
            ProcessStartInfo info = new ProcessStartInfo(file, arguments);
            info.UseShellExecute = true;
            info.Verb = "runas";
            using (Process process = Process.Start(info))
            {
                if (process == null) return -1;
                if (!process.WaitForExit(timeout))
                {
                    try { process.Kill(); } catch (Exception) { }
                    return -1;
                }
                return process.ExitCode;
            }
        }
        catch (Exception error)
        {
            Log("Environment: elevated installer failed: " + error.Message);
            return -1;
        }
    }

    private static int Run(string file, string arguments, string workingDirectory, int timeout, string logPrefix)
    {
        ProcessStartInfo info = new ProcessStartInfo(file, arguments);
        info.WorkingDirectory = workingDirectory;
        info.UseShellExecute = false;
        info.CreateNoWindow = true;
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;
        info.StandardOutputEncoding = Encoding.UTF8;
        info.StandardErrorEncoding = Encoding.UTF8;
        info.EnvironmentVariables["PATH"] = Path.Combine(Root, "node") + ";" +
            Path.Combine(Root, "git", "cmd") + ";" +
            Path.Combine(Root, "desktop", "plugin-tools", "node_modules", ".bin") + ";" +
            Environment.GetEnvironmentVariable("PATH");
        info.EnvironmentVariables["npm_config_cache"] = Path.Combine(DataRoot, "npm-cache");
        info.EnvironmentVariables["NO_COLOR"] = "1";
        using (Process process = new Process { StartInfo = info })
        {
            process.OutputDataReceived += delegate(object sender, DataReceivedEventArgs e)
            {
                if (!String.IsNullOrEmpty(e.Data)) Log(logPrefix + ": " + e.Data);
            };
            process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e)
            {
                if (!String.IsNullOrEmpty(e.Data)) Log(logPrefix + ": " + e.Data);
            };
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            if (!process.WaitForExit(timeout))
            {
                try { process.Kill(); } catch (Exception) { }
                return -1;
            }
            process.WaitForExit();
            return process.ExitCode;
        }
    }

    private static string Capture(string file, string arguments, string workingDirectory, int timeout)
    {
        ProcessStartInfo info = new ProcessStartInfo(file, arguments);
        info.WorkingDirectory = workingDirectory;
        info.UseShellExecute = false;
        info.CreateNoWindow = true;
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;
        info.StandardOutputEncoding = Encoding.UTF8;
        info.StandardErrorEncoding = Encoding.UTF8;
        info.EnvironmentVariables["PATH"] = Path.Combine(Root, "node") + ";" +
            Path.Combine(Root, "git", "cmd") + ";" +
            Path.Combine(Root, "desktop", "plugin-tools", "node_modules", ".bin") + ";" +
            Environment.GetEnvironmentVariable("PATH");
        using (Process process = new Process { StartInfo = info })
        {
            StringBuilder output = new StringBuilder();
            StringBuilder error = new StringBuilder();
            process.OutputDataReceived += delegate(object sender, DataReceivedEventArgs e)
            {
                if (e.Data != null) output.AppendLine(e.Data);
            };
            process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e)
            {
                if (e.Data != null) error.AppendLine(e.Data);
            };
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            if (!process.WaitForExit(timeout))
            {
                try { process.Kill(); } catch (Exception) { }
                throw new TimeoutException(file + " did not exit in time");
            }
            process.WaitForExit();
            if (process.ExitCode != 0)
                throw new InvalidOperationException(error.Length > 0 ? error.ToString() : output.ToString());
            return output.ToString();
        }
    }

    private static bool IsMicrosoftSigned(string file)
    {
        try
        {
            X509Certificate2 certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(file));
            using (X509Chain chain = new X509Chain())
            {
                chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                chain.ChainPolicy.RevocationFlag = X509RevocationFlag.ExcludeRoot;
                if (!chain.Build(certificate)) return false;
            }
            return certificate.Subject.IndexOf("Microsoft", StringComparison.OrdinalIgnoreCase) >= 0;
        }
        catch (Exception) { return false; }
    }

    private static string Escape(string value)
    {
        if (value == null) return "";
        return value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n");
    }
}
