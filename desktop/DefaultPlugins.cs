using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

internal sealed class RecommendedPlugin
{
    internal string Name = "";
    internal string Version = "";
    internal string Spec = "";
    internal string Source = "";
    internal string Category = "";
    internal string Title = "";
    internal string Description = "";
    internal string Url = "";
    internal bool Required;
}

// Reads defaults/plugins.json and runs the Node installer for the user's
// selection. Required plugins (market, proxy, network) are always included.
internal static class DefaultPlugins
{
    internal static bool Available(string root)
    {
        return File.Exists(Path.Combine(root, "defaults", "profile", "package.json")) &&
               File.Exists(Path.Combine(root, "defaults", "plugins.json"));
    }

    internal static List<RecommendedPlugin> Catalog(string root)
    {
        var result = new List<RecommendedPlugin>();
        try
        {
            string file = Path.Combine(root, "defaults", "plugins.json");
            if (!File.Exists(file)) return result;
            var serializer = new JavaScriptSerializer();
            var data = serializer.Deserialize<Dictionary<string, object>>(File.ReadAllText(file, Encoding.UTF8));
            object rows;
            if (data == null || !data.TryGetValue("plugins", out rows) || !(rows is IEnumerable)) return result;
            foreach (object item in (IEnumerable)rows)
            {
                var row = item as Dictionary<string, object>;
                if (row == null) continue;
                var plugin = new RecommendedPlugin();
                plugin.Name = Text(row, "name");
                plugin.Version = Text(row, "version");
                plugin.Spec = Text(row, "spec");
                plugin.Source = Text(row, "source");
                plugin.Category = Text(row, "category");
                plugin.Title = Text(row, "title");
                plugin.Description = Text(row, "description");
                plugin.Url = Text(row, "url");
                plugin.Required = Bool(row, "required");
                if (plugin.Name == "") continue;
                if (plugin.Title == "") plugin.Title = plugin.Name;
                if (plugin.Category == "") plugin.Category = plugin.Required ? "必需" : "其他";
                if (plugin.Spec == "") plugin.Spec = plugin.Version;
                result.Add(plugin);
            }
        }
        catch (Exception error)
        {
            RuntimeEnvironment.Log("Recommended plugins: catalog read failed: " + error.Message);
        }
        return result;
    }

    internal static bool Install(string root, string home, ProxyBridge bridge, Action<string> log, IList<string> selected)
    {
        try
        {
            string script = Path.Combine(root, "desktop", "install-plugins.cjs");
            string node = Path.Combine(root, "node", "node.exe");
            if (!File.Exists(script) || !File.Exists(node))
            {
                log("Recommended plugins: installer or node missing");
                return false;
            }
            string proxy = "";
            if (bridge != null && bridge.Current.HasProxy)
                proxy = bridge.Current.HttpsProxy != "" ? bridge.Current.HttpsProxy : bridge.Current.HttpProxy;
            bool mirror = RuntimeEnvironment.NetworkNeedsMirror(proxy, log);

            var names = new List<string>();
            if (selected != null) foreach (string name in selected) if (!String.IsNullOrEmpty(name)) names.Add(name);
            bool ok = RunInstaller(root, home, node, script, bridge, log, names, mirror);
            if (!ok && !mirror)
            {
                log("Recommended plugins: direct install failed, retrying through mirrors");
                ok = RunInstaller(root, home, node, script, bridge, log, names, true);
            }
            return ok;
        }
        catch (Exception error)
        {
            log("Recommended plugins: " + error.Message);
            return false;
        }
    }

    private static bool RunInstaller(string root, string home, string node, string script,
        ProxyBridge bridge, Action<string> log, List<string> names, bool mirror)
    {
        var info = new ProcessStartInfo(node);
        // A trailing backslash before a closing quote escapes that quote in the
        // Windows command line, so trim it from the root directory.
        string rootArg = root.TrimEnd('\\');
        string homeArg = home.TrimEnd('\\');
        string arguments = "\"" + script + "\" --root \"" + rootArg + "\" --home \"" + homeArg + "\" --plugins \"" +
            String.Join(",", names.ToArray()) + "\"";
        if (mirror) arguments += " --mirror";
        info.Arguments = arguments;
        info.WorkingDirectory = root;
        info.UseShellExecute = false;
        info.CreateNoWindow = true;
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;
        info.StandardOutputEncoding = Encoding.UTF8;
        info.StandardErrorEncoding = Encoding.UTF8;
        string path = Path.Combine(root, "node") + ";" +
                      Path.Combine(root, "desktop", "plugin-tools", "node_modules", ".bin") + ";" +
                      Path.Combine(root, "git", "cmd") + ";" +
                      Environment.GetEnvironmentVariable("PATH");
        info.EnvironmentVariables["PATH"] = path;
        info.EnvironmentVariables["DSH_HOME"] = home;
        info.EnvironmentVariables["npm_config_cache"] = Path.Combine(home, "npm-cache");
        info.EnvironmentVariables["NO_COLOR"] = "1";
        info.EnvironmentVariables["GIT_TERMINAL_PROMPT"] = "0";
        if (bridge != null)
        {
            bridge.ApplyEnvironment(info);
            string bridgeProxy = info.EnvironmentVariables["HTTPS_PROXY"];
            if (String.IsNullOrEmpty(bridgeProxy)) bridgeProxy = info.EnvironmentVariables["HTTP_PROXY"];
            if (!String.IsNullOrEmpty(bridgeProxy))
            {
                // pnpm ignores HTTP_PROXY; npm config variables do reach it.
                info.EnvironmentVariables["npm_config_proxy"] = bridgeProxy;
                info.EnvironmentVariables["npm_config_https_proxy"] = bridgeProxy;
            }
        }
        log("Recommended plugins: requested " + names.Count + " plugin(s) (" + (mirror ? "mirror" : "direct") + " network)");
        log("Recommended plugins: command " + arguments);
        using (var process = new Process { StartInfo = info })
        {
            process.OutputDataReceived += delegate(object sender, DataReceivedEventArgs e)
            {
                if (e.Data != null) log("plugins: " + e.Data);
            };
            process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e)
            {
                if (e.Data != null) log("plugins: " + e.Data);
            };
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            if (!process.WaitForExit(60 * 60 * 1000))
            {
                try { process.Kill(); } catch (Exception) { }
                log("Recommended plugins: timed out");
                return false;
            }
            process.WaitForExit();
            log("Recommended plugins: exit " + process.ExitCode);
            return process.ExitCode == 0;
        }
    }

    private static string Text(Dictionary<string, object> row, string key)
    {
        object value;
        return row.TryGetValue(key, out value) && value != null ? Convert.ToString(value) : "";
    }

    private static bool Bool(Dictionary<string, object> row, string key)
    {
        object value;
        if (!row.TryGetValue(key, out value) || value == null) return false;
        if (value is bool) return (bool)value;
        return String.Equals(Convert.ToString(value), "true", StringComparison.OrdinalIgnoreCase);
    }
}
