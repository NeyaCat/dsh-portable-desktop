using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

internal static class Program
{
    internal static readonly string Root = AppDomain.CurrentDomain.BaseDirectory;
    [STAThread]
    private static void Main(string[] args)
    {
        // Wait before acquiring the single-instance mutex, otherwise a restart
        // would merely activate the closing window and lose the launch.
        if (args.Length == 2 && args[0] == "--restart-after")
        {
            int previous;
            if (!Int32.TryParse(args[1], out previous)) return;
            try { using (var process = Process.GetProcessById(previous))
                if (!process.WaitForExit(30000)) return; }
            catch (ArgumentException) {}
        }
        if (Array.IndexOf(args,"--verify-exit-settings") >= 0)
        {
            string verify = Path.Combine(Root, "data", ".verify");
            Directory.CreateDirectory(verify);
            DesktopPreferences.FilePath = Path.Combine(verify, "dsh-preferences-test-" + Guid.NewGuid().ToString("N") + ".json");
        }
        // Headless self-test of the proxy bridge: no window and no single-instance
        // mutex, an exit code of 0/1, and PASS/FAIL lines in data/desktop.log.
        if (Array.IndexOf(args, "--verify-proxy-bridge") >= 0)
        {
            Environment.ExitCode = ProxyBridgeSelfTest.Run();
            return;
        }
        if (Array.IndexOf(args, "--verify-environment") >= 0)
        {
            Environment.ExitCode = EnvironmentCheck.Verify();
            return;
        }
        if (Array.IndexOf(args, "--repair-environment") >= 0)
        {
            Environment.ExitCode = EnvironmentCheck.RepairBundled();
            return;
        }
        if (Array.IndexOf(args, "--preview-plugin-picker") >= 0)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            var catalog = DefaultPlugins.Catalog(Root);
            foreach (var plugin in catalog)
                RuntimeEnvironment.Log("Picker preview: " + plugin.Name + " url=" + plugin.Url + " title=" + plugin.Title);
            using (var picker = new PluginPicker(catalog))
                picker.ShowDialog();
            return;
        }
        Run();
    }

    private static void Run()
    {
        // Each portable folder owns one instance in the current Windows session.
        string key;
        using (var hash = SHA256.Create())
            key = BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(
                Path.GetFullPath(Root).TrimEnd('\\').ToUpperInvariant()))).Replace("-", "");
        bool owner;
        using (var mutex = new Mutex(true, @"Local\DSH.Desktop." + key, out owner))
        using (var activate = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\DSH.Activate." + key))
        {
            if (!owner)
            {
                Native.AllowSetForegroundWindow(-1);
                activate.Set();
                return;
            }
            try
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                // Register before environment repair or plugin installation so an old cleanup worker yields.
                Updates.WriteRuntime("");
                if (!EnvironmentCheck.EnsureReady()) return;
                using (var window = new DesktopWindow())
                {
                    // Auto-reset event retains activation requests received during startup.
                    window.Shown += delegate
                    {
                        var listener = new Thread(delegate()
                        {
                            while (activate.WaitOne())
                            {
                                try { window.BeginInvoke((Action)window.ActivateExisting); }
                                catch (InvalidOperationException) { return; }
                            }
                        });
                        listener.IsBackground = true;
                        listener.Start();
                    };
                    Application.Run(window);
                }
            }
            catch (Exception error)
            {
                MessageBox.Show(error.Message, "DSH Desktop", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally { mutex.ReleaseMutex(); }
        }
    }
}

internal sealed class DesktopWindow : Form
{
    private readonly WebView2 web = new WebView2();
    private readonly System.Collections.Generic.List<WebView2> popupRelays = new System.Collections.Generic.List<WebView2>();
    private bool browserRelayVerified;
    private readonly LoadingSurface status = new LoadingSurface();
    private TaskCompletionSource<string> address = new TaskCompletionSource<string>();
    private Process backend;
    private IntPtr job;
    private bool closing;
    private Uri origin;
    private readonly object logLock = new object();
    private readonly ContextMenuStrip iconMenu = new ContextMenuStrip();
    private NotifyIcon tray;
    private ContextMenuStrip trayMenu;
    private Icon trayIcon;
    private bool trayMinimized;
    private bool forceExit;
    private string runtimeRoot = Program.Root;
    private string environmentHome = Path.Combine(Program.Root,"data"), environmentId = "";
    // Carries the network mode chosen in the dsh-network-config settings page to
    // the backend environment and to each profile's .npmrc.
    private ProxyBridge proxyBridge;
    private bool environmentTrial, checkingEnvironment;
    // A rejected candidate is terminal for this window: without this flag the
    // 90s watchdog and any later ui-ready message would overwrite the specific
    // reason with a generic timeout.
    private bool environmentFailed;
    // Activation states observed in the last good run, used as the baseline a
    // candidate must not regress from. Absolute "everything must be live" is
    // unsatisfiable on real profiles: some plugins never activate even in a
    // healthy environment (a source checkout without built artifacts).
    private const string EnvironmentBaseline = "data/environment-baseline.json";
    private object expectedPlugins = new string[0];
    // Plugins the running environment expects; the source of the baseline record.
    private object environmentPlugins = new string[0];
    private string environmentSummary = "";
    private string environmentWarning = "";
    private string runtimeVersion = "";
    private string offeredVersion;
    private bool updateChecked, installingUpdate, restarting;
    private bool backendRestartRequested, backendWasReady, uiReady, pluginNoticeShown;
    private string previousVersion = "";
    private int lifecycleStep, verificationBackend;
    private IntPtr verificationWindow;

    private async Task VerifyExitSettings()
    {
        int dialogs=0;
        var handled=new System.Collections.Generic.List<CloseConfirmation>();
        try {
            await Task.Delay(4000);
            offeredVersion="9.9.9-test";
            UpdateToast("available","更新提示可读性验证");
            await Task.Delay(200);
            string toast = await web.CoreWebView2.ExecuteScriptAsync(@"JSON.stringify({blur:getComputedStyle(document.getElementById('dsh-update-toast')).backdropFilter,ignore:!document.getElementById('dsh-update-ignore').hidden,manager:[...document.querySelectorAll('#dsh-update-toast button')].some(b=>b.textContent==='版本管理')})");
            Log("Toast verification: "+toast);
            using(var image=File.Create(Path.Combine(Program.Root,"desktop","toast-preview.png")))
                await web.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png,image);
            await web.CoreWebView2.ExecuteScriptAsync("document.getElementById('dsh-update-ignore').click()");
            await Task.Delay(200);
            if(!DesktopPreferences.IsIgnored("9.9.9-test") || DesktopPreferences.IsIgnored("9.9.10-test")) throw new Exception("Ignore persistence failed");
            Log("Preferences file: "+DesktopPreferences.FilePath);
            using(var driver=new System.Windows.Forms.Timer {Interval=200}) {
                driver.Tick += delegate {
                    foreach(Form form in Application.OpenForms) {
                        var prompt=form as CloseConfirmation;
                        if(prompt==null || handled.Contains(prompt)) continue;
                        handled.Add(prompt);
                        dialogs++;
                        try {
                            // Capture the real composition (DWM caption included), not
                            // a synthetic DrawToBitmap that would show a white frame.
                            Native.SetWindowPos(prompt.Handle,new IntPtr(-1),0,0,0,0,0x0043);
                            prompt.Refresh(); Application.DoEvents(); Thread.Sleep(150); Application.DoEvents();
                            using(var bitmap=new Bitmap(prompt.Bounds.Width,prompt.Bounds.Height))
                            using(var graphics=Graphics.FromImage(bitmap)) {
                                graphics.CopyFromScreen(prompt.Bounds.Location,Point.Empty,prompt.Bounds.Size);
                                bitmap.Save(Path.Combine(Program.Root,"desktop","exit-preview.png"));
                            }
                            Native.SetWindowPos(prompt.Handle,new IntPtr(-2),0,0,0,0,0x0043);
                            if(dialogs==1) {
                                if(prompt.Exit.Text!="退出 DSH" || prompt.Minimize.Text!="最小化到托盘") throw new Exception("Close prompt actions mismatch");
                                Dictionary<string,string> promptSkin=prompt.Skin;
                                if(prompt.BackColor!=SkinTheme.ToColor(promptSkin,"bg",Color.Empty)) throw new Exception("Close prompt background is not the skin background");
                                if(prompt.Font.Name!=SkinTheme.FontFamily(promptSkin)) throw new Exception("Close prompt font is not the skin font");
                                if(prompt.SurfaceColor!=SkinTheme.ToColor(promptSkin,"surface",Color.Empty)) throw new Exception("Close prompt surface is not the skin surface");
                                if(prompt.Minimize.BorderColor==prompt.BackColor || prompt.Exit.BorderColor==prompt.BackColor) throw new Exception("Close prompt buttons are not skinned");
                                if(!prompt.Exit.Danger || !prompt.Minimize.Primary) throw new Exception("Close prompt action roles changed");
                                int dark;
                                if(Native.DwmGetWindowAttribute(prompt.Handle,20,out dark,4)==0 &&
                                    dark!=(promptSkin.ContainsKey("scheme") && promptSkin["scheme"]=="light" ? 0 : 1))
                                    throw new Exception("Close prompt DWM frame is not the skin scheme");
                                // Title-bar close/Esc keeps the window and backend alive.
                                prompt.DialogResult=DialogResult.Cancel;
                            } else if(dialogs==2) {
                                prompt.Remember.Checked=true;
                                prompt.Minimize.PerformClick();
                            } else {
                                prompt.Remember.Checked=true;
                                Log("PASS: close dialog cancel keeps backend, minimize hides window with tray icon, remembered minimize skips the prompt, preferences reset, remembered exit terminates");
                                prompt.Exit.PerformClick();
                            }
                        } catch(Exception error) { Log("Close prompt verification failed: "+error.Message); forceExit=true; Close(); }
                        return;
                    }
                };
                driver.Start();
                Close();
                if(closing || !Visible) throw new Exception("Cancel did not keep the window open");
                if(backend==null || backend.HasExited) throw new Exception("Cancel stopped the backend");
                Close();
                if(!trayMinimized || Visible) throw new Exception("Minimize did not hide the window");
                if(backend==null || backend.HasExited) throw new Exception("Minimize stopped the backend");
                if(DesktopPreferences.CloseAction!="minimize") throw new Exception("Remembered minimize was not persisted");
                if(tray==null || !tray.Visible || String.IsNullOrEmpty(tray.Text)) throw new Exception("Tray icon is not visible");
                if(trayMenu==null || trayMenu.Items.Count<4 ||
                    trayMenu.Items[0].Text!="打开 DeepSeek Harness" ||
                    trayMenu.Items[trayMenu.Items.Count-1].Text!="退出 DSH")
                    throw new Exception("Tray context menu is incomplete");
                Dictionary<string,string> menuSkin=SkinTheme.Load();
                if(!(trayMenu.Renderer is SkinMenuRenderer)) throw new Exception("Tray menu is not skinned");
                if(trayMenu.BackColor!=SkinTheme.ToColor(menuSkin,"surface",Color.Empty)) throw new Exception("Tray menu background is not the skin surface");
                if(trayMenu.Font.Name!=SkinTheme.FontFamily(menuSkin)) throw new Exception("Tray menu font is not the skin font");
                trayMenu.Show(new Point(120,120));
                Application.DoEvents(); Thread.Sleep(150); Application.DoEvents();
                using(var bitmap=new Bitmap(trayMenu.Bounds.Width,trayMenu.Bounds.Height))
                using(var graphics=Graphics.FromImage(bitmap)) {
                    graphics.CopyFromScreen(trayMenu.Bounds.Location,Point.Empty,trayMenu.Bounds.Size);
                    bitmap.Save(Path.Combine(Program.Root,"desktop","menu-preview.png"));
                }
                trayMenu.Close();
                bool white=TrayIconFactory.TaskbarUsesDarkTheme();
                using(var bitmap=TrayIconFactory.CreateBitmap(Icon,32))
                    if(!MatchesTrayTone(bitmap,white)) throw new Exception("Tray icon tone is not "+(white?"white":"black"));
                Log("Tray icon tone="+(white?"white":"black")+"; menu items="+trayMenu.Items.Count);
                ((ToolStripMenuItem)trayMenu.Items[0]).PerformClick();
                if(trayMinimized || !Visible) throw new Exception("Tray menu did not restore the window");
                Close();
                if(!trayMinimized || Visible) throw new Exception("Remembered minimize still prompted");
                RestoreFromTray();
                if(dialogs!=2) throw new Exception("A remembered decision asked again");
                DesktopPreferences.SaveCloseAction(null);
                if(DesktopPreferences.CloseAction!="") throw new Exception("Close preference reset failed");
                Close();
            }
            if(!closing) throw new Exception("Exit prompt did not close the window");
        } catch(Exception error) { Log("Exit verification failed: "+error.Message);forceExit=true;Close(); }
    }

    private static bool MatchesTrayTone(Bitmap bitmap, bool white)
    {
        bool opaque=false;
        for(int y=0;y<bitmap.Height;y++)
            for(int x=0;x<bitmap.Width;x++)
            {
                Color pixel=bitmap.GetPixel(x,y);
                if(pixel.A<96) continue;
                opaque=true;
                if(white) { if(pixel.R<220 || pixel.G<220 || pixel.B<220) return false; }
                else if(pixel.R>32 || pixel.G>32 || pixel.B>32) return false;
            }
        return opaque;
    }

    private bool exitPromptOpen, backendEnded;
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        Log("FormClosing reason=" + e.CloseReason + " closing=" + closing + " restarting=" + restarting +
            " backendEnded=" + backendEnded + " forceExit=" + forceExit);
        if (!closing && !restarting && !backendEnded && !forceExit && e.CloseReason == CloseReason.UserClosing)
        {
            if (exitPromptOpen) { e.Cancel = true; return; }
            string action;
            exitPromptOpen = true;
            try { action = ResolveCloseAction(); }
            finally { exitPromptOpen = false; }
            if (action == "minimize") { e.Cancel = true; MinimizeToTray(); return; }
            if (action != "exit") { e.Cancel = true; Log("Close cancelled"); return; }
        }
        closing = true;
        selectionTimer.Stop();
        RemoveTrayIcon();
        StopBackend();
        base.OnFormClosing(e);
    }

    // Ask once, then act on the remembered choice. The legacy
    // skipExitConfirmation switch is intentionally ignored: it described the old
    // cancel/force-quit prompt and must not silently bypass this dialog.
    private string ResolveCloseAction()
    {
        string remembered = DesktopPreferences.CloseAction;
        if (remembered == "exit" || remembered == "minimize")
        {
            Log("Close request: " + remembered + " (remembered)");
            RecordCloseAction(remembered, true);
            return remembered;
        }
        Dictionary<string,string> skin = SkinTheme.Load();
        using(var prompt = new CloseConfirmation(skin))
        {
            DialogResult result = prompt.ShowDialog(this);
            string action = result == DialogResult.OK ? "exit" : result == DialogResult.Retry ? "minimize" : "";
            if (action == "") return "";
            bool remember = prompt.Remember.Checked;
            Log("Close request: " + action + (remember ? " (remembered)" : " (once)"));
            if (remember)
            {
                try { DesktopPreferences.SaveCloseAction(action); }
                catch(Exception error) { Log("Close preference could not be saved: "+error.Message); }
            }
            RecordCloseAction(action, remember);
            return action;
        }
    }

    private void RecordCloseAction(string action, bool remembered)
    {
        try { DesktopPreferences.RecordCloseAction(action,remembered); }
        catch(Exception error) { Log("Close history could not be saved: "+error.Message); }
    }

    // A remembered choice must stay reversible from the always-available icon
    // menu, otherwise "exit" would remove every route back to this dialog.
    private void ResetClosePreference()
    {
        try
        {
            DesktopPreferences.SaveCloseAction(null);
            Log("Close preference reset; next close asks again");
            UpdateToast("info", "已清除记住的关闭操作，下次点击 × 时会重新询问。");
        }
        catch(Exception error) { Log("Close preference could not be reset: "+error.Message); }
    }

    private void MinimizeToTray()
    {
        if (trayMinimized || closing) return;
        if (!EnsureTrayIcon())
        {
            Log("Close: tray icon could not be created; window kept visible");
            MessageBox.Show(this,"无法创建系统托盘图标，窗口将保持打开。","最小化到托盘",MessageBoxButtons.OK,MessageBoxIcon.Warning);
            return;
        }
        trayMinimized = true;
        Hide();
        Log("Close: minimized to tray");
    }

    private void MinimizeFromMenu()
    {
        if (trayMinimized) return;
        Log("Close: minimize requested from icon menu");
        RecordCloseAction("minimize",false);
        MinimizeToTray();
    }

    private bool EnsureTrayIcon()
    {
        if (tray != null) return true;
        try
        {
            trayMenu = new ContextMenuStrip();
            var open = new ToolStripMenuItem("打开 DeepSeek Harness");
            open.Click += delegate { RestoreFromTray(); };
            trayMenu.Items.Add(open);
            trayMenu.Items.Add("新会话", null, async delegate { RestoreFromTray(); await NewSession(); });
            trayMenu.Items.Add("版本管理", null, delegate { OpenVersionSwitch(); });
            trayMenu.Items.Add(new ToolStripSeparator());
            trayMenu.Items.Add("退出 DSH", null, delegate { ExitFromTray(); });
            SkinMenuRenderer.Style(trayMenu,true);
            trayMenu.Opening += delegate { SkinMenuRenderer.Style(trayMenu,true); };
            trayIcon = TrayIconFactory.Create(Icon,32);
            tray = new NotifyIcon { Text="DeepSeek Harness（单击打开）", Icon=trayIcon,
                ContextMenuStrip=trayMenu, Visible=true };
            tray.DoubleClick += delegate { RestoreFromTray(); };
            tray.MouseClick += delegate(object sender,MouseEventArgs args) {
                if (args.Button == MouseButtons.Left) RestoreFromTray();
            };
            Log("Tray icon created");
            return true;
        }
        catch(Exception error)
        {
            Log("Tray icon could not be created: "+error.Message);
            RemoveTrayIcon();
            return false;
        }
    }

    private void RestoreFromTray()
    {
        if (closing) return;
        RemoveTrayIcon();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Show();
        Activate();
        Native.SetForegroundWindow(Handle);
        Log("Restored from tray");
    }

    private void RemoveTrayIcon()
    {
        var oldTray=tray; tray=null;
        var oldMenu=trayMenu; trayMenu=null;
        var oldIcon=trayIcon; trayIcon=null;
        trayMinimized=false;
        if(oldTray==null && oldMenu==null && oldIcon==null) return;
        if(oldTray!=null) { try { oldTray.Visible=false; } catch(Exception error) { Log("Tray icon cleanup: "+error.Message); } }
        // This can run from the tray menu's own click handler, so the controls
        // are disposed after the current menu message has finished.
        Action dispose=delegate {
            if(oldTray!=null) { try { oldTray.Dispose(); } catch(Exception) { } }
            if(oldMenu!=null) { try { oldMenu.Dispose(); } catch(Exception) { } }
            if(oldIcon!=null) { try { oldIcon.Dispose(); } catch(Exception) { } }
        };
        if(closing || IsDisposed || !IsHandleCreated) dispose();
        else { try { BeginInvoke(dispose); } catch(InvalidOperationException) { dispose(); } }
    }

    private void ExitFromTray()
    {
        Log("Close: exit requested from tray menu");
        RecordCloseAction("exit",false);
        forceExit=true;
        Close();
    }

    private async Task NewSession()
    {
        try { if (web.CoreWebView2 != null)
            await web.CoreWebView2.ExecuteScriptAsync("document.querySelector('[class*=\"_newSession\"]')?.click()"); }
        catch(Exception error) { Log("New session command failed: "+error.Message); }
    }

    private void ShowLoading(string message)
    {
        status.ApplyTheme(); BackColor = status.BackColor;
        web.DefaultBackgroundColor = BackColor;
        status.Failed = false; status.Text = message; status.Visible = true; status.BringToFront();
    }

    private void ApplicationReady()
    {
        if (uiReady || environmentFailed) return;
        if (environmentTrial && !checkingEnvironment) { VerifyEnvironment(); return; }
        if (environmentTrial) return;
        uiReady = true; status.Visible = false;
        Log("Application mounted");
        if(environmentWarning!="") { UpdateToast("error",environmentWarning);environmentWarning=""; }
        if (!pluginNoticeShown && previousVersion != "" && previousVersion != runtimeVersion)
        {
            pluginNoticeShown = true;
            Log("Plugin update reminder: " + previousVersion + " -> " + runtimeVersion);
            web.CoreWebView2.PostWebMessageAsJson(Updates.Json(new { type = "plugin-reminder",
                text = "DSH 已从 " + previousVersion + " 切换到 " + runtimeVersion + "。" + environmentSummary + " 可在版本管理查看插件状态与恢复点。" }));
        }
        try { File.WriteAllText(Path.Combine(Program.Root,"data","desktop-last-version.txt"),runtimeVersion); }
        catch (IOException error) { Log(error.Message); }
        RecordEnvironmentBaseline();
    }

    // The next switch compares its candidate against these states, so a plugin
    // that is already broken here cannot block an otherwise healthy candidate.
    private async void RecordEnvironmentBaseline()
    {
        await Task.Delay(5000);
        if (environmentTrial || environmentFailed || closing || web.CoreWebView2 == null) return;
        try { await ProbePluginStates("environment-baseline", environmentPlugins); }
        catch (Exception error) { Log("Plugin baseline probe failed: " + error.Message); }
    }

    private async void VerifyEnvironment()
    {
        if (environmentFailed) return;
        checkingEnvironment = true;
        try { await ProbePluginStates("environment-health", expectedPlugins); }
        catch (Exception error) {
            environmentFailed = true; StopBackend();
            Fail("新环境验证未完成：" + error.Message + "。请通过版本管理恢复上一环境。");
        }
    }

    // A mounted page shell is insufficient, but "every plugin must be live" is not
    // the honest test either: real profiles carry plugins that never activate even
    // in a healthy environment (a source checkout without built artifacts). Report
    // the observed states and let the caller compare against the last good run.
    private async Task ProbePluginStates(string channel, object names)
    {
        string script = "(async()=>{try{const expected=" + Updates.Json(names) + ";let data=null;"
            + "for(let attempt=0;attempt<3;attempt++){const r=await fetch('/dsh-market/installed');if(!r.ok)throw Error('插件状态接口不可用');data=await r.json();"
            + "const a=data&&data.activation;if(!a||typeof a!=='object')throw Error('插件状态格式不可用');"
            + "if(expected.every(n=>a[n]))break;await new Promise(d=>setTimeout(d,1500));}"
            + "const activation=(data&&data.activation)||{},states={};"
            + "for(const n of expected){const a=activation[n];states[n]=a?{state:String(a.state||'unknown'),reasons:(Array.isArray(a.reasons)?a.reasons:[]).map(String).slice(0,3)}:{state:'unknown',reasons:[]};}"
            + "chrome.webview.postMessage('desktop:" + channel + ":'+JSON.stringify({states}));"
            + "}catch(e){chrome.webview.postMessage('desktop:" + channel + ":'+JSON.stringify({error:String((e&&e.message)||e)}));}})()";
        await web.CoreWebView2.ExecuteScriptAsync(script);
    }

    private sealed class PluginObservation
    {
        internal string State = "unknown";
        internal string Reason = "";
    }

    private sealed class HealthVerdict
    {
        internal int Count;
        internal string Summary = "";
        internal string Detail = "";
    }

    private static HealthVerdict Failed(string detail)
    {
        return new HealthVerdict { Count = 1, Summary = detail, Detail = detail };
    }

    private static Dictionary<string, PluginObservation> ReadObservations(object value)
    {
        var result = new Dictionary<string, PluginObservation>(StringComparer.OrdinalIgnoreCase);
        var rows = value as Dictionary<string, object>;
        if (rows == null) return result;
        foreach (var row in rows)
        {
            var entry = new PluginObservation();
            var detail = row.Value as Dictionary<string, object>;
            if (detail != null && detail.ContainsKey("state") && detail["state"] != null)
            {
                entry.State = detail["state"].ToString();
                var reasons = detail.ContainsKey("reasons") ? detail["reasons"] as object[] : null;
                if (reasons != null)
                    foreach (var reason in reasons)
                        if (reason != null) { entry.Reason = reason.ToString(); break; }
            }
            else if (row.Value != null) entry.State = row.Value.ToString();
            result[row.Key] = entry;
        }
        return result;
    }

    private Dictionary<string, PluginObservation> ReadEnvironmentBaseline()
    {
        var empty = new Dictionary<string, PluginObservation>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var row = new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<Dictionary<string, object>>(Updates.ReadFile(EnvironmentBaseline));
            if (row == null) return empty;
            // A record written by this very candidate says nothing about a regression.
            if (Updates.Value(row, "environmentId") == environmentId) return empty;
            return row.ContainsKey("states") ? ReadObservations(row["states"]) : empty;
        }
        catch (Exception) { return empty; }
    }

    private static bool IsHardPluginFailure(string state)
    {
        return state == "broken" || state == "missing" || state == "incompatible";
    }

    // "" when the candidate is no worse than the last good run. An unknown baseline
    // never blocks: there is nothing to regress from, and the backend plus UI mount
    // are already verified by the time this runs.
    private HealthVerdict EnvironmentRegressions(Dictionary<string, PluginObservation> candidate)
    {
        var verdict = new HealthVerdict();
        var baseline = ReadEnvironmentBaseline();
        var regressions = new List<string>();
        var observed = new List<string>();
        foreach (var row in candidate)
        {
            PluginObservation was;
            bool known = baseline.TryGetValue(row.Key, out was) && was.State != "";
            string state = row.Value.State;
            bool regressed = state != "live" && known &&
                (was.State == "live" || (IsHardPluginFailure(state) && !IsHardPluginFailure(was.State)));
            if (regressed)
                regressions.Add(row.Key + "=" + state + (was.State == "live" ? " (基线 live)" : " (基线 " + was.State + ")")
                    + (row.Value.Reason != "" ? "：" + row.Value.Reason : ""));
            observed.Add(row.Key + "=" + state);
        }
        Log("Candidate plugin states: " + String.Join("; ", observed.ToArray())
            + (baseline.Count == 0 ? " | 无基线，仅按启动与界面判定" : ""));
        if (regressions.Count == 0) return verdict;
        regressions.Sort(StringComparer.Ordinal);
        verdict.Count = regressions.Count;
        verdict.Detail = String.Join("；", regressions.ToArray());
        var summary = new List<string>();
        foreach (var entry in regressions)
        {
            if (summary.Count >= 3) break;
            summary.Add(entry.Split('：')[0]);
        }
        verdict.Summary = String.Join("；", summary.ToArray()) + (regressions.Count > 3 ? "；等 " + regressions.Count + " 项" : "");
        return verdict;
    }

    private readonly HashSet<string> failedStartupPlugins = new HashSet<string>();


    private bool repairingStartup;
    private int startupRepairCount;
    private readonly HashSet<string> suspendedStartupPlugins = new HashSet<string>();
    private async Task<bool> RepairStartupPlugins(IEnumerable<string> requested)
    {
        if(closing || uiReady || repairingStartup || startupRepairCount >= 3) return false;
        var known=DefaultPlugins.Catalog(Program.Root);
        var names=new List<string>();
        foreach(string name in requested)
            if(!suspendedStartupPlugins.Contains(name) && known.Exists(p=>p.Name==name)) names.Add(name);
        if(names.Count==0) return false;
        repairingStartup=true;startupRepairCount++;
        try {
            StopBackend();
            ShowLoading("正在暂停与当前版本不兼容的插件，保留配置并重新启动…");
            await Task.Run(() => DefaultPlugins.DisableFailed(Program.Root,environmentHome,runtimeRoot,String.Join(",",names.ToArray()),Log));
            foreach(string name in names) suspendedStartupPlugins.Add(name);
            var remaining=new List<object>();
            foreach(object name in (System.Collections.IEnumerable)expectedPlugins)
                if(!suspendedStartupPlugins.Contains(Convert.ToString(name))) remaining.Add(name);
            expectedPlugins=remaining.ToArray();
            environmentWarning="与当前 DSH 不兼容的推荐插件已暂停，原配置保留："+String.Join("、",new List<string>(suspendedStartupPlugins).ToArray())+"。";
            if(closing)return false;
            await LaunchBackendAsync();return true;
        } finally {repairingStartup=false;}
    }

    private async Task LaunchBackendAsync()
    {
        uiReady = false; backendWasReady = false;
        address = new TaskCompletionSource<string>();
        ShowLoading(backendRestartRequested ? "正在重新启动 DSH 后台…" : "正在启动 DSH 后台与插件…");
        StartBackend(environmentHome);
        var pending = address;
        var completed = await Task.WhenAny(pending.Task, Task.Delay(TimeSpan.FromMinutes(2)));
        if (closing) return;
        if (completed != pending.Task) throw new TimeoutException("后台启动超时，请查看 data/desktop.log。");
        string url = await pending.Task;
        string[] failures; lock(logLock) { failures=new List<string>(failedStartupPlugins).ToArray(); failedStartupPlugins.Clear(); }
        if(await RepairStartupPlugins(failures)) return;
        backendWasReady = true; backendRestartRequested = false;
        ShowLoading("正在加载 DSH 界面…");
        origin = new Uri(url);
        web.CoreWebView2.Navigate(url);
        if(environmentTrial) WatchEnvironment();
        WatchPageLoad(url);
    }

    private async void WatchEnvironment()
    {
        string candidate=environmentId;
        await Task.Delay(90000);
        // A specific rejection is the better message; never replace it with a timeout.
        if(!closing && !environmentFailed && environmentTrial && candidate==environmentId) {
            environmentFailed = true;
            StopBackend();
            Fail("新环境未在限定时间内完成界面和插件验证。数据已保留，请关闭窗口并在版本管理中恢复上一环境。");
        }
    }

    private async void BackendExited(Process exited)
    {
        if (closing || !Object.ReferenceEquals(backend, exited)) return;
        int code = exited.ExitCode;
        Log("Backend exited code=" + code + "; restart=" + backendRestartRequested);
        if (backendRestartRequested)
        {
            StopBackend();
            try { await LaunchBackendAsync(); }
            catch (Exception error) { StopBackend(); Fail("后台重启失败：" + error.Message); }
        }
        else if (backendWasReady) { backendEnded = true; Close(); }
        else address.TrySetException(new Exception("DSH 后台启动失败，退出码 " + code + "。请查看 data/desktop.log。"));
    }

    private readonly System.Windows.Forms.Timer selectionTimer = new System.Windows.Forms.Timer { Interval = 2000 };
    private string selectionStamp = "";

    private void Restart()
    {
        if (restarting || installingUpdate) return;
        try
        {
            restarting = true;
            Process.Start(new ProcessStartInfo(Application.ExecutablePath,
                "--restart-after " + Process.GetCurrentProcess().Id) { UseShellExecute = false });
            Close();
        }
        catch (Exception error) { restarting = false; UpdateToast("error", "重启失败：" + error.Message); }
    }


    private void UpdateToast(string state, string text)
    {
        if (closing || IsDisposed || !IsHandleCreated) return;
        if (InvokeRequired)
        {
            try { BeginInvoke((Action)(() => UpdateToast(state, text))); } catch (InvalidOperationException) {}
            return;
        }
        if (web.CoreWebView2 != null)
            web.CoreWebView2.PostWebMessageAsJson(Updates.Json(new { type = "update", state = state, text = text }));
    }

    private async Task CheckUpdates()
    {
        try
        {
            var data = await Updates.Run("catalog " + runtimeVersion);
            var selected = (System.Collections.Generic.Dictionary<string, object>)data["selected"];
            string next = Updates.Value(selected, "version");
            if (selected.ContainsKey("pendingTransaction"))
                UpdateToast("ready", "已准备 " + next + "，重启时保存恢复点并切换。当前环境继续运行。");
            else if ((bool)data["updateAvailable"])
            {
                offeredVersion = Updates.Value(data, "recommended");
                if (DesktopPreferences.IsIgnored(offeredVersion)) return;
                UpdateToast("available", "发现 DSH " + offeredVersion + "（当前 " + runtimeVersion + "）。是否后台安装？安装完成后下次启动生效。");
            }
        }
        catch (Exception error) { UpdateToast("error", "更新检查失败，不影响使用：" + error.Message); }
    }

    private async Task InstallUpdate()
    {
        if (installingUpdate || String.IsNullOrEmpty(offeredVersion)) return;
        installingUpdate = true;
        UpdateToast("installing", "正在后台安装 " + offeredVersion + "，可以继续使用当前窗口。");
        try
        {
            var plan = await Updates.Run("install " + offeredVersion, text => UpdateToast("installing", text));
            Process.Start(new ProcessStartInfo(Path.Combine(Program.Root,"Version Switch.exe"), "--plan " + Updates.Value(plan,"id")) { UseShellExecute = false });
            UpdateToast("review", "兼容性分析已完成，请在版本管理中确认插件及数据变更后准备更新。");
        }
        catch (Exception error) { UpdateToast("error", "更新未生效：" + error.Message); }
        finally { installingUpdate = false; }
    }

    internal DesktopWindow()
    {
        // Every launch starts from one documented skin baseline, then the live
        // DSH page recaptures it as soon as the application has mounted.
        SkinTheme.Ensure();
        Text = "DeepSeek Harness";
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        FormBorderStyle = FormBorderStyle.None;
        Size = new Size(1280, 900);
        MinimumSize = new Size(800, 600);
        StartPosition = FormStartPosition.CenterScreen;
        web.Dock = DockStyle.Fill;
        status.Dock = DockStyle.Fill;
        BackColor = status.BackColor; web.DefaultBackgroundColor = BackColor;
        status.Text = "正在启动 DeepSeek Harness…";
        
        Controls.Add(web);
        Controls.Add(status);
        Shown += async delegate {
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "--verify-lifecycle") >= 0) {
                using (var bitmap = new Bitmap(Width,Height)) { DrawToBitmap(bitmap,new Rectangle(Point.Empty,Size)); bitmap.Save(Path.Combine(Program.Root,"desktop","loading-preview.png")); }
                Log("Loading background=" + BackColor);
            }
            await StartAsync();
        };
        FormClosed += delegate { closing = true; DefaultPlugins.Cancel(); RemoveTrayIcon(); selectionTimer.Dispose(); StopBackend(); if (proxyBridge != null) { proxyBridge.Dispose(); proxyBridge = null; } foreach (var popup in popupRelays.ToArray()) popup.Dispose(); popupRelays.Clear(); web.Dispose(); StartCacheCleanup(); };
        selectionTimer.Tick += async delegate {
            if (!updateChecked || installingUpdate || closing) return;
            string stamp = Updates.ReadFile("environments/state.json");
            if (stamp == selectionStamp) return;
            selectionStamp = stamp;
            try {
                var selected = await Updates.Run("resolve");
                string next = Updates.Value(selected, "version");
                if (selected.ContainsKey("pendingTransaction")) UpdateToast("ready", next + " 已准备，重启时保存恢复点并整体切换。");
            } catch (Exception error) { Log("Selection check: " + error.Message); }
        };
        selectionStamp = Updates.ReadFile("environments/state.json");
        selectionTimer.Start();
        Resize += delegate { UpdateWindowEffects(); };
    }

    // Keep the standard resize/minimize/maximize styles for Windows shortcuts
    // and taskbar behavior, while rendering all visible chrome inside WebView.
    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            // WS_CAPTION is retained for DWM's native animations. NCCALCSIZE
            // removes its visible area; the WebView still paints the title bar.
            parameters.Style |= 0x00C00000 | 0x00040000 | 0x00020000 | 0x00010000 | 0x00080000;
            return parameters;
        }
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == 0x0083) // WM_NCCALCSIZE
        {
            // A maximized WS_THICKFRAME window extends its invisible resize
            // borders beyond the monitor. Keep WebView inside the visible area.
            if (Native.IsZoomed(Handle))
            {
                var rect = (Native.Rect)Marshal.PtrToStructure(message.LParam, typeof(Native.Rect));
                uint dpi = Native.GetDpiForWindow(Handle);
                int x = Native.GetSystemMetricsForDpi(32, dpi) + Native.GetSystemMetricsForDpi(92, dpi);
                int y = Native.GetSystemMetricsForDpi(33, dpi) + Native.GetSystemMetricsForDpi(92, dpi);
                rect.Left += x; rect.Right -= x;
                rect.Top += y; rect.Bottom -= y;
                Marshal.StructureToPtr(rect, message.LParam, false);
            }
            message.Result = IntPtr.Zero;
            return;
        }
        base.WndProc(ref message);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // Do not disable composition or use a clipping Region/layered window:
        // those defeat native corner rounding and minimize/maximize transitions.
        int enabled = 0;
        Native.DwmSetWindowAttribute(Handle, 3, ref enabled, 4); // transitions not force-disabled
        var margins = new Native.Margins { Left = 1, Right = 1, Top = 1, Bottom = 1 };
        Native.DwmExtendFrameIntoClientArea(Handle, ref margins);
        UpdateWindowEffects();
    }

    private void UpdateWindowEffects()
    {
        if (!IsHandleCreated || closing) return;
        int corner = WindowState == FormWindowState.Maximized ? 1 : 2;
        Native.DwmSetWindowAttribute(Handle, 33, ref corner, 4);
        if (web.CoreWebView2 != null)
            web.CoreWebView2.PostWebMessageAsJson("{\"type\":\"window-state\",\"maximized\":" +
                (WindowState == FormWindowState.Maximized ? "true" : "false") + "}");
    }

    private void SystemCommand(int command)
    {
        MaximizedBounds = Screen.FromHandle(Handle).WorkingArea;
        Native.SendMessage(Handle, 0x0112, new IntPtr(command), IntPtr.Zero);
    }

    private void ToggleMaximize()
    {
        SystemCommand(WindowState == FormWindowState.Maximized ? 0xF120 : 0xF030);
    }

    private void ShowIconMenu()
    {
        iconMenu.Items.Clear();
        iconMenu.Items.Add("展开 / 收起侧栏", null, async delegate {
            await web.CoreWebView2.ExecuteScriptAsync("document.querySelector('[class*=\"_logoRow\"] [class*=\"_toggle\"]')?.click()");
        });
        iconMenu.Items.Add("新会话", null, async delegate { await NewSession(); });
        iconMenu.Items.Add(new ToolStripSeparator());
        iconMenu.Items.Add("后退", null, delegate { web.CoreWebView2.GoBack(); }).Enabled = web.CoreWebView2.CanGoBack;
        iconMenu.Items.Add("前进", null, delegate { web.CoreWebView2.GoForward(); }).Enabled = web.CoreWebView2.CanGoForward;
        iconMenu.Items.Add("刷新", null, delegate { web.CoreWebView2.Reload(); });
        iconMenu.Items.Add("放大页面", null, delegate { web.ZoomFactor = Math.Min(2.0, web.ZoomFactor + 0.1); });
        iconMenu.Items.Add("缩小页面", null, delegate { web.ZoomFactor = Math.Max(0.5, web.ZoomFactor - 0.1); });
        iconMenu.Items.Add("重置页面缩放", null, delegate { web.ZoomFactor = 1; });
        iconMenu.Items.Add("开发者工具", null, delegate { web.CoreWebView2.OpenDevToolsWindow(); });
        iconMenu.Items.Add("版本管理", null, delegate { OpenVersionSwitch(); });
        iconMenu.Items.Add(new ToolStripSeparator());
        iconMenu.Items.Add("最小化", null, delegate { SystemCommand(0xF020); });
        iconMenu.Items.Add("最小化到系统托盘", null, delegate { MinimizeFromMenu(); });
        iconMenu.Items.Add(WindowState == FormWindowState.Maximized ? "还原窗口" : "最大化", null, delegate { ToggleMaximize(); });
        iconMenu.Items.Add("关闭窗口…", null, delegate { Close(); });
        iconMenu.Items.Add("下次关闭时重新询问", null, delegate { ResetClosePreference(); });
        iconMenu.Items.Add("退出 DSH", null, delegate { forceExit=true; RecordCloseAction("exit",false); Close(); });
        SkinMenuRenderer.Style(iconMenu);
        iconMenu.Show(Cursor.Position);
    }

    private async void ReceiveChromeCommand(object sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        Uri source;
        // Only the application's own top-level origin may control this window.
        if (origin == null || !Uri.TryCreate(e.Source, UriKind.Absolute, out source) || !IsLocal(source)) return;
        string command;
        try { command = e.TryGetWebMessageAsString(); }
        catch (ArgumentException) { return; }
        if (command.StartsWith("desktop:environment-health:") && environmentTrial) {
            try {
                var health = new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<System.Collections.Generic.Dictionary<string,object>>(command.Substring("desktop:environment-health:".Length));
                var verdict = health.ContainsKey("error")
                    ? Failed("无法读取插件状态：" + Updates.Value(health, "error"))
                    : EnvironmentRegressions(ReadObservations(health.ContainsKey("states") ? health["states"] : null));
                if (verdict.Count > 0) {
                    environmentFailed = true;
                    StopBackend();
                    Log("Candidate environment rejected (" + verdict.Count + "): " + verdict.Detail);
                    Fail("候选环境插件未全部通过验证（" + verdict.Count + " 项）：" + verdict.Summary
                        + "\r\n候选数据已保留；请关闭窗口，在 Version Switch 中恢复上一环境。");
                    return;
                }
                await Updates.Run("healthy " + environmentId);
                environmentTrial=false; checkingEnvironment=false; ApplicationReady();
            } catch(Exception error) {
                environmentFailed = true; StopBackend();
                Fail("环境健康确认失败："+error.Message+"。请在版本管理恢复。");
            }
            return;
        }
        if (command.StartsWith("desktop:environment-baseline:"))
        {
            // Learned only from a confirmed environment: a candidate's own states
            // must never become the baseline it is judged against.
            if (environmentTrial || environmentFailed) return;
            try
            {
                var payload = new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<System.Collections.Generic.Dictionary<string,object>>(command.Substring("desktop:environment-baseline:".Length));
                if (payload.ContainsKey("error")) { Log("Plugin baseline skipped: " + Updates.Value(payload, "error")); return; }
                var states = ReadObservations(payload.ContainsKey("states") ? payload["states"] : null);
                if (states.Count == 0) return;
                var flat = new Dictionary<string,object>();
                int live = 0;
                foreach (var row in states) { flat[row.Key] = row.Value.State; if (row.Value.State == "live") live++; }
                File.WriteAllText(Path.Combine(Program.Root, EnvironmentBaseline), Updates.Json(new {
                    environmentId = environmentId, version = runtimeVersion,
                    recordedAt = DateTime.Now.ToString("s"), states = flat }), new UTF8Encoding(false));
                Log("Plugin baseline recorded: " + live + "/" + flat.Count + " live (environment " + environmentId + ")");
            }
            catch (Exception error) { Log("Plugin baseline write failed: " + error.Message); }
            return;
        }
        if (command.StartsWith("desktop:theme:"))
        {
            Updates.SaveTheme(command.Substring(14));
            return;
        }
        if (command.StartsWith("desktop:title:"))
        {
            string title = command.Substring(14);
            if (title.StartsWith("DeepSeek Harness")) Text = title.Substring(0, Math.Min(title.Length, 512));
            return;
        }
        if (command.StartsWith("desktop:")) Log("Window command: " + command);
        switch (command)
        {
            case "desktop:ui-ready": ApplicationReady(); break;
            case "desktop:update-restart": Restart(); break;
            case "desktop:update-ignore":
                if(!installingUpdate && !String.IsNullOrEmpty(offeredVersion)) {
                    try { DesktopPreferences.SaveIgnored(offeredVersion); UpdateToast("ignored", "已忽略此版本"); }
                    catch(Exception error) { UpdateToast("error","无法保存忽略记录："+error.Message); }
                }
                break;
            case "desktop:update-install": InstallUpdate(); break;
            case "desktop:update-versions": OpenVersionSwitch(); break;
            case "desktop:menu": ShowIconMenu(); break;
            case "desktop:minimize": SystemCommand(0xF020); break;
            case "desktop:maximize": ToggleMaximize(); break;
            case "desktop:close": Close(); break;
            case "desktop:drag":
                Native.ReleaseCapture();
                Native.SendMessage(Handle, 0x00A1, new IntPtr(2), IntPtr.Zero);
                break;
            default:
                int edge;
                if (command.StartsWith("desktop:resize:") && int.TryParse(command.Substring(15), out edge) &&
                    edge >= 1 && edge <= 8 && WindowState != FormWindowState.Maximized)
                {
                    Native.ReleaseCapture();
                    Native.SendMessage(Handle, 0x0112, new IntPtr(0xF000 + edge), IntPtr.Zero);
                }
                break;
        }
    }

    internal void ActivateExisting()
    {
        // A duplicate launch must also bring back a window that was hidden into
        // the tray instead of only handling a normal taskbar minimize.
        RestoreFromTray();
        if (WindowState == FormWindowState.Minimized) SystemCommand(0xF120);
        Show();
        Activate();
        Native.SetForegroundWindow(Handle);
        Log("Existing window activated");
    }

    private void OpenVersionSwitch()
    {
        try { Process.Start(new ProcessStartInfo(Path.Combine(Program.Root, "Version Switch.exe")) { UseShellExecute = true }); }
        catch (Exception error) { UpdateToast("error", "无法打开版本管理：" + error.Message); }
    }

    // First run: show the recommended-plugin picker. Required plugins are
    // locked; optional plugins start checked and can be deselected. A failed
    // attempt is retried on the next launch.
    private async Task EnsureRecommendedPluginsAsync(string home)
    {
        try
        {
            if (!DefaultPlugins.Available(Program.Root)) return;
            string profile = Path.Combine(home, "profiles", "web");
            string decision = DesktopPreferences.RecommendedPlugins;
            if (decision == "installed" || decision == "declined") return;
            // An existing profile belongs to the user; never replace it.
            if (decision == "" && File.Exists(Path.Combine(profile, "package.json"))) return;
            List<RecommendedPlugin> catalog = DefaultPlugins.Catalog(Program.Root);
            if (catalog.Count == 0)
            {
                Log("Recommended plugins: catalog is empty or unreadable");
                return;
            }
            List<string> selected;
            using (var picker = new PluginPicker(catalog))
            {
                if (picker.ShowDialog(this) != DialogResult.OK)
                {
                    DesktopPreferences.SaveRecommendedPlugins("declined");
                    Log("Recommended plugins: declined");
                    return;
                }
                selected = picker.SelectedNames;
            }
            long free = RuntimeEnvironment.FreeDiskBytes();
            if (free > 0 && free < 3L * 1024 * 1024 * 1024)
                MessageBox.Show(this, "当前磁盘剩余空间不足 3 GB。\r\n首次安装插件可能失败，建议先清理磁盘或只保留必需组件。",
                    "DSH 首次启动", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            DesktopPreferences.SaveRecommendedPlugins("failed");
            Log("Recommended plugins: selected " + selected.Count + " plugin(s)");
            ShowLoading("正在安装所选插件（必需组件优先，首次约下载 200 MB）…");
            status.BeginInstall();
            bool ok = await Task.Run(delegate { return DefaultPlugins.Install(Program.Root, home, proxyBridge, Log, selected, row => {
                if(closing) return;
                try { BeginInvoke((Action)(() => { if(!closing) status.ReportInstall(row); })); } catch(InvalidOperationException) {}
            }, runtimeRoot); });
            status.EndInstall();
            if(closing) return;
            if (proxyBridge != null) Log("ProxyBridge: npmrc " + proxyBridge.SyncNow());
            if (ok)
            {
                DesktopPreferences.SaveRecommendedPlugins("installed");
                Log("Recommended plugins: installed");
            }
            else
            {
                Log("Recommended plugins: failed");
                MessageBox.Show(this, "插件安装未完成。必需组件会优先重试；请检查网络后重启，或稍后在插件市场中安装其余插件。",
                    "DSH 首次启动", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            ShowLoading("正在初始化窗口组件…");
        }
        catch (Exception error)
        {
            status.EndInstall();
            Log("Recommended plugins failed: " + error.Message);
        }
    }

    private async Task StartAsync()
    {
        try
        {
            string data = Path.Combine(Program.Root, "data");
            Directory.CreateDirectory(data);
            Directory.CreateDirectory(Path.Combine(Program.Root, "workspace"));
            // Resolve once per launch. An updater publishing a different next.json
            // later cannot change this window's live runtime or its dependencies.
            status.Text = "正在读取版本配置…";
            previousVersion = Updates.ReadFile("data/desktop-last-version.txt").Trim();
            if (previousVersion == "") {
                try {
                    var prior = new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<System.Collections.Generic.Dictionary<string,object>>(Updates.ReadFile("data/desktop-runtime.json"));
                    previousVersion = Updates.Value(prior,"version");
                } catch (Exception) {}
            }
            var runtime = await Updates.Run("boot", message => { try { BeginInvoke((Action)(() => status.Text=message)); } catch(InvalidOperationException) {} });
            runtimeRoot = Updates.Value(runtime, "root");
            runtimeVersion = Updates.Value(runtime, "version");
            environmentId=Updates.Value(runtime,"environmentId");
            environmentHome=Updates.Value(runtime,"home");
            if(environmentHome=="") environmentHome=data;
            // The settings page writes its choice next to this backend's home, so
            // the bridge reads exactly the file the plugin writes, then follows it.
            proxyBridge = new ProxyBridge(environmentHome, Log);
            proxyBridge.Initialize();
            await EnsureRecommendedPluginsAsync(environmentHome);
            if(closing) return;
            await Task.Run(() => DefaultPlugins.ValidateInstalled(Program.Root, environmentHome, runtimeRoot, Log));
            environmentTrial=runtime.ContainsKey("trial") && (bool)runtime["trial"];
            if(runtime.ContainsKey("expectedPlugins")) expectedPlugins=runtime["expectedPlugins"];
            if(runtime.ContainsKey("plugins")) environmentPlugins=runtime["plugins"];
            environmentSummary=Updates.Value(runtime,"summary");
            environmentWarning=Updates.Value(runtime,"warning");
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "--verify-lifecycle") >= 0) previousVersion = "0.2.0-rc.2";
            Updates.WriteRuntime(runtimeVersion);
            if (runtime.ContainsKey("warning")) Log(Updates.Value(runtime, "warning"));
            // Browser storage travels with the portable folder.
            status.Text = "正在初始化窗口组件…";
            var environment = await RuntimeEnvironment.CreateWebView2EnvironmentAsync(
                Path.Combine(data, "desktop-webview", environmentId==""?"legacy":environmentId));
            if (closing) return;
            await web.EnsureCoreWebView2Async(environment);
            if (closing) return;
            web.CoreWebView2.Settings.IsStatusBarEnabled = false;
            web.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            web.CoreWebView2.WebMessageReceived += ReceiveChromeCommand;
            await web.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync("addEventListener('error',e=>chrome.webview.postMessage('desktop:plugin-error:'+e.message));addEventListener('unhandledrejection',e=>chrome.webview.postMessage('desktop:plugin-error:'+String(e.reason)));const originalError=console.error;console.error=(...args)=>{chrome.webview.postMessage('desktop:plugin-error:'+args.map(String).join(' ').slice(0,1800));originalError(...args)};");
            web.CoreWebView2.WebMessageReceived += async delegate(object sender, CoreWebView2WebMessageReceivedEventArgs e) {
                Uri page; if(origin == null || !Uri.TryCreate(e.Source,UriKind.Absolute,out page) || !IsLocal(page)) return;
                string message; try { message=e.TryGetWebMessageAsString(); } catch(ArgumentException) {return;}
                if(message.StartsWith("desktop:startup-error:") || message.StartsWith("desktop:plugin-error:"))
                    Log(Regex.Replace(message, @"https?://\S+", "[URL redacted]"));
                if(!uiReady && message.StartsWith("desktop:plugin-error:")) {
                    var failed=new List<string>();
                    foreach(Match match in Regex.Matches(message, @"(?:^|\s)(@?[a-zA-Z0-9_.-]+(?:/[a-zA-Z0-9_.-]+)?): (?:pending|failed|broken)")) failed.Add(match.Groups[1].Value);
                    try { await RepairStartupPlugins(failed); } catch(Exception error) { Fail("插件兼容性修复失败："+error.Message); }
                }
            };
            web.CoreWebView2.WindowCloseRequested += delegate { Close(); };
            // Observe the actual accepted response, preserving the market's origin,
            // permission and in-progress-install checks. The launcher owns the new
            // process tree; the plugin's detached helper cannot survive our job cleanup.
            web.CoreWebView2.WebResourceResponseReceived += delegate(object sender, CoreWebView2WebResourceResponseReceivedEventArgs e) {
                Uri target;
                if (origin == null || !Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out target) || !IsLocal(target)) return;
                if (e.Request.Method == "POST" && e.Response.StatusCode == 202 &&
                    (target.AbsolutePath == "/dsh-market/restart" || target.AbsolutePath == "/dsh-market/api/v1/restart")) {
                    backendRestartRequested = true;
                    Log("Accepted internal restart");
                    ShowLoading("正在停止旧后台，准备重新启动…");
                }
            };
            using (var script = new StreamReader(typeof(Program).Assembly.GetManifestResourceStream("DesktopChrome.js")))
            using (var bitmap = Icon.ToBitmap())
            using (var image = new MemoryStream())
            {
                bitmap.Save(image, System.Drawing.Imaging.ImageFormat.Png);
                await web.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(script.ReadToEnd().Replace(
                    "__DSH_ICON_DATA__", "data:image/png;base64," + Convert.ToBase64String(image.ToArray())));
            }
            web.CoreWebView2.NewWindowRequested += HandleNewWindow;
            web.CoreWebView2.NavigationStarting += delegate(object sender, CoreWebView2NavigationStartingEventArgs e)
            {
                Uri target;
                if (origin != null && Uri.TryCreate(e.Uri, UriKind.Absolute, out target) && !IsLocal(target))
                {
                    e.Cancel = true;
                    OpenLink(e.Uri);
                }
            };
            web.CoreWebView2.NavigationCompleted += async delegate(object sender, CoreWebView2NavigationCompletedEventArgs e)
            {
                if (e.IsSuccess)
                {
                    Log("UI ready");
                    if (Array.IndexOf(Environment.GetCommandLineArgs(), "--verify-browser-relay") >= 0) {
                        await web.CoreWebView2.ExecuteScriptAsync("window.__relayTest=window.open('about:blank','_blank');if(window.__relayTest){window.__relayTest.opener=null;setTimeout(()=>window.__relayTest.location.replace('https://example.invalid/dsh-browser-relay-test'),300)}");
                        await Task.Delay(2500);
                        File.WriteAllText(Path.Combine(Program.Root,"desktop/browser-relay-verification.txt"), browserRelayVerified && IsLocal(new Uri(web.CoreWebView2.Source)) ? "PASS: delayed blank popup forwarded; main application unchanged" : "FAIL: popup relay did not forward");
                    }
                    if (Array.IndexOf(Environment.GetCommandLineArgs(), "--verify-plugins") >= 0) {
                        await Task.Delay(10000);
                        bool restartTest = lifecycleStep++ == 0;
                        await web.CoreWebView2.ExecuteScriptAsync("window.__portableRestartTest=" + (restartTest ? "true" : "false") + ";" + File.ReadAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "desktop", "VerifyPlugins.js")));
                    }
                    if(Array.IndexOf(Environment.GetCommandLineArgs(),"--verify-exit-settings")>=0) { await VerifyExitSettings(); return; }
                    if (Array.IndexOf(Environment.GetCommandLineArgs(), "--verify-lifecycle") >= 0)
                    {
                        try {
                            if (lifecycleStep == 0) {
                                lifecycleStep = 1;
                                await Task.Delay(4000);
                                if (!uiReady || status.Visible) throw new Exception("App readiness failed");
                                string reminder = await web.CoreWebView2.ExecuteScriptAsync("document.getElementById('dsh-plugin-toast').textContent.includes('插件不会自动更新')");
                                if (reminder != "true") throw new Exception("Plugin reminder missing");
                                verificationBackend=backend.Id; verificationWindow=Handle;
                                Log("Lifecycle initial backend="+verificationBackend+" window="+Handle);
                                await web.CoreWebView2.ExecuteScriptAsync("fetch('/dsh-market/restart',{method:'POST',headers:{'content-type':'application/json'},body:'{}'})");
                            } else if (lifecycleStep == 1) {
                                lifecycleStep = 2;
                                await Task.Delay(12000);
                                if (backend.Id == verificationBackend || Handle != verificationWindow || status.Visible) throw new Exception("In-window restart failed");
                                try { using(var old = Process.GetProcessById(verificationBackend)) if(!old.HasExited) throw new Exception("Old backend survived"); } catch(ArgumentException) {}
                                Log("PASS: internal restart, same window, new backend="+backend.Id+", old backend exited, plugin reminder shown");
                                // Exercise the backend-exit notification path as well as the actual restart route.
                                backend.Kill();
                            }
                        } catch(Exception error) { Log("Lifecycle verification failed: "+error.Message); }
                        return;
                    }
                    if (Array.IndexOf(Environment.GetCommandLineArgs(), "--verify-restart") >= 0)
                    {
                        await Task.Delay(18000);
                        UpdateToast("ready", "重启流程验证：已安装版本就绪。");
                        await Task.Delay(300);
                        string button = await web.CoreWebView2.ExecuteScriptAsync("!document.getElementById('dsh-update-restart').hidden");
                        if (button != "true") { Log("Restart verification failed: missing button"); return; }
                        Log("Restart verification: clicking ready button PID=" + Process.GetCurrentProcess().Id);
                        await web.CoreWebView2.ExecuteScriptAsync("setTimeout(()=>document.getElementById('dsh-update-restart').click(),100)");
                        return;
                    }
                    if (!updateChecked && Array.IndexOf(Environment.GetCommandLineArgs(), "--verify-ui") < 0)
                    {
                        updateChecked = true;
                        CheckUpdates();
                    }
                    if (Array.IndexOf(Environment.GetCommandLineArgs(), "--verify-ui") >= 0)
                    {
                        try
                        {
                            // Allow the user's boot animation to finish before visual QA.
                            await Task.Delay(18000);
                            string result = await web.CoreWebView2.ExecuteScriptAsync(
                                "JSON.stringify({bar:!!document.getElementById('dsh-desktop-chrome'),background:getComputedStyle(document.getElementById('dsh-desktop-chrome')).backgroundColor,buttons:document.querySelectorAll('#dsh-desktop-chrome button').length,icon:document.getElementById('dsh-desktop-icon').naturalWidth>0,toggleHidden:[...document.querySelectorAll('[class*=\"_logoRow\"] [class*=\"_toggle\"]')].every(x=>getComputedStyle(x).display==='none')})");
                            Log("Chrome verification: " + result);
                            using (var output = File.Create(Path.Combine(Program.Root, "desktop", "preview.png")))
                                await web.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, output);
                            string chromeHeight = await web.CoreWebView2.ExecuteScriptAsync("document.getElementById('dsh-desktop-chrome').getBoundingClientRect().height");
                            if (chromeHeight != "28") throw new Exception("Compact title bar height failed");
                            // Exercise real CSS backgrounds, then remove the temporary rule;
                            // the user's persisted skin and wallpaper are never changed.
                            await web.CoreWebView2.ExecuteScriptAsync("window.__toneTest=document.createElement('style');document.head.append(__toneTest);__toneTest.textContent='html,body,[class*=\"_frame\"],[class*=\"_frame\"]::before{background:#111 !important}'");
                            await Task.Delay(700);
                            string darkTone = await web.CoreWebView2.ExecuteScriptAsync("document.getElementById('dsh-desktop-icon').dataset.tone");
                            await web.CoreWebView2.ExecuteScriptAsync("__toneTest.textContent='html,body,[class*=\"_frame\"],[class*=\"_frame\"]::before{background:#fff !important}'");
                            await Task.Delay(700);
                            string lightTone = await web.CoreWebView2.ExecuteScriptAsync("document.getElementById('dsh-desktop-icon').dataset.tone");
                            await web.CoreWebView2.ExecuteScriptAsync("__toneTest.remove();delete window.__toneTest");
                            if (darkTone != "\"white\"" || lightTone != "\"black\"") throw new Exception("Automatic icon contrast failed");
                            Log("PASS: 28px title bar; dark background -> white icon; light background -> black icon");
                            WindowState = FormWindowState.Normal;
                            await Task.Delay(300);
                            int corner;
                            if (Native.DwmGetWindowAttribute(Handle, 33, out corner, 4) != 0 || corner != 2)
                                throw new Exception("Normal window corner preference failed");
                            await web.CoreWebView2.ExecuteScriptAsync("window.__desktopSavedTitle=document.title;document.title='排查check_time为空原因 — DeepSeek Harness'");
                            await Task.Delay(200);
                            if (Text != "DeepSeek Harness - 排查check_time为空原因") throw new Exception("Dynamic title failed");
                            await web.CoreWebView2.ExecuteScriptAsync("document.title=window.__desktopSavedTitle;delete window.__desktopSavedTitle");
                            await web.CoreWebView2.ExecuteScriptAsync("document.getElementById('dsh-desktop-icon').dispatchEvent(new MouseEvent('contextmenu',{bubbles:true,cancelable:true}))");
                            await Task.Delay(200);
                            if (!iconMenu.Visible) throw new Exception("Icon context menu failed");
                            if (!(iconMenu.Renderer is SkinMenuRenderer)) throw new Exception("Icon menu is not skinned");
                            if (iconMenu.Font.Name != SkinTheme.FontFamily(SkinTheme.Load())) throw new Exception("Icon menu font is not the skin font");
                            iconMenu.Close();
                            await web.CoreWebView2.ExecuteScriptAsync("document.querySelectorAll('#dsh-desktop-chrome button')[1].click()");
                            await Task.Delay(300);
                            if (WindowState != FormWindowState.Maximized) throw new Exception("Maximize button failed");
                            var visibleClient = RectangleToScreen(ClientRectangle);
                            var expectedClient = Screen.FromHandle(Handle).WorkingArea;
                            Log("Maximized client=" + visibleClient + "; work area=" + expectedClient);
                            if (visibleClient != expectedClient) throw new Exception("Maximized client must fit monitor work area");
                            if (Native.DwmGetWindowAttribute(Handle, 33, out corner, 4) != 0 || corner != 1)
                                throw new Exception("Maximized window corner preference failed");
                            await web.CoreWebView2.ExecuteScriptAsync("document.querySelectorAll('#dsh-desktop-chrome button')[1].click()");
                            await Task.Delay(300);
                            if (WindowState != FormWindowState.Normal) throw new Exception("Restore button failed");
                            await web.CoreWebView2.ExecuteScriptAsync("document.querySelectorAll('#dsh-desktop-chrome button')[0].click()");
                            await Task.Delay(300);
                            if (WindowState != FormWindowState.Minimized) throw new Exception("Minimize button failed");
                            ActivateExisting();
                            Log("Chrome verified");
                        }
                        catch (Exception error) { Log("Chrome verification failed: " + error.Message); }
                    }
                }
                else Fail("页面加载失败：" + e.WebErrorStatus + "。请关闭窗口后重新启动。");
            };
            await LaunchBackendAsync();
        }
        catch (Exception error)
        {
            if (closing) return;
            StopBackend();
            Fail("启动失败：" + error.Message + "\r\n\r\n可关闭窗口后打开 Version Switch 检查或恢复环境。\r\n日志：data\\desktop.log");
        }
    }

    private bool IsLocal(Uri target)
    {
        return target.Scheme == origin.Scheme && target.Host == origin.Host && target.Port == origin.Port;
    }

    private void OpenLink(string value)
    {
        Uri target;
        if (!Uri.TryCreate(value, UriKind.Absolute, out target)) return;
        if (origin != null && IsLocal(target)) { web.CoreWebView2.Navigate(value); return; }
        OpenBrowser(value);
    }

    // OAuth UI preopens about:blank before its asynchronous authorization request.
    // Supply a real WindowProxy so its later location.replace reaches us; never
    // load the remote authorization page in the embedded browser. The original
    // DSH backend keeps ownership of state/PKCE and its loopback callback.
    private async void HandleNewWindow(object sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        if (!String.IsNullOrEmpty(e.Uri) && e.Uri != "about:blank") { OpenLink(e.Uri); return; }
        var deferral = e.GetDeferral();
        WebView2 relay = null;
        try {
            if (closing || popupRelays.Count >= 8) return;
            relay = new WebView2 { Visible = false, Size = new Size(1,1) };
            popupRelays.Add(relay); Controls.Add(relay);
            await relay.EnsureCoreWebView2Async(web.CoreWebView2.Environment);
            if (closing) return;
            var owned = relay;
            relay.CoreWebView2.NavigationStarting += delegate(object source, CoreWebView2NavigationStartingEventArgs navigation) {
                if (navigation.Uri == "about:blank") return;
                navigation.Cancel = true;
                OpenBrowser(navigation.Uri);
                BeginInvoke(new Action(delegate { ReleasePopup(owned); }));
            };
            relay.CoreWebView2.WindowCloseRequested += delegate { ReleasePopup(owned); };
            e.NewWindow = relay.CoreWebView2;
            ExpirePopup(owned);
        }
        catch (Exception) { if (relay != null) ReleasePopup(relay); Log("Browser popup initialization failed"); }
        finally { deferral.Complete(); }
    }

    private async void ExpirePopup(WebView2 popup)
    {
        await Task.Delay(TimeSpan.FromMinutes(10));
        if (!closing) ReleasePopup(popup);
    }

    private void ReleasePopup(WebView2 popup)
    {
        if (!popupRelays.Remove(popup)) return;
        Controls.Remove(popup); popup.Dispose();
    }

    private void OpenBrowser(string value)
    {
        Uri target;
        if (!Uri.TryCreate(value, UriKind.Absolute, out target) ||
            (target.Scheme != "https" && target.Scheme != "http")) return;
        if (Array.IndexOf(Environment.GetCommandLineArgs(), "--verify-browser-relay") >= 0 &&
            value == "https://example.invalid/dsh-browser-relay-test") { browserRelayVerified = true; return; }
        try { Process.Start(new ProcessStartInfo(value) { UseShellExecute = true }); }
        catch (Exception) {
            Log("Default browser launch failed");
            MessageBox.Show(this,"无法打开系统默认浏览器。请使用登录框中的“复制登录链接”，粘贴到浏览器完成登录。","打开浏览器",MessageBoxButtons.OK,MessageBoxIcon.Information);
        }
    }

    private void StartBackend(string data)
    {
        // Closing the job terminates only this launcher's backend process tree,
        // including on a launcher crash. Other Node applications are unaffected.
        job = Native.CreateJobObject(IntPtr.Zero, null);
        if (job == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
        var limits = new Native.ExtendedLimits();
        limits.BasicLimitInformation.LimitFlags = 0x2000; // KILL_ON_JOB_CLOSE
        if (!Native.SetInformationJobObject(job, 9, ref limits, Marshal.SizeOf(limits)))
            throw new System.ComponentModel.Win32Exception();
        var info = new ProcessStartInfo(Path.Combine(Program.Root, "node", "node.exe"));
        info.Arguments = "--use-env-proxy \"" + Path.Combine(runtimeRoot, "node_modules", "@deepseek-ai", "dsh", "lib", "bin.js") +
            "\" web --host 127.0.0.1 --port 0 --no-open";
        info.WorkingDirectory = Program.Root;
        info.UseShellExecute = false;
        info.CreateNoWindow = true;
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;
        info.StandardOutputEncoding = Encoding.UTF8;
        info.StandardErrorEncoding = Encoding.UTF8;
        info.EnvironmentVariables["DSH_HOME"] = data;
        info.EnvironmentVariables["PATH"] = Path.Combine(Program.Root, "node") + ";" + Path.Combine(Program.Root,"desktop","plugin-tools","node_modules",".bin") + ";" + Path.Combine(Program.Root,"git","cmd") + ";" + Environment.GetEnvironmentVariable("PATH");
        info.EnvironmentVariables["npm_config_cache"] = Path.Combine(data, "npm-cache");
        info.EnvironmentVariables["NO_COLOR"] = "1";
        // pnpm ignores HTTP_PROXY and the Windows system proxy, and the settings
        // page's policy only reached the harness's own HTTP client. This hands the
        // backend — and every pnpm it spawns — the mode the user picked.
        if (proxyBridge != null) proxyBridge.ApplyEnvironment(info);
        backend = new Process { StartInfo = info, EnableRaisingEvents = true };
        backend.OutputDataReceived += ReceiveLine;
        backend.ErrorDataReceived += ReceiveLine;
        var owned = backend;
        backend.Exited += delegate
        {
            if (closing) return;
            try { BeginInvoke((Action)(() => BackendExited(owned))); }
            catch (InvalidOperationException) { }
        };
        backend.Start();
        if (!Native.AssignProcessToJobObject(job, backend.Handle))
        {
            backend.Kill();
            throw new System.ComponentModel.Win32Exception();
        }
        backend.BeginOutputReadLine();
        backend.BeginErrorReadLine();
        Log("Backend started PID=" + backend.Id);
        Log("Runtime version=" + runtimeVersion);
    }

    private void ReceiveLine(object sender, DataReceivedEventArgs e)
    {
        if (e.Data == null || !Object.ReferenceEquals(sender, backend)) return;
        foreach(Match failed in Regex.Matches(e.Data, @"(?:^|\s)(@?[a-zA-Z0-9_.-]+(?:/[a-zA-Z0-9_.-]+)?) \([^)]*\): failed to import"))
            lock(logLock) failedStartupPlugins.Add(failed.Groups[1].Value);
        var match = Regex.Match(e.Data, @"dsh web: (http://127\.0\.0\.1:\d+/\S*)");
        if (match.Success) address.TrySetResult(match.Groups[1].Value);
        // Startup URLs carry credentials and must not be saved in the log.
        Log(Regex.Replace(e.Data, @"https?://\S+", "[URL redacted]"));
    }

    private void Fail(string message)
    {
        status.Failed = true;
        status.Text = message;
        status.Visible = true;
        status.BringToFront();
        Log(message);
    }

    private void Log(string message)
    {
        try
        {
            lock (logLock)
            {
                string path = Path.Combine(Program.Root, "data", "desktop.log");
                if (File.Exists(path) && new FileInfo(path).Length > 2 * 1024 * 1024)
                    File.WriteAllText(path, "");
                File.AppendAllText(path, DateTime.Now.ToString("s") + " " + message + Environment.NewLine);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private async void WatchPageLoad(string requestedUrl)
    {
        await Task.Delay(45000);
        if(closing || uiReady || origin == null || origin.AbsoluteUri != requestedUrl || environmentFailed) return;
        try {
            string state = await web.CoreWebView2.ExecuteScriptAsync("JSON.stringify({ready:document.readyState,root:!!document.getElementById('root'),boot:!!document.querySelector('[data-dsh-boot]'),error:!!document.querySelector('[data-dsh-boot-error]')})");
            Log("Page mount timeout: " + state);
            Fail("DSH 页面未完成加载。可能有插件启动失败，请检查 data/desktop.log 中的 startup-error 或 plugin-error。关闭后可在版本管理检查插件。");
        } catch(Exception error) { Fail("界面加载失败：" + error.Message); }
    }

    private void StartCacheCleanup()
    {
        try {
            var script=Path.Combine(Program.Root,"desktop","cache-cleanup.cjs");
            if(!File.Exists(script)) return;
            Process.Start(new ProcessStartInfo(Path.Combine(Program.Root,"node","node.exe"),
                "\"" + script + "\" --wait-for-pid " + Process.GetCurrentProcess().Id) {
                WorkingDirectory=Program.Root,UseShellExecute=false,CreateNoWindow=true
            });
        } catch(Exception error) { Log("Cache cleanup deferred: " + error.Message); }
    }

    private void StopBackend()
    {
        var old = backend; backend = null;
        if (job != IntPtr.Zero) { Native.TerminateJobObject(job,1); Native.CloseHandle(job); job = IntPtr.Zero; }
        if (old != null) { try { old.WaitForExit(5000); } catch (InvalidOperationException) {} old.Dispose(); }
    }
}

internal static class Native
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect { internal int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] internal static extern bool IsZoomed(IntPtr h);
    [DllImport("user32.dll")] internal static extern uint GetDpiForWindow(IntPtr h);
    [DllImport("user32.dll")] internal static extern int GetSystemMetricsForDpi(int index, uint dpi);
    [StructLayout(LayoutKind.Sequential)]
    internal struct Margins { internal int Left, Right, Top, Bottom; }
    [DllImport("dwmapi.dll")] internal static extern int DwmSetWindowAttribute(IntPtr h, int attribute, ref int value, int size);
    [DllImport("dwmapi.dll")] internal static extern int DwmGetWindowAttribute(IntPtr h, int attribute, out int value, int size);
    [DllImport("dwmapi.dll")] internal static extern int DwmExtendFrameIntoClientArea(IntPtr h, ref Margins margins);
    [DllImport("user32.dll")] internal static extern bool ReleaseCapture();
    [DllImport("user32.dll")] internal static extern IntPtr SendMessage(IntPtr h, int message, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] internal static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] internal static extern bool DestroyIcon(IntPtr icon);
    [DllImport("user32.dll")] internal static extern bool AllowSetForegroundWindow(int processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr CreateJobObject(IntPtr attributes, string name);
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool SetInformationJobObject(IntPtr job, int kind, ref ExtendedLimits limits, int length);
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll")] internal static extern bool TerminateJobObject(IntPtr job, uint exitCode);
    [DllImport("kernel32.dll")] internal static extern bool CloseHandle(IntPtr handle);
    [StructLayout(LayoutKind.Sequential)]
    internal struct BasicLimits
    {
        internal long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        internal uint LimitFlags;
        internal UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        internal uint ActiveProcessLimit;
        internal UIntPtr Affinity;
        internal uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct IoCounters { internal ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct ExtendedLimits
    {
        internal BasicLimits BasicLimitInformation;
        internal IoCounters IoInfo;
        internal UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }
}
