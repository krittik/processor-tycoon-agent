using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace ProcessorTycoonModApi;

// The game's look for mod UI: its Roboto fonts, its sprites and the colours of the current theme (light or dark). Game
// classes are read by name (ThemeManager, Colors), so this layer compiles without the game's assemblies. Values fall back
// to the light theme as measured from the native windows.
internal enum Paint
{
    Window, Window2, TopBar, TopBarText, CloseIcon, CloseHover, Text, TextLow, Header, Button, ButtonText, ButtonTextOff,
    Cta, CtaText, Field, Line, LineActive, Scroll, ScrollBack, Positive, Negative, Row, RowAlt, Icon, Tab, TabBar, TabText,
    TabTextHover, TabTextOn, Toggle, Check, Clear, Tray,
}

internal static class Theme
{
    private static readonly Dictionary<Paint, (string member, string light)> members = new()
    {
        [Paint.Window] = ("Background", "#F5F5F5"), [Paint.Window2] = ("Background2", "#E9EBEE"), [Paint.TopBar] = ("TopBarBackground", "#DFE2E6"),
        [Paint.TopBarText] = ("TopBarText", "#2E3033"), [Paint.CloseIcon] = ("TopBarCloseIcon", "#1A1A1A"), [Paint.CloseHover] = ("TopBarCloseButton", "#E63D17"),
        [Paint.Text] = ("Text", "#141414"), [Paint.TextLow] = ("TextLowHierarchy", "#4C4C4C"), [Paint.Header] = ("Header", "#141414"),
        [Paint.Button] = ("ButtonBackground", "#FFFFFF"), [Paint.ButtonText] = ("ButtonText", "#141414"), [Paint.ButtonTextOff] = ("ButtonTextDisabled", "#9297A0"),
        [Paint.Cta] = ("CTAButtonBackground", "#2784D2"), [Paint.CtaText] = ("CTAButtonText", "#FFFFFF"), [Paint.Field] = ("InputFieldBackground", "#FFFFFF"),
        [Paint.Line] = ("InputBottomLineUnselected", "#878787"), [Paint.LineActive] = ("InputBottomLineSelected", "#2784D2"),
        [Paint.Scroll] = ("ScrollBarHandle", "#878787"), [Paint.ScrollBack] = ("ScrollBarBackground", "#E0E0E0"),
        [Paint.Row] = ("SpreadsheetColor1", "#FFFFFF"), [Paint.RowAlt] = ("SpreadsheetColor2", "#F0F1F3"),
        [Paint.Tab] = ("TabBackground", "#D9DADB"), [Paint.TabBar] = ("TabBar", "#2784D2"),
        [Paint.Toggle] = ("ToggleBackground", "#FFFFFF"), [Paint.Check] = ("ToggleCheckmark", "#1A1A1A"),
    };
    private static readonly Dictionary<Paint, Color> colors = members.ToDictionary(p => p.Key, p => Hex(p.Value.light));
    private static readonly Dictionary<string, Sprite?> sprites = new();
    private static object? shownTheme;
    private static Type? themeManager;

    static Theme()
    {
        colors[Paint.Positive] = Hex("#126605"); colors[Paint.Negative] = Hex("#C62828"); colors[Paint.Icon] = Hex("#858688");
        colors[Paint.TabText] = Hex("#8B8B8C"); colors[Paint.TabTextHover] = Hex("#5C5C5D"); colors[Paint.TabTextOn] = Hex("#111111");
        colors[Paint.Clear] = Color.clear;
        colors[Paint.Tray] = Hex("#C7CCD1");   // bottom-bar texts: the bar is dark in every theme
    }

    public static TMP_FontAsset? Font { get; private set; }
    public static TMP_FontAsset? Mono { get; private set; }
    public static bool Dark { get; private set; }
    // Changes whenever colours or fonts change; Painted re-applies its colour then.
    public static int Revision { get; private set; }

    public static Color Of(Paint paint) => colors[paint];
    public static string Hex(Paint paint) => "#" + ColorUtility.ToHtmlStringRGB(colors[paint]);

    // Cheap unless the theme or the loaded fonts changed; overlays call it every frame.
    public static void Refresh()
    {
        if (Font == null)
        {
            var fonts = Resources.FindObjectsOfTypeAll<TMP_FontAsset>();
            Font = fonts.FirstOrDefault(f => f.name == "Roboto-Regular SDF");
            Mono = fonts.FirstOrDefault(f => f.name == "RobotoMono-Regular SDF") ?? Font;
            if (Font != null) Revision++;
        }
        object? theme;
        try
        {
            themeManager ??= GameType("ProcessorTycoon.UI.Themes.ThemeManager");
            var manager = themeManager?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            theme = manager == null ? null : Member(manager, "CurrentTheme");
        }
        catch (Exception) { theme = null; }
        if (theme == null || ReferenceEquals(theme, shownTheme)) return;
        shownTheme = theme;
        // Theme colours are Color32.
        foreach (var pair in members) if (Member(theme, pair.Value.member) is Color32 c) colors[pair.Key] = c;
        Dark = Member(theme, "IsDark") is true;
        var tabs = Member(theme, "TabGroupText");
        if (tabs != null && Member(tabs, "NormalColor") is Color32 normal && Member(tabs, "MouseOverColor") is Color32 over && Member(tabs, "SelectedColor") is Color32 selected)
        { colors[Paint.TabText] = normal; colors[Paint.TabTextHover] = over; colors[Paint.TabTextOn] = selected; }
        colors[Paint.Icon] = Dark ? colors[Paint.TextLow] : Hex("#858688");   // the native info icon (ThemeableIcon)
        try
        {
            var palette = GameType("ProcessorTycoon.Colors")?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            if (palette != null)
            {
                if (AsColor(palette.GetType().GetMethod("DynamicPositiveColor")?.Invoke(palette, null)) is Color positive) colors[Paint.Positive] = positive;
                if (AsColor(palette.GetType().GetMethod("DynamicNegativeColor")?.Invoke(palette, null)) is Color negative) colors[Paint.Negative] = negative;
            }
        }
        catch (Exception) { }
        Revision++;
    }

    // A sprite the game has loaded (icons such as "info_icon", "close", "email_icon"; "RoundCorners42"; "shadow").
    public static Sprite? Sprite(string name)
    {
        if (sprites.TryGetValue(name, out var cached) && cached != null) return cached;
        return sprites[name] = Resources.FindObjectsOfTypeAll<Sprite>().FirstOrDefault(s => s.name == name);
    }

    // Scene changes can unload sprites and fonts.
    public static void Reset()
    {
        sprites.Clear();
        Font = null;
        shownTheme = null;
    }

    internal static Type? GameType(string name) => AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(name, false)).FirstOrDefault(t => t != null);

    internal static object? Member(object target, string name)
    {
        var type = target.GetType();
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        return type.GetProperty(name, flags)?.GetValue(target) ?? type.GetField(name, flags)?.GetValue(target);
    }

    private static Color? AsColor(object? value) => value is Color c ? c : value is Color32 c32 ? c32 : null;

    public static Color Hex(string html) => ColorUtility.TryParseHtmlString(html, out var c) ? c : Color.magenta;
}

// Keeps a graphic in its theme colour, re-applied after theme changes.
internal sealed class Painted : MonoBehaviour
{
    public Graphic Target = null!;
    public Paint Paint;
    private int revision = -1;

    public static void Add(Graphic graphic, Paint paint)
    {
        var painted = graphic.gameObject.AddComponent<Painted>();
        painted.Target = graphic;
        painted.Paint = paint;
        painted.Apply();
    }

    public static void Set(Graphic graphic, Paint paint)
    {
        var painted = graphic.GetComponents<Painted>().FirstOrDefault(p => p.Target == graphic);
        if (painted == null) Add(graphic, paint);
        else { painted.Paint = paint; painted.enabled = true; painted.Apply(); }
    }

    // Stops painting (the caller sets the colour itself).
    public static void Clear(Graphic graphic) { foreach (var p in graphic.GetComponents<Painted>()) p.enabled = false; }

    private void Apply()
    {
        Target.color = Theme.Of(Paint);
        if (Target is TMP_Text text && Theme.Font != null && text.font != Theme.Font && text.font != Theme.Mono) text.font = Theme.Font;
        revision = Theme.Revision;
    }

    private void LateUpdate() { if (revision != Theme.Revision) Apply(); }
}

// Native controls drop their outline in dark themes; call-to-action buttons have none (Off).
internal sealed class LightOnly : MonoBehaviour
{
    public Behaviour Target = null!;
    public bool Off;
    private void LateUpdate() { if (Target != null) Target.enabled = !Off && !Theme.Dark; }
}
