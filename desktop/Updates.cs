using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

internal static class Updates
{
    internal static readonly string Root = AppDomain.CurrentDomain.BaseDirectory;
    internal static string Json(object data) { return new JavaScriptSerializer().Serialize(data); }
    internal static string Value(Dictionary<string, object> row, string key)
    { return row.ContainsKey(key) && row[key] != null ? row[key].ToString() : ""; }

    internal static string ReadFile(string relative)
    {
        try { return File.ReadAllText(Path.Combine(Root, relative)); }
        catch (IOException) { return ""; }
        catch (UnauthorizedAccessException) { return ""; }
    }

    internal static void SaveTheme(string json)
    {
        // One cache, one validator: skins are captured by the DSH window and
        // re-sampled by every secondary window through SkinTheme.
        SkinTheme.Save(json);
    }

    private static void WriteState(string name, string json)
    {
        string target = Path.Combine(Root, "data", name), temp = target + ".tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(target));
        File.WriteAllText(temp, json, Encoding.UTF8);
        if (File.Exists(target)) File.Replace(temp, target, null);
        else File.Move(temp, target);
    }

    internal static void WriteRuntime(string version)
    {
        using (var process = Process.GetCurrentProcess())
            WriteState("desktop-runtime.json", Json(new { pid = process.Id,
                started = process.StartTime.ToUniversalTime().Ticks.ToString(), version = version }));
    }

    internal static string RunningVersion()
    {
        try {
            var row = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(ReadFile("data/desktop-runtime.json"));
            using (var process = Process.GetProcessById(Convert.ToInt32(row["pid"]))) {
                if (process.StartTime.ToUniversalTime().Ticks.ToString() == Value(row, "started") &&
                    String.Equals(process.MainModule.FileName, Path.Combine(Root, "DeepSeek Harness.exe"), StringComparison.OrdinalIgnoreCase))
                    return Value(row, "version");
            }
        } catch (Exception) { /* Closed process or a stale record is not a live version. */ }
        return "";
    }

    internal static Task<Dictionary<string, object>> Run(string command, Action<string> progress = null)
    {
        return Task.Run(() =>
        {
            var info = new ProcessStartInfo(Path.Combine(Root, "node", "node.exe"),
                "\"" + Path.Combine(Root, "desktop", "update-worker.cjs") + "\" " + command);
            info.WorkingDirectory = Root;
            info.UseShellExecute = false;
            info.CreateNoWindow = true;
            info.RedirectStandardOutput = true;
            info.RedirectStandardError = true;
            info.StandardOutputEncoding = Encoding.UTF8;
            info.StandardErrorEncoding = Encoding.UTF8;
            using (var process = new Process { StartInfo = info })
            {
                process.Start();
                var stderr = process.StandardError.ReadToEndAsync();
                Dictionary<string, object> result = null;
                string failure = null, line;
                while ((line = process.StandardOutput.ReadLine()) != null)
                {
                    var row = new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024 }.Deserialize<Dictionary<string, object>>(line);
                    if (row.ContainsKey("progress") && progress != null) progress(Value(row, "progress"));
                    if (row.ContainsKey("result")) result = row["result"] as Dictionary<string, object>;
                    if (row.ContainsKey("error")) failure = Value(row, "error");
                }
                process.WaitForExit();
                if (process.ExitCode != 0 || result == null)
                    throw new Exception(failure ?? "版本服务未完成，请检查网络或 versions/install.log。");
                return result;
            }
        });
    }
}
