using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace ProcessorTycoonModApi;

// Builders for native-looking controls: the game's RoundCorners42 sprite and Roboto font, theme colours, outlines that
// disappear in dark themes like the game's, the hand cursor, the native button press and tooltips. Layout groups size
// everything. The conventions these follow are in docs/CONVENTIONS.md.
internal static class Ui
{
    public const string Rounded = "RoundCorners42";

    public static RectTransform Rect(string name, Transform parent)
    {
        var rect = (RectTransform)new GameObject(name, typeof(RectTransform)).transform;
        rect.SetParent(parent, false);
        return rect;
    }

    // Corner scale as on the native controls (pixelsPerUnitMultiplier of RoundCorners42): windows 3, buttons, dropdowns and
    // toggles 5, scrollbars 4, input fields 10. A null paint leaves the colour to the caller.
    public static Image Fill(RectTransform rect, Paint? paint, string? sprite = Rounded, float corners = 5)
    {
        var image = rect.gameObject.AddComponent<Image>();
        var native = sprite != null ? Theme.Sprite(sprite) : null;
        if (native != null) { image.sprite = native; image.type = Image.Type.Sliced; image.pixelsPerUnitMultiplier = corners; }
        if (paint.HasValue) Painted.Add(image, paint.Value);
        return image;
    }

    public static Image Box(Transform parent, string name, Paint? paint, string? sprite = Rounded, float corners = 5) => Fill(Rect(name, parent), paint, sprite, corners);

    public static TextMeshProUGUI Label(Transform parent, string text, float size = 15, Paint paint = Paint.Text, TextAlignmentOptions align = TextAlignmentOptions.MidlineLeft, bool wrap = false)
    {
        var label = Rect("Text", parent).gameObject.AddComponent<TextMeshProUGUI>();
        if (Theme.Font != null) label.font = Theme.Font;
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

    public static Image Icon(Transform parent, string sprite, float size, Paint paint) => Icon(parent, Theme.Sprite(sprite), size, paint);

    public static Image Icon(Transform parent, Sprite? sprite, float size, Paint paint)
    {
        var image = Rect("Icon " + (sprite != null ? sprite.name : ""), parent).gameObject.AddComponent<Image>();
        image.sprite = sprite;
        image.preserveAspect = true;
        image.raycastTarget = false;
        Painted.Add(image, paint);
        Size(image, size, size);
        image.rectTransform.sizeDelta = new Vector2(size, size);
        return image;
    }

    public static VerticalLayoutGroup Column(Transform parent, float spacing = 6, int padding = 0, string name = "Column")
    {
        var group = Rect(name, parent).gameObject.AddComponent<VerticalLayoutGroup>();
        group.spacing = spacing;
        group.padding = new RectOffset(padding, padding, padding, padding);
        group.childControlWidth = group.childControlHeight = true;
        group.childForceExpandWidth = true;
        group.childForceExpandHeight = false;
        return group;
    }

    public static HorizontalLayoutGroup Row(Transform parent, float spacing = 6, float height = 30, string name = "Row")
    {
        var group = Rect(name, parent).gameObject.AddComponent<HorizontalLayoutGroup>();
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

    public static void Space(Transform parent, float height) => Size(Rect("Space", parent), height: height);

    // Takes the remaining width of a row (pushes what follows to the right).
    public static void Spacer(Transform row) => Size(Rect("Spacer", row), flexWidth: 1);

    public static void Stretch(RectTransform rect, float left, float right, float top = 0, float bottom = 0)
    {
        rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one;
        rect.offsetMin = new Vector2(left, bottom); rect.offsetMax = new Vector2(-right, -top);
    }

    public static void Clear(Transform parent)
    {
        for (var i = parent.childCount - 1; i >= 0; i--) UnityEngine.Object.Destroy(parent.GetChild(i).gameObject);
    }

    // A native button: white rounded box with an outline (light themes), or the blue call-to-action style; the native press
    // and the hand cursor. The label sizes the button (padding 14).
    public static Button Button(Transform parent, string label, Action onClick, bool cta = false, float width = -1, float height = 28, float size = 15)
    {
        var image = Box(parent, "Button " + label, Paint.Button);
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
        button.onClick.AddListener(() => onClick());
        image.gameObject.AddComponent<HandCursor>();
        image.gameObject.AddComponent<PressFeedback>().Target = image.rectTransform;
        var fit = image.gameObject.AddComponent<HorizontalLayoutGroup>();
        fit.padding = new RectOffset(14, 14, 0, 0);
        fit.childAlignment = TextAnchor.MiddleCenter;
        fit.childControlWidth = fit.childControlHeight = true;
        fit.childForceExpandWidth = fit.childForceExpandHeight = true;
        Label(image.transform, label, size, Paint.ButtonText, TextAlignmentOptions.Center);
        var layout = Size(image, height: height);
        // The inner layout group would report flexible size and stretch the button to the row's height.
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

    public static void SetLabel(Button button, string label) => button.GetComponentInChildren<TextMeshProUGUI>().text = label;

    // Native input: optional title above, white field, grey bottom line that turns blue while editing. changed runs on every
    // edit, or only when editing ends with commitOnEnd.
    public static TMP_InputField Input(Transform parent, string title, string value, int limit, Action<string> changed, string placeholder = "", float width = -1, float flexWidth = -1, bool commitOnEnd = false, TMP_InputField.ContentType type = TMP_InputField.ContentType.Standard)
    {
        var column = Column(parent, 3, name: "Input " + title);
        if (width >= 0) Size(column, width, flexWidth: 0);
        if (flexWidth >= 0) Size(column, flexWidth: flexWidth);
        if (title.Length > 0) Label(column.transform, title, 15);
        var field = Box(column.transform, "Field", Paint.Field, corners: 10);
        Size(field, height: 28);
        var outline = field.gameObject.AddComponent<Outline>();
        outline.effectColor = new Color(0, 0, 0, .22f);
        outline.effectDistance = new Vector2(1, -1);
        field.gameObject.AddComponent<LightOnly>().Target = outline;
        var area = Rect("Text Area", field.transform);
        Stretch(area, 8, 8);
        area.gameObject.AddComponent<RectMask2D>();
        var text = Label(area, "", 15);
        Stretch(text.rectTransform, 0, 0);
        var hint = Label(area, placeholder, 15, Paint.ButtonTextOff);
        hint.fontStyle = FontStyles.Italic;
        Stretch(hint.rectTransform, 0, 0);
        var line = Box(field.transform, "BottomLine", Paint.Line, null);
        var lineRect = line.rectTransform;
        lineRect.anchorMin = new Vector2(0, 0); lineRect.anchorMax = new Vector2(1, 0); lineRect.pivot = new Vector2(.5f, 0);
        lineRect.sizeDelta = new Vector2(0, 2); lineRect.anchoredPosition = Vector2.zero;
        var input = field.gameObject.AddComponent<TMP_InputField>();
        input.textViewport = area;
        input.textComponent = text;
        input.placeholder = hint;
        input.characterLimit = limit;
        input.contentType = type;
        input.caretColor = Theme.Of(Paint.Text);
        input.customCaretColor = true;
        input.text = value;
        if (commitOnEnd) input.onEndEdit.AddListener(v => changed(v));
        else input.onValueChanged.AddListener(v => changed(v));
        input.onSelect.AddListener(_ => Painted.Set(line, Paint.LineActive));
        input.onDeselect.AddListener(_ => Painted.Set(line, Paint.Line));
        field.gameObject.AddComponent<TextCursor>();
        return input;
    }

    // Scrolling list: rows are added under the returned content.
    public static RectTransform Scroll(Transform parent, float height, Paint background = Paint.Window2)
    {
        var frame = Box(parent, "Scroll", background);
        Size(frame, height: height);
        var view = Rect("Viewport", frame.transform);
        Stretch(view, 4, 14, 4, 4);
        view.gameObject.AddComponent<RectMask2D>();
        var content = Column(view, 2, name: "Content");
        var contentRect = (RectTransform)content.transform;
        contentRect.anchorMin = new Vector2(0, 1); contentRect.anchorMax = new Vector2(1, 1); contentRect.pivot = new Vector2(.5f, 1);
        contentRect.sizeDelta = Vector2.zero;
        content.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        var bar = Box(frame.transform, "Scrollbar", Paint.ScrollBack, corners: 4);
        var barRect = bar.rectTransform;
        barRect.anchorMin = new Vector2(1, 0); barRect.anchorMax = new Vector2(1, 1); barRect.pivot = new Vector2(1, .5f);
        barRect.sizeDelta = new Vector2(8, -8); barRect.anchoredPosition = new Vector2(-3, 0);
        var handleArea = Rect("Sliding Area", bar.transform);
        Stretch(handleArea, 0, 0);
        var handle = Box(handleArea, "Handle", Paint.Scroll, corners: 4);
        Stretch(handle.rectTransform, 0, 0);
        var scrollbar = bar.gameObject.AddComponent<Scrollbar>();
        scrollbar.handleRect = handle.rectTransform;
        scrollbar.targetGraphic = handle;
        scrollbar.direction = Scrollbar.Direction.BottomToTop;
        var scroll = frame.gameObject.AddComponent<ScrollRect>();
        scroll.viewport = view;
        scroll.content = contentRect;
        scroll.horizontal = false;
        scroll.movementType = ScrollRect.MovementType.Clamped;
        scroll.scrollSensitivity = 25;
        scroll.verticalScrollbar = scrollbar;
        scroll.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.AutoHide;
        return contentRect;
    }

    // The version in a window footer: a quiet text that turns into a link on hover and opens the About window.
    public static TextMeshProUGUI VersionLink(Transform parent, string text, Action action)
    {
        var label = Label(parent, text, 14, Paint.TextLow);
        label.raycastTarget = true;
        var button = label.gameObject.AddComponent<Button>();
        button.transition = Selectable.Transition.None;
        button.navigation = new Navigation { mode = Navigation.Mode.None };
        button.onClick.AddListener(() => action());
        label.gameObject.AddComponent<HandCursor>();
        label.gameObject.AddComponent<LinkHover>().Text = label;
        return label;
    }

    // A link inside a text (see Links), underlined in the call-to-action colour of the current theme. Texts with links
    // need rewriting after a theme change (Theme.Revision).
    public static string Link(string text, string url) => $"<link=\"{url}\"><u><color={Theme.Hex(Paint.Cta)}>{text}</color></u></link>";

    // A text whose <link="url"> parts open their web page when clicked (hand cursor over the links).
    public static void Links(TextMeshProUGUI label)
    {
        label.raycastTarget = true;
        label.gameObject.AddComponent<TextLinks>();
    }

    // Section header: icon, title and, when there is more to say, an info icon with that text in its tooltip.
    public static TextMeshProUGUI Section(Transform parent, string title, string icon, string tip = "")
    {
        Space(parent, 2);
        var row = Row(parent, 6, 24);
        Icon(row.transform, icon, 20, Paint.Header);
        var header = Header(row.transform, title);
        if (tip.Length > 0) Info(row.transform, title, tip);
        Spacer(row.transform);
        return header;
    }

    // The game's grey info icon; hovering it shows the text.
    public static Tip Info(Transform parent, string header, string text)
    {
        var icon = Icon(parent, "info_icon", 18, Paint.Icon);
        icon.raycastTarget = true;
        return Tip.On(icon.gameObject, header, text);
    }

    // Status line: a coloured dot (Painted.Set it) and a short text; returns the row for an info icon or buttons.
    public static (Image dot, TextMeshProUGUI text, HorizontalLayoutGroup row) Status(Transform parent, float size = 16)
    {
        var row = Row(parent, 8, 24);
        var dot = Icon(row.transform, "circle_png_small", 10, Paint.TextLow);
        var text = Label(row.transform, "", size);
        return (dot, text, row);
    }

    // A choice between a few options, drawn like the game's tab strips (Analysis window): grey band, the chosen option in
    // dark text with a blue bar under it. selected() is read every frame, so the strip follows changes made elsewhere.
    public static RectTransform Segments(Transform parent, string[] options, Func<int> selected, Action<int> choose, float height = 28)
    {
        var band = Box(parent, "Segments", Paint.Tab);
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
            var bar = Box(hit, "Bar", Paint.TabBar, null);
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

    // The game's checkbox (the Business window's "Only our contracts"): rounded box with the "done" check mark and a label;
    // the whole row toggles. get() is read every frame.
    public static HorizontalLayoutGroup Check(Transform parent, string text, Func<bool> get, Action<bool> set)
    {
        var row = Row(parent, 8, 22);
        row.gameObject.AddComponent<Image>().color = Color.clear;
        var button = row.gameObject.AddComponent<Button>();
        button.transition = Selectable.Transition.None;
        button.navigation = new Navigation { mode = Navigation.Mode.None };
        button.onClick.AddListener(() => set(!get()));
        row.gameObject.AddComponent<HandCursor>();
        var box = Box(row.transform, "Box", Paint.Toggle);
        Size(box, 20, 20);
        var outline = box.gameObject.AddComponent<Outline>();
        outline.effectColor = new Color(0, 0, 0, .28f);
        outline.effectDistance = new Vector2(1, -1);
        box.gameObject.AddComponent<LightOnly>().Target = outline;
        var mark = Icon(box.transform, "done", 20, Paint.Check);
        Stretch(mark.rectTransform, 0, 0);
        Label(row.transform, text, 15);
        var view = row.gameObject.AddComponent<CheckView>();
        view.Get = get; view.Mark = mark;
        return row;
    }

    // A small icon that works as a button, for secondary actions in rows: hand cursor, press, the call-to-action colour on
    // hover and its tooltip.
    public static Button IconButton(Transform parent, string sprite, float size, string tip, Action onClick, Paint paint = Paint.TextLow) => IconButton(parent, Theme.Sprite(sprite), size, tip, onClick, paint);

    public static Button IconButton(Transform parent, Sprite? sprite, float size, string tip, Action onClick, Paint paint = Paint.TextLow)
    {
        var icon = Icon(parent, sprite, size, paint);
        icon.raycastTarget = true;
        var button = icon.gameObject.AddComponent<Button>();
        button.transition = Selectable.Transition.None;
        button.navigation = new Navigation { mode = Navigation.Mode.None };
        button.onClick.AddListener(() => onClick());
        icon.gameObject.AddComponent<HandCursor>();
        icon.gameObject.AddComponent<PressFeedback>().Target = icon.rectTransform;
        var hover = icon.gameObject.AddComponent<IconHover>();
        hover.Icon = icon; hover.Normal = paint;
        if (tip.Length > 0) Tip.On(icon.gameObject, tip, "");
        return button;
    }

    // Room for Mark in front of a linked name; put it right before the <link>.
    public const string MarkSpace = "<space=1.2em>";

    // An icon in front of a linked name inside a text (for example the Claude mark before "Claude Code"), in the room left
    // by MarkSpace.
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
}
