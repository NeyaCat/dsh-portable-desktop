using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

// Turns the network mode the user picks in the dsh-network-config settings page
// into the two things a package install actually needs: proxy environment
// variables on the backend this launcher starts, and a marker-fenced .npmrc in
// every profile.
//
// Why this exists (measured, 2026-10-22): the settings page stores
// $DSH_HOME/network-config.json and hands the choice to
// @deepseek-ai/dsh-http-proxy, which only covers the harness's own HTTP client.
// pnpm is a separate process: it ignores HTTP_PROXY and the Windows system
// proxy and reads only npm config (.npmrc / npm_config_*). It also ignores the
// plugin's mode entirely, so a GitHub-hosted plugin whose tarball is 26 MB sat
// at "处理中" while every byte went direct.
//
// The file location is not guessed: the launcher passes the same path it sets
// as DSH_HOME for the backend, so the file read here is the one the plugin
// writes.
internal sealed class ProxyBridge : IDisposable
{
    internal const string MarkerStart = "# >>> dsh-proxy-bridge (managed by the desktop launcher; do not edit between markers)";
    internal const string MarkerEnd = "# <<< dsh-proxy-bridge";
    private const string StartPrefix = "# >>> dsh-proxy-bridge";
    private const string EndPrefix = "# <<< dsh-proxy-bridge";
    internal const string ConfigName = "network-config.json";
    private const string RegistryKey = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
    private const string RunRecord = ".plugin-manager\\run.json";
    private const string LockFile = "package.json.lock";

    private readonly string homeDir;
    private readonly Action<string> logSink;
    private FileSystemWatcher watcher;
    private Timer debounce;
    private bool disposed;

    internal ProxyBridge(string homeDir, Action<string> log)
    {
        this.homeDir = homeDir ?? "";
        this.logSink = log ?? delegate { };
    }

    // A method rather than a field call: `using System.Diagnostics` puts a private
    // Log type in scope, so a bare `log(...)` reads like that type here.
    private void Log(string message)
    {
        try { logSink(message); }
        catch (Exception) { }
    }

    // One resolved policy: what to put in the environment and in .npmrc.
    internal sealed class Resolution
    {
        internal string Mode = "unset";
        internal string HttpProxy = "";
        internal string HttpsProxy = "";
        internal string NoProxy = "";
        internal string Source = "none";
        internal string Detail = "";

        internal bool HasProxy { get { return HttpProxy != "" || HttpsProxy != ""; } }

        internal string Summary
        {
            get
            {
                return "mode=" + Mode + " source=" + Source
                    + " http=" + (HttpProxy == "" ? "-" : HttpProxy)
                    + " https=" + (HttpsProxy == "" ? "-" : HttpsProxy)
                    + (Detail == "" ? "" : " (" + Detail + ")");
            }
        }
    }

    internal Resolution Current = new Resolution();

    internal string HomeDir { get { return homeDir; } }
    internal string ConfigPath { get { return Path.Combine(homeDir, ConfigName); } }

    // Resolve, apply to disk, clean up after a killed install, then follow the
    // settings file. Never throws: a broken proxy setting must not stop the app
    // from starting.
    internal void Initialize()
    {
        try
        {
            Current = Resolve(homeDir, ReadRegistryText, logSink);
            Log("ProxyBridge: " + Current.Summary);
            Log("ProxyBridge: npmrc " + SyncNpmrc(Current));
            Log("ProxyBridge: locks " + CleanStaleLocks(homeDir, logSink));
            Watch();
        }
        catch (Exception error)
        {
            Log("ProxyBridge: init failed: " + error.Message);
        }
    }

    // A profile can be created after Initialize() (fresh first-run install), so
    // write the marker-fenced .npmrc once more without starting a second watcher.
    internal string SyncNow()
    {
        try { return SyncNpmrc(Current); }
        catch (Exception error) { Log("ProxyBridge: npmrc sync failed: " + error.Message); return "failed"; }
    }

    // Environment the backend (and every pnpm it spawns through dsh-market)
    // inherits. A direct or unset policy removes inherited proxies instead of
    // leaving a stale one from the launching shell.
    internal void ApplyEnvironment(ProcessStartInfo info)
    {
        if (info == null) return;
        var names = new[] { "HTTP_PROXY", "HTTPS_PROXY", "NO_PROXY", "http_proxy", "https_proxy", "no_proxy" };
        // pnpm resolves a `github:` spec with git, and the form pnpm builds is
        // ssh://git@github.com/... — an interactive SSH fetch with no terminal
        // waits for a credential prompt forever. That hang is what "处理中"
        // looks like for a GitHub plugin. Rewriting GitHub SSH to HTTPS keeps it
        // non-interactive, the proxy carries the fetch, and GIT_TERMINAL_PROMPT=0
        // turns any remaining prompt into a fast failure instead of a hang.
        var gitNames = new[]
        {
            "GIT_CONFIG_COUNT",
            "GIT_CONFIG_KEY_0", "GIT_CONFIG_VALUE_0",
            "GIT_CONFIG_KEY_1", "GIT_CONFIG_VALUE_1",
            "GIT_CONFIG_KEY_2", "GIT_CONFIG_VALUE_2",
            "GIT_CONFIG_KEY_3", "GIT_CONFIG_VALUE_3",
            "GIT_TERMINAL_PROMPT",
        };
        if (!Current.HasProxy)
        {
            foreach (var name in names)
            {
                try { info.EnvironmentVariables.Remove(name); }
                catch (Exception) { }
            }
            foreach (var name in gitNames)
            {
                try { info.EnvironmentVariables.Remove(name); }
                catch (Exception) { }
            }
            return;
        }
        string http = Current.HttpProxy != "" ? Current.HttpProxy : Current.HttpsProxy;
        string https = Current.HttpsProxy != "" ? Current.HttpsProxy : Current.HttpProxy;
        info.EnvironmentVariables["HTTP_PROXY"] = http;
        info.EnvironmentVariables["HTTPS_PROXY"] = https;
        info.EnvironmentVariables["http_proxy"] = http;
        info.EnvironmentVariables["https_proxy"] = https;
        if (Current.NoProxy != "")
        {
            info.EnvironmentVariables["NO_PROXY"] = Current.NoProxy;
            info.EnvironmentVariables["no_proxy"] = Current.NoProxy;
        }
        info.EnvironmentVariables["GIT_CONFIG_COUNT"] = "4";
        info.EnvironmentVariables["GIT_CONFIG_KEY_0"] = "url.https://github.com/.insteadOf";
        info.EnvironmentVariables["GIT_CONFIG_VALUE_0"] = "git@github.com:";
        info.EnvironmentVariables["GIT_CONFIG_KEY_1"] = "url.https://github.com/.insteadOf";
        info.EnvironmentVariables["GIT_CONFIG_VALUE_1"] = "ssh://git@github.com/";
        info.EnvironmentVariables["GIT_CONFIG_KEY_2"] = "http.proxy";
        info.EnvironmentVariables["GIT_CONFIG_VALUE_2"] = https;
        info.EnvironmentVariables["GIT_CONFIG_KEY_3"] = "https.proxy";
        info.EnvironmentVariables["GIT_CONFIG_VALUE_3"] = https;
        info.EnvironmentVariables["GIT_TERMINAL_PROMPT"] = "0";
    }

    // Write or clear the fenced block in every profile. User content outside the
    // markers is preserved byte for byte, and a file that held nothing but our
    // block is removed instead of left empty.
    internal string SyncNpmrc(Resolution resolution)
    {
        var profiles = ProfileDirs(homeDir);
        if (profiles.Count == 0) return "skipped (no profiles)";
        var parts = new List<string>();
        foreach (var profile in profiles)
        {
            try { parts.Add(Path.GetFileName(profile) + "=" + SyncOne(profile, resolution)); }
            catch (Exception error) { parts.Add(Path.GetFileName(profile) + "=failed(" + error.Message + ")"); }
        }
        return String.Join(", ", parts.ToArray());
    }

    private string SyncOne(string profileDir, Resolution resolution)
    {
        string path = Path.Combine(profileDir, ".npmrc");
        string existing = "";
        bool existed = File.Exists(path);
        if (existed)
        {
            try { existing = File.ReadAllText(path); }
            catch (IOException) { return "unreadable"; }
            catch (UnauthorizedAccessException) { return "denied"; }
        }
        // Nothing to apply and no block of ours to remove: never touch a file the
        // user owns (a rewrite would also normalise its line endings).
        if (!resolution.HasProxy
            && existing.IndexOf(StartPrefix, StringComparison.Ordinal) < 0
            && existing.IndexOf(EndPrefix, StringComparison.Ordinal) < 0)
            return existed ? "unchanged" : "clear";
        string stripped = StripBlock(existing);
        var utf8 = new UTF8Encoding(false);
        if (!resolution.HasProxy)
        {
            if (stripped.Length == 0)
            {
                File.Delete(path);
                return "removed";
            }
            bool trailingNewline = existing.Length > 0
                && (existing[existing.Length - 1] == '\n' || existing[existing.Length - 1] == '\r');
            File.WriteAllText(path, stripped + (trailingNewline ? Environment.NewLine : ""), utf8);
            return "cleared";
        }
        var block = new StringBuilder();
        if (stripped.Length > 0) block.Append(stripped).Append(Environment.NewLine);
        block.Append(MarkerStart).Append(Environment.NewLine);
        if (resolution.HttpProxy != "") block.Append("proxy=").Append(resolution.HttpProxy).Append(Environment.NewLine);
        if (resolution.HttpsProxy != "") block.Append("https-proxy=").Append(resolution.HttpsProxy).Append(Environment.NewLine);
        if (resolution.NoProxy != "") block.Append("noproxy=").Append(resolution.NoProxy).Append(Environment.NewLine);
        block.Append(MarkerEnd).Append(Environment.NewLine);
        string wanted = block.ToString();
        if (wanted == existing) return "unchanged";
        File.WriteAllText(path, wanted, utf8);
        return "written";
    }

    // Remove our fenced block, and any orphan marker line, leaving every other
    // line as the user wrote it.
    internal static string StripBlock(string text)
    {
        if (String.IsNullOrEmpty(text)) return "";
        var lines = Regex.Split(text, "\r\n|\n|\r");
        var kept = new List<string>();
        bool inside = false;
        foreach (var line in lines)
        {
            string trimmed = line.Trim();
            if (trimmed.StartsWith(StartPrefix, StringComparison.Ordinal)) { inside = true; continue; }
            if (trimmed.StartsWith(EndPrefix, StringComparison.Ordinal)) { inside = false; continue; }
            if (inside) continue;
            kept.Add(line);
        }
        return String.Join(Environment.NewLine, kept.ToArray()).TrimEnd('\r', '\n');
    }

    // A profile is a directory with a package.json. `profiles/node_modules` is
    // pnpm's hoisted workspace directory in this portable layout, not a profile:
    // writing a .npmrc there would look load-bearing and be meaningless.
    internal static List<string> ProfileDirs(string home)
    {
        var result = new List<string>();
        try
        {
            string root = Path.Combine(home, "profiles");
            if (!Directory.Exists(root)) return result;
            foreach (var dir in Directory.GetDirectories(root))
            {
                string name = Path.GetFileName(dir);
                if (name.StartsWith(".", StringComparison.Ordinal)) continue;
                if (String.Equals(name, "node_modules", StringComparison.OrdinalIgnoreCase)) continue;
                if (!File.Exists(Path.Combine(dir, "package.json"))) continue;
                result.Add(dir);
            }
        }
        catch (Exception) { }
        result.Sort(StringComparer.OrdinalIgnoreCase);
        return result;
    }

    // A killed install leaves .plugin-manager/run.json and package.json.lock
    // behind, and every later install waits on that lock (lockWaitMs, 120 s)
    // before failing. Only records whose process is gone are removed: a live
    // pid means an install is really running.
    internal static string CleanStaleLocks(string home, Action<string> log)
    {
        var profiles = ProfileDirs(home);
        if (profiles.Count == 0) return "skipped (no profiles)";
        var parts = new List<string>();
        foreach (var profile in profiles)
        {
            var row = new List<string>();
            string runPath = Path.Combine(profile, RunRecord);
            int runPid = ReadPidFromJson(runPath);
            row.Add(ClearOne(runPath, runPid, "run.json", log));
            string lockPath = Path.Combine(profile, LockFile);
            int lockPid = ReadPidFromText(lockPath);
            row.Add(ClearOne(lockPath, lockPid, LockFile, log));
            parts.Add(Path.GetFileName(profile) + "=" + String.Join("/", row.ToArray()));
        }
        return String.Join(", ", parts.ToArray());
    }

    private static string ClearOne(string path, int pid, string label, Action<string> log)
    {
        if (!File.Exists(path)) return label + ":none";
        if (pid <= 0) return label + ":kept(unparsed)";
        // A record younger than a couple of minutes belongs to a run that may be
        // starting right now; process liveness cannot be probed reliably enough
        // in every Windows session to justify deleting on that alone.
        DateTime written;
        try { written = File.GetLastWriteTimeUtc(path); }
        catch (Exception) { written = DateTime.UtcNow; }
        if (DateTime.UtcNow - written < TimeSpan.FromMinutes(2)) return label + ":kept(fresh)";
        if (IsRunning(pid)) return label + ":kept(pid " + pid + " alive)";
        try
        {
            File.Delete(path);
            if (log != null) log("ProxyBridge: removed stale " + label + " (pid " + pid + " is gone)");
            return label + ":cleared";
        }
        catch (Exception error) { return label + ":failed(" + error.Message + ")"; }
    }

    internal static bool IsRunning(int pid)
    {
        if (pid <= 0) return false;
        try
        {
            using (var process = Process.GetProcessById(pid)) { return process != null; }
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    private static int ReadPidFromText(string path)
    {
        try
        {
            if (!File.Exists(path)) return 0;
            int pid;
            return Int32.TryParse(File.ReadAllText(path).Trim(), out pid) ? pid : 0;
        }
        catch (Exception) { return 0; }
    }

    private static int ReadPidFromJson(string path)
    {
        try
        {
            if (!File.Exists(path)) return 0;
            var row = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(path));
            if (row == null || !row.ContainsKey("pid") || row["pid"] == null) return 0;
            return Convert.ToInt32(row["pid"]);
        }
        catch (Exception) { return 0; }
    }

    // ---- resolution -------------------------------------------------------

    internal static Resolution Resolve(string home, Func<string> registryReader, Action<string> log)
    {
        var resolution = new Resolution();
        string path = Path.Combine(home ?? "", ConfigName);
        string mode = "system";
        string customProxy = "";
        string customBypass = "";
        if (!File.Exists(path))
        {
            resolution.Detail = ConfigName + " absent, following the plugin default";
        }
        else
        {
            try
            {
                var row = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(path));
                if (row != null && row.ContainsKey("mode") && row["mode"] != null)
                {
                    string stored = row["mode"].ToString().Trim().ToLowerInvariant();
                    if (stored == "custom" || stored == "system" || stored == "direct") mode = stored;
                    else resolution.Detail = "unknown mode '" + stored + "', following system";
                }
                if (row != null && row.ContainsKey("custom") && row["custom"] is Dictionary<string, object>)
                {
                    var custom = (Dictionary<string, object>)row["custom"];
                    if (custom.ContainsKey("proxy") && custom["proxy"] != null) customProxy = custom["proxy"].ToString();
                    if (custom.ContainsKey("bypass") && custom["bypass"] != null) customBypass = custom["bypass"].ToString();
                }
            }
            catch (Exception error)
            {
                resolution.Detail = ConfigName + " unreadable (" + error.Message + "), following system";
            }
        }
        resolution.Mode = mode;
        if (mode == "direct")
        {
            resolution.Source = "config:direct";
            return resolution;
        }
        if (mode == "custom")
        {
            resolution.Source = "config:custom";
            resolution.HttpProxy = NormalizeProxyUrl(customProxy);
            resolution.HttpsProxy = resolution.HttpProxy;
            if (resolution.HttpProxy == "")
            {
                resolution.Detail = Append(resolution.Detail, "custom proxy unusable ('" + customProxy + "'), cleared");
                return resolution;
            }
            resolution.NoProxy = NormalizeNoProxy(customBypass);
            return resolution;
        }
        // system: the same resolution the plugin performs, so both halves of the
        // harness reach the network one way.
        string text = null;
        if (registryReader != null)
        {
            try { text = registryReader(); }
            catch (Exception error) { resolution.Detail = Append(resolution.Detail, "registry unreadable (" + error.Message + ")"); }
        }
        if (text != null && text.Trim() != "")
        {
            var fromRegistry = InterpretRegistry(text);
            resolution.Source = "registry";
            resolution.HttpProxy = fromRegistry.HttpProxy;
            resolution.HttpsProxy = fromRegistry.HttpsProxy;
            resolution.NoProxy = fromRegistry.NoProxy;
            resolution.Detail = Append(resolution.Detail, fromRegistry.Detail);
            if (!fromRegistry.Enabled) resolution.Detail = Append(resolution.Detail, "system proxy disabled");
            return resolution;
        }
        var fromEnv = FromEnvironment();
        resolution.Source = "env";
        resolution.HttpProxy = fromEnv.HttpProxy;
        resolution.HttpsProxy = fromEnv.HttpsProxy;
        resolution.NoProxy = fromEnv.NoProxy;
        resolution.Detail = Append(resolution.Detail, "registry unavailable, used inherited proxy environment");
        return resolution;
    }

    private static string Append(string first, string second)
    {
        if (String.IsNullOrEmpty(first)) return second ?? "";
        if (String.IsNullOrEmpty(second)) return first;
        return first + "; " + second;
    }

    // `reg query` prints "    Name    TYPE    Data"; a value with no data prints
    // the "(value not set)" sentinel. Mirrors the plugin's parser.
    internal static Dictionary<string, string> ParseRegistryQuery(string output)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (String.IsNullOrEmpty(output)) return values;
        var pattern = new Regex(@"^\s{2,}(\S.*?)\s{4,}(REG_[A-Z_]+)\s{4,}(.*)$");
        foreach (var line in Regex.Split(output, "\r\n|\n|\r"))
        {
            var match = pattern.Match(line);
            if (!match.Success) continue;
            string name = match.Groups[1].Value.Trim();
            string data = match.Groups[3].Value.Trim();
            if (data == "(value not set)" || data == "") continue;
            values[name] = data;
        }
        return values;
    }

    internal sealed class RegistryView
    {
        internal bool Enabled;
        internal string HttpProxy = "";
        internal string HttpsProxy = "";
        internal string NoProxy = "";
        internal string Detail = "";
    }

    internal static RegistryView InterpretRegistry(string output)
    {
        var view = new RegistryView();
        Dictionary<string, string> values = ParseRegistryQuery(output);
        string enabledText;
        int enabled = 0;
        if (values.TryGetValue("ProxyEnable", out enabledText))
        {
            string text = enabledText.Trim();
            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                try { enabled = Convert.ToInt32(text.Substring(2), 16); } catch (Exception) { enabled = 0; }
            }
            else { Int32.TryParse(text, out enabled); }
        }
        view.Enabled = enabled != 0;
        string server;
        if (!values.TryGetValue("ProxyServer", out server)) server = "";
        string bypass;
        if (!values.TryGetValue("ProxyOverride", out bypass)) bypass = "";
        view.NoProxy = NormalizeNoProxy(bypass);
        if (!view.Enabled) return view;
        foreach (var entry in SplitProxyServer(server))
        {
            if (entry[0] == "http" && view.HttpProxy == "") view.HttpProxy = entry[1];
            else if (entry[0] == "https" && view.HttpsProxy == "") view.HttpsProxy = entry[1];
        }
        return view;
    }

    // WinINET stores either one host:port for every scheme or a scheme=host:port
    // list. socks= is dropped: the harness's transport refuses SOCKS and pnpm has
    // no business receiving a scheme nobody can honour.
    internal static List<string[]> SplitProxyServer(string server)
    {
        var result = new List<string[]>();
        if (String.IsNullOrEmpty(server) || server.Trim() == "") return result;
        string value = server.Trim();
        if (value.IndexOf('=') < 0)
        {
            string url = NormalizeProxyUrl(value);
            if (url != "")
            {
                result.Add(new[] { "http", url });
                result.Add(new[] { "https", url });
            }
            return result;
        }
        foreach (var part in value.Split(';'))
        {
            int at = part.IndexOf('=');
            if (at <= 0) continue;
            string scheme = part.Substring(0, at).Trim().ToLowerInvariant();
            if (scheme != "http" && scheme != "https") continue;
            string url = NormalizeProxyUrl(part.Substring(at + 1));
            if (url == "") continue;
            result.Add(new[] { scheme, url });
        }
        return result;
    }

    // Only http(s) reach pnpm and the harness's own transport, so anything else
    // (socks, pac, a bare word) is reported as unusable instead of silently
    // routing direct. A scheme-less host:port is what the Windows dialog writes,
    // so it is given http://.
    internal static string NormalizeProxyUrl(string value)
    {
        if (value == null) return "";
        string text = value.Trim();
        if (text == "") return "";
        if (!Regex.IsMatch(text, "^[a-zA-Z][a-zA-Z0-9+.-]*://"))
        {
            text = "http://" + text;
        }
        Uri parsed;
        if (!Uri.TryCreate(text, UriKind.Absolute, out parsed)) return "";
        if (parsed.Scheme != "http" && parsed.Scheme != "https") return "";
        if (parsed.Host == "") return "";
        return parsed.GetLeftPart(UriPartial.Authority);
    }

    // Keep what the user wrote (Windows wildcards and CIDR forms are meaningful
    // to the plugin's matcher) and always keep loopback local: a proxy that
    // catches 127.0.0.1 breaks the desktop's own local traffic.
    internal static string NormalizeNoProxy(string bypass)
    {
        var entries = new List<string>();
        if (!String.IsNullOrEmpty(bypass))
        {
            foreach (var raw in bypass.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string entry = raw.Trim();
                if (entry == "") continue;
                if (entries.Contains(entry)) continue;
                entries.Add(entry);
            }
        }
        foreach (var local in new[] { "127.0.0.1", "localhost", "::1" })
        {
            bool found = false;
            foreach (var entry in entries) if (String.Equals(entry, local, StringComparison.OrdinalIgnoreCase)) found = true;
            if (!found) entries.Add(local);
        }
        return String.Join(",", entries.ToArray());
    }

    internal static Resolution FromEnvironment()
    {
        var resolution = new Resolution();
        resolution.Source = "env";
        string http = FirstEnv("http_proxy", "HTTP_PROXY");
        string https = FirstEnv("https_proxy", "HTTPS_PROXY");
        if (http == "" ) http = FirstEnv("all_proxy", "ALL_PROXY");
        if (https == "") https = http;
        resolution.HttpProxy = NormalizeProxyUrl(http);
        resolution.HttpsProxy = NormalizeProxyUrl(https);
        resolution.NoProxy = NormalizeNoProxy(FirstEnv("no_proxy", "NO_PROXY"));
        resolution.Mode = "system";
        return resolution;
    }

    private static string FirstEnv(string lower, string upper)
    {
        try
        {
            string value = Environment.GetEnvironmentVariable(lower);
            if (!String.IsNullOrEmpty(value) && value.Trim() != "") return value.Trim();
            value = Environment.GetEnvironmentVariable(upper);
            if (!String.IsNullOrEmpty(value) && value.Trim() != "") return value.Trim();
        }
        catch (Exception) { }
        return "";
    }

    internal static string ReadRegistryText()
    {
        var info = new ProcessStartInfo("reg.exe", "query \"HKCU\\" + RegistryKey + "\"");
        info.UseShellExecute = false;
        info.CreateNoWindow = true;
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;
        using (var process = new Process { StartInfo = info })
        {
            process.Start();
            string output = process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            if (!process.WaitForExit(5000))
            {
                try { process.Kill(); } catch (Exception) { }
                throw new TimeoutException("reg query 超时");
            }
            if (process.ExitCode != 0) throw new InvalidOperationException("reg query 退出码 " + process.ExitCode);
            return output;
        }
    }

    // ---- following the settings page --------------------------------------

    // The settings page saves atomically (temp file + rename), so the file is
    // replaced rather than rewritten in place; every notification type is watched
    // and coalesced.
    private void Watch()
    {
        if (homeDir == "" || !Directory.Exists(homeDir)) return;
        watcher = new FileSystemWatcher(homeDir, ConfigName);
        watcher.NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size;
        FileSystemEventHandler handler = delegate { ScheduleRefresh(); };
        RenamedEventHandler renamed = delegate { ScheduleRefresh(); };
        watcher.Created += handler;
        watcher.Changed += handler;
        watcher.Deleted += handler;
        watcher.Renamed += renamed;
        watcher.EnableRaisingEvents = true;
        Log("ProxyBridge: watching " + ConfigPath);
    }

    private void ScheduleRefresh()
    {
        if (disposed) return;
        try
        {
            if (debounce == null) debounce = new Timer(Refresh, null, Timeout.Infinite, Timeout.Infinite);
            debounce.Change(400, Timeout.Infinite);
        }
        catch (ObjectDisposedException) { }
    }

    private void Refresh(object state)
    {
        if (disposed) return;
        try
        {
            Current = Resolve(homeDir, ReadRegistryText, logSink);
            Log("ProxyBridge: changed -> " + Current.Summary);
            Log("ProxyBridge: npmrc " + SyncNpmrc(Current));
            if (Current.HasProxy)
                Log("ProxyBridge: 代理已写入 profile 的 .npmrc（下一次安装立即生效）；后端环境变量需重启桌面后生效。");
            else
                Log("ProxyBridge: 已清除 profile 的 .npmrc 代理行；后端环境变量需重启桌面后生效。");
        }
        catch (Exception error)
        {
            Log("ProxyBridge: refresh failed: " + error.Message);
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try { if (watcher != null) { watcher.EnableRaisingEvents = false; watcher.Dispose(); } }
        catch (Exception) { }
        watcher = null;
        try { if (debounce != null) { debounce.Dispose(); } }
        catch (Exception) { }
        debounce = null;
    }
}

// Self-test for the bridge. Runs without a window (--verify-proxy-bridge), so a
// test script can assert on data/desktop.log without opening anything.
internal static class ProxyBridgeSelfTest
{
    internal static int Run()
    {
        var failures = new List<string>();
        var passes = new List<string>();
        string root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", ".verify",
            "dsh-proxy-bridge-test-" + Guid.NewGuid().ToString("N"));
        string home = Path.Combine(root, "home");
        string profile = Path.Combine(home, "profiles", "web");
        Action<string> log = delegate(string message) { WriteLog(message); };
        try
        {
            Directory.CreateDirectory(profile);
            File.WriteAllText(Path.Combine(profile, "package.json"), "{}");
            // pnpm's hoisted workspace directory sits beside the profiles and has
            // its own package.json; it must never receive a .npmrc.
            string hoisted = Path.Combine(home, "profiles", "node_modules");
            Directory.CreateDirectory(hoisted);
            File.WriteAllText(Path.Combine(hoisted, "package.json"), "{}");
            string configPath = Path.Combine(home, ProxyBridge.ConfigName);
            Func<string> disabledRegistry = delegate { return "HKEY_CURRENT_USER\\Software\\Microsoft\\Windows\\CurrentVersion\\Internet Settings\r\n    ProxyEnable    REG_DWORD    0x0\r\n"; };
            Func<string> enabledRegistry = delegate { return "HKEY_CURRENT_USER\\Software\\Microsoft\\Windows\\CurrentVersion\\Internet Settings\r\n    ProxyEnable    REG_DWORD    0x1\r\n    ProxyServer    REG_SZ    http=127.0.0.1:10808;https=127.0.0.1:10808\r\n    ProxyOverride    REG_SZ    <local>;localhost;127.*;192.168.*;*.local.com\r\n"; };

            // 1. Missing document: the plugin's default mode, nothing to apply.
            var missing = ProxyBridge.Resolve(home, disabledRegistry, log);
            Check(passes, failures, "missing document falls back to system", missing.Mode == "system" && !missing.HasProxy);
            Check(passes, failures, "npmrc untouched without a proxy", NpmrcAction(profile, missing) == "clear" && !File.Exists(NpmrcPath(profile)));

            // 2. Custom proxy: written, and the environment carries it.
            WriteConfig(configPath, "custom", "http://127.0.0.1:10808", "<local>;localhost");
            var custom = ProxyBridge.Resolve(home, disabledRegistry, log);
            Check(passes, failures, "custom proxy resolves", custom.HasProxy && custom.HttpProxy == "http://127.0.0.1:10808");
            Check(passes, failures, "npmrc written for custom", NpmrcAction(profile, custom) == "written");
            Check(passes, failures, "hoisted node_modules is not a profile", !File.Exists(Path.Combine(home, "profiles", "node_modules", ".npmrc")));
            string npmrc = File.ReadAllText(NpmrcPath(profile));
            Check(passes, failures, "npmrc carries proxy and noproxy", npmrc.Contains("proxy=http://127.0.0.1:10808") && npmrc.Contains("noproxy=") && npmrc.Contains("127.0.0.1"));
            var info = new ProcessStartInfo();
            info.EnvironmentVariables.Clear();
            new ProxyBridge(home, log) { Current = custom }.ApplyEnvironment(info);
            Check(passes, failures, "environment carries the proxy",
                info.EnvironmentVariables["HTTPS_PROXY"] == "http://127.0.0.1:10808"
                && info.EnvironmentVariables["no_proxy"] != null);
            Check(passes, failures, "git rewrites GitHub SSH to HTTPS",
                info.EnvironmentVariables["GIT_CONFIG_COUNT"] == "4"
                && info.EnvironmentVariables["GIT_CONFIG_VALUE_0"] == "git@github.com:"
                && info.EnvironmentVariables["GIT_CONFIG_VALUE_1"] == "ssh://git@github.com/"
                && info.EnvironmentVariables["GIT_CONFIG_VALUE_3"] == "http://127.0.0.1:10808"
                && info.EnvironmentVariables["GIT_TERMINAL_PROMPT"] == "0");

            // 3. Idempotent: the second sync changes nothing on disk.
            string before = File.ReadAllText(NpmrcPath(profile));
            string again = NpmrcAction(profile, custom);
            Check(passes, failures, "second sync is a no-op", again == "unchanged" && File.ReadAllText(NpmrcPath(profile)) == before);

            // 4. User content survives both directions, byte for byte.
            string seed = "# my own note" + Environment.NewLine + "registry=https://registry.npmmirror.com" + Environment.NewLine;
            File.WriteAllText(NpmrcPath(profile), seed);
            NpmrcAction(profile, custom);
            string merged = File.ReadAllText(NpmrcPath(profile));
            Check(passes, failures, "user registry line kept", merged.Contains("registry=https://registry.npmmirror.com") && merged.Contains("proxy="));
            WriteConfig(configPath, "direct", "", "");
            var direct = ProxyBridge.Resolve(home, disabledRegistry, log);
            Check(passes, failures, "direct clears the block", NpmrcAction(profile, direct) == "cleared");
            Check(passes, failures, "user file restored exactly", File.ReadAllText(NpmrcPath(profile)) == seed);
            var cleared = new ProcessStartInfo();
            cleared.EnvironmentVariables.Clear();
            cleared.EnvironmentVariables["HTTPS_PROXY"] = "http://stale:1";
            cleared.EnvironmentVariables["GIT_CONFIG_COUNT"] = "4";
            new ProxyBridge(home, log) { Current = direct }.ApplyEnvironment(cleared);
            Check(passes, failures, "direct removes an inherited proxy",
                cleared.EnvironmentVariables["HTTPS_PROXY"] == null
                && cleared.EnvironmentVariables["GIT_CONFIG_COUNT"] == null);

            // 5. A file that held only our block is deleted, not emptied.
            File.WriteAllText(NpmrcPath(profile), ProxyBridge.MarkerStart + Environment.NewLine + "proxy=http://x:1" + Environment.NewLine + ProxyBridge.MarkerEnd + Environment.NewLine);
            NpmrcAction(profile, direct);
            Check(passes, failures, "our-only file removed", !File.Exists(NpmrcPath(profile)));

            // 6. An unusable custom value (SOCKS has no path through pnpm) is
            // treated as unset instead of being passed on.
            WriteConfig(configPath, "custom", "socks5://127.0.0.1:1080", "");
            var socks = ProxyBridge.Resolve(home, disabledRegistry, log);
            Check(passes, failures, "socks custom is refused", !socks.HasProxy && socks.Detail.IndexOf("unusable") >= 0);

            // 7. A broken document must not break startup.
            File.WriteAllText(configPath, "{not json");
            var broken = ProxyBridge.Resolve(home, disabledRegistry, log);
            Check(passes, failures, "malformed document falls back to system", broken.Mode == "system" && broken.Detail.IndexOf("unreadable") >= 0);

            // 8. Registry parsing matches what Windows writes.
            var view = ProxyBridge.InterpretRegistry(enabledRegistry());
            Check(passes, failures, "registry proxy parsed",
                view.Enabled && view.HttpProxy == "http://127.0.0.1:10808" && view.HttpsProxy == "http://127.0.0.1:10808");
            Check(passes, failures, "registry bypass normalized", view.NoProxy.Contains("192.168.*") && view.NoProxy.Contains("localhost") && view.NoProxy.Contains("127.0.0.1"));
            var none = ProxyBridge.InterpretRegistry("    ProxyEnable    REG_DWORD    0x0\r\n    ProxyServer    REG_SZ    127.0.0.1:7890\r\n");
            Check(passes, failures, "disabled system proxy yields nothing", !none.Enabled && !none.HttpProxy.EndsWith(":7890"));

            // 9. Stale locks: a dead pid with an old record is removed, a fresh
            // record or a live pid is left alone.
            string runPath = Path.Combine(profile, ".plugin-manager", "run.json");
            Directory.CreateDirectory(Path.GetDirectoryName(runPath));
            File.WriteAllText(runPath, "{\"pid\":999999,\"grouped\":false}");
            string lockPath = Path.Combine(profile, "package.json.lock");
            File.WriteAllText(lockPath, "999998");
            File.SetLastWriteTimeUtc(runPath, DateTime.UtcNow.AddMinutes(-30));
            File.SetLastWriteTimeUtc(lockPath, DateTime.UtcNow.AddMinutes(-30));
            ProxyBridge.CleanStaleLocks(home, log);
            Check(passes, failures, "stale locks removed", !File.Exists(runPath) && !File.Exists(lockPath));
            File.WriteAllText(lockPath, "999998");
            ProxyBridge.CleanStaleLocks(home, log);
            Check(passes, failures, "fresh lock kept", File.Exists(lockPath));
            File.WriteAllText(lockPath, Process.GetCurrentProcess().Id.ToString());
            File.SetLastWriteTimeUtc(lockPath, DateTime.UtcNow.AddMinutes(-30));
            ProxyBridge.CleanStaleLocks(home, log);
            Check(passes, failures, "live lock kept", File.Exists(lockPath));
            File.WriteAllText(lockPath, "not-a-pid");
            ProxyBridge.CleanStaleLocks(home, log);
            Check(passes, failures, "unparsed lock kept", File.Exists(lockPath));
        }
        catch (Exception error)
        {
            failures.Add("unexpected error: " + error);
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch (Exception) { }
        }
        foreach (var pass in passes) WriteLog("ProxyBridge verify PASS: " + pass);
        foreach (var fail in failures) WriteLog("ProxyBridge verify FAIL: " + fail);
        if (failures.Count == 0)
        {
            WriteLog("Proxy bridge verified (" + passes.Count + " checks)");
            return 0;
        }
        WriteLog("Proxy bridge verification failed: " + String.Join(" | ", failures.ToArray()));
        return 1;
    }

    private static void Check(List<string> passes, List<string> failures, string name, bool ok)
    {
        if (ok) passes.Add(name); else failures.Add(name);
    }

    private static string NpmrcPath(string profile) { return Path.Combine(profile, ".npmrc"); }

    private static string NpmrcAction(string profile, ProxyBridge.Resolution resolution)
    {
        var bridge = new ProxyBridge(Path.GetDirectoryName(Path.GetDirectoryName(profile)), delegate { })
        {
            Current = resolution
        };
        string summary = bridge.SyncNpmrc(resolution);
        int at = summary.IndexOf('=');
        return at < 0 ? summary : summary.Substring(at + 1);
    }

    private static void WriteConfig(string path, string mode, string proxy, string bypass)
    {
        var json = new JavaScriptSerializer().Serialize(new Dictionary<string, object>
        {
            { "version", 1 },
            { "mode", mode },
            { "custom", new Dictionary<string, object> { { "proxy", proxy }, { "bypass", bypass } } }
        });
        File.WriteAllText(path, json, new UTF8Encoding(false));
    }

    private static void WriteLog(string message)
    {
        try
        {
            string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "desktop.log"),
                DateTime.Now.ToString("s") + " " + message + Environment.NewLine);
        }
        catch (Exception) { }
    }
}
