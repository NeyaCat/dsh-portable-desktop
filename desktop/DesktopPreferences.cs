using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows.Forms;
using System.Web.Script.Serialization;

internal static class DesktopPreferences
{
    internal static string FilePath = Path.Combine(Updates.Root,"data","desktop-preferences.json");
    internal const int MaxCloseHistory = 20;

    private static Dictionary<string, object> Read()
    {
        try { return new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(File.ReadAllText(FilePath)); }
        catch (IOException) { return new Dictionary<string,object>(); }
        catch (ArgumentException) { return new Dictionary<string,object>(); }
    }

    private static void Write(Dictionary<string, object> data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
        string temp=FilePath+".tmp"; File.WriteAllText(temp,Updates.Json(data));
        if(File.Exists(FilePath)) File.Replace(temp,FilePath,null); else File.Move(temp,FilePath);
    }

    internal static bool IsIgnored(string version)
    {
        object value; if (!Read().TryGetValue("ignoredVersions",out value)) return false;
        var list=value as System.Collections.IEnumerable;
        if(list != null) foreach(object item in list) if(String.Equals(item as string,version,StringComparison.Ordinal)) return true;
        return false;
    }

    internal static void SaveIgnored(string version)
    {
        var data=Read();
        var versions=new List<string>(); object old;
        if(data.TryGetValue("ignoredVersions",out old) && old is System.Collections.IEnumerable)
            foreach(object item in (System.Collections.IEnumerable)old) if(item is string) versions.Add((string)item);
        if(!versions.Contains(version)) versions.Add(version);
        data["ignoredVersions"]=versions;
        Write(data);
    }

    // The close button no longer has a "never ask again" boolean. It remembers
    // one explicit decision instead: "exit" or "minimize". An empty value keeps
    // asking, which is the default for every portable folder.
    internal static string CloseAction
    {
        get
        {
            object value;
            if(!Read().TryGetValue("closeAction",out value)) return "";
            string text=value as string;
            return text=="exit" || text=="minimize" ? text : "";
        }
    }

    internal static void SaveCloseAction(string action)
    {
        var data=Read();
        if(action=="exit" || action=="minimize") data["closeAction"]=action;
        else data.Remove("closeAction");
        Write(data);
    }

    // Keep a short audit trail so the user's last window operations can be
    // inspected in desktop-preferences.json instead of only in desktop.log.
    internal static void RecordCloseAction(string action, bool remembered)
    {
        var data=Read();
        var history=new List<Dictionary<string,object>>();
        object old;
        if(data.TryGetValue("closeHistory",out old) && old is System.Collections.IEnumerable)
            foreach(object item in (System.Collections.IEnumerable)old)
            {
                var row=item as Dictionary<string,object>;
                if(row != null) history.Add(row);
            }
        var entry=new Dictionary<string,object>();
        entry["at"]=DateTime.Now.ToString("s");
        entry["action"]=action;
        entry["remembered"]=remembered;
        history.Add(entry);
        if(history.Count>MaxCloseHistory) history.RemoveRange(0,history.Count-MaxCloseHistory);
        data["closeHistory"]=history;
        Write(data);
    }
}

// Styled entirely from the same SkinTheme cache the Version Switch window uses:
// background, surface card, border, font family, corner radius, hover/focus and
// neutral text-coloured outlines. No separate palette and no brand-colour fill.
internal sealed class CloseConfirmation : Form
{
    internal readonly CheckBox Remember;
    internal readonly ExitActionButton Exit;
    internal readonly ExitActionButton Minimize;
    internal Dictionary<string, string> Skin;

    private readonly SkinSurface card;
    private readonly Label heading, subtitle, exitTitle, exitText, minimizeTitle, minimizeText, hint;
    private readonly System.Windows.Forms.Timer themeTimer = new System.Windows.Forms.Timer { Interval = 1200 };
    private readonly List<Font> fonts = new List<Font>();
    private string themeSignature = "";
    private Color surface = Color.FromArgb(35,35,36);

    internal Color SurfaceColor { get { return card.Surface; } }

    internal CloseConfirmation(Dictionary<string, string> skin)
    {
        Skin = skin ?? SkinTheme.Load();
        Text = "关闭 DeepSeek Harness";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        ClientSize = new Size(520,292);
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = MinimizeBox = false; ShowInTaskbar = false;
        AutoScaleDimensions = new SizeF(96F,96F);
        AutoScaleMode = AutoScaleMode.Dpi;

        heading = new Label { Text="关闭 DeepSeek Harness", AutoSize=false, Location=new Point(28,16), Size=new Size(464,30) };
        subtitle = new Label { Text="选择本次关闭要执行的操作；按 Esc 可继续使用。", AutoSize=false, Location=new Point(28,48), Size=new Size(464,22) };
        card = new SkinSurface { Location=new Point(28,100), Size=new Size(464,118) };
        exitTitle = new Label { Text="退出 DSH", AutoSize=false, Location=new Point(16,12), Size=new Size(432,20) };
        exitText = new Label { Text="结束本次启动的 DSH 后台，正在执行的任务也会停止。", AutoSize=false, Location=new Point(16,34), Size=new Size(432,22) };
        minimizeTitle = new Label { Text="最小化到托盘", AutoSize=false, Location=new Point(16,62), Size=new Size(432,20) };
        minimizeText = new Label { Text="窗口隐藏到右下角系统托盘，后台任务继续运行。", AutoSize=false, Location=new Point(16,84), Size=new Size(432,22) };
        card.Controls.AddRange(new Control[]{exitTitle,exitText,minimizeTitle,minimizeText});

        Remember = new CheckBox { Text="记住我的选择", AutoSize=true, Location=new Point(28,247), TabIndex=0 };
        hint = new Label { Text="勾选后下次点击 × 直接执行所选操作；按 Esc 或关闭本窗口可继续使用。", AutoSize=false, Location=new Point(28,72), Size=new Size(464,20) };
        Exit = new ExitActionButton { Text="退出 DSH", DialogResult=DialogResult.OK, Size=new Size(136,40), Location=new Point(208,236), Danger=true, TabIndex=2 };
        Minimize = new ExitActionButton { Text="最小化到托盘", DialogResult=DialogResult.Retry, Size=new Size(136,40), Location=new Point(356,236), Primary=true, TabIndex=1 };

        Controls.AddRange(new Control[]{heading,subtitle,card,Remember,Exit,Minimize,hint});
        AcceptButton = Minimize;

        themeSignature = SkinTheme.Signature();
        ApplySkin(Skin);
        themeTimer.Tick += delegate {
            string signature = SkinTheme.Signature();
            if (signature == themeSignature) return;
            themeSignature = signature;
            Skin = SkinTheme.Load();
            ApplySkin(Skin);
        };
        themeTimer.Start();
    }

    private void ApplySkin(Dictionary<string, string> skin)
    {
        Color background = SkinTheme.ToColor(skin,"bg",Color.FromArgb(21,21,23));
        surface = SkinTheme.ToColor(skin,"surface",Color.FromArgb(35,35,36));
        Color text = SkinTheme.ToColor(skin,"text",Color.FromArgb(235,238,242));
        Color muted = SkinTheme.ToColor(skin,"muted",Color.FromArgb(173,178,184));
        Color danger = SkinTheme.ToColor(skin,"danger",Color.FromArgb(236,19,19));
        Color hover = SkinTheme.Blend(background,skin,"hover",SkinTheme.Mix(background,text,0.08f));
        Color focus = SkinTheme.ToColor(skin,"focus",text);
        Color border = SkinTheme.Blend(background,skin,"border",SkinTheme.Mix(background,text,0.12f));
        int radius = SkinTheme.RadiusPixels(skin,"radius",12);
        int radiusSm = SkinTheme.RadiusPixels(skin,"radiusSm",8);

        BackColor = background; ForeColor = text;
        card.BackColor = background; card.Surface = surface; card.Border = border; card.Radius = radius;
        foreach (Control label in card.Controls) label.BackColor = surface;
        heading.ForeColor = text;
        subtitle.ForeColor = muted;
        exitTitle.ForeColor = text;
        exitText.ForeColor = muted;
        minimizeTitle.ForeColor = text;
        minimizeText.ForeColor = muted;
        hint.ForeColor = muted; hint.BackColor = background;
        Remember.BackColor = background; Remember.ForeColor = muted;
        Minimize.ApplySkin(background,text,text,hover,focus,radiusSm);
        Exit.ApplySkin(background,danger,danger,SkinTheme.Mix(background,danger,0.10f),danger,radiusSm);

        SetFont(heading,14f,FontStyle.Bold);
        SetFont(this,9f,FontStyle.Regular);
        SetFont(subtitle,9f,FontStyle.Regular);
        SetFont(exitTitle,10f,FontStyle.Regular);
        SetFont(exitText,8.5f,FontStyle.Regular);
        SetFont(minimizeTitle,10f,FontStyle.Regular);
        SetFont(minimizeText,8.5f,FontStyle.Regular);
        SetFont(Remember,9f,FontStyle.Regular);
        SetFont(Exit,9f,FontStyle.Regular);
        SetFont(Minimize,9f,FontStyle.Regular);
        SetFont(hint,8.25f,FontStyle.Regular);

        card.Invalidate();
        SkinTheme.ApplyWindowFrame(this,skin);
    }

    private void SetFont(Control control, float points, FontStyle style)
    {
        Font font = SkinTheme.CreateFont(Skin,points,style);
        control.Font = font;
        fonts.Add(font);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        SkinTheme.ApplyWindowFrame(this,Skin);
    }

    protected override bool ProcessCmdKey(ref Message message, Keys keyData)
    {
        // Esc keeps the current window alive; it never records a decision.
        if (keyData == Keys.Escape) { DialogResult=DialogResult.Cancel; return true; }
        return base.ProcessCmdKey(ref message,keyData);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) themeTimer.Dispose();
        base.Dispose(disposing);
        if (disposing) {
            // Controls are gone now; release the theme fonts we created.
            foreach (Font font in fonts) font.Dispose();
            fonts.Clear();
        }
    }
}

// Surface card painted from the same tokens; children keep the card colour so
// the rounded corners show the dialog background behind them.
internal sealed class SkinSurface : Panel
{
    internal Color Surface = Color.FromArgb(35,35,36);
    internal Color Border = Color.FromArgb(60,60,60);
    internal int Radius = 12;

    internal SkinSurface()
    {
        SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.ResizeRedraw,true);
    }

    internal static GraphicsPath Rounded(Rectangle rect, int radius)
    {
        var path = new GraphicsPath();
        int diameter = Math.Max(2,radius*2);
        if (diameter >= Math.Min(rect.Width,rect.Height)) {
            diameter = Math.Max(2,Math.Min(rect.Width,rect.Height)-1);
        }
        path.AddArc(rect.Left,rect.Top,diameter,diameter,180,90);
        path.AddArc(rect.Right-diameter,rect.Top,diameter,diameter,270,90);
        path.AddArc(rect.Right-diameter,rect.Bottom-diameter,diameter,diameter,0,90);
        path.AddArc(rect.Left,rect.Bottom-diameter,diameter,diameter,90,90);
        path.CloseFigure();
        return path;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Color background = Parent != null ? Parent.BackColor : BackColor;
        e.Graphics.Clear(background);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        int radius = Math.Max(2,(int)Math.Round(Radius*e.Graphics.DpiX/96.0));
        var rect = new Rectangle(0,0,Width-1,Height-1);
        using (var path=Rounded(rect,radius)) {
            using (var brush=new SolidBrush(Surface)) e.Graphics.FillPath(brush,path);
            using (var pen=new Pen(Border,1)) e.Graphics.DrawPath(pen,path);
        }
    }
}

// Neutral outline button from Version Switch's compact language: transparent
// fill, text-coloured border (stronger for the primed action), hover from the
// cached hover token, no accent fill.
internal sealed class ExitActionButton : Button
{
    internal bool Primary;
    internal bool Danger;
    internal Color BorderColor = Color.FromArgb(120,120,120);
    internal Color HoverFill = Color.FromArgb(50,50,50);
    internal Color PressFill = Color.FromArgb(60,60,60);
    internal Color FocusColor = Color.FromArgb(235,238,242);
    internal int CornerRadius = 8;
    private bool hovered, pressed;

    internal ExitActionButton()
    {
        SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer,true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        Cursor = Cursors.Hand;
    }

    internal void ApplySkin(Color background, Color textColor, Color accentColor, Color hoverFill, Color focusColor, int cornerRadius)
    {
        BackColor = background;
        ForeColor = textColor;
        BorderColor = SkinTheme.Mix(background,accentColor,Primary ? 0.62f : 0.46f);
        HoverFill = hoverFill;
        PressFill = SkinTheme.Mix(background,accentColor,0.16f);
        FocusColor = focusColor;
        CornerRadius = cornerRadius;
        Invalidate();
    }

    protected override void OnMouseEnter(EventArgs e) { hovered=true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hovered=false; pressed=false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { pressed=true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { pressed=false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(BackColor);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        int radius = Math.Max(2,(int)Math.Round(CornerRadius*e.Graphics.DpiX/96.0));
        var rect = new Rectangle(1,1,Width-3,Height-3);
        Color fill = pressed ? PressFill : hovered ? HoverFill : BackColor;
        using (var path=SkinSurface.Rounded(rect,radius)) {
            using (var brush=new SolidBrush(fill)) e.Graphics.FillPath(brush,path);
            using (var pen=new Pen(Focused ? FocusColor : BorderColor,Focused ? 2 : 1)) e.Graphics.DrawPath(pen,path);
        }
        TextRenderer.DrawText(e.Graphics,Text,Font,ClientRectangle,ForeColor,
            TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter|TextFormatFlags.NoPrefix);
    }
}

// Native menus are unavoidable in the tray, so their renderer consumes the same
// cached tokens as Version Switch instead of using the Windows default chrome.
internal sealed class SkinMenuRenderer : ToolStripRenderer
{
    private static readonly Dictionary<string, Font> FontCache = new Dictionary<string, Font>();
    private readonly Color background, surface, text, muted, border, hover, danger;
    private readonly bool trayStyle;
    private readonly int radius;

    internal SkinMenuRenderer(Dictionary<string, string> skin, bool trayStyle = false)
    {
        background = SkinTheme.ToColor(skin,"bg",Color.FromArgb(21,21,23));
        surface = SkinTheme.ToColor(skin,"surface",Color.FromArgb(35,35,36));
        text = SkinTheme.ToColor(skin,"text",Color.FromArgb(235,238,242));
        muted = SkinTheme.ToColor(skin,"muted",Color.FromArgb(173,178,184));
        border = SkinTheme.Blend(surface,skin,"border",SkinTheme.Mix(surface,text,0.12f));
        hover = SkinTheme.Blend(surface,skin,"hover",SkinTheme.Mix(surface,text,0.08f));
        radius = SkinTheme.RadiusPixels(skin,"radiusSm",6);
        danger = SkinTheme.ToColor(skin,"danger",Color.FromArgb(236,19,19));
        this.trayStyle = trayStyle;
    }

    internal static void Style(ContextMenuStrip menu, bool trayStyle = false)
    {
        if (menu == null) return;
        Dictionary<string, string> skin = SkinTheme.Load();
        Color surface = SkinTheme.ToColor(skin,"surface",Color.FromArgb(35,35,36));
        Color text = SkinTheme.ToColor(skin,"text",Color.FromArgb(235,238,242));
        menu.SuspendLayout();
        try {
            menu.Renderer = new SkinMenuRenderer(skin,trayStyle);
            menu.BackColor = surface;
            menu.ForeColor = text;
            menu.Font = MenuFont(skin,trayStyle);
            menu.ShowImageMargin = false;
            menu.ShowCheckMargin = false;
            menu.Padding = new Padding(trayStyle ? 8 : 6);
            menu.DropShadowEnabled = true;
            foreach (ToolStripItem item in menu.Items) {
                item.ForeColor = text;
                item.BackColor = Color.Transparent;
                if (item is ToolStripSeparator) {
                    item.AutoSize = false;
                    item.Height = 9;
                    item.Margin = new Padding(6,2,6,2);
                } else {
                    item.Padding = new Padding(8,5,8,5);
                    if (trayStyle) {
                        // Size from the rendered font so larger display scales retain breathing room.
                        float scale = menu.Font.Height / 17f;
                        item.AutoSize = false;
                        item.Size = new Size((int)(252*scale),Math.Max(34,(int)(34*scale)));
                        item.Margin = Padding.Empty;
                    }
                }
            }
        } finally { menu.ResumeLayout(); }
        if (trayStyle) {
            // Native dropdown sizing otherwise measures only text and clips the icon column.
            float scale=menu.Font.Height/17f;
            int edge=Math.Max(6,(int)(6*scale));
            // Dropdowns ignore outer Padding during native layout, but honor item margins.
            if(menu.Items.Count>0) {
                menu.Items[0].Margin=new Padding(0,edge,0,0);
                menu.Items[menu.Items.Count-1].Margin=new Padding(0,0,0,edge);
            }
            int height=4;
            foreach(ToolStripItem item in menu.Items) height+=item.Height+item.Margin.Vertical;
            menu.AutoSize=false;
            menu.Size=new Size((int)(252*scale),height);
            // Clip the actual native popup, not only its painted border.
            Region oldRegion=menu.Region;
            using(var outline=SkinSurface.Rounded(new Rectangle(0,0,menu.Width,menu.Height),(int)(12*scale)))
                menu.Region=new Region(outline);
            if(oldRegion!=null) oldRegion.Dispose();
        }
    }

    private static Font MenuFont(Dictionary<string, string> skin, bool trayStyle)
    {
        string family = SkinTheme.FontFamily(skin);
        string key = family+(trayStyle ? "|10" : "|9");
        Font font;
        if (!FontCache.TryGetValue(key,out font)) {
            font = SkinTheme.CreateFont(skin,trayStyle ? 10f : 9f,FontStyle.Regular);
            FontCache[key] = font;
        }
        return font;
    }

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        using (var brush=new SolidBrush(surface)) e.Graphics.FillRectangle(brush,e.ToolStrip.ClientRectangle);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        var bounds=e.ToolStrip.ClientRectangle;
        e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;
        using(var pen=new Pen(border,1)) {
            if(trayStyle) {
                using(var outline=SkinSurface.Rounded(new Rectangle(0,0,bounds.Width-1,bounds.Height-1),(int)(12*(e.ToolStrip.Font.Height/17f))))
                    e.Graphics.DrawPath(pen,outline);
            } else e.Graphics.DrawRectangle(pen,0,0,bounds.Width-1,bounds.Height-1);
        }
    }
    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        if (!e.Item.Selected && !e.Item.Pressed) return;
        float scale=e.Item.Font.Height/17f;
        int inset=(int)(10*scale), vertical=Math.Max(2,(int)(2*scale));
        var rect = trayStyle ? new Rectangle(inset,vertical,e.Item.Width-2*inset,e.Item.Height-2*vertical) : new Rectangle(2,1,e.Item.Width-4,e.Item.Height-2);
        e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;
        int corner = Math.Max(2,radius);
        using (var path=SkinSurface.Rounded(rect,corner))
        using (var brush=new SolidBrush(hover)) e.Graphics.FillPath(brush,path);
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        Color color = e.Item.Enabled ? text : muted;
        Rectangle textBounds = e.TextRectangle;
        if (trayStyle) {
            if (e.Item.Enabled && e.Item.Text=="退出 DSH") color=danger;
            float scale=e.TextFont.Height/17f;
            int inset=(int)(48*scale);
            textBounds=new Rectangle(inset,0,e.Item.Width-inset-12,e.Item.Height);
            DrawTrayIcon(e.Graphics,e.Item,color,scale);
        }
        TextRenderer.DrawText(e.Graphics,e.Text,e.TextFont,textBounds,color,
            TextFormatFlags.Left|TextFormatFlags.VerticalCenter|TextFormatFlags.SingleLine|
            TextFormatFlags.NoPrefix|TextFormatFlags.EndEllipsis);
    }

    // Small line icons avoid platform-specific symbol fonts and keep all four actions aligned.
    private static void DrawTrayIcon(Graphics graphics, ToolStripItem item, Color color, float scale)
    {
        var state=graphics.Save();
        try {
            graphics.SmoothingMode=SmoothingMode.AntiAlias;
            graphics.TranslateTransform(22*scale,(item.Height-16*scale)/2f);
            graphics.ScaleTransform(scale,scale);
            using(var pen=new Pen(color,1.35f)) {
                pen.StartCap=pen.EndCap=LineCap.Round;
                if(item.Text=="新会话") {
                    graphics.DrawLine(pen,8,2,8,14); graphics.DrawLine(pen,2,8,14,8);
                } else if(item.Text=="版本管理") {
                    graphics.DrawRectangle(pen,2,5,12,9); graphics.DrawLine(pen,4,2,12,2);
                    graphics.DrawLine(pen,6,9,10,9);
                } else if(item.Text=="退出 DSH") {
                    graphics.DrawArc(pen,2,2,12,12,-50,280); graphics.DrawLine(pen,8,0,8,7);
                } else {
                    graphics.DrawRectangle(pen,1,2,14,12); graphics.DrawLine(pen,1,6,15,6);
                }
            }
        } finally { graphics.Restore(state); }
    }
    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        var bounds = e.Item.ContentRectangle;
        int y = bounds.Top + bounds.Height/2;
        using (var pen=new Pen(border,1))
            e.Graphics.DrawLine(pen,bounds.Left+8,y,bounds.Right-8,y);
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        var rect = e.ArrowRectangle;
        int x = rect.Left+rect.Width/2;
        int y = rect.Top+rect.Height/2;
        using (var brush=new SolidBrush(text))
        using (var path=new GraphicsPath()) {
            path.AddPolygon(new Point[]{ new Point(x-4,y-2),new Point(x+4,y-2),new Point(x,y+3) });
            e.Graphics.FillPath(brush,path);
        }
    }
}

// The bundled ICO paints the whale as a black silhouette on transparency, so the
// taskbar icon reuses that silhouette twice: black as requested, and the same
// shape in white when Windows itself uses a dark taskbar (black would vanish).
internal static class TrayIconFactory
{
    internal static Icon Create(Icon sourceIcon, int size)
    {
        using(var bitmap = CreateBitmap(sourceIcon,size))
        {
            IntPtr handle=bitmap.GetHicon();
            try { using(var temporary=Icon.FromHandle(handle)) return (Icon)temporary.Clone(); }
            finally { Native.DestroyIcon(handle); }
        }
    }

    internal static Bitmap CreateBitmap(Icon sourceIcon, int size)
    {
        bool white=TaskbarUsesDarkTheme();
        var canvas=new Bitmap(size,size,System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            using(var graphics=Graphics.FromImage(canvas))
            using(var resized=new Icon(sourceIcon,size,size))
            {
                graphics.Clear(Color.Transparent);
                graphics.InterpolationMode=InterpolationMode.HighQualityBicubic;
                graphics.PixelOffsetMode=PixelOffsetMode.HighQuality;
                graphics.DrawIcon(resized,new Rectangle(0,0,size,size));
            }
            for(int y=0;y<size;y++)
                for(int x=0;x<size;x++)
                {
                    Color pixel=canvas.GetPixel(x,y);
                    if(pixel.A==0) continue;
                    canvas.SetPixel(x,y,white ? Color.FromArgb(pixel.A,255,255,255) : Color.FromArgb(pixel.A,0,0,0));
                }
            return canvas;
        }
        catch { canvas.Dispose(); throw; }
    }

    internal static bool TaskbarUsesDarkTheme()
    {
        try
        {
            using(var key=Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
            {
                object value=key==null ? null : key.GetValue("SystemUsesLightTheme");
                if(value is int) return (int)value==0;
            }
        }
        catch (Exception) { }
        return false;
    }
}
