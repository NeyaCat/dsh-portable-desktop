using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using System.Web.Script.Serialization;

// One cached copy of the live DSH skin.
//
// The DSH window samples its own computed styles when it opens (and again after
// every skin/theme change) and stores the result in data/desktop-theme.json.
// Every secondary window - version manager, splash surface, exit prompt - reads
// that cache before it paints, so it starts in the current skin instead of a
// hard-coded guess. When the cache is missing or not trustworthy the built-in
// Default palette is used, so these windows never depend on the DSH window
// having run first.
internal static class SkinTheme
{
    internal const string RelativePath = "data/desktop-theme.json";
    internal const int Schema = 2;
    internal const int MaxBytes = 12288;

    // Colour tokens, in the order the version manager consumes them.
    internal static readonly string[] ColorKeys = {
        "bg", "surface", "surface2", "surface3", "text", "muted", "tertiary",
        "accent", "soft", "onAccent", "border", "hover", "focus", "danger", "success", "warn"
    };
    internal static readonly string[] LengthKeys = { "radiusSm", "radius", "radiusLg", "fontSize" };
    internal static readonly string[] FontKeys = { "font" };
    internal static readonly string[] ShadowKeys = { "shadow" };
    internal static readonly string[] WallpaperKeys = { "wallpaper" };

    // JSON key -> CSS custom property used by the secondary windows.
    private static readonly string[,] CssNames = {
        { "bg", "--bg" }, { "surface", "--surface" }, { "surface2", "--surface-2" }, { "surface3", "--surface-3" },
        { "text", "--text" }, { "muted", "--muted" }, { "tertiary", "--tertiary" },
        { "accent", "--accent" }, { "soft", "--soft" }, { "onAccent", "--on-accent" },
        { "border", "--border" }, { "hover", "--hover" }, { "focus", "--focus" },
        { "danger", "--danger" }, { "success", "--success" }, { "warn", "--warn" },
        { "radiusSm", "--radius-sm" }, { "radius", "--radius" }, { "radiusLg", "--radius-lg" },
        { "font", "--font" }, { "fontSize", "--font-size" }, { "shadow", "--shadow" }, { "wallpaper", "--wallpaper" }
    };

    private static readonly Regex ColorValue = new Regex(
        @"^(#[0-9a-fA-F]{3,8}|transparent|(rgb|rgba|hsl|hsla|hwb|lab|lch|oklab|oklch|color|color-mix)\([0-9a-zA-Z.,%\s/+-]{0,140}\))$",
        RegexOptions.Compiled);
    private static readonly Regex LengthValue = new Regex(
        @"^\d{1,4}(\.\d{1,4})?(px|rem|em|%)?$", RegexOptions.Compiled);
    private static readonly Regex FontValue = new Regex(
        @"^[0-9a-zA-Z ,_.\-""\u4e00-\u9fff]{1,160}$", RegexOptions.Compiled);
    private static readonly Regex ShadowValue = new Regex(
        @"^(none|[0-9a-zA-Z.,%\s()#/+-]{1,240})$", RegexOptions.Compiled);
    private static readonly Regex WallpaperValue = new Regex(
        @"^(none|(repeating-)?(linear|radial|conic)-gradient\([0-9a-zA-Z.,%\s()#/+-]{1,900}\))$", RegexOptions.Compiled);
    // A value that reaches CSS/JS may never smuggle a second declaration or a URL.
    private static readonly string[] Forbidden = { "url(", "expression", "javascript:", "<", ">", "{", "}", ";", "\\" };

    // The built-in skin used when DSH has never exported one.
    internal static readonly Dictionary<string, string> Default = CreateDefault();

    private static Dictionary<string, string> CreateDefault()
    {
        var skin = new Dictionary<string, string>();
        skin["bg"] = "rgb(21,21,23)";
        skin["surface"] = "rgb(35,35,36)";
        skin["surface2"] = "rgb(44,44,46)";
        skin["surface3"] = "rgb(53,54,56)";
        skin["text"] = "rgb(235,238,242)";
        skin["muted"] = "rgb(173,178,184)";
        skin["tertiary"] = "rgb(129,133,140)";
        skin["accent"] = "rgb(86,134,254)";
        skin["soft"] = "rgba(86,134,254,0.16)";
        skin["onAccent"] = "rgb(255,255,255)";
        skin["border"] = "rgba(255,255,255,0.12)";
        skin["hover"] = "rgba(255,255,255,0.08)";
        skin["focus"] = "rgb(86,134,254)";
        skin["danger"] = "rgb(236,19,19)";
        skin["success"] = "rgb(34,197,94)";
        skin["warn"] = "rgb(245,158,11)";
        skin["radiusSm"] = "8px";
        skin["radius"] = "12px";
        skin["radiusLg"] = "16px";
        skin["font"] = "\"Segoe UI\", \"Microsoft YaHei UI\", sans-serif";
        skin["fontSize"] = "14px";
        skin["shadow"] = "none";
        skin["wallpaper"] = "none";
        skin["scheme"] = "dark";
        return skin;
    }

    // Default merged with every trustworthy cached token. Never throws.
    internal static Dictionary<string, string> Load()
    {
        var skin = new Dictionary<string, string>(Default);
        try {
            var cached = Read();
            if (cached != null)
                foreach (var pair in cached) {
                    string clean;
                    if (CleanValue(pair.Key, Convert.ToString(pair.Value), out clean)) skin[pair.Key] = clean;
                }
        } catch (Exception) { /* A broken cache falls back to the default skin. */ }
        return skin;
    }

    // Signature used to detect that the live DSH skin changed. Metadata such as
    // the capture time is excluded, so an unchanged skin compares equal.
    internal static string Signature()
    {
        var skin = Load();
        var keys = new List<string>(ColorKeys);
        keys.AddRange(LengthKeys); keys.AddRange(FontKeys); keys.AddRange(ShadowKeys); keys.AddRange(WallpaperKeys);
        keys.Add("scheme");
        var text = new StringBuilder();
        foreach (string key in keys) {
            string value;
            if (skin.TryGetValue(key, out value)) text.Append(key).Append('=').Append(value).Append('\n');
        }
        return text.ToString();
    }

    internal static string Json(Dictionary<string, string> skin)
    {
        var payload = new Dictionary<string, object>();
        payload["version"] = Schema;
        payload["capturedAt"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
        payload["source"] = "dsh-ui";
        foreach (var pair in skin) if (pair.Key != "version" && pair.Key != "capturedAt" && pair.Key != "source") payload[pair.Key] = pair.Value;
        return new JavaScriptSerializer().Serialize(payload);
    }

    // The DSH window forwards its sampled skin here. Values are re-validated
    // before they touch disk, so the cache can never carry CSS/script injection.
    internal static void Save(string json)
    {
        try {
            if (String.IsNullOrEmpty(json) || json.Length > MaxBytes) return;
            var raw = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json);
            if (raw == null) return;
            var skin = new Dictionary<string, string>();
            foreach (var pair in raw) {
                string clean;
                if (pair.Key == "scheme" || pair.Key == "version" || pair.Key == "source" || pair.Key == "capturedAt") continue;
                if (CleanValue(pair.Key, Convert.ToString(pair.Value), out clean)) skin[pair.Key] = clean;
            }
            // Without a background and foreground there is nothing worth caching.
            if (!skin.ContainsKey("bg") || !skin.ContainsKey("text")) return;
            var merged = new Dictionary<string, string>(Default);
            foreach (var pair in skin) merged[pair.Key] = pair.Value;
            if (raw.ContainsKey("scheme")) {
                string scheme;
                if (CleanValue("scheme", Convert.ToString(raw["scheme"]), out scheme)) merged["scheme"] = scheme;
            }
            Write(Json(merged));
        } catch (Exception) { /* A skin export must never interrupt the application. */ }
    }

    // Seed the cache so every window starts from the same documented baseline.
    internal static void Ensure()
    {
        try { if (Read() == null) Write(Json(Default)); }
        catch (Exception) { /* A read-only folder still renders with Default. */ }
    }

    private static Dictionary<string, object> Read()
    {
        string path = Path.Combine(Updates.Root, RelativePath);
        if (!File.Exists(path)) return null;
        string json;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var reader = new StreamReader(stream, Encoding.UTF8))
            json = reader.ReadToEnd();
        if (json.Length == 0 || json.Length > MaxBytes) return null;
        var parsed = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json);
        return parsed == null || parsed.Count == 0 ? null : parsed;
    }

    private static void Write(string json)
    {
        string path = Path.Combine(Updates.Root, RelativePath);
        string temp = path + ".tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllText(temp, json, new UTF8Encoding(false));
        if (File.Exists(path)) File.Replace(temp, path, null);
        else File.Move(temp, path);
    }

    internal static bool CleanValue(string key, string value, out string clean)
    {
        clean = null;
        if (String.IsNullOrEmpty(key) || String.IsNullOrEmpty(value)) return false;
        value = value.Trim();
        if (value.Length == 0 || value.Length > 1000) return false;
        foreach (string token in Forbidden) if (value.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0) return false;
        Regex rule = null;
        if (Array.IndexOf(ColorKeys, key) >= 0) rule = ColorValue;
        else if (Array.IndexOf(LengthKeys, key) >= 0) rule = LengthValue;
        else if (Array.IndexOf(FontKeys, key) >= 0) rule = FontValue;
        else if (Array.IndexOf(ShadowKeys, key) >= 0) rule = ShadowValue;
        else if (Array.IndexOf(WallpaperKeys, key) >= 0) rule = WallpaperValue;
        else if (key == "scheme") { if (value != "dark" && value != "light") return false; clean = value; return true; }
        if (rule == null || !rule.IsMatch(value)) return false;
        clean = value;
        return true;
    }

    internal static Color ToColor(Dictionary<string, string> skin, string key, Color fallback)
    {
        try {
            string value;
            return skin != null && skin.TryGetValue(key, out value) ? ToColor(value, fallback) : fallback;
        } catch (Exception) { return fallback; }
    }

    internal static Color ToColor(string value, Color fallback)
    {
        if (String.IsNullOrEmpty(value)) return fallback;
        Match rgb = Regex.Match(value, @"^rgba?\(\s*(\d{1,3})\s*,\s*(\d{1,3})\s*,\s*(\d{1,3})");
        if (rgb.Success)
            return Color.FromArgb(Clamp(rgb.Groups[1].Value), Clamp(rgb.Groups[2].Value), Clamp(rgb.Groups[3].Value));
        Match hex = Regex.Match(value, @"^#([0-9a-fA-F]{6}|[0-9a-fA-F]{8})$");
        if (hex.Success) {
            string digits = hex.Groups[1].Value;
            return Color.FromArgb(Convert.ToInt32(digits.Substring(0, 2), 16), Convert.ToInt32(digits.Substring(2, 2), 16), Convert.ToInt32(digits.Substring(4, 2), 16));
        }
        return fallback;
    }

    private static int Clamp(string digits)
    {
        int value = Int32.Parse(digits);
        return value < 0 ? 0 : value > 255 ? 255 : value;
    }

    // Apply every cached token before the document paints. documentElement may
    // not exist yet at document creation, so the assignment is retried as soon
    // as the parser creates <html>.
    internal static string BootstrapScript(Dictionary<string, string> skin)
    {
        var pairs = new StringBuilder();
        for (int i = 0; i < CssNames.GetLength(0); i++) {
            string value;
            if (skin == null || !skin.TryGetValue(CssNames[i, 0], out value)) continue;
            string clean;
            if (!CleanValue(CssNames[i, 0], value, out clean)) continue;
            pairs.Append("['").Append(CssNames[i, 1]).Append("','")
                 .Append(clean.Replace("\\", "").Replace("'", "")).Append("'],");
        }
        string scheme = "dark";
        string cached;
        if (skin != null && skin.TryGetValue("scheme", out cached) && cached == "light") scheme = "light";
        return "(function(){var pairs=[" + pairs.ToString().TrimEnd(',') + "];" +
               "var apply=function(){var root=document.documentElement;if(!root)return false;" +
               "for(var i=0;i<pairs.length;i++)root.style.setProperty(pairs[i][0],pairs[i][1]);" +
               "root.style.colorScheme='" + scheme + "';window.__dshSkin='cached';return true};" +
               "if(apply())return;" +
               "var observer=new MutationObserver(function(){if(apply())observer.disconnect()});" +
               "observer.observe(document,{childList:true,subtree:true});" +
               "document.addEventListener('DOMContentLoaded',function(){if(apply())observer.disconnect()},{once:true});" +
               "})();";
    }

    internal static int ColorRef(Color color) { return color.R | (color.G << 8) | (color.B << 16); }

    // The same cached tokens Versions Switch injects into its page are used by
    // native surfaces. These helpers keep one resolution path (font family,
    // radius, translucent blends) instead of letting every window invent one.
    internal static string FontFamily(Dictionary<string, string> skin)
    {
        string value = "";
        if (skin != null) skin.TryGetValue("font", out value);
        if (!String.IsNullOrEmpty(value)) {
            string fallback = null;
            foreach (string raw in value.Split(',')) {
                string family = raw.Trim().Trim('"').Trim('\'').Trim();
                if (family.Length == 0 || IsGenericFamily(family) || !InstalledFamily(family)) continue;
                if (fallback == null) fallback = family;
                if (family.IndexOf("YaHei", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    family.IndexOf("雅黑", StringComparison.Ordinal) >= 0) return family;
            }
            if (fallback != null) return fallback;
        }
        return "Microsoft YaHei UI";
    }

    private static bool IsGenericFamily(string family)
    {
        switch (family.ToLowerInvariant()) {
            case "sans-serif": case "serif": case "monospace": case "cursive":
            case "fantasy": case "system-ui": case "ui-sans-serif": case "ui-serif":
            case "ui-monospace": case "ui-rounded": case "math": case "emoji":
                return true;
            default: return false;
        }
    }

    private static bool InstalledFamily(string family)
    {
        try { using (var installed = new FontFamily(family)) return true; }
        catch (Exception) { return false; }
    }

    internal static Font CreateFont(Dictionary<string, string> skin, float points, FontStyle style)
    {
        try { return new Font(FontFamily(skin), points, style, GraphicsUnit.Point); }
        catch (Exception) { return new Font("Microsoft YaHei UI", points, style, GraphicsUnit.Point); }
    }

    internal static int RadiusPixels(Dictionary<string, string> skin, string key, int fallback)
    {
        string value;
        if (skin == null || !skin.TryGetValue(key, out value)) return fallback;
        Match match = Regex.Match(value, @"^(\d{1,4})(?:\.\d+)?px$");
        int radius;
        if (match.Success && Int32.TryParse(match.Groups[1].Value, out radius) && radius > 0)
            return Math.Min(radius, 40);
        return fallback;
    }

    internal static Color Mix(Color a, Color b, float amount)
    {
        amount = Math.Max(0f, Math.Min(1f, amount));
        return Color.FromArgb(
            (int)Math.Round(a.R + (b.R - a.R) * amount),
            (int)Math.Round(a.G + (b.G - a.G) * amount),
            (int)Math.Round(a.B + (b.B - a.B) * amount));
    }

    // CSS tokens may be translucent (borders, hover, soft fills). Native GDI+
    // paints need a concrete colour, so composite them over the surface first.
    internal static Color Blend(Color background, Dictionary<string, string> skin, string key, Color fallback)
    {
        string value;
        if (skin == null || !skin.TryGetValue(key, out value)) return fallback;
        return Mix(background, ToColor(value, fallback), Alpha(value) / 255f);
    }

    internal static int Alpha(string value)
    {
        if (String.IsNullOrEmpty(value)) return 255;
        Match rgba = Regex.Match(value, @"^rgba\(\s*\d{1,3}\s*,\s*\d{1,3}\s*,\s*\d{1,3}\s*,\s*([0-9.]+)\s*\)$");
        if (rgba.Success) {
            double alpha;
            if (Double.TryParse(rgba.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out alpha))
                return Math.Max(0, Math.Min(255, (int)Math.Round(alpha * 255)));
        }
        Match hex = Regex.Match(value, @"^#[0-9a-fA-F]{8}$");
        if (hex.Success) return Convert.ToInt32(value.Substring(7, 2), 16);
        return 255;
    }

    // Match the DWM frame Version Switch uses: the native caption, dark mode,
    // border and rounded corners all follow the same cached skin.
    internal static void ApplyWindowFrame(Form form, Dictionary<string, string> skin)
    {
        if (form == null) return;
        try {
            Color background = ToColor(skin, "bg", form.BackColor);
            form.BackColor = background;
            if (!form.IsHandleCreated) return;
            bool light = skin != null && skin.ContainsKey("scheme") && skin["scheme"] == "light";
            int dark = light ? 0 : 1;
            DwmSetWindowAttribute(form.Handle, 20, ref dark, 4);
            int caption = ColorRef(background);
            DwmSetWindowAttribute(form.Handle, 35, ref caption, 4);
            int text = ColorRef(ToColor(skin, "text", Color.FromArgb(235,238,242)));
            DwmSetWindowAttribute(form.Handle, 36, ref text, 4);
            int border = ColorRef(ToColor(skin, "border", background));
            DwmSetWindowAttribute(form.Handle, 34, ref border, 4);
            int corner = 2;
            DwmSetWindowAttribute(form.Handle, 33, ref corner, 4);
        } catch (Exception) { /* Older Windows builds ignore caption colours. */ }
    }

    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
}
