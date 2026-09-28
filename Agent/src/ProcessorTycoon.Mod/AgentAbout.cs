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

// The About window, drawn like the game's own windows: rounded frame with shadow, title bar with close button, the current
// theme's colours (read from the game's ThemeManager when shown; light-theme values otherwise), Roboto, sections and a
// footer strip with buttons. Kept visually in step with the Multiplayer mod's About window.
internal sealed class AgentAbout
{
    private enum Paint { Window, Window2, TopBar, TopBarText, CloseIcon, CloseHover, Text, TextLow, Header, Button, ButtonText, Cta, CtaText }
    private const string Rounded = "RoundCorners42";
    private const float Width = 440;
    private static readonly Dictionary<Paint, (string member, string light)> themeMembers = new()
    {
        [Paint.Window] = ("Background", "#F5F5F5"), [Paint.Window2] = ("Background2", "#E9EBEE"), [Paint.TopBar] = ("TopBarBackground", "#DFE2E6"),
        [Paint.TopBarText] = ("TopBarText", "#2E3033"), [Paint.CloseIcon] = ("TopBarCloseIcon", "#1A1A1A"), [Paint.CloseHover] = ("TopBarCloseButton", "#E63D17"),
        [Paint.Text] = ("Text", "#141414"), [Paint.TextLow] = ("TextLowHierarchy", "#4C4C4C"), [Paint.Header] = ("Header", "#141414"),
        [Paint.Button] = ("ButtonBackground", "#FFFFFF"), [Paint.ButtonText] = ("ButtonText", "#141414"), [Paint.Cta] = ("CTAButtonBackground", "#2784D2"), [Paint.CtaText] = ("CTAButtonText", "#FFFFFF"),
    };
    private readonly Dictionary<Paint, Color> colors = themeMembers.ToDictionary(p => p.Key, p => Hex(p.Value.light));
    private readonly List<(Graphic graphic, Paint paint)> painted = new();
    private readonly List<Outline> lightOutlines = new();
    private readonly RectTransform holder;
    private readonly TMP_FontAsset font;
    private readonly TextMeshProUGUI byline;
    private bool dark;

    public AgentAbout(Transform parent, TMP_FontAsset fallbackFont, Sprite icon, Sprite fallbackClose)
    {
        font = Resources.FindObjectsOfTypeAll<TMP_FontAsset>().FirstOrDefault(f => f.name == "Roboto-Regular SDF") ?? fallbackFont;
        holder = Rect("Agent about", parent);
        holder.anchorMin = holder.anchorMax = holder.pivot = new Vector2(.5f, .5f);
        holder.sizeDelta = Vector2.zero;
        var shadow = Fill(Rect("Shadow", holder), null, "shadow", 1);
        shadow.color = Color.white;
        var outline = Fill(Rect("Outline", holder), null, Rounded, 3);
        outline.color = new Color(0, 0, 0, .6f);
        var frame = Fill(Rect("Frame", holder), Paint.Window, Rounded, 3).rectTransform;
        frame.anchorMin = frame.anchorMax = frame.pivot = new Vector2(.5f, .5f);
        frame.sizeDelta = new Vector2(Width, 100);
        frame.gameObject.AddComponent<Mask>().showMaskGraphic = true;
        var layout = frame.gameObject.AddComponent<VerticalLayoutGroup>();
        layout.childControlWidth = layout.childControlHeight = true;
        layout.childForceExpandWidth = true;
        layout.childForceExpandHeight = false;
        frame.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        var follow = holder.gameObject.AddComponent<FollowFrame>();
        follow.Frame = frame; follow.Shadow = shadow.rectTransform; follow.Outline = outline.rectTransform;

        var bar = Fill(Rect("TopBar", frame), Paint.TopBar, null);
        Size(bar, height: 30);
        var barShadow = bar.gameObject.AddComponent<Shadow>();
        barShadow.effectColor = new Color(0, 0, 0, .12f);
        barShadow.effectDistance = new Vector2(0, -1);
        bar.gameObject.AddComponent<Drag>().Target = holder;
        var title = Label(bar.transform, "About Agent", 18, Paint.TopBarText);
        Stretch(title.rectTransform, 10, 44);
        var close = Fill(Rect("CloseButton", bar.transform), null, null);
        close.color = Color.clear;
        var closeRect = close.rectTransform;
        closeRect.anchorMin = new Vector2(1, 0); closeRect.anchorMax = new Vector2(1, 1); closeRect.pivot = new Vector2(1, .5f);
        closeRect.sizeDelta = new Vector2(40, 0); closeRect.anchoredPosition = Vector2.zero;
        var closeIcon = Icon(close.transform, NativeSprite("close") ?? fallbackClose, 22, Paint.CloseIcon);
        closeIcon.rectTransform.anchorMin = closeIcon.rectTransform.anchorMax = closeIcon.rectTransform.pivot = new Vector2(.5f, .5f);
        closeIcon.rectTransform.anchoredPosition = Vector2.zero;
        var closeButton = close.gameObject.AddComponent<Button>();
        closeButton.transition = Selectable.Transition.None;
        closeButton.onClick.AddListener(() => SetVisible(false));
        var hover = close.gameObject.AddComponent<CloseHover>();
        hover.Back = close; hover.Icon = closeIcon; hover.Owner = this;

        var body = Column(frame, 8, 12);
        var head = Row(body, 12, 52);
        Icon(head, icon, 44, Paint.Cta);
        var names = Column(head, 0, 0);
        Size(names, flexWidth: 1);
        Label(names, "<b>" + AgentInfo.Name + "</b>", 18);
        Label(names, $"Version {AgentInfo.Version}  “{AgentInfo.Release}”  ·  {AgentInfo.License} License", 15, Paint.TextLow);
        Label(body, "Lets AI agents and scripts play through the visible game with native player actions, and shows what they do.", 15, wrap: true);
        Label(body, "Credits", 18, Paint.Header);
        byline = Label(body, "", 16, wrap: true);
        byline.raycastTarget = true;
        byline.gameObject.AddComponent<TextLinks>();
        Label(body, "Built with", 18, Paint.Header);
        foreach (var (name, note) in AgentInfo.ThirdParty) Label(body, name, 15).name = "ThirdParty " + note;
        Size(Rect("Space", body), height: 2);
        Label(body, AgentInfo.Disclaimer, 13, Paint.TextLow, wrap: true);

        var strip = Fill(Rect("Footer", frame), Paint.Window2, null);
        var footer = strip.gameObject.AddComponent<HorizontalLayoutGroup>();
        footer.padding = new RectOffset(12, 12, 8, 8);
        footer.spacing = 8;
        footer.childAlignment = TextAnchor.MiddleRight;
        footer.childControlWidth = footer.childControlHeight = true;
        footer.childForceExpandWidth = footer.childForceExpandHeight = false;
        Button(strip.transform, "Report an issue", () => Application.OpenURL(AgentInfo.Issues));
        Button(strip.transform, "Discord", () => Application.OpenURL(AgentInfo.Discord));
        Button(strip.transform, "GitHub", () => Application.OpenURL(AgentInfo.Repository), cta: true);
        Button(strip.transform, "Close", () => SetVisible(false));
        holder.gameObject.SetActive(false);
    }

    public bool Visible => holder.gameObject.activeSelf;

    public void SetVisible(bool visible)
    {
        if (visible) { ApplyTheme(); holder.anchoredPosition = Vector2.zero; holder.SetAsLastSibling(); }
        holder.gameObject.SetActive(visible);
    }

    private void ApplyTheme()
    {
        var theme = CurrentTheme();
        if (theme != null)
        {
            // Theme colours are Color32.
            foreach (var pair in themeMembers) if (Member(theme, pair.Value.member) is Color32 c) colors[pair.Key] = c;
            dark = Member(theme, "IsDark") is true;
        }
        foreach (var (graphic, paint) in painted) graphic.color = colors[paint];
        foreach (var outline in lightOutlines) outline.enabled = !dark;
        var low = ColorUtility.ToHtmlStringRGB(colors[Paint.TextLow]);
        foreach (var text in holder.GetComponentsInChildren<TextMeshProUGUI>(true).Where(t => t.name.StartsWith("ThirdParty ", StringComparison.Ordinal)))
            text.text = text.text.Split(new[] { "  <color" }, StringSplitOptions.None)[0] + $"  <color=#{low}>{text.name.Substring("ThirdParty ".Length)}</color>";
        var cta = ColorUtility.ToHtmlStringRGB(colors[Paint.Cta]);
        byline.text = AgentInfo.Byline((name, url) => $"<link=\"{url}\"><u><color=#{cta}>{name}</color></u></link>");
    }

    // The game's current UI theme (ProcessorTycoon.UI.Themes.ThemeManager), used only to colour this window.
    private static object? CurrentTheme()
    {
        try
        {
            var type = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("ProcessorTycoon.UI.Themes.ThemeManager", false)).FirstOrDefault(t => t != null);
            var manager = type?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            return manager == null ? null : Member(manager, "CurrentTheme");
        }
        catch (Exception) { return null; }
    }

    private static object? Member(object target, string name)
    {
        var type = target.GetType();
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        return type.GetProperty(name, flags)?.GetValue(target) ?? type.GetField(name, flags)?.GetValue(target);
    }

    private void Button(Transform parent, string text, Action action, bool cta = false)
    {
        var image = Fill(Rect("Button " + text, parent), cta ? Paint.Cta : Paint.Button, Rounded, 5);
        if (!cta)
        {
            var outline = image.gameObject.AddComponent<Outline>();
            outline.effectColor = new Color(0, 0, 0, .28f);
            outline.effectDistance = new Vector2(1, -1);
            lightOutlines.Add(outline);
        }
        var button = image.gameObject.AddComponent<Button>();
        var states = ColorBlock.defaultColorBlock;
        states.highlightedColor = new Color(.93f, .93f, .93f);
        states.pressedColor = new Color(.84f, .84f, .84f);
        states.selectedColor = Color.white;
        states.fadeDuration = .08f;
        button.colors = states;
        button.navigation = new Navigation { mode = Navigation.Mode.None };
        button.onClick.AddListener(() => action());
        var fit = image.gameObject.AddComponent<HorizontalLayoutGroup>();
        fit.padding = new RectOffset(14, 14, 0, 0);
        fit.childAlignment = TextAnchor.MiddleCenter;
        fit.childControlWidth = fit.childControlHeight = true;
        fit.childForceExpandWidth = fit.childForceExpandHeight = true;
        Label(image.transform, text, 15, cta ? Paint.CtaText : Paint.ButtonText, TextAlignmentOptions.Center);
        var size = Size(image, height: 28);
        size.flexibleWidth = size.flexibleHeight = 0;
        size.minWidth = 70;
    }

    private TextMeshProUGUI Label(Transform parent, string text, float size, Paint paint = Paint.Text, TextAlignmentOptions align = TextAlignmentOptions.MidlineLeft, bool wrap = false)
    {
        var label = Rect("Text", parent).gameObject.AddComponent<TextMeshProUGUI>();
        label.font = font;
        label.fontSize = size;
        label.text = text;
        label.alignment = align;
        label.raycastTarget = false;
        label.richText = true;
        label.textWrappingMode = wrap ? TextWrappingModes.Normal : TextWrappingModes.NoWrap;
        label.overflowMode = wrap ? TextOverflowModes.Overflow : TextOverflowModes.Ellipsis;
        painted.Add((label, paint));
        var layout = label.gameObject.AddComponent<LayoutElement>();
        if (!wrap) layout.preferredHeight = Mathf.Ceil(size * 1.35f);
        return label;
    }

    private Image Icon(Transform parent, Sprite sprite, float size, Paint paint)
    {
        var image = Rect("Icon", parent).gameObject.AddComponent<Image>();
        image.sprite = sprite;
        image.preserveAspect = true;
        image.raycastTarget = false;
        painted.Add((image, paint));
        var layout = Size(image, size, size);
        image.rectTransform.sizeDelta = new Vector2(size, size);
        return image;
    }

    private Image Fill(RectTransform rect, Paint? paint, string? sprite, float corners = 5)
    {
        var image = rect.gameObject.AddComponent<Image>();
        var native = sprite != null ? NativeSprite(sprite) : null;
        if (native != null) { image.sprite = native; image.type = Image.Type.Sliced; image.pixelsPerUnitMultiplier = corners; }
        if (paint.HasValue) painted.Add((image, paint.Value));
        else image.raycastTarget = false;
        return image;
    }

    private static Transform Column(Transform parent, float spacing, int padding)
    {
        var group = Rect("Column", parent).gameObject.AddComponent<VerticalLayoutGroup>();
        group.spacing = spacing;
        group.padding = new RectOffset(padding, padding, padding, padding);
        group.childControlWidth = group.childControlHeight = true;
        group.childForceExpandWidth = true;
        group.childForceExpandHeight = false;
        return group.transform;
    }

    private static Transform Row(Transform parent, float spacing, float height)
    {
        var group = Rect("Row", parent).gameObject.AddComponent<HorizontalLayoutGroup>();
        group.spacing = spacing;
        group.childAlignment = TextAnchor.MiddleLeft;
        group.childControlWidth = group.childControlHeight = true;
        group.childForceExpandWidth = group.childForceExpandHeight = false;
        Size(group, height: height);
        return group.transform;
    }

    private static LayoutElement Size(Component c, float width = -1, float height = -1, float flexWidth = -1)
    {
        var layout = c.GetComponent<LayoutElement>() ?? c.gameObject.AddComponent<LayoutElement>();
        if (width >= 0) layout.preferredWidth = layout.minWidth = width;
        if (height >= 0) layout.preferredHeight = layout.minHeight = height;
        if (flexWidth >= 0) layout.flexibleWidth = flexWidth;
        return layout;
    }

    private static RectTransform Rect(string name, Transform parent)
    {
        var rect = (RectTransform)new GameObject(name, typeof(RectTransform)).transform;
        rect.SetParent(parent, false);
        return rect;
    }

    private static void Stretch(RectTransform rect, float left, float right) { rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one; rect.offsetMin = new Vector2(left, 0); rect.offsetMax = new Vector2(-right, 0); }
    private static Sprite? NativeSprite(string name) => Resources.FindObjectsOfTypeAll<Sprite>().FirstOrDefault(s => s.name == name);
    private static Color Hex(string html) => ColorUtility.TryParseHtmlString(html, out var c) ? c : Color.magenta;

    // Opens the web page of a clicked <link="url"> in a text.
    private sealed class TextLinks : MonoBehaviour, IPointerClickHandler
    {
        public void OnPointerClick(PointerEventData data)
        {
            var text = GetComponent<TMP_Text>();
            var index = TMP_TextUtilities.FindIntersectingLink(text, data.position, null);
            if (index >= 0) Application.OpenURL(text.textInfo.linkInfo[index].GetLinkID());
        }
    }

    // Keeps the outline and shadow sized to the frame, which fits its content.
    private sealed class FollowFrame : MonoBehaviour
    {
        public RectTransform Frame = null!, Shadow = null!, Outline = null!;
        private void LateUpdate()
        {
            var size = Frame.rect.size;
            Place(Outline, size + new Vector2(2, 2), 1);
            Place(Shadow, size + new Vector2(29, 29), 0);
            Frame.anchoredPosition = Vector2.zero;
        }
        private static void Place(RectTransform rect, Vector2 size, int order) { rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(.5f, .5f); rect.sizeDelta = size; rect.anchoredPosition = Vector2.zero; rect.SetSiblingIndex(order); }
    }

    private sealed class Drag : MonoBehaviour, IDragHandler, IBeginDragHandler
    {
        public RectTransform Target = null!;
        public void OnBeginDrag(PointerEventData data) => Target.SetAsLastSibling();
        public void OnDrag(PointerEventData data)
        {
            var canvas = Target.GetComponentInParent<Canvas>();
            Target.anchoredPosition += data.delta / (canvas != null ? canvas.scaleFactor : 1f);
        }
    }

    private sealed class CloseHover : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
    {
        public Image Back = null!, Icon = null!;
        public AgentAbout Owner = null!;
        public void OnPointerEnter(PointerEventData data) { Back.color = Owner.colors[Paint.CloseHover]; Icon.color = Owner.colors[Paint.CtaText]; }
        public void OnPointerExit(PointerEventData data) { Back.color = Color.clear; Icon.color = Owner.colors[Paint.CloseIcon]; }
    }
}
