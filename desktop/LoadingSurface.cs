using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

// Keep the native whale splash visible throughout startup and plugin installation.
internal sealed class LoadingSurface : Control
{
    private readonly Timer animation = new Timer { Interval = 40 };
    private readonly GraphicsPath logo = CreateLogo();
    private int frame;
    private Color accent = Color.FromArgb(86,134,254), muted, track;
    private bool installing;
    private string pluginTitle = "", installStage = "正在检查下载源";
    private string activePlugin = "";
    private int totalPlugins = 1, completedPlugins, singlePercent = -1;
    private DateTime stageChanged = DateTime.MinValue;
    internal bool Failed;

    internal LoadingSurface()
    {
        Dock = DockStyle.Fill;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        BackColor = Color.FromArgb(21,21,23); ForeColor = Color.FromArgb(235,238,242);
        ApplyTheme();
        animation.Tick += delegate { if (Visible) { frame++; Invalidate(); } };
        animation.Start();
        MouseDown += delegate(object s, MouseEventArgs e) {
            if (e.Y < 30 && e.X > Width - 44) FindForm().Close();
            else if (e.Y < 30) { Native.ReleaseCapture(); Native.SendMessage(FindForm().Handle, 0x00A1, new IntPtr(2), IntPtr.Zero); }
        };
    }
    internal void ApplyTheme()
    {
        var skin = SkinTheme.Load();
        BackColor = SkinTheme.ToColor(skin, "bg", Color.FromArgb(21,21,23));
        ForeColor = SkinTheme.ToColor(skin, "text", Color.FromArgb(235,238,242));
        accent = SkinTheme.ToColor(skin, "accent", Color.FromArgb(86,134,254));
        muted = SkinTheme.ToColor(skin, "muted", SkinTheme.Mix(BackColor, ForeColor, .56f));
        track = SkinTheme.Mix(BackColor, ForeColor, .10f);
    }
    internal void BeginInstall()
    {
        installing = true; Failed = false; activePlugin = ""; pluginTitle = "";
        installStage = "正在检查下载源"; totalPlugins = 1; completedPlugins = 0; singlePercent = -1;
        stageChanged = DateTime.MinValue; Invalidate();
    }
    internal void ReportInstall(System.Collections.Generic.Dictionary<string, object> row)
    {
        string plugin = Updates.Value(row, "plugin"), stage = Updates.Value(row, "stage");
        bool changed = activePlugin != plugin;
        activePlugin = plugin; pluginTitle = Updates.Value(row, "title");
        totalPlugins = Math.Max(1, Convert.ToInt32(row["total"]));
        completedPlugins = Math.Max(0, Math.Min(totalPlugins, Convert.ToInt32(row["completed"])));
        singlePercent = row["percent"] == null ? -1 : Math.Max(0, Math.Min(100, Convert.ToInt32(row["percent"])));
        string label;
        switch(stage) {
            case "download": label = "正在下载"; break;
            case "cache": label = "正在复用缓存"; break;
            case "install": label = "正在安装"; break;
            case "verify": label = "正在校验"; break;
            case "done": label = "安装完成"; break;
            case "resolve": label = "正在准备依赖"; break;
            case "dry-run": label = "正在准备安装"; break;
            default: label = "正在处理依赖"; break;
        }
        // Package-manager events often alternate several times a second. Keep
        // the visible stage calm while detailed diagnostics still go to disk.
        if(changed || stage == "done" || (DateTime.UtcNow - stageChanged).TotalMilliseconds >= 1400) {
            installStage = label; stageChanged = DateTime.UtcNow;
        }
        Invalidate();
    }
    internal void EndInstall() { installing = false; Invalidate(); }
    private void Bar(Graphics g, RectangleF bounds, int percent, bool secondary)
    {
        using(var path = SkinSurface.Rounded(Rectangle.Round(bounds), 2))
        using(var brush = new SolidBrush(track)) g.FillPath(brush,path);
        var saved = g.Save();
        g.SetClip(bounds);
        float width = percent < 0 ? bounds.Width * .24f : bounds.Width * percent / 100f;
        float left = percent < 0 ? bounds.X - width + (frame % 90) / 89f * (bounds.Width + width) : bounds.X;
        if(width > 0) using(var path = SkinSurface.Rounded(Rectangle.Round(new RectangleF(left,bounds.Y,width,bounds.Height)),2))
        using(var brush = new SolidBrush(secondary ? SkinTheme.Mix(BackColor, accent, .72f) : accent)) g.FillPath(brush,path);
        g.Restore(saved);
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g=e.Graphics; g.Clear(BackColor); g.SmoothingMode=SmoothingMode.AntiAlias;
        g.TextRenderingHint=System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        float center=Height/2f, top=installing ? center-155 : center-75;
        float wave=Failed?0:(float)Math.Sin(frame/15.0);
        var saved=g.Save();
        g.TranslateTransform(Width/2f-58,top+wave*3); g.ScaleTransform(5,5);
        using(var brush=new SolidBrush(Color.FromArgb(Failed?170:(int)(220+wave*25),ForeColor)))g.FillPath(brush,logo);
        g.Restore(saved);
        using(var name=new Font("Segoe UI",17,FontStyle.Regular))
        using(var small=new Font("Microsoft YaHei UI",10))
        using(var caption=new Font("Microsoft YaHei UI",8.5f))
        using(var brush=new SolidBrush(ForeColor))
        using(var dim=new SolidBrush(muted))
        using(var centered=new StringFormat { Alignment=StringAlignment.Center,LineAlignment=StringAlignment.Center,Trimming=StringTrimming.EllipsisCharacter,FormatFlags=StringFormatFlags.NoWrap }) {
            g.DrawString("DeepSeek Harness",name,brush,new RectangleF(0,top+105,Width,40),centered);
            g.DrawString("×",name,dim,new RectangleF(Width-44,0,44,30),centered);
            if(installing && !Failed) {
                float width=Math.Min(360,Width-80),x=(Width-width)/2;
                g.DrawString(pluginTitle==""?"正在准备推荐插件":pluginTitle,small,brush,new RectangleF(x,center+16,width,28),centered);
                g.DrawString(installStage+"…",caption,dim,new RectangleF(x,center+46,width,24),centered);
                using(var left=new StringFormat {Alignment=StringAlignment.Near})
                using(var right=new StringFormat {Alignment=StringAlignment.Far}) {
                    g.DrawString("当前插件",caption,dim,new RectangleF(x,center+89,width,20),left);
                    g.DrawString(singlePercent<0?"进行中":singlePercent+"%",caption,dim,new RectangleF(x,center+89,width,20),right);
                    Bar(g,new RectangleF(x,center+114,width,4),singlePercent,false);
                    g.DrawString("整体进度",caption,dim,new RectangleF(x,center+137,width,20),left);
                    g.DrawString(completedPlugins+" / "+totalPlugins,caption,dim,new RectangleF(x,center+137,width,20),right);
                    Bar(g,new RectangleF(x,center+162,width,3),completedPlugins*100/totalPlugins,true);
                }
            } else {
                using(var wrapped=new StringFormat { Alignment=StringAlignment.Center, LineAlignment=StringAlignment.Center })
                    g.DrawString(Text,small,brush,new RectangleF(35,Math.Max(center+90,Height-125),Width-70,90),wrapped);
                if(!Failed)for(int i=0;i<3;i++)using(var dot=new SolidBrush(Color.FromArgb(80+(int)(150*(.5+.5*Math.Sin(frame/8.0-i))),accent)))
                    g.FillEllipse(dot,Width/2f-18+i*15,center+85,6,6);
            }
        }
    }
    protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Invalidate(); }
    protected override void Dispose(bool disposing) { if(disposing){animation.Dispose();logo.Dispose();}base.Dispose(disposing); }
    private static GraphicsPath CreateLogo() { var p=new GraphicsPath();
p.StartFigure();
p.AddBezier(22.9168f,1.43018f,22.6713f,1.31018f,22.5658f,1.53918f,22.4223f,1.65519f);
p.AddBezier(22.4223f,1.65519f,22.3733f,1.69269f,22.3318f,1.74169f,22.2903f,1.78669f);
p.AddBezier(22.2903f,1.78669f,21.9317f,2.1697f,21.5127f,2.42121f,20.9657f,2.39121f);
p.AddBezier(20.9657f,2.39121f,20.1657f,2.34621f,19.4827f,2.59771f,18.8787f,3.20973f);
p.AddBezier(18.8787f,3.20973f,18.7502f,2.45521f,18.3236f,2.0047f,17.6746f,1.71569f);
p.AddBezier(17.6746f,1.71569f,17.3351f,1.56568f,16.9916f,1.41518f,16.7536f,1.08867f);
p.AddBezier(16.7536f,1.08867f,16.5876f,0.856163f,16.5421f,0.597155f,16.4591f,0.341647f);
p.AddBezier(16.4591f,0.341647f,16.4061f,0.187643f,16.3536f,0.0301382f,16.1761f,0.00363739f);
p.AddBezier(16.1761f,0.00363739f,15.9836f,-0.0263635f,15.9081f,0.135141f,15.8326f,0.270145f);
p.AddBezier(15.8326f,0.270145f,15.5306f,0.822162f,15.4136f,1.43018f,15.4251f,2.0462f);
p.AddBezier(15.4251f,2.0462f,15.4516f,3.43174f,16.0366f,4.53527f,17.1991f,5.3203f);
p.AddBezier(17.1991f,5.3203f,17.3311f,5.4103f,17.3651f,5.5003f,17.3236f,5.63181f);
p.AddBezier(17.3236f,5.63181f,17.2441f,5.90231f,17.1501f,6.16482f,17.0671f,6.43533f);
p.AddBezier(17.0671f,6.43533f,17.0141f,6.60784f,16.9351f,6.64584f,16.7501f,6.57033f);
p.AddBezier(16.7501f,6.57033f,16.1121f,6.30383f,15.5611f,5.90931f,15.074f,5.4328f);
p.AddBezier(15.074f,5.4328f,14.2475f,4.63328f,13.5f,3.75075f,12.568f,3.05973f);
p.AddBezier(12.568f,3.05973f,12.349f,2.89822f,12.13f,2.74822f,11.9034f,2.60522f);
p.AddBezier(11.9034f,2.60522f,10.9524f,1.68169f,12.028f,0.923165f,12.277f,0.833162f);
p.AddBezier(12.277f,0.833162f,12.5375f,0.739159f,12.3675f,0.41615f,11.5259f,0.42015f);
p.AddBezier(11.5259f,0.42015f,10.6844f,0.42365f,9.91439f,0.705658f,8.93286f,1.08117f);
p.AddBezier(8.93286f,1.08117f,8.78935f,1.13767f,8.63835f,1.17867f,8.48384f,1.21267f);
p.AddBezier(8.48384f,1.21267f,7.59332f,1.04367f,6.66829f,1.00617f,5.70226f,1.11517f);
p.AddBezier(5.70226f,1.11517f,3.88321f,1.31768f,2.43016f,2.1777f,1.36213f,3.64575f);
p.AddBezier(1.36213f,3.64575f,0.0790928f,5.4103f,-0.222916f,7.41536f,0.146595f,9.50642f);
p.AddBezier(0.146595f,9.50642f,0.535106f,11.7105f,1.66014f,13.535f,3.38869f,14.9616f);
p.AddBezier(3.38869f,14.9616f,5.18125f,16.4406f,7.24581f,17.1657f,9.60138f,17.0266f);
p.AddBezier(9.60138f,17.0266f,11.0319f,16.9441f,12.6245f,16.7526f,14.421f,15.2321f);
p.AddBezier(14.421f,15.2321f,14.874f,15.4576f,15.3496f,15.5476f,16.1381f,15.6151f);
p.AddBezier(16.1381f,15.6151f,16.7456f,15.6716f,17.3306f,15.5851f,17.7836f,15.4911f);
p.AddBezier(17.7836f,15.4911f,18.4931f,15.3411f,18.4441f,14.6841f,18.1876f,14.5636f);
p.AddBezier(18.1876f,14.5636f,16.1081f,13.595f,16.5646f,13.9891f,16.1496f,13.67f);
p.AddBezier(16.1496f,13.67f,17.2061f,12.42f,18.8202f,10.1979f,19.3182f,7.17235f);
p.AddBezier(19.3182f,7.17235f,19.3672f,6.83834f,19.4297f,6.36783f,19.4222f,6.09732f);
p.AddBezier(19.4222f,6.09732f,19.4182f,5.93231f,19.4562f,5.86831f,19.6447f,5.84931f);
p.AddBezier(19.6447f,5.84931f,20.1657f,5.78931f,20.6712f,5.64681f,21.1357f,5.3913f);
p.AddBezier(21.1357f,5.3913f,22.4833f,4.65528f,23.0268f,3.44624f,23.1548f,1.9972f);
p.AddBezier(23.1548f,1.9972f,23.1738f,1.77569f,23.1508f,1.54668f,22.9168f,1.43018f);
p.CloseFigure();
p.StartFigure();
p.AddBezier(11.1749f,14.4736f,9.15936f,12.889f,8.18184f,12.3675f,7.77832f,12.39f);
p.AddBezier(7.77832f,12.39f,7.40081f,12.4125f,7.46881f,12.8445f,7.55182f,13.126f);
p.AddBezier(7.55182f,13.126f,7.63882f,13.404f,7.75182f,13.5955f,7.91033f,13.8396f);
p.AddBezier(7.91033f,13.8396f,8.01983f,14.0011f,8.09533f,14.2411f,7.80083f,14.4216f);
p.AddBezier(7.80083f,14.4216f,7.15181f,14.8231f,6.02327f,14.2866f,5.97027f,14.2601f);
p.AddBezier(5.97027f,14.2601f,4.65673f,13.4865f,3.5587f,12.4655f,2.78467f,11.069f);
p.AddBezier(2.78467f,11.069f,2.03715f,9.72493f,1.60314f,8.28289f,1.53164f,6.74384f);
p.AddBezier(1.53164f,6.74384f,1.51264f,6.37233f,1.62214f,6.24082f,1.99215f,6.17332f);
p.AddBezier(1.99215f,6.17332f,2.47916f,6.08332f,2.98118f,6.06432f,3.46769f,6.13582f);
p.AddBezier(3.46769f,6.13582f,5.52476f,6.43633f,7.27581f,7.35586f,8.74385f,8.8129f);
p.AddBezier(8.74385f,8.8129f,9.58188f,9.64243f,10.2159f,10.634f,10.8689f,11.6025f);
p.AddBezier(10.8689f,11.6025f,11.5634f,12.631f,12.3105f,13.611f,13.262f,14.4146f);
p.AddBezier(13.262f,14.4146f,13.598f,14.6961f,13.866f,14.9101f,14.1225f,15.0681f);
p.AddBezier(14.1225f,15.0681f,13.349f,15.1546f,12.058f,15.1731f,11.1749f,14.4746f);
p.AddLine(11.1749f,14.4746f,11.1749f,14.4736f);
p.CloseFigure();
p.StartFigure();
p.AddBezier(12.141f,8.25988f,12.141f,8.09488f,12.273f,7.96338f,12.439f,7.96338f);
p.AddBezier(12.439f,7.96338f,12.4765f,7.96338f,12.5105f,7.97088f,12.541f,7.98188f);
p.AddBezier(12.541f,7.98188f,12.5825f,7.99688f,12.6205f,8.01938f,12.6505f,8.05338f);
p.AddBezier(12.6505f,8.05338f,12.7035f,8.10588f,12.7335f,8.18088f,12.7335f,8.25988f);
p.AddBezier(12.7335f,8.25988f,12.7335f,8.42489f,12.6015f,8.55639f,12.4355f,8.55639f);
p.AddBezier(12.4355f,8.55639f,12.2695f,8.55639f,12.141f,8.42489f,12.141f,8.25988f);
p.CloseFigure();
p.StartFigure();
p.AddBezier(15.1415f,9.79893f,14.949f,9.87793f,14.7565f,9.94544f,14.5715f,9.95294f);
p.AddBezier(14.5715f,9.95294f,14.2845f,9.96794f,13.9715f,9.85143f,13.8015f,9.70893f);
p.AddBezier(13.8015f,9.70893f,13.5375f,9.48742f,13.3485f,9.36342f,13.2695f,8.97691f);
p.AddBezier(13.2695f,8.97691f,13.2355f,8.8119f,13.2545f,8.55639f,13.2845f,8.40989f);
p.AddBezier(13.2845f,8.40989f,13.3525f,8.09438f,13.277f,7.89187f,13.0545f,7.70787f);
p.AddBezier(13.0545f,7.70787f,12.8735f,7.55786f,12.643f,7.51636f,12.39f,7.51636f);
p.AddBezier(12.39f,7.51636f,12.2955f,7.51636f,12.209f,7.47486f,12.1445f,7.44136f);
p.AddBezier(12.1445f,7.44136f,12.039f,7.38886f,11.9519f,7.25735f,12.035f,7.09585f);
p.AddBezier(12.035f,7.09585f,12.0615f,7.04335f,12.19f,6.91584f,12.22f,6.89334f);
p.AddBezier(12.22f,6.89334f,12.5635f,6.69784f,12.9595f,6.76184f,13.326f,6.90834f);
p.AddBezier(13.326f,6.90834f,13.6655f,7.04735f,13.9225f,7.30236f,14.292f,7.66287f);
p.AddBezier(14.292f,7.66287f,14.6695f,8.09838f,14.7375f,8.21838f,14.9525f,8.54539f);
p.AddBezier(14.9525f,8.54539f,15.1225f,8.8009f,15.277f,9.06341f,15.3831f,9.36392f);
p.AddBezier(15.3831f,9.36392f,15.4471f,9.55142f,15.3641f,9.70493f,15.1415f,9.79893f);
p.CloseFigure();
        return p;
    }
}
