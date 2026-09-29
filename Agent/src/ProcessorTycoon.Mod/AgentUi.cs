using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace ProcessorTycoonMod;

// Native-looking pieces for the mod's own windows (Agent window, About), kept in step with the Multiplayer mod's windows:
// the game's RoundCorners42 sprite and Roboto font, the current theme's colours, the hand cursor and the native button press.
// Game classes are only read by name for looks (ThemeManager colours, Colors, CursorController); nothing about gameplay.
//
// UI conventions shared with the Multiplayer mod (its Kit follows the same rules):
// - A window is a title bar with a close button, a body and a footer strip. A main window's footer starts with the version
//   link (opens About) and ends with actions for the whole window; the title bar's close button is the only Close.
// - At most one call-to-action (blue) button per section: the most likely next step.
// - State comes first, as a status line (coloured dot, short text); body text shows state and actions only.
// - Explanations go into an info icon's or a button's tooltip; a tooltip starts with a short header.
// - A choice between modes or preset values is a segmented strip (the game's tab style), never blue buttons.
// - Sizes: section headers 18 with a 20 px icon, body 15, secondary 14 in the low-hierarchy colour, fine print 13;
//   buttons and fields 28 px high with 15 px text.
// - Credited names link to their closest page.
internal static class AgentUi
{
    internal enum Paint { Window, Window2, TopBar, TopBarText, CloseIcon, CloseHover, Text, TextLow, Header, Button, ButtonText, Cta, CtaText, Field, Line, LineActive, Positive, Negative, Icon, Tab, TabBar, TabText, TabTextHover, TabTextOn, Toggle, Check }
    public const string Rounded = "RoundCorners42";
    private static readonly Dictionary<Paint, (string member, string light)> themeMembers = new()
    {
        [Paint.Window] = ("Background", "#F5F5F5"), [Paint.Window2] = ("Background2", "#E9EBEE"), [Paint.TopBar] = ("TopBarBackground", "#DFE2E6"),
        [Paint.TopBarText] = ("TopBarText", "#2E3033"), [Paint.CloseIcon] = ("TopBarCloseIcon", "#1A1A1A"), [Paint.CloseHover] = ("TopBarCloseButton", "#E63D17"),
        [Paint.Text] = ("Text", "#141414"), [Paint.TextLow] = ("TextLowHierarchy", "#4C4C4C"), [Paint.Header] = ("Header", "#141414"),
        [Paint.Button] = ("ButtonBackground", "#FFFFFF"), [Paint.ButtonText] = ("ButtonText", "#141414"), [Paint.Cta] = ("CTAButtonBackground", "#2784D2"),
        [Paint.CtaText] = ("CTAButtonText", "#FFFFFF"), [Paint.Field] = ("InputFieldBackground", "#FFFFFF"), [Paint.Line] = ("InputBottomLineUnselected", "#878787"),
        [Paint.LineActive] = ("InputBottomLineSelected", "#2784D2"), [Paint.Tab] = ("TabBackground", "#D9DADB"), [Paint.TabBar] = ("TabBar", "#2784D2"),
        [Paint.Toggle] = ("ToggleBackground", "#FFFFFF"), [Paint.Check] = ("ToggleCheckmark", "#1A1A1A"),
    };
    private static readonly Dictionary<Paint, Color> colors = themeMembers.ToDictionary(p => p.Key, p => Hex(p.Value.light));
    private static readonly Dictionary<string, Sprite?> sprites = new();
    private static object? shownTheme;
    private static Type? themeManager;
    public static bool Dark { get; private set; }
    public static int Revision { get; private set; }
    public static TMP_FontAsset? Font { get; private set; }
    // The overlay canvas; tooltips draw on it.
    public static RectTransform? Root { get; set; }

    static AgentUi()
    {
        colors[Paint.Positive] = Hex("#126605"); colors[Paint.Negative] = Hex("#C62828"); colors[Paint.Icon] = Hex("#858688");
        colors[Paint.TabText] = Hex("#8B8B8C"); colors[Paint.TabTextHover] = Hex("#5C5C5D"); colors[Paint.TabTextOn] = Hex("#111111");
    }

    public static Color Of(Paint paint) => colors[paint];

    // Called every frame by the overlay; cheap unless the theme changed.
    public static void Refresh()
    {
        if (Font == null) { Font = Resources.FindObjectsOfTypeAll<TMP_FontAsset>().FirstOrDefault(f => f.name == "Roboto-Regular SDF"); if (Font != null) Revision++; }
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
        foreach (var pair in themeMembers) if (Member(theme, pair.Value.member) is Color32 c) colors[pair.Key] = c;
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
                if (Paintable(palette.GetType().GetMethod("DynamicPositiveColor")?.Invoke(palette, null)) is Color positive) colors[Paint.Positive] = positive;
                if (Paintable(palette.GetType().GetMethod("DynamicNegativeColor")?.Invoke(palette, null)) is Color negative) colors[Paint.Negative] = negative;
            }
        }
        catch (Exception) { }
        Revision++;
    }

    public static Sprite? Sprite(string name)
    {
        if (sprites.TryGetValue(name, out var cached) && cached != null) return cached;
        return sprites[name] = Resources.FindObjectsOfTypeAll<Sprite>().FirstOrDefault(s => s.name == name);
    }

    public static RectTransform Rect(string name, Transform parent)
    {
        var rect = (RectTransform)new GameObject(name, typeof(RectTransform)).transform;
        rect.SetParent(parent, false);
        return rect;
    }

    // Corner scale as on the native controls (pixelsPerUnitMultiplier of RoundCorners42): windows 3, buttons 5, inputs 10.
    public static Image Fill(RectTransform rect, Paint? paint, string? sprite = Rounded, float corners = 5)
    {
        var image = rect.gameObject.AddComponent<Image>();
        var native = sprite != null ? Sprite(sprite) : null;
        if (native != null) { image.sprite = native; image.type = Image.Type.Sliced; image.pixelsPerUnitMultiplier = corners; }
        if (paint.HasValue) Painted.Add(image, paint.Value);
        return image;
    }

    public static TextMeshProUGUI Label(Transform parent, string text, float size = 15, Paint paint = Paint.Text, TextAlignmentOptions align = TextAlignmentOptions.MidlineLeft, bool wrap = false)
    {
        var label = Rect("Text", parent).gameObject.AddComponent<TextMeshProUGUI>();
        if (Font != null) label.font = Font;
        label.fontSize = size;
        label.text = text;
        label.alignment = align;
        label.raycastTarget = false;
        label.richText = true;
        label.textWrappingMode = wrap ? TextWrappingModes.Normal : TextWrappingModes.NoWrap;
        label.overflowMode = wrap ? TextOverflowModes.Overflow : TextOverflowModes.Ellipsis;
        Painted.Add(label, paint);
        var layout = label.gameObject.AddComponent<LayoutElement>();
        if (!wrap) layout.preferredHeight = Mathf.Ceil(size * 1.35f);
        return label;
    }

    public static TextMeshProUGUI Header(Transform parent, string text) => Label(parent, text, 18, Paint.Header);

    public static Image Icon(Transform parent, Sprite? sprite, float size, Paint paint)
    {
        var image = Rect("Icon", parent).gameObject.AddComponent<Image>();
        image.sprite = sprite;
        image.preserveAspect = true;
        image.raycastTarget = false;
        Painted.Add(image, paint);
        Size(image, size, size);
        image.rectTransform.sizeDelta = new Vector2(size, size);
        return image;
    }

    public static Transform Column(Transform parent, float spacing = 6, int padding = 0)
    {
        var group = Rect("Column", parent).gameObject.AddComponent<VerticalLayoutGroup>();
        group.spacing = spacing;
        group.padding = new RectOffset(padding, padding, padding, padding);
        group.childControlWidth = group.childControlHeight = true;
        group.childForceExpandWidth = true;
        group.childForceExpandHeight = false;
        return group.transform;
    }

    public static HorizontalLayoutGroup Row(Transform parent, float spacing = 6, float height = 30)
    {
        var group = Rect("Row", parent).gameObject.AddComponent<HorizontalLayoutGroup>();
        group.spacing = spacing;
        group.childAlignment = TextAnchor.MiddleLeft;
        group.childControlWidth = group.childControlHeight = true;
        group.childForceExpandWidth = group.childForceExpandHeight = false;
        Size(group, height: height);
        return group;
    }

    public static LayoutElement Size(Component c, float width = -1, float height = -1, float flexWidth = -1)
    {
        var layout = c.GetComponent<LayoutElement>() ?? c.gameObject.AddComponent<LayoutElement>();
        if (width >= 0) layout.preferredWidth = layout.minWidth = width;
        if (height >= 0) layout.preferredHeight = layout.minHeight = height;
        if (flexWidth >= 0) layout.flexibleWidth = flexWidth;
        return layout;
    }

    public static void Stretch(RectTransform rect, float left, float right) { rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one; rect.offsetMin = new Vector2(left, 0); rect.offsetMax = new Vector2(-right, 0); }

    // A native button: white rounded box with an outline (light themes), or the blue call-to-action style; the native press.
    public static Button Button(Transform parent, string text, Action action, bool cta = false, float width = -1, float height = 28, float size = 15)
    {
        var image = Fill(Rect("Button " + text, parent), Paint.Button);
        var outline = image.gameObject.AddComponent<Outline>();
        outline.effectColor = new Color(0, 0, 0, .28f);
        outline.effectDistance = new Vector2(1, -1);
        image.gameObject.AddComponent<LightOnly>().Target = outline;
        var button = image.gameObject.AddComponent<Button>();
        button.targetGraphic = image;
        var states = ColorBlock.defaultColorBlock;
        states.highlightedColor = new Color(.93f, .93f, .93f);
        states.pressedColor = new Color(.84f, .84f, .84f);
        states.selectedColor = Color.white;
        states.disabledColor = new Color(1, 1, 1, .55f);
        states.fadeDuration = .08f;
        button.colors = states;
        button.navigation = new Navigation { mode = Navigation.Mode.None };
        button.onClick.AddListener(() => action());
        image.gameObject.AddComponent<HandCursor>();
        image.gameObject.AddComponent<AgentButtonFeedback>().Target = image.rectTransform;
        var fit = image.gameObject.AddComponent<HorizontalLayoutGroup>();
        fit.padding = new RectOffset(14, 14, 0, 0);
        fit.childAlignment = TextAnchor.MiddleCenter;
        fit.childControlWidth = fit.childControlHeight = true;
        fit.childForceExpandWidth = fit.childForceExpandHeight = true;
        Label(image.transform, text, size, Paint.ButtonText, TextAlignmentOptions.Center);
        var layout = Size(image, height: height);
        layout.flexibleWidth = layout.flexibleHeight = 0;
        layout.minWidth = width >= 0 ? width : 60;
        if (width >= 0) layout.preferredWidth = width;
        Style(button, cta);
        return button;
    }

    // Switches a button between the plain and the call-to-action style (the primary action can move with the state).
    public static void Style(Button button, bool cta)
    {
        Painted.Set(button.GetComponent<Image>(), cta ? Paint.Cta : Paint.Button);
        Painted.Set(button.GetComponentInChildren<TextMeshProUGUI>(), cta ? Paint.CtaText : Paint.ButtonText);
        button.GetComponent<LightOnly>().Off = cta;
    }

    // Native input: white field with a grey bottom line that turns blue while editing.
    public static TMP_InputField Input(Transform parent, string value, int limit, Action<string> submitted, float width, TMP_InputField.ContentType type = TMP_InputField.ContentType.Standard)
    {
        var field = Fill(Rect("Field", parent), Paint.Field, Rounded, 10);
        Size(field, width, 28);
        var outline = field.gameObject.AddComponent<Outline>();
        outline.effectColor = new Color(0, 0, 0, .22f);
        outline.effectDistance = new Vector2(1, -1);
        field.gameObject.AddComponent<LightOnly>().Target = outline;
        var area = Rect("Text Area", field.transform);
        Stretch(area, 8, 8);
        area.gameObject.AddComponent<RectMask2D>();
        var text = Label(area, "", 15, Paint.Text, TextAlignmentOptions.MidlineRight);
        Stretch(text.rectTransform, 0, 0);
        var line = Fill(Rect("BottomLine", field.transform), Paint.Line, null);
        var lineRect = line.rectTransform;
        lineRect.anchorMin = new Vector2(0, 0); lineRect.anchorMax = new Vector2(1, 0); lineRect.pivot = new Vector2(.5f, 0);
        lineRect.sizeDelta = new Vector2(0, 2); lineRect.anchoredPosition = Vector2.zero;
        var input = field.gameObject.AddComponent<TMP_InputField>();
        input.textViewport = area;
        input.textComponent = text;
        input.characterLimit = limit;
        input.contentType = type;
        input.caretColor = Of(Paint.Text);
        input.customCaretColor = true;
        input.text = value;
        input.onEndEdit.AddListener(v => submitted(v));
        input.onSelect.AddListener(_ => Painted.Set(line, Paint.LineActive));
        input.onDeselect.AddListener(_ => Painted.Set(line, Paint.Line));
        return input;
    }

    // The version in a window footer: a quiet text that turns into a link on hover and opens the About window.
    public static void VersionLink(Transform parent, string text, Action action)
    {
        var label = Label(parent, text, 14, Paint.TextLow);
        label.raycastTarget = true;
        var button = label.gameObject.AddComponent<Button>();
        button.transition = Selectable.Transition.None;
        button.navigation = new Navigation { mode = Navigation.Mode.None };
        button.onClick.AddListener(() => action());
        label.gameObject.AddComponent<HandCursor>();
        label.gameObject.AddComponent<LinkHover>().Text = label;
    }

    // A link inside a text (see Links), underlined in the call-to-action colour of the current theme.
    public static string Link(string text, string url) => $"<link=\"{url}\"><u><color=#{ColorUtility.ToHtmlStringRGB(Of(Paint.Cta))}>{text}</color></u></link>";

    // A text whose <link="url"> parts open their web page when clicked (hand cursor over the links).
    public static void Links(TextMeshProUGUI label)
    {
        label.raycastTarget = true;
        label.gameObject.AddComponent<TextLinks>();
    }

    // The game's grey info icon; hovering it shows the text.
    public static Tip Info(Transform parent, string header, string text)
    {
        var icon = Icon(parent, Sprite("info_icon"), 18, Paint.Icon);
        icon.raycastTarget = true;
        return Tip.On(icon.gameObject, header, text);
    }

    // Status line: a coloured dot (Painted.Set it) and a short text; returns the row for an info icon or buttons.
    public static (Image dot, TextMeshProUGUI text, HorizontalLayoutGroup row) Status(Transform parent, float size = 16)
    {
        var row = Row(parent, 8, 24);
        var dot = Icon(row.transform, Sprite("circle_png_small"), 10, Paint.TextLow);
        var text = Label(row.transform, "", size);
        return (dot, text, row);
    }

    // A choice between a few options, drawn like the game's tab strips (Analysis window): grey band, the chosen option in
    // dark text with a blue bar under it. selected() is read every frame, so the strip follows changes made elsewhere.
    public static RectTransform Segments(Transform parent, string[] options, Func<int> selected, Action<int> choose, float height = 28)
    {
        var band = Fill(Rect("Segments", parent), Paint.Tab, Rounded, 5);
        Size(band, height: height).flexibleWidth = 0;
        var row = band.gameObject.AddComponent<HorizontalLayoutGroup>();
        row.padding = new RectOffset(3, 3, 0, 0);
        row.childControlWidth = row.childControlHeight = true;
        row.childForceExpandWidth = row.childForceExpandHeight = true;
        var view = band.gameObject.AddComponent<SegmentsView>();
        view.Selected = selected;
        for (var i = 0; i < options.Length; i++)
        {
            var index = i;
            var hit = Rect("Option " + options[i], band.transform);
            hit.gameObject.AddComponent<Image>().color = Color.clear;
            var button = hit.gameObject.AddComponent<Button>();
            button.transition = Selectable.Transition.None;
            button.navigation = new Navigation { mode = Navigation.Mode.None };
            button.onClick.AddListener(() => choose(index));
            hit.gameObject.AddComponent<HandCursor>();
            var fit = hit.gameObject.AddComponent<HorizontalLayoutGroup>();
            fit.padding = new RectOffset(8, 8, 0, 0);
            fit.childControlWidth = fit.childControlHeight = true;
            fit.childForceExpandWidth = fit.childForceExpandHeight = true;
            var text = Label(hit, options[i], 15, Paint.TabText, TextAlignmentOptions.Center);
            var bar = Fill(Rect("Bar", hit), Paint.TabBar, null);
            bar.gameObject.AddComponent<LayoutElement>().ignoreLayout = true;
            var barRect = bar.rectTransform;
            barRect.anchorMin = Vector2.zero; barRect.anchorMax = new Vector2(1, 0); barRect.pivot = new Vector2(.5f, 0);
            barRect.sizeDelta = new Vector2(-12, 2); barRect.anchoredPosition = new Vector2(0, 3);
            var hover = hit.gameObject.AddComponent<SegmentHover>();
            hover.View = view; hover.Index = index;
            view.Options.Add((text, bar));
        }
        return band.rectTransform;
    }

    // The game's checkbox (Business window's "Only our contracts"): rounded box with the "done" check mark and a label;
    // the whole row toggles.
    public static void Check(Transform parent, string text, Func<bool> get, Action<bool> set)
    {
        var row = Row(parent, 8, 22);
        row.gameObject.AddComponent<Image>().color = Color.clear;
        var button = row.gameObject.AddComponent<Button>();
        button.transition = Selectable.Transition.None;
        button.navigation = new Navigation { mode = Navigation.Mode.None };
        button.onClick.AddListener(() => set(!get()));
        row.gameObject.AddComponent<HandCursor>();
        var box = Fill(Rect("Box", row.transform), Paint.Toggle);
        Size(box, 20, 20);
        var outline = box.gameObject.AddComponent<Outline>();
        outline.effectColor = new Color(0, 0, 0, .28f);
        outline.effectDistance = new Vector2(1, -1);
        box.gameObject.AddComponent<LightOnly>().Target = outline;
        var mark = Icon(box.transform, Sprite("done"), 20, Paint.Check);
        Stretch(mark.rectTransform, 0, 0);
        Label(row.transform, text, 15);
        var view = row.gameObject.AddComponent<CheckView>();
        view.Get = get; view.Mark = mark;
    }

    // Room for Mark in front of a linked name; put it right before the <link>.
    public const string MarkSpace = "<space=1.2em>";

    // An icon in front of a linked name inside a text (the Claude mark before "Claude Code"), in the room left by MarkSpace.
    public static void Mark(TextMeshProUGUI text, string linkId, Sprite sprite)
    {
        var rect = Rect("Mark", text.transform);
        rect.gameObject.AddComponent<LayoutElement>().ignoreLayout = true;
        var image = rect.gameObject.AddComponent<Image>();
        image.sprite = sprite;
        image.preserveAspect = true;
        image.raycastTarget = false;
        var mark = text.gameObject.AddComponent<InlineMark>();
        mark.Text = text; mark.Icon = rect; mark.LinkId = linkId;
    }

    internal static Type? GameType(string name) => AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(name, false)).FirstOrDefault(t => t != null);

    private static object? Member(object target, string name)
    {
        var type = target.GetType();
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        return type.GetProperty(name, flags)?.GetValue(target) ?? type.GetField(name, flags)?.GetValue(target);
    }

    private static Color? Paintable(object? value) => value is Color c ? c : value is Color32 c32 ? c32 : null;

    private static Color Hex(string html) => ColorUtility.TryParseHtmlString(html, out var c) ? c : Color.magenta;
}

// A window like the game's own: rounded frame with shadow and outline, a 30 px title bar with a close button (drag it to
// move the window), a body column and an optional footer strip. It can dock above a bottom-bar item until it is dragged.
internal sealed class AgentWindow
{
    public readonly RectTransform Holder, Frame;
    public readonly Transform Body;
    public event Action? Closed;
    private readonly Placement placement;

    public AgentWindow(Transform parent, string title, float width)
    {
        Holder = AgentUi.Rect("Window " + title, parent);
        Holder.anchorMin = Holder.anchorMax = Holder.pivot = new Vector2(.5f, .5f);
        Holder.sizeDelta = Vector2.zero;
        var shadow = AgentUi.Fill(AgentUi.Rect("Shadow", Holder), null, "shadow", 1);
        shadow.color = Color.white; shadow.raycastTarget = false;
        var outline = AgentUi.Fill(AgentUi.Rect("Outline", Holder), null, AgentUi.Rounded, 3);
        outline.color = new Color(0, 0, 0, .6f); outline.raycastTarget = false;
        Frame = AgentUi.Fill(AgentUi.Rect("Frame", Holder), AgentUi.Paint.Window, AgentUi.Rounded, 3).rectTransform;
        Frame.anchorMin = Frame.anchorMax = Frame.pivot = new Vector2(.5f, .5f);
        Frame.sizeDelta = new Vector2(width, 100);
        Frame.gameObject.AddComponent<Mask>().showMaskGraphic = true;
        var layout = Frame.gameObject.AddComponent<VerticalLayoutGroup>();
        layout.childControlWidth = layout.childControlHeight = true;
        layout.childForceExpandWidth = true;
        layout.childForceExpandHeight = false;
        Frame.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        placement = Holder.gameObject.AddComponent<Placement>();
        placement.Frame = Frame; placement.Shadow = shadow.rectTransform; placement.Outline = outline.rectTransform;

        var bar = AgentUi.Fill(AgentUi.Rect("TopBar", Frame), AgentUi.Paint.TopBar, null);
        AgentUi.Size(bar, height: 30);
        var barShadow = bar.gameObject.AddComponent<Shadow>();
        barShadow.effectColor = new Color(0, 0, 0, .12f);
        barShadow.effectDistance = new Vector2(0, -1);
        bar.gameObject.AddComponent<Drag>().Owner = placement;
        var titleText = AgentUi.Label(bar.transform, title, 18, AgentUi.Paint.TopBarText);
        AgentUi.Stretch(titleText.rectTransform, 10, 44);
        var close = AgentUi.Fill(AgentUi.Rect("CloseButton", bar.transform), null, null);
        close.color = Color.clear;
        close.raycastTarget = true;
        var closeRect = close.rectTransform;
        closeRect.anchorMin = new Vector2(1, 0); closeRect.anchorMax = new Vector2(1, 1); closeRect.pivot = new Vector2(1, .5f);
        closeRect.sizeDelta = new Vector2(40, 0); closeRect.anchoredPosition = Vector2.zero;
        var icon = AgentUi.Icon(close.transform, AgentUi.Sprite("close"), 22, AgentUi.Paint.CloseIcon);
        icon.rectTransform.anchorMin = icon.rectTransform.anchorMax = icon.rectTransform.pivot = new Vector2(.5f, .5f);
        icon.rectTransform.anchoredPosition = Vector2.zero;
        var closeButton = close.gameObject.AddComponent<Button>();
        closeButton.transition = Selectable.Transition.None;
        closeButton.onClick.AddListener(Close);
        close.gameObject.AddComponent<HandCursor>();
        var hover = close.gameObject.AddComponent<CloseHover>();
        hover.Back = close; hover.Icon = icon;
        Body = AgentUi.Column(Frame, 8, 12);
        Holder.gameObject.SetActive(false);
    }

    public bool Visible => Holder.gameObject.activeSelf;

    // Footer strip like the native windows': a band with the version link (main windows) on the left and actions on the right.
    public HorizontalLayoutGroup Footer(Action? openAbout)
    {
        var strip = AgentUi.Fill(AgentUi.Rect("Footer", Frame), AgentUi.Paint.Window2, null);
        var row = strip.gameObject.AddComponent<HorizontalLayoutGroup>();
        row.padding = new RectOffset(12, 12, 8, 8);
        row.spacing = 8;
        row.childAlignment = TextAnchor.MiddleRight;
        row.childControlWidth = row.childControlHeight = true;
        row.childForceExpandWidth = row.childForceExpandHeight = false;
        AgentUi.Size(row, height: 44);
        if (openAbout != null) AgentUi.VersionLink(row.transform, AgentInfo.Short, openAbout);
        AgentUi.Size(AgentUi.Rect("Spacer", row.transform), flexWidth: 1);
        return row;
    }

    // Docks the window above a bottom-bar item (right edges aligned) until the player drags it; null centres it.
    public void Show(RectTransform? dockAbove = null)
    {
        placement.DockAbove = dockAbove;
        if (dockAbove == null) Holder.anchoredPosition = Vector2.zero;
        Holder.gameObject.SetActive(true);
        Holder.SetAsLastSibling();
    }

    public void Close()
    {
        if (!Visible) return;
        Holder.gameObject.SetActive(false);
        Closed?.Invoke();
    }

    private sealed class Placement : MonoBehaviour
    {
        public RectTransform Frame = null!, Shadow = null!, Outline = null!;
        public RectTransform? DockAbove;

        private void LateUpdate()
        {
            var size = Frame.rect.size;
            Place(Outline, size + new Vector2(2, 2), 1);
            Place(Shadow, size + new Vector2(29, 29), 0);
            Frame.anchoredPosition = Vector2.zero;
            var holder = (RectTransform)transform;
            var parent = (RectTransform)holder.parent;
            if (DockAbove != null && DockAbove.gameObject.activeInHierarchy)
            {
                var corner = parent.InverseTransformPoint(DockAbove.TransformPoint(new Vector3(DockAbove.rect.xMax, DockAbove.rect.yMax)));
                holder.anchoredPosition = new Vector2(corner.x - size.x / 2, corner.y + 6 + size.y / 2);
            }
            // Keep the whole window on screen.
            var half = parent.rect.size / 2;
            var p = holder.anchoredPosition;
            holder.anchoredPosition = new Vector2(Mathf.Clamp(p.x, -half.x + size.x / 2, half.x - size.x / 2), Mathf.Clamp(p.y, -half.y + size.y / 2, half.y - size.y / 2));
        }

        private static void Place(RectTransform rect, Vector2 size, int order) { rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(.5f, .5f); rect.sizeDelta = size; rect.anchoredPosition = Vector2.zero; rect.SetSiblingIndex(order); }
    }

    private sealed class Drag : MonoBehaviour, IDragHandler, IBeginDragHandler
    {
        public Placement Owner = null!;
        public void OnBeginDrag(PointerEventData data) { Owner.DockAbove = null; Owner.transform.SetAsLastSibling(); }
        public void OnDrag(PointerEventData data)
        {
            var canvas = Owner.GetComponentInParent<Canvas>();
            ((RectTransform)Owner.transform).anchoredPosition += data.delta / (canvas != null ? canvas.scaleFactor : 1f);
        }
    }

    private sealed class CloseHover : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
    {
        public Image Back = null!, Icon = null!;
        public void OnPointerEnter(PointerEventData data) { Painted.Set(Back, AgentUi.Paint.CloseHover); Painted.Set(Icon, AgentUi.Paint.CtaText); }
        public void OnPointerExit(PointerEventData data) { Painted.Clear(Back); Back.color = Color.clear; Painted.Set(Icon, AgentUi.Paint.CloseIcon); }
    }
}

// Keeps a graphic in its theme colour; re-applied when the theme changes.
internal sealed class Painted : MonoBehaviour
{
    public Graphic Target = null!;
    public AgentUi.Paint Paint;
    private int revision = -1;

    public static void Add(Graphic graphic, AgentUi.Paint paint)
    {
        var painted = graphic.gameObject.AddComponent<Painted>();
        painted.Target = graphic;
        painted.Paint = paint;
        painted.Apply();
    }

    public static void Set(Graphic graphic, AgentUi.Paint paint)
    {
        var painted = graphic.GetComponents<Painted>().FirstOrDefault(p => p.Target == graphic);
        if (painted == null) Add(graphic, paint);
        else { painted.Paint = paint; painted.enabled = true; painted.Apply(); }
    }

    public static void Clear(Graphic graphic) { foreach (var p in graphic.GetComponents<Painted>()) p.enabled = false; }

    private void Apply()
    {
        Target.color = AgentUi.Of(Paint);
        if (Target is TMP_Text text && AgentUi.Font != null && text.font != AgentUi.Font) text.font = AgentUi.Font;
        revision = AgentUi.Revision;
    }

    private void LateUpdate() { if (revision != AgentUi.Revision) Apply(); }
}

// Native controls drop their outline in dark themes; call-to-action buttons have none (Off).
internal sealed class LightOnly : MonoBehaviour
{
    public Behaviour Target = null!;
    public bool Off;
    private void LateUpdate() { if (Target != null) Target.enabled = !Off && !AgentUi.Dark; }
}

// The game's hand cursor over clickable things (ProcessorTycoon.CursorController, by name).
internal sealed class HandCursor : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
{
    private static MethodInfo? set;
    private static object? controller;
    private static Type? cursorType;

    public void OnPointerEnter(PointerEventData data) { var s = GetComponent<Selectable>(); if (s == null || s.interactable) Set("Hand"); }
    public void OnPointerExit(PointerEventData data) => Set("Mouse");
    private void OnDisable() => Set("Mouse");

    internal static void Set(string cursor)
    {
        try
        {
            if (controller == null || controller is Object o && o == null)
            {
                var type = AgentUi.GameType("ProcessorTycoon.CursorController");
                controller = type?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                set = type?.GetMethod("SetCursorType");
                cursorType = set?.GetParameters().FirstOrDefault()?.ParameterType;
            }
            if (controller != null && set != null && cursorType != null) set.Invoke(controller, new[] { Enum.Parse(cursorType, cursor) });
        }
        catch (Exception) { }
    }
}

// Opens the web page of a clicked <link="url"> in a text; the hand cursor shows over the links.
internal sealed class TextLinks : MonoBehaviour, IPointerClickHandler, IPointerMoveHandler, IPointerExitHandler
{
    private bool overLink;

    public void OnPointerClick(PointerEventData data)
    {
        var index = LinkAt(data);
        if (index >= 0) Application.OpenURL(GetComponent<TMP_Text>().textInfo.linkInfo[index].GetLinkID());
    }

    public void OnPointerMove(PointerEventData data) => Hand(LinkAt(data) >= 0);
    public void OnPointerExit(PointerEventData data) => Hand(false);
    private void OnDisable() => Hand(false);
    private int LinkAt(PointerEventData data) => TMP_TextUtilities.FindIntersectingLink(GetComponent<TMP_Text>(), data.position, null);

    private void Hand(bool over)
    {
        if (over == overLink) return;
        overLink = over;
        HandCursor.Set(over ? "Hand" : "Mouse");
    }
}

// Version link: underlined in the call-to-action colour while hovered.
internal sealed class LinkHover : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
{
    public TextMeshProUGUI Text = null!;
    public void OnPointerEnter(PointerEventData data) { Painted.Set(Text, AgentUi.Paint.Cta); Text.fontStyle |= FontStyles.Underline; }
    public void OnPointerExit(PointerEventData data) { Painted.Set(Text, AgentUi.Paint.TextLow); Text.fontStyle &= ~FontStyles.Underline; }
}

// Hover text for the mod's own controls. Header and Text may change while shown.
internal sealed class Tip : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
{
    public string Header = "", Text = "";

    public static Tip On(GameObject target, string header, string text)
    {
        if (target.GetComponent<Graphic>() == null) target.AddComponent<Image>().color = Color.clear;
        var tip = target.AddComponent<Tip>();
        tip.Header = header;
        tip.Text = text;
        return tip;
    }

    public void OnPointerEnter(PointerEventData data) => TipView.Hover(this);
    public void OnPointerExit(PointerEventData data) => TipView.Leave(this);
    private void OnDisable() => TipView.Leave(this);
}

// The one tooltip box of the overlay, drawn like the game's (dark translucent box, white text) and shown after the game's
// delay (0.15 s) next to the mouse. It lives on the overlay canvas so it shows above the mod's windows.
internal sealed class TipView : MonoBehaviour
{
    private static TipView? instance;
    private Tip? target;
    private float since;
    private RectTransform box = null!;
    private TextMeshProUGUI header = null!, text = null!;

    public static void Hover(Tip tip)
    {
        if (instance == null && AgentUi.Root != null) instance = Create(AgentUi.Root);
        if (instance == null) return;
        instance.target = tip;
        instance.since = Time.unscaledTime;
    }

    public static void Leave(Tip tip) { if (instance != null && instance.target == tip) instance.target = null; }

    private static TipView Create(RectTransform parent)
    {
        var root = AgentUi.Rect("Tooltip", parent);
        root.anchorMin = root.anchorMax = Vector2.zero;
        root.sizeDelta = Vector2.zero;
        var view = root.gameObject.AddComponent<TipView>();
        view.box = AgentUi.Rect("Box", root);
        view.box.anchorMin = view.box.anchorMax = Vector2.zero;
        var back = AgentUi.Fill(view.box, null);
        back.color = new Color(.137f, .137f, .137f, .93f);
        back.raycastTarget = false;
        var column = view.box.gameObject.AddComponent<VerticalLayoutGroup>();
        column.padding = new RectOffset(10, 10, 7, 9);
        column.spacing = 2;
        column.childControlWidth = column.childControlHeight = true;
        column.childForceExpandWidth = true;
        column.childForceExpandHeight = false;
        view.box.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        view.header = Plain(AgentUi.Label(view.box, "", 17), Color.white);
        view.text = Plain(AgentUi.Label(view.box, "", 15, wrap: true), new Color(1, 1, 1, .88f));
        view.box.gameObject.SetActive(false);
        return view;
    }

    private static TextMeshProUGUI Plain(TextMeshProUGUI label, Color color)
    {
        DestroyImmediate(label.GetComponent<Painted>());
        label.color = color;
        return label;
    }

    private void LateUpdate()
    {
        var show = target != null && target.isActiveAndEnabled && Time.unscaledTime - since >= .15f;
        if (box.gameObject.activeSelf != show) box.gameObject.SetActive(show);
        if (!show) return;
        transform.SetAsLastSibling();
        if (header.text != target!.Header || text.text != target.Text)
        {
            if (AgentUi.Font != null) header.font = text.font = AgentUi.Font;
            header.text = target.Header;
            text.text = target.Text;
            header.gameObject.SetActive(target.Header.Length > 0);
            var width = Mathf.Max(header.GetPreferredValues(target.Header).x, text.GetPreferredValues(target.Text).x) + 21;
            box.sizeDelta = new Vector2(Mathf.Min(320, width), box.sizeDelta.y);
        }
        // Below-right of the mouse, flipped to stay on screen.
        var canvas = GetComponentInParent<Canvas>();
        var scale = Mathf.Max(.01f, canvas != null ? canvas.scaleFactor : 1f);
        var mouse = (Vector2)Input.mousePosition / scale;
        var size = box.sizeDelta;
        bool left = mouse.x + 14 + size.x > Screen.width / scale, up = mouse.y - 20 - size.y < 0;
        box.pivot = new Vector2(left ? 1 : 0, up ? 0 : 1);
        box.anchoredPosition = mouse + new Vector2(left ? -6 : 14, up ? 12 : -20);
    }
}

internal sealed class SegmentsView : MonoBehaviour
{
    public Func<int> Selected = () => -1;
    public int Hovered = -1;
    public readonly List<(TextMeshProUGUI text, Image bar)> Options = new();
    private string shown = "";

    private void LateUpdate()
    {
        var selected = Selected();
        var state = $"{selected}|{Hovered}|{AgentUi.Revision}";
        if (state == shown) return;
        shown = state;
        for (var i = 0; i < Options.Count; i++)
        {
            Painted.Set(Options[i].text, i == selected ? AgentUi.Paint.TabTextOn : i == Hovered ? AgentUi.Paint.TabTextHover : AgentUi.Paint.TabText);
            Options[i].bar.enabled = i == selected;
        }
    }
}

internal sealed class SegmentHover : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
{
    public SegmentsView View = null!;
    public int Index;
    public void OnPointerEnter(PointerEventData data) => View.Hovered = Index;
    public void OnPointerExit(PointerEventData data) { if (View.Hovered == Index) View.Hovered = -1; }
}

internal sealed class CheckView : MonoBehaviour
{
    public Func<bool> Get = () => false;
    public Image Mark = null!;
    private void LateUpdate() { var on = Get(); if (Mark.enabled != on) Mark.enabled = on; }
}

// Keeps Mark's icon in front of the first character of its link, centred on the capital letters.
internal sealed class InlineMark : MonoBehaviour
{
    public TextMeshProUGUI Text = null!;
    public RectTransform Icon = null!;
    public string LinkId = "";

    private void LateUpdate()
    {
        var info = Text.textInfo;
        for (var i = 0; info != null && i < info.linkCount; i++)
        {
            if (info.linkInfo[i].GetLinkID() != LinkId) continue;
            var c = info.characterInfo[info.linkInfo[i].linkTextfirstCharacterIndex];
            var size = Text.fontSize;
            Icon.anchorMin = Icon.anchorMax = Text.rectTransform.pivot;
            Icon.pivot = new Vector2(1, .5f);
            Icon.sizeDelta = new Vector2(size, size);
            Icon.anchoredPosition = new Vector2(c.bottomLeft.x - size * .12f, (c.topLeft.y + c.bottomLeft.y) / 2);
            if (!Icon.gameObject.activeSelf) Icon.gameObject.SetActive(true);
            return;
        }
        if (Icon.gameObject.activeSelf) Icon.gameObject.SetActive(false);
    }
}

// The Claude mark (Anthropic's spark) for the credits, drawn in code rather than shipped as a file: twelve slightly
// uneven rays around a small core, in Claude's orange.
internal static class ClaudeMark
{
    private static readonly (float angle, float length)[] Rays =
        { (7, .89f), (48, .92f), (81, .89f), (116, 1f), (148, .95f), (180, .94f), (214, .89f), (237, .96f), (266, .93f), (299, .92f), (316, .94f), (346, .91f) };
    private static Sprite? sprite;

    public static Sprite Sprite => sprite != null ? sprite : sprite = Draw(64);

    private static Sprite Draw(int size)
    {
        var texture = new Texture2D(size, size, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp, name = "Claude mark" };
        var pixels = new Color32[size * size];
        float centre = size / 2f, radius = size / 2f - 1;
        const int samples = 4;
        for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
            {
                var hits = 0;
                for (var sy = 0; sy < samples; sy++)
                    for (var sx = 0; sx < samples; sx++)
                        if (Inside((x + (sx + .5f) / samples - centre) / radius, (y + (sy + .5f) / samples - centre) / radius)) hits++;
                pixels[y * size + x] = new Color32(0xD9, 0x77, 0x57, (byte)(255 * hits / (samples * samples)));
            }
        texture.SetPixels32(pixels);
        texture.Apply();
        return UnityEngine.Sprite.Create(texture, new Rect(0, 0, size, size), new Vector2(.5f, .5f), 100);
    }

    // Unit coordinates, the longest ray reaching 1.
    private static bool Inside(float x, float y)
    {
        if (x * x + y * y < .27f * .27f) return true;
        foreach (var (angle, length) in Rays)
        {
            float a = angle * Mathf.Deg2Rad, dx = Mathf.Cos(a), dy = Mathf.Sin(a);
            float along = x * dx + y * dy, across = Mathf.Abs(y * dx - x * dy);
            if (along >= 0 && along <= length && across <= Mathf.Lerp(.1f, .065f, along / length)) return true;
            float tx = x - dx * length, ty = y - dy * length;
            if (tx * tx + ty * ty <= .065f * .065f) return true;
        }
        return false;
    }
}
