using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

internal static class VersionSwitch
{
    [STAThread]
    private static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new VersionWindow());
    }
}

internal sealed class VersionWindow : Form
{
    private readonly WebView2 web = new WebView2 { Dock = DockStyle.Fill };
    private readonly Timer timer = new Timer { Interval = 1200 };
    private readonly Dictionary<string, bool> versions = new Dictionary<string, bool>();
    private Dictionary<string, string> skin;
    private bool busy, ready, verifySkin, refreshing;
    private string lastTheme = "", lastRunning = "", lastPointer = "";
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr h,int m,IntPtr w,IntPtr l);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern int GetSystemMetricsForDpi(int index,uint dpi);
    [StructLayout(LayoutKind.Sequential)] private struct FrameRect { public int Left,Top,Right,Bottom; }
    protected override void WndProc(ref Message message) {
        if(message.Msg==0x83) {
            if(WindowState==FormWindowState.Maximized) {
                var rect=(FrameRect)Marshal.PtrToStructure(message.LParam,typeof(FrameRect));
                uint dpi=GetDpiForWindow(Handle);
                int x=GetSystemMetricsForDpi(32,dpi)+GetSystemMetricsForDpi(92,dpi);
                int y=GetSystemMetricsForDpi(33,dpi)+GetSystemMetricsForDpi(92,dpi);
                rect.Left+=x;rect.Right-=x;rect.Top+=y;rect.Bottom-=y;
                Marshal.StructureToPtr(rect,message.LParam,false);
            }
            message.Result=IntPtr.Zero;return;
        }
        base.WndProc(ref message);
    }

    internal VersionWindow()
    {
        // A window opened before DSH has ever exported a skin still needs a
        // documented baseline, and an already cached skin must be applied
        // before the WebView paints anything.
        SkinTheme.Ensure();
        skin = SkinTheme.Load();
        verifySkin = Array.IndexOf(Environment.GetCommandLineArgs(), "--verify-skin") >= 0;
        Text = "DeepSeek Harness - 版本管理";
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        Size = new Size(960, 760); MinimumSize = new Size(720, 560);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = SkinTheme.ToColor(skin, "bg", Color.FromArgb(21, 21, 23));
        web.DefaultBackgroundColor = BackColor;
        Controls.Add(web);
        Shown += async delegate { await Initialize(); };
        FormClosed += delegate { timer.Dispose(); web.Dispose(); };
        timer.Tick += async delegate {
            if (!ready || busy || refreshing) return;
            refreshing = true;
            try {
            SyncTheme();
            string running = Updates.RunningVersion(), pointer = Updates.ReadFile("environments/state.json");
            if (running != lastRunning || pointer != lastPointer) {
                await SyncSelection();
                await LoadEnvironments();
                lastRunning = running; lastPointer = pointer;
            }
            } catch (Exception error) {
                // Timer callbacks are async void: a failed worker must not escape
                // to WinForms' unhandled-exception/JIT dialog.
                Status("刷新环境失败：" + error.Message + "，请刷新重试。");
            } finally { refreshing = false; }
        };
    }
    private void Post(object data)
    {
        if (IsDisposed || !IsHandleCreated) return;
        if (InvokeRequired) { try { BeginInvoke((Action)(() => Post(data))); } catch (InvalidOperationException) {} return; }
        if (ready) web.CoreWebView2.PostWebMessageAsJson(Updates.Json(data));
    }
    private void Status(string text) { Post(new { type = "status", text = text, busy = busy }); }
    private void SyncTheme()
    {
        // Compare the resolved skin, not the file text: a recaptured but
        // unchanged skin must not repaint the window.
        string signature = SkinTheme.Signature();
        if (signature == lastTheme) return;
        lastTheme = signature;
        skin = SkinTheme.Load();
        Post(new { type = "theme", theme = skin });
        ApplyFrame(skin);
    }
    private void ApplyFrame(Dictionary<string, string> theme)
    {
        try {
            Color background = SkinTheme.ToColor(theme, "bg", BackColor);
            BackColor = background;
            web.DefaultBackgroundColor = background;
            if (!IsHandleCreated) return;
            bool light = theme.ContainsKey("scheme") && theme["scheme"] == "light";
            int dark = light ? 0 : 1;
            DwmSetWindowAttribute(Handle, 20, ref dark, 4);
            // Keep the native title bar inside the same skin as the page.
            int caption = -2; // DWM_COLOR_NONE; HTML caption shares the page background.
            DwmSetWindowAttribute(Handle, 35, ref caption, 4);
            int corner = 2; DwmSetWindowAttribute(Handle,33,ref corner,4);
            int text = SkinTheme.ColorRef(SkinTheme.ToColor(theme, "text", Color.FromArgb(235, 238, 242)));
            DwmSetWindowAttribute(Handle, 36, ref text, 4);
            int border = SkinTheme.ColorRef(SkinTheme.ToColor(theme, "border", background));
            DwmSetWindowAttribute(Handle, 34, ref border, 4);
        } catch (Exception) { /* Older Windows builds ignore caption colours. */ }
    }
    private async Task Initialize()
    {
        try {
            var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(Updates.Root, "data", "version-switch-webview"));
            if (IsDisposed) return;
            await web.EnsureCoreWebView2Async(env);
            web.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            web.CoreWebView2.Settings.IsStatusBarEnabled = false;
            // Install the cached skin at document creation, so this window never
            // paints the built-in palette while waiting for a message.
            await web.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(SkinTheme.BootstrapScript(skin));
            using(var bitmap=Icon.ToBitmap()) using(var stream=new MemoryStream()) {
                bitmap.Save(stream,System.Drawing.Imaging.ImageFormat.Png);
                await web.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync("window.__captionIcon='data:image/png;base64,"+Convert.ToBase64String(stream.ToArray())+"';");
            }
            web.CoreWebView2.NewWindowRequested += delegate(object s, CoreWebView2NewWindowRequestedEventArgs e) { e.Handled = true; };
            web.CoreWebView2.NavigationStarting += delegate(object s, CoreWebView2NavigationStartingEventArgs e) { if (ready) e.Cancel = true; };
            web.CoreWebView2.WebMessageReceived += async delegate(object s, CoreWebView2WebMessageReceivedEventArgs e) {
                if (e.Source != "about:blank") return;
                string command;
                try { command = e.TryGetWebMessageAsString(); } catch (ArgumentException) { return; }
                if(command.StartsWith("window:")) {
                    if(command=="window:close") Close();
                    else if(command=="window:min") WindowState=FormWindowState.Minimized;
                    else if(command=="window:max") WindowState=WindowState==FormWindowState.Maximized?FormWindowState.Normal:FormWindowState.Maximized;
                    else {
                        int hit=2;
                        if(command!="window:drag" && (!command.StartsWith("window:resize:") || !Int32.TryParse(command.Substring(14),out hit) || hit<10 || hit>17)) return;
                        if(hit!=2 && WindowState==FormWindowState.Maximized)return;
                        ReleaseCapture();SendMessage(Handle,0xA1,(IntPtr)hit,IntPtr.Zero);
                    }
                }
                else if (command == "ready") {
                    ready = true; SyncTheme(); timer.Start();
                    if (verifySkin) { await VerifySkin(); return; }
                    await LoadVersions();
                }
                else if (command == "refresh" && !busy) await LoadVersions();
                else if (command.StartsWith("install:") && !busy) {
                    string version = command.Substring(8);
                    // Never execute a page-supplied argument unless it was returned by the catalogue.
                    if (versions.ContainsKey(version) && versions[version]) await Install(version);
                }
                else if (!busy && (command.StartsWith("prepare:") || command.StartsWith("restore:") || command=="cancel" || command=="recover" || command=="environments" || command.StartsWith("pin:") || command=="cleanup-preview" || command.StartsWith("cleanup:") || command.StartsWith("open-environment:"))) {
                    await EnvironmentCommand(command);
                }
            };
            using (var reader = new StreamReader(typeof(VersionSwitch).Assembly.GetManifestResourceStream("VersionSwitch.html")))
                web.NavigateToString(reader.ReadToEnd());
        } catch (Exception error) { MessageBox.Show(this, error.Message, "版本管理启动失败"); }
    }
    private async Task SyncSelection()
    {
        try { var selected = await Updates.Run("resolve"); Post(new { type = "selection", selected = selected, running = Updates.RunningVersion() }); }
        catch (Exception error) { Status("读取版本状态失败：" + error.Message); }
    }
    private async Task LoadVersions()
    {
        busy = true; Status("正在查询官方版本源…");
        try {
            await LoadEnvironments();
            var data = await Updates.Run("catalog");
            versions.Clear();
            foreach (Dictionary<string, object> row in (IEnumerable)data["versions"])
                versions[Updates.Value(row, "version")] = (bool)row["compatible"];
            Post(new { type = "catalog", data = data, running = Updates.RunningVersion() });
            busy = false; Status("先分析兼容性，确认变更后后台准备；重启时保存恢复点并切换。");
            string[] args=Environment.GetCommandLineArgs();
            int index=Array.IndexOf(args,"--plan");
            if(index>=0 && index+1<args.Length && ValidId(args[index+1])) {
                var plan=new System.Web.Script.Serialization.JavaScriptSerializer { MaxJsonLength=8*1024*1024 }.DeserializeObject(Updates.ReadFile("transactions/"+args[index+1]+"/plan.json"));
                Post(new {type="plan",plan=plan});
            }
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "--verify-ui") >= 0) await Verify();
        } catch (Exception error) { busy = false; Status("查询失败：" + error.Message + "，请刷新重试。"); }
    }
    private static bool ValidId(string id) { return System.Text.RegularExpressions.Regex.IsMatch(id,@"^[a-zA-Z0-9-]{1,80}$"); }
    private async Task LoadEnvironments() { Post(new {type="environments",data=await Updates.Run("environments")}); }
    private async Task EnvironmentCommand(string command)
    {
        busy=true;Status("正在处理环境操作…");
        try {
            string[] pieces=command.Split(':'); string worker=pieces[0];
            if(worker=="environments") { await LoadEnvironments(); busy=false;Status("环境列表已刷新");return; }
            if(worker=="prepare" && pieces.Length>=2 && pieces.Length<=3 && ValidId(pieces[1]) && (pieces.Length==2||pieces[2]=="fresh")) worker+=" "+pieces[1]+(pieces.Length==3?" fresh":"");
            else if(worker=="cleanup" && pieces.Length==2 && ValidId(pieces[1])) worker+=" "+pieces[1];
            else if(worker=="open-environment" && pieces.Length==2 && ValidId(pieces[1])) worker+=" "+pieces[1];
            else if(worker=="restore" && pieces.Length==3 && ValidId(pieces[1]) && (pieces[2]=="strict"||pieces[2]=="compatible")) worker+=" "+pieces[1]+" "+pieces[2];
            else if(worker=="pin" && pieces.Length==3 && System.Text.RegularExpressions.Regex.IsMatch(pieces[1],@"^(?:@[a-zA-Z0-9_.-]+/)?[a-zA-Z0-9_.-]+$") && (pieces[2]=="true"||pieces[2]=="false")) worker+=" "+pieces[1]+" "+pieces[2];
            else if(worker!="cancel" && worker!="recover" && worker!="cleanup-preview") throw new Exception("无效环境操作");
            if((worker=="recover" || pieces[0]=="open-environment") && Updates.RunningVersion()!="") throw new Exception("请先关闭 DSH 窗口，再切换保留环境；当前数据会保留。");
            var result=await Updates.Run(worker,Status);
            if(pieces[0]=="restore") Post(new {type="plan",plan=result});
            else if(pieces[0]=="cleanup-preview") Post(new {type="cleanup-preview",data=result});
            else Post(new {type="plan-done"});
            await LoadEnvironments();await SyncSelection();
            busy=false;Status(pieces[0]=="prepare" ? "更新已准备。重启 DSH 时将保存当前数据并整体切换。" : "操作完成；所有环境数据仍保留。");
        } catch(Exception error) {busy=false;Status("未完成："+error.Message);}
    }
    private async Task Verify()
    {
        await Task.Delay(1500);
        string result = await web.CoreWebView2.ExecuteScriptAsync(@"JSON.stringify({
            rows:document.querySelectorAll('#list .row').length,
            current:document.querySelectorAll('.row.running').length,
            badge:document.querySelector('.row.running .badge.active')?.textContent,
            background:getComputedStyle(document.body).backgroundColor,
            accent:getComputedStyle(document.documentElement).getPropertyValue('--accent'),
            version:document.getElementById('current').textContent})");
        File.WriteAllText(Path.Combine(Updates.Root, "desktop", "version-verification.json"), result);
        using (var output = File.Create(Path.Combine(Updates.Root, "desktop", "version-preview.png")))
            await web.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, output);
        string search = await web.CoreWebView2.ExecuteScriptAsync(@"document.getElementById('search').value='not-a-version';document.getElementById('search').dispatchEvent(new Event('input'));document.querySelectorAll('#list .row').length");
        if (search != "0") throw new Exception("Version search failed");
        await web.CoreWebView2.ExecuteScriptAsync("document.getElementById('search').value='';document.getElementById('search').dispatchEvent(new Event('input'))");
        Post(new { type = "theme", theme = new { bg = "rgb(250, 250, 250)", text = "rgb(20, 20, 20)", scheme = "light" } });
        await Task.Delay(100);
        string color = await web.CoreWebView2.ExecuteScriptAsync("getComputedStyle(document.body).backgroundColor");
        if (color != "\"rgb(250, 250, 250)\"") throw new Exception("Live theme update failed");
        lastTheme = ""; SyncTheme();
        File.AppendAllText(Path.Combine(Updates.Root, "desktop", "version-verification.json"), "\nPASS: search and live theme update");
    }

    // Verification-only path: proves this window starts from the cached skin
    // without a message and still follows live skin changes. No network used.
    private async Task VerifySkin()
    {
        var errors = new List<string>();
        var evidence = new Dictionary<string, object>();
        try {
            var cached = SkinTheme.Load();
            string expectedBg = Digits(SkinTheme.ToColor(cached, "bg", Color.Black));
            string expectedAccent = cached.ContainsKey("accent") ? cached["accent"] : "";
            // Seed rows so the screenshot shows the real card/toolbar styling.
            var rows = new List<object>();
            foreach (string version in new[] { "0.2.1-rc.2", "0.2.1-rc.1", "0.2.0" })
                rows.Add(new { version = version, published = "2026-10-21T00:00:00Z",
                    channel = version.EndsWith("rc.2") ? "预发布 · 推荐" : "预发布", compatible = true });
            Post(new { type = "catalog", data = new { versions = rows, selected = new { version = "0.2.1-rc.2" } }, running = "0.2.1-rc.2" });
            await Task.Delay(600);
            string report = await web.CoreWebView2.ExecuteScriptAsync(@"JSON.stringify({
                bootstrapped: window.__dshSkin || '',
                tokens: ['--bg','--surface','--surface-2','--surface-3','--text','--muted','--tertiary','--accent','--soft','--on-accent','--border','--hover','--focus','--danger','--success','--warn','--radius-sm','--radius','--radius-lg','--font','--font-size','--shadow','--wallpaper'].filter(v=>document.documentElement.style.getPropertyValue(v)!=='').length,
                background: getComputedStyle(document.body).backgroundColor,
                accent: getComputedStyle(document.documentElement).getPropertyValue('--accent').trim(),
                radius: getComputedStyle(document.documentElement).getPropertyValue('--radius').trim(),
                font: getComputedStyle(document.body).fontFamily,
                cardRadius: getComputedStyle(document.querySelector('.summary > div')).borderTopLeftRadius,
                scheme: getComputedStyle(document.body).colorScheme, caption: getComputedStyle(document.getElementById('caption')).backgroundColor, buttonText:getComputedStyle(document.querySelector('.analyze')).color, text:getComputedStyle(document.body).color, buttonFill:getComputedStyle(document.querySelector('.analyze')).backgroundColor })");
            // ExecuteScriptAsync returns a JSON string literal; unwrap it first.
            string json = new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<string>(report);
            var parsed = new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json);
            if(Updates.Value(parsed,"caption")!="rgba(0, 0, 0, 0)") errors.Add("caption is not transparent");
            if(Updates.Value(parsed,"buttonText")!=Updates.Value(parsed,"text")) errors.Add("button does not follow skin text");
            if(Updates.Value(parsed,"buttonFill")!="rgba(0, 0, 0, 0)") errors.Add("button still has a solid brand fill");
            evidence["catalog"] = "synthetic rows; style verification only";
            evidence["expectedBackground"] = expectedBg;
            evidence["expectedAccent"] = expectedAccent;
            foreach (var pair in parsed) evidence[pair.Key] = pair.Value;
            if (Updates.Value(parsed, "bootstrapped") != "cached") errors.Add("cached skin was not installed before the page painted");
            if (Convert.ToInt32(parsed["tokens"]) < 23) errors.Add("cached skin did not cover every token: " + Updates.Value(parsed, "tokens"));
            if (Digits(Updates.Value(parsed, "background")) != expectedBg) errors.Add("background is " + Updates.Value(parsed, "background") + ", cache says " + expectedBg);
            if (Updates.Value(parsed, "accent") != expectedAccent) errors.Add("accent is " + Updates.Value(parsed, "accent") + ", cache says " + expectedAccent);
            string expectedRadius = cached.ContainsKey("radius") ? cached["radius"] : "";
            if (expectedRadius != "" && Updates.Value(parsed, "cardRadius") != expectedRadius)
                errors.Add("card radius is " + Updates.Value(parsed, "cardRadius") + ", cache says " + expectedRadius);
            string compact = await web.CoreWebView2.ExecuteScriptAsync(@"(()=>{const r=document.querySelector('#list .row'),b=r.querySelector('.analyze');return r.getBoundingClientRect().height<=56&&b.getBoundingClientRect().height<=30&&getComputedStyle(r).borderLeftWidth==='1px'&&!document.querySelector('footer button')})()");
            if(compact!="true")errors.Add("compact row geometry failed");
            await web.CoreWebView2.ExecuteScriptAsync("for(let i=0;i<24;i++)document.getElementById('list').append(document.querySelector('#list .row').cloneNode(true));document.getElementById('content').scrollTop=220");
            await Task.Delay(100);
            string sticky=await web.CoreWebView2.ExecuteScriptAsync("Math.abs(document.querySelector('.sticky-tools').getBoundingClientRect().top-document.getElementById('content').getBoundingClientRect().top)<2&&document.querySelector('header').getBoundingClientRect().bottom<30");
            if(sticky!="true")errors.Add("search toolbar did not stay pinned");
            await web.CoreWebView2.ExecuteScriptAsync("render();document.getElementById('content').scrollTop=0");
            evidence["compactLayout"] = compact; evidence["stickyToolbar"] = sticky;
            // Live updates keep working for a skin change after the page loaded.
            var light = new Dictionary<string, string>(skin);
            light["bg"] = "rgb(250,250,250)"; light["text"] = "rgb(20,20,20)"; light["scheme"] = "light";
            Post(new { type = "theme", theme = light });
            await Task.Delay(200);
            string live = await web.CoreWebView2.ExecuteScriptAsync("getComputedStyle(document.body).backgroundColor");
            evidence["liveBackground"] = live;
            string lightOutline = await web.CoreWebView2.ExecuteScriptAsync("getComputedStyle(document.querySelector('.analyze')).color");
            evidence["lightOutline"] = lightOutline;
            if(Digits(lightOutline)!="20,20,20")errors.Add("light theme button did not follow text");
            if (Digits(live) != "250,250,250") errors.Add("live skin update failed: " + live);
            Post(new { type = "theme", theme = skin });
            await Task.Delay(200);
            using (var output = File.Create(Path.Combine(Updates.Root, "desktop", "skin-preview.png")))
                await web.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, output);
        } catch (Exception error) { errors.Add(error.Message); }
        evidence["result"] = errors.Count == 0 ? "PASS" : "FAIL";
        if (errors.Count > 0) evidence["errors"] = errors;
        try {
            File.WriteAllText(Path.Combine(Updates.Root, "desktop", "skin-verification.json"),
                new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(evidence));
        } catch (IOException) { /* Evidence is best effort. */ }
        Environment.ExitCode = errors.Count == 0 ? 0 : 1;
        try { if (IsHandleCreated) BeginInvoke((Action)Close); } catch (InvalidOperationException) {}
    }
    private static string Digits(Color color) { return color.R + "," + color.G + "," + color.B; }
    private static string Digits(string css)
    {
        var match = System.Text.RegularExpressions.Regex.Match(css ?? "", @"rgba?\((\d+)\D+(\d+)\D+(\d+)");
        return match.Success ? match.Groups[1].Value + "," + match.Groups[2].Value + "," + match.Groups[3].Value : css ?? "";
    }

    private async Task Install(string version)
    {
        busy = true; Status("正在准备 " + version + "，可以继续使用 DSH…");
        try {
            var plan=await Updates.Run("install " + version, Status);
            Post(new {type="plan",plan=plan});
            busy = false; Status("兼容性分析完成。请查看并确认插件与数据变更；尚未改变下次启动环境。");
        } catch (Exception error) { busy = false; Status("未切换版本：" + error.Message); }
    }
}
