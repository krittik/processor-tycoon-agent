using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace ProcessorTycoonModApi;

// Transparent lines in a bottom corner of the screen, like the Agent mod's action feed (bottom right) or a chat (bottom
// left). New lines fade out after Lifetime seconds. Hovering the lines, or typing, focuses the feed: the recent history on
// a dark backdrop, the mouse wheel scrolls back. An optional input line (chat) opens with OpenInput or Enter (EnterOpens)
// and raises Submitted. The lines never block clicks on the game; only the open input takes the keyboard, and the game
// ignores its hotkeys while a text field is focused.
internal sealed class Feed
{
    private sealed class Entry { public string Text = ""; public Sprite? Icon; public int Count = 1; public float Time; }
    private sealed class Line { public RectTransform Rect = null!; public Image Icon = null!; public TextMeshProUGUI Text = null!; }

    private const float Gap = 3, IconSpace = 22, InputHeight = 28;
    private readonly Overlay overlay;
    private readonly RectTransform panel;
    private readonly Image backdrop;
    private readonly List<Entry> entries = new();
    private readonly List<Line> lines = new();
    private readonly RectTransform inputRow;
    private readonly TMP_InputField input;
    private Rect zone;
    private bool hovered;
    private int scroll, openFrame = -1, activeFrame = -1, deselectFrame = -1, closedFrame = -10;

    public bool RightSide { get; }
    public bool Enabled = true;
    public float Side = 12, Bottom = 46, MaxWidth = 760;
    public float Lifetime = 18, FadeTime = 4, IdleAlpha = .78f;
    public int IdleLines = 6, FocusLines = 12, History = 100;
    public Color TextColor = new(.76f, .78f, .8f);
    // Enter opens the input line when no other text field has the keyboard.
    public bool EnterOpens { get; set; }
    public event Action<string>? Submitted;

    public Feed(Overlay overlay, bool rightSide, bool richText = true, string placeholder = "")
    {
        this.overlay = overlay;
        RightSide = rightSide;
        panel = Ui.Rect(rightSide ? "Feed right" : "Feed left", overlay.Root);
        panel.anchorMin = panel.anchorMax = panel.pivot = new Vector2(rightSide ? 1 : 0, 0);
        backdrop = Ui.Fill(Ui.Rect("Backdrop", panel), null);
        backdrop.color = new Color(.137f, .137f, .137f, .6f);
        backdrop.raycastTarget = false;
        var backRect = backdrop.rectTransform;
        backRect.anchorMin = backRect.anchorMax = backRect.pivot = Vector2.zero;
        for (var i = 0; i < 24; i++)
        {
            var rect = Ui.Rect("Line", panel);
            rect.anchorMin = rect.anchorMax = rect.pivot = Vector2.zero;
            var text = Ui.Label(rect, "", 15);
            Painted.Clear(text);
            text.richText = richText;
            text.textWrappingMode = TextWrappingModes.Normal;
            text.overflowMode = TextOverflowModes.Overflow;
            text.alignment = rightSide ? TextAlignmentOptions.BottomRight : TextAlignmentOptions.BottomLeft;
            // The icon sits beside the text's last line (Layout).
            var iconRect = Ui.Rect("Icon", rect);
            iconRect.anchorMin = iconRect.anchorMax = iconRect.pivot = Vector2.zero;
            iconRect.sizeDelta = new Vector2(16, 16);
            var icon = iconRect.gameObject.AddComponent<Image>();
            icon.raycastTarget = false;
            icon.preserveAspect = true;
            rect.gameObject.SetActive(false);
            lines.Add(new Line { Rect = rect, Icon = icon, Text = text });
        }
        inputRow = Ui.Rect("Input", panel);
        inputRow.anchorMin = inputRow.anchorMax = inputRow.pivot = Vector2.zero;
        var field = Ui.Fill(inputRow, null, Ui.Rounded, 5);
        field.color = new Color(.1f, .1f, .1f, .85f);
        var area = Ui.Rect("Text Area", inputRow);
        Ui.Stretch(area, 8, 8);
        area.gameObject.AddComponent<RectMask2D>();
        var typed = Ui.Label(area, "", 15);
        Painted.Clear(typed);
        typed.color = Color.white;
        typed.richText = false;
        Ui.Stretch(typed.rectTransform, 0, 0);
        var hint = Ui.Label(area, placeholder, 15);
        Painted.Clear(hint);
        hint.color = new Color(1, 1, 1, .45f);
        hint.fontStyle = FontStyles.Italic;
        Ui.Stretch(hint.rectTransform, 0, 0);
        input = inputRow.gameObject.AddComponent<TMP_InputField>();
        input.textViewport = area;
        input.textComponent = typed;
        input.placeholder = hint;
        input.characterLimit = 300;
        input.customCaretColor = true;
        input.caretColor = Color.white;
        input.selectionColor = new Color(1, 1, 1, .25f);
        input.navigation = new Navigation { mode = Navigation.Mode.None };
        input.onFocusSelectAll = false;   // a prefilled "@Name " stays; typing continues after it
        input.onSubmit.AddListener(Submit);
        inputRow.gameObject.AddComponent<TextCursor>();
        inputRow.gameObject.SetActive(false);
    }

    public bool InputOpen => inputRow.gameObject.activeSelf;
    // Another text field (a game or mod window) has the keyboard.
    public static bool TypingElsewhere
    {
        get
        {
            var selected = EventSystem.current != null ? EventSystem.current.currentSelectedGameObject : null;
            return selected != null && selected.GetComponent<TMP_InputField>() != null;
        }
    }

    // A new line; with repeatable, the same text as the last line adds "×2", "×3"… instead.
    public void Add(string text, Sprite? icon = null, bool repeatable = false)
    {
        text = text.Replace('\n', ' ').Replace('\r', ' ').Trim();
        var last = entries.LastOrDefault();
        if (repeatable && last != null && last.Text == text && last.Icon == icon) { last.Count++; last.Time = Time.unscaledTime; return; }
        entries.Add(new Entry { Text = text, Icon = icon, Time = Time.unscaledTime });
        if (entries.Count > History) entries.RemoveAt(0);
        scroll = 0;
    }

    public void Clear() { entries.Clear(); scroll = 0; }

    // Opens the input line on the next frame (a key that opened it must not also type into it), with prefill appended to a
    // kept draft only when the draft is empty.
    public void OpenInput(string prefill = "")
    {
        if (!Enabled) return;
        if (prefill.Length > 0) input.text = prefill;
        openFrame = Time.frameCount + 1;
    }

    public void CloseInput()
    {
        if (!InputOpen && openFrame < 0) return;
        openFrame = -1;
        input.DeactivateInputField();
        inputRow.gameObject.SetActive(false);
        closedFrame = Time.frameCount;
        // One frame later, so a key that closed it (Esc) is not also seen by the game as unfocused input.
        deselectFrame = Time.frameCount + 1;
    }

    private void Submit(string text)
    {
        if (text.Trim().Length > 0) Submitted?.Invoke(text.Trim());
        input.text = "";
        CloseInput();
    }

    public void Tick()
    {
        var frame = Time.frameCount;
        if (deselectFrame == frame && EventSystem.current != null && EventSystem.current.currentSelectedGameObject == input.gameObject) EventSystem.current.SetSelectedGameObject(null);
        if (!Enabled) { if (InputOpen) CloseInput(); panel.gameObject.SetActive(false); return; }
        if (!panel.gameObject.activeSelf) panel.gameObject.SetActive(true);
        if (openFrame == frame) Activate();
        if (InputOpen)
        {
            if (UnityEngine.Input.GetKeyDown(KeyCode.Escape)) CloseInput();
            else if (frame > activeFrame + 2 && !input.isFocused) CloseInput();   // clicked elsewhere; the draft stays
        }
        else if (EnterOpens && openFrame < 0 && frame - closedFrame > 1 && (UnityEngine.Input.GetKeyDown(KeyCode.Return) || UnityEngine.Input.GetKeyDown(KeyCode.KeypadEnter)) && !TypingElsewhere)
            OpenInput();
        Layout();
    }

    private void Activate()
    {
        openFrame = -1;
        activeFrame = Time.frameCount;
        inputRow.gameObject.SetActive(true);
        EventSystem.current?.SetSelectedGameObject(input.gameObject);
        input.ActivateInputField();
        input.caretPosition = input.text.Length;
    }

    private void Layout()
    {
        var now = Time.unscaledTime;
        var mouse = overlay.Mouse;
        hovered = zone.width > 0 && zone.Contains(mouse);
        var focus = InputOpen || hovered;
        if (focus && hovered)
        {
            var wheel = UnityEngine.Input.mouseScrollDelta.y;
            if (wheel != 0) scroll = Mathf.Clamp(scroll + (wheel > 0 ? 1 : -1), 0, Mathf.Max(0, entries.Count - FocusLines));
        }
        if (!focus) scroll = 0;
        var shown = focus
            ? entries.Take(entries.Count - scroll).Reverse().Take(FocusLines).ToList()
            : entries.Where(e => now - e.Time < Lifetime).Reverse().Take(IdleLines).ToList();
        var width = Mathf.Clamp(overlay.Width - 40, 250, MaxWidth);
        var y = InputOpen ? InputHeight + 4 : 0f;
        var contentWidth = 0f;
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var visible = i < shown.Count && i < (focus ? FocusLines : IdleLines);
            if (line.Rect.gameObject.activeSelf != visible) line.Rect.gameObject.SetActive(visible);
            if (!visible) continue;
            var entry = shown[i];
            var text = entry.Text + (entry.Count > 1 ? $" ×{entry.Count}" : "");
            var textMax = width - (entry.Icon != null ? IconSpace : 0);
            if (line.Text.text != text) line.Text.text = text;
            if (Theme.Font != null && line.Text.font != Theme.Font) line.Text.font = Theme.Font;
            var size = line.Text.GetPreferredValues(text, textMax, 0);
            float textWidth = Mathf.Min(size.x, textMax), height = Mathf.Max(20, size.y);
            var alpha = focus ? 1f : Mathf.Clamp01((Lifetime - (now - entry.Time)) / FadeTime) * IdleAlpha;
            var color = TextColor; color.a = alpha;
            line.Text.color = color;
            line.Icon.color = color;
            line.Icon.sprite = entry.Icon;
            line.Icon.enabled = entry.Icon != null;
            line.Rect.sizeDelta = new Vector2(width, height);
            line.Rect.anchoredPosition = new Vector2(0, y);
            var textRect = line.Text.rectTransform;
            textRect.anchorMin = textRect.anchorMax = textRect.pivot = new Vector2(RightSide ? 1 : 0, 0);
            textRect.sizeDelta = new Vector2(textMax, height);
            textRect.anchoredPosition = new Vector2(RightSide || entry.Icon == null ? 0 : IconSpace, 0);
            // The icon sits just before the text, at its last line.
            var iconX = RightSide ? width - textWidth - IconSpace : 0;
            ((RectTransform)line.Icon.transform).anchoredPosition = new Vector2(iconX, 2);
            contentWidth = Mathf.Max(contentWidth, textWidth + (entry.Icon != null ? IconSpace : 0));
            y += height + Gap;
        }
        if (InputOpen)
        {
            inputRow.sizeDelta = new Vector2(width, InputHeight);
            inputRow.anchoredPosition = Vector2.zero;
            contentWidth = width;
        }
        var any = shown.Count > 0 || InputOpen;
        panel.sizeDelta = new Vector2(width, y);
        panel.anchoredPosition = new Vector2(RightSide ? -Side : Side, Bottom);
        // Focus area: the lines themselves, a little padded; the backdrop covers it while focused.
        var left = RightSide ? width - contentWidth : 0;
        var back = backdrop.rectTransform;
        back.anchoredPosition = new Vector2(left - 8, -6);
        back.sizeDelta = new Vector2(contentWidth + 16, y + 10);
        backdrop.enabled = any && focus && shown.Count > 0;
        var origin = RightSide ? new Vector2(overlay.Width - Side - width, Bottom) : new Vector2(Side, Bottom);
        zone = any ? new Rect(origin.x + left - 8, origin.y - 6, contentWidth + 16, y + 10) : default;
    }
}
