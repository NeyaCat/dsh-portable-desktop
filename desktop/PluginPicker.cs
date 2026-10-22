using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

// First-run recommended-plugin picker. Required plugins are shown checked and
// locked; every other plugin is grouped by category and starts checked. The
// external link opens the plugin's documentation so the choice is informed.
internal sealed class PluginPicker : Form
{
    private readonly List<RecommendedPlugin> catalog;
    private readonly Dictionary<string, string> skin;
    private readonly List<CheckBox> optionalChecks = new List<CheckBox>();
    private readonly Dictionary<RecommendedPlugin, CheckBox> checkByPlugin = new Dictionary<RecommendedPlugin, CheckBox>();
    private readonly List<Control> layoutOrder = new List<Control>();
    private readonly List<Font> fonts = new List<Font>();
    private readonly ToolTip tips = new ToolTip();
    private readonly Color background, surface, text, muted, border, accent, hover;
    private readonly int radius, radiusSm;

    private readonly Label heading = new Label();
    private readonly Label subtitle = new Label();
    private readonly Label summary = new Label();
    private readonly Panel scroll = new Panel();
    private readonly Panel content = new Panel();
    private readonly ExitActionButton selectAll = new ExitActionButton();
    private readonly ExitActionButton selectNone = new ExitActionButton();
    private readonly ExitActionButton cancel = new ExitActionButton();
    private readonly ExitActionButton install = new ExitActionButton();

    internal PluginPicker(List<RecommendedPlugin> plugins)
    {
        catalog = plugins ?? new List<RecommendedPlugin>();
        skin = SkinTheme.Load();
        background = SkinTheme.ToColor(skin, "bg", Color.FromArgb(21, 21, 23));
        surface = SkinTheme.ToColor(skin, "surface", Color.FromArgb(35, 35, 36));
        text = SkinTheme.ToColor(skin, "text", Color.FromArgb(235, 238, 242));
        muted = SkinTheme.ToColor(skin, "muted", Color.FromArgb(173, 178, 184));
        border = SkinTheme.Blend(background, skin, "border", SkinTheme.Mix(background, text, 0.12f));
        accent = SkinTheme.ToColor(skin, "accent", Color.FromArgb(86, 134, 254));
        hover = SkinTheme.Blend(background, skin, "hover", SkinTheme.Mix(background, text, 0.08f));
        radius = SkinTheme.RadiusPixels(skin, "radius", 12);
        radiusSm = SkinTheme.RadiusPixels(skin, "radiusSm", 8);

        Text = "DSH 首次启动 - 选择推荐插件";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.Sizable;
        ShowInTaskbar = false;
        MinimizeBox = false;
        MaximizeBox = false;
        ClientSize = new Size(860, 720);
        MinimumSize = new Size(720, 560);
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        DoubleBuffered = true;
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        BackColor = background;
        ForeColor = text;

        heading.Text = "选择推荐插件";
        subtitle.Text = "选择安装后，插件市场、代理和网络配置会自动包含且不能取消；选择“暂不安装”则不安装任何插件。其余插件可逐项勾选，安装前会检测网络，直连不可用时自动切换镜像源。";
        summary.Text = "";

        scroll.AutoScroll = true;
        scroll.BackColor = background;
        scroll.BorderStyle = BorderStyle.None;
        content.BackColor = background;
        scroll.Controls.Add(content);

        selectAll.Text = "全选";
        selectNone.Text = "全不选";
        cancel.Text = "暂不安装";
        install.Text = "安装所选";
        cancel.DialogResult = DialogResult.Cancel;
        install.DialogResult = DialogResult.OK;
        AcceptButton = install;
        CancelButton = cancel;
        selectAll.Click += delegate { SetOptional(true); };
        selectNone.Click += delegate { SetOptional(false); };

        Controls.Add(heading);
        Controls.Add(subtitle);
        Controls.Add(scroll);
        Controls.Add(summary);
        Controls.Add(selectAll);
        Controls.Add(selectNone);
        Controls.Add(cancel);
        Controls.Add(install);

        BuildContent();
        ApplySkin();
        UpdateSummary();
        ApplyButtonSkin(selectAll, false);
        ApplyButtonSkin(selectNone, false);
        ApplyButtonSkin(cancel, false);
        ApplyButtonSkin(install, true);
        LayoutChrome();
    }

    internal List<string> SelectedNames
    {
        get
        {
            var names = new List<string>();
            foreach (RecommendedPlugin plugin in catalog)
                if (plugin.Required) names.Add(plugin.Name);
            foreach (RecommendedPlugin plugin in catalog)
            {
                CheckBox check;
                if (!plugin.Required && checkByPlugin.TryGetValue(plugin, out check) && check.Checked)
                    names.Add(plugin.Name);
            }
            return names;
        }
    }

    private void BuildContent()
    {
        var required = new List<RecommendedPlugin>();
        foreach (RecommendedPlugin plugin in catalog)
            if (plugin.Required) required.Add(plugin);
        AddSection("必需组件", "选择安装时，以下三件是后续安装和管理插件的基础，始终包含。", required);

        string[] categories = { "主题", "辅助", "布局", "功能" };
        var seen = new HashSet<string>();
        foreach (string category in categories)
        {
            var items = new List<RecommendedPlugin>();
            foreach (RecommendedPlugin plugin in catalog)
                if (!plugin.Required && plugin.Category == category) items.Add(plugin);
            seen.Add(category);
            if (items.Count > 0) AddSection(category, "", items);
        }
        var others = new List<RecommendedPlugin>();
        foreach (RecommendedPlugin plugin in catalog)
            if (!plugin.Required && !seen.Contains(plugin.Category)) others.Add(plugin);
        if (others.Count > 0) AddSection("其他", "", others);
    }

    private void AddSection(string category, string note, List<RecommendedPlugin> plugins)
    {
        Label header = new Label();
        header.Text = category;
        header.AutoSize = false;
        header.Height = 32;
        header.ForeColor = text;
        header.BackColor = background;
        header.Padding = new Padding(4, 4, 0, 0);
        content.Controls.Add(header);
        layoutOrder.Add(header);
        SetFont(header, 11f, FontStyle.Bold);

        if (!String.IsNullOrEmpty(note))
        {
            Label hint = new Label();
            hint.Text = note;
            hint.AutoSize = false;
            hint.Height = 22;
            hint.ForeColor = muted;
            hint.BackColor = background;
            hint.Padding = new Padding(4, 0, 0, 0);
            content.Controls.Add(hint);
            layoutOrder.Add(hint);
            SetFont(hint, 8.5f, FontStyle.Regular);
        }

        foreach (RecommendedPlugin plugin in plugins)
        {
            Control row = CreateRow(plugin);
            content.Controls.Add(row);
            layoutOrder.Add(row);
        }
    }

    private Control CreateRow(RecommendedPlugin plugin)
    {
        SkinSurface row = new SkinSurface();
        row.Height = 64;
        row.BackColor = background;
        row.Surface = surface;
        row.Border = border;
        row.Radius = radius;

        CheckBox check = new CheckBox();
        check.Text = "";
        check.AutoSize = false;
        check.Size = new Size(22, 22);
        check.Location = new Point(14, 20);
        check.Checked = true;
        check.AutoCheck = !plugin.Required;
        check.TabStop = !plugin.Required;
        check.BackColor = surface;
        check.ForeColor = text;
        check.FlatStyle = FlatStyle.Flat;
        if (!plugin.Required)
        {
            optionalChecks.Add(check);
            checkByPlugin[plugin] = check;
            check.CheckedChanged += delegate { UpdateSummary(); };
        }

        Label title = new Label();
        title.Text = plugin.Title + (plugin.Required ? "  ·  必需" : "");
        title.AutoSize = false;
        title.Height = 22;
        title.ForeColor = text;
        title.BackColor = surface;

        Label description = new Label();
        description.Text = plugin.Description;
        description.AutoSize = false;
        description.Height = 20;
        description.ForeColor = muted;
        description.BackColor = surface;
        description.AutoEllipsis = true;

        Label link = new Label();
        link.Text = "查看功能 ↗";
        link.AutoSize = false;
        link.Height = 18;
        link.Width = 96;
        link.TextAlign = ContentAlignment.MiddleRight;
        link.ForeColor = accent;
        link.BackColor = surface;
        link.Cursor = Cursors.Hand;
        link.Visible = !String.IsNullOrEmpty(plugin.Url);
        link.Click += delegate { OpenUrl(plugin.Url); };

        row.Controls.Add(check);
        row.Controls.Add(title);
        row.Controls.Add(description);
        row.Controls.Add(link);
        row.Resize += delegate { LayoutRow(row, title, description, link); };
        LayoutRow(row, title, description, link);

        tips.SetToolTip(title, plugin.Name + " " + plugin.Version);
        tips.SetToolTip(description, plugin.Description);
        if (!String.IsNullOrEmpty(plugin.Url)) tips.SetToolTip(link, plugin.Url);
        SetFont(title, 10f, FontStyle.Bold);
        SetFont(description, 8.5f, FontStyle.Regular);
        SetFont(link, 8.5f, FontStyle.Underline);
        SetFont(check, 9f, FontStyle.Regular);
        return row;
    }

    private static void LayoutRow(Control row, Label title, Label description, Label link)
    {
        title.SetBounds(48, 8, Math.Max(100, row.Width - 62), 22);
        // Reserve the link column even while the form is not visible yet:
        // Control.Visible is false until the parent is shown.
        description.SetBounds(48, 32, Math.Max(120, row.Width - 58 - 104), 20);
        link.SetBounds(Math.Max(48, row.Width - 110), 32, 96, 18);
    }

    private void ApplySkin()
    {
        BackColor = background;
        ForeColor = text;
        heading.ForeColor = text;
        heading.BackColor = background;
        subtitle.ForeColor = muted;
        subtitle.BackColor = background;
        summary.ForeColor = muted;
        summary.BackColor = background;
        SetFont(heading, 16f, FontStyle.Bold);
        SetFont(subtitle, 9f, FontStyle.Regular);
        SetFont(summary, 9f, FontStyle.Regular);
        SkinTheme.ApplyWindowFrame(this, skin);
    }

    private void ApplyButtonSkin(ExitActionButton button, bool primary)
    {
        button.Primary = primary;
        button.ApplySkin(background, text, text, hover, text, radiusSm);
        SetFont(button, 9f, FontStyle.Regular);
    }

    private void LayoutChrome()
    {
        int margin = 20;
        heading.SetBounds(margin, 14, ClientSize.Width - margin * 2, 32);
        subtitle.SetBounds(margin, 50, ClientSize.Width - margin * 2, 36);
        scroll.SetBounds(margin, 96, ClientSize.Width - margin * 2, Math.Max(120, ClientSize.Height - 190));
        int y = ClientSize.Height - 72;
        install.SetBounds(ClientSize.Width - margin - 120, y, 120, 40);
        cancel.SetBounds(install.Left - 10 - 104, y, 104, 40);
        selectNone.SetBounds(cancel.Left - 10 - 88, y, 88, 40);
        selectAll.SetBounds(selectNone.Left - 10 - 88, y, 88, 40);
        summary.SetBounds(margin + 4, y + 10, Math.Max(160, selectAll.Left - margin - 12), 24);
        LayoutContent();
    }

    private void LayoutContent()
    {
        int width = scroll.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 2;
        if (width < 240) width = 240;
        content.Width = width;
        int y = 0;
        foreach (Control control in layoutOrder)
        {
            control.Left = 0;
            control.Width = width;
            control.Top = y;
            y += control.Height + 8;
        }
        content.Height = y + 4;
    }

    private void SetOptional(bool value)
    {
        foreach (CheckBox check in optionalChecks) check.Checked = value;
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        int required = 0, selected = 0;
        foreach (RecommendedPlugin plugin in catalog)
            if (plugin.Required) { required++; selected++; }
        foreach (CheckBox check in optionalChecks)
            if (check.Checked) selected++;
        summary.Text = "已选择 " + selected + " 个插件（" + required + " 个必需）";
    }

    private void OpenUrl(string url)
    {
        if (String.IsNullOrEmpty(url)) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception error)
        {
            MessageBox.Show(this, "无法打开链接：" + error.Message, "DSH Desktop",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void SetFont(Control control, float points, FontStyle style)
    {
        Font font = SkinTheme.CreateFont(skin, points, style);
        control.Font = font;
        fonts.Add(font);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        SkinTheme.ApplyWindowFrame(this, skin);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (scroll != null) LayoutChrome();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            tips.Dispose();
            foreach (Font font in fonts) font.Dispose();
            fonts.Clear();
        }
        base.Dispose(disposing);
    }
}
