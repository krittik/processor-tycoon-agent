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

// Native-looking pieces for the mod's own windows (Agent settings, About), kept in step with the Multiplayer mod's windows:
// the game's RoundCorners42 sprite and Roboto font, the current theme's colours, the hand cursor and the native button press.
// Game classes are only read by name for looks (ThemeManager colours, CursorController); nothing about gameplay.
internal static class AgentUi
{
    internal enum Paint { Window, Window2, TopBar, TopBarText, CloseIcon, CloseHover, Text, TextLow, Header, Button, ButtonText, Cta, CtaText, Field, Line, LineActive }
    private const string Rounded = "RoundCorners42";
    private static readonly Dictionary<Paint, (string member, string light)> themeMembers = new()
    {
        [Paint.Window] = ("Background", "#F5F5F5"), [Paint.Window2] = ("Background2", "#E9EBEE"), [Paint.TopBar] = ("TopBarBackground", "#DFE2E6"),
        [Paint.TopBarText] = ("TopBarText", "#2E3033"), [Paint.CloseIcon] = ("TopBarCloseIcon", "#1A1A1A"), [Paint.CloseHover] = ("TopBarCloseButton", "#E63D17"),
        [Paint.Text] = ("Text", "#141414"), [Paint.TextLow] = ("TextLowHierarchy", "#4C4C4C"), [Paint.Header] = ("Header", "#141414"),
        [Paint.Button] = ("ButtonBackground", "#FFFFFF"), [Paint.ButtonText] = ("ButtonText", "#141414"), [Paint.Cta] = ("CTAButtonBackground", "#2784D2"),
        [Paint.CtaText] = ("CTAButtonText", "#FFFFFF"), [Paint.Field] = ("InputFieldBackground", "#FFFFFF"), [Paint.Line] = ("InputBottomLineUnselected", "#878787"),
        [Paint.LineActive] = ("InputBottomLineSelected", "#2784D2"),
    };
    private static readonly Dictionary<Paint, Color> colors = themeMembers.ToDictionary(p => p.Key, p => Hex(p.Value.light));
    private static readonly Dictionary<string, Sprite?> sprites = new();
    private static object? shownTheme;
    private static Type? themeManager;
    public static bool Dark { get; private set; }
    public static int Revision { get; private set; }
    public static TMP_FontAsset? Font { get; private set; }

    public static Color Of(Paint paint) => colors[paint];

    // Called every frame by the overlay; cheap unless the theme changed.
    public static void Refresh()
    {
        if (Font == null) { Font = Resources.FindObjectsOfTypeAll<TMP_FontAsset>().FirstOrDefault(f => f.name == "Roboto-Regular SDF"); if (Font != null) Revision++; }
        object? theme;
        try
        {
            themeManager ??= AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("ProcessorTycoon.UI.Themes.ThemeManager", false)).FirstOrDefault(t => t != null);
            var manager = themeManager?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            theme = manager == null ? null : Member(manager, "CurrentTheme");
        }
        catch (Exception) { theme = null; }
        if (theme == null || ReferenceEquals(theme, shownTheme)) return;
        shownTheme = theme;
        // Theme colours are Color32.
        foreach (var pair in themeMembers) if (Member(theme, pair.Value.member) is Color32 c) colors[pair.Key] = c;
        Dark = Member(theme, "IsDark") is true;
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

    public static TextMeshProUGUI Label(Transform parent, string text, float size = 16, Paint paint = Paint.Text, TextAlignmentOptions align = TextAlignmentOptions.MidlineLeft, bool wrap = false)
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
        var image = Fill(Rect("Button " + text, parent), cta ? Paint.Cta : Paint.Button);
        if (!cta)
        {
            var outline = image.gameObject.AddComponent<Outline>();
            outline.effectColor = new Color(0, 0, 0, .28f);
            outline.effectDistance = new Vector2(1, -1);
            image.gameObject.AddComponent<LightOnly>().Target = outline;
        }
        var button = image.gameObject.AddComponent<Button>();
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
        Label(image.transform, text, size, cta ? Paint.CtaText : Paint.ButtonText, TextAlignmentOptions.Center);
        var layout = Size(image, height: height);
        layout.flexibleWidth = layout.flexibleHeight = 0;
        layout.minWidth = width >= 0 ? width : 60;
        if (width >= 0) layout.preferredWidth = width;
        return button;
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

    private static object? Member(object target, string name)
    {
        var type = target.GetType();
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        return type.GetProperty(name, flags)?.GetValue(target) ?? type.GetField(name, flags)?.GetValue(target);
    }

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
        var outline = AgentUi.Fill(AgentUi.Rect("Outline", Holder), null, "RoundCorners42", 3);
        outline.color = new Color(0, 0, 0, .6f); outline.raycastTarget = false;
        Frame = AgentUi.Fill(AgentUi.Rect("Frame", Holder), AgentUi.Paint.Window, "RoundCorners42", 3).rectTransform;
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

    // Footer strip like the native windows': a band with the version link on the left and buttons on the right.
    public HorizontalLayoutGroup Footer(Action? openAbout)
    {
        var strip = AgentUi.Fill(AgentUi.Rect("Footer", Frame), AgentUi.Paint.Window2, null);
        var row = strip.gameObject.AddComponent<HorizontalLayoutGroup>();
        row.padding = new RectOffset(12, 12, 8, 8);
        row.spacing = 8;
        row.childAlignment = TextAnchor.MiddleRight;
        row.childControlWidth = row.childControlHeight = true;
        row.childForceExpandWidth = row.childForceExpandHeight = false;
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

// Native controls drop their outline in dark themes.
internal sealed class LightOnly : MonoBehaviour
{
    public Behaviour Target = null!;
    private void LateUpdate() { if (Target != null) Target.enabled = !AgentUi.Dark; }
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

    private static void Set(string cursor)
    {
        try
        {
            if (controller == null || controller is Object o && o == null)
            {
                var type = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("ProcessorTycoon.CursorController", false)).FirstOrDefault(t => t != null);
                controller = type?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                set = type?.GetMethod("SetCursorType");
                cursorType = set?.GetParameters().FirstOrDefault()?.ParameterType;
            }
            if (controller != null && set != null && cursorType != null) set.Invoke(controller, new[] { Enum.Parse(cursorType, cursor) });
        }
        catch (Exception) { }
    }
}

// Opens the web page of a clicked <link="url"> in a text.
internal sealed class TextLinks : MonoBehaviour, IPointerClickHandler
{
    public void OnPointerClick(PointerEventData data)
    {
        var text = GetComponent<TMP_Text>();
        var index = TMP_TextUtilities.FindIntersectingLink(text, data.position, null);
        if (index >= 0) Application.OpenURL(text.textInfo.linkInfo[index].GetLinkID());
    }
}

// Version link: underlined in the call-to-action colour while hovered.
internal sealed class LinkHover : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
{
    public TextMeshProUGUI Text = null!;
    public void OnPointerEnter(PointerEventData data) { Painted.Set(Text, AgentUi.Paint.Cta); Text.fontStyle |= FontStyles.Underline; }
    public void OnPointerExit(PointerEventData data) { Painted.Set(Text, AgentUi.Paint.TextLow); Text.fontStyle &= ~FontStyles.Underline; }
}
