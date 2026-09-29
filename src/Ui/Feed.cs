using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace ProcessorTycoonModApi;

// Transparent lines in a bottom corner of the screen, like the Agent mod's action feed (bottom right) or a chat (bottom
// left). New lines fade out after Lifetime seconds. Hovering the input line or a FocusOn element (a bottom-bar entry; the
// lines themselves only in a feed with neither), or typing, focuses it: the history on a dark backdrop, the mouse wheel (or
// Page Up / Page Down while typing) scrolls back, and a thin bar shows where. Passing over the lines does not focus it, so a
// game window under them stays usable; hide the feed (Enabled) while the game shows a screen there (GameScreen).
// An optional input line (chat) at the bottom: InputAlwaysVisible keeps it on screen (dim, showing IdleHint) and a click
// or Enter starts typing; otherwise it appears only while typing (Enter with EnterOpens, or OpenInput). Submitted gets the
// text. The lines never block clicks on the game; only the input line takes clicks and, while typing, the keyboard (the game
// ignores its hotkeys while a text field is focused).
internal sealed class Feed
{
    private sealed class Entry { public string Text = ""; public Sprite? Icon; public int Count = 1; public float Time; }
    private sealed class Line { public RectTransform Rect = null!; public Image Icon = null!; public TextMeshProUGUI Text = null!; }

    private const float Gap = 3, IconSpace = 22, InputHeight = 28, HoverDelay = .3f;
    private readonly Overlay overlay;
    private readonly RectTransform panel;
    private readonly Image backdrop, scrollBar;
    private readonly List<Entry> entries = new();
    private readonly List<Line> lines = new();
    private readonly List<RectTransform> focusTargets = new();
    private readonly RectTransform inputRow;
    private readonly Image field;
    private readonly TMP_InputField input;
    private readonly TextMeshProUGUI hint;
    private readonly string placeholder;
    private readonly Vector3[] corners = new Vector3[4];
    private Rect zone;
    private bool typing, focused;
    private float targetHoverSince = -1;
    private int scroll, openFrame = -1, activeFrame = -1, deselectFrame = -1, closedFrame = -10;

    public bool RightSide { get; }
    public bool Enabled = true;
    public float Side = 12, Bottom = 46, MaxWidth = 760;
    public float Lifetime = 18, FadeTime = 4, IdleAlpha = .78f;
    public int IdleLines = 6, FocusLines = 12, History = 100;
    public Color TextColor = new(.76f, .78f, .8f);
    // Enter starts typing when no other text field has the keyboard.
    public bool EnterOpens { get; set; }
    // The input line stays on screen, dim with IdleHint (for example "Press Enter to chat"), below the lines.
    public bool InputAlwaysVisible { get; set; }
    public string IdleHint { get; set; } = "";
    public event Action<string>? Submitted;

    public Feed(Overlay overlay, bool rightSide, bool richText = true, string placeholder = "")
    {
        this.overlay = overlay;
        this.placeholder = placeholder;
        RightSide = rightSide;
        panel = Ui.Rect(rightSide ? "Feed right" : "Feed left", overlay.Root);
        panel.anchorMin = panel.anchorMax = panel.pivot = new Vector2(rightSide ? 1 : 0, 0);
        backdrop = Ui.Fill(Ui.Rect("Backdrop", panel), null);
        backdrop.color = new Color(.137f, .137f, .137f, .88f);   // readable over desktop icons
        backdrop.raycastTarget = false;
        var backRect = backdrop.rectTransform;
        backRect.anchorMin = backRect.anchorMax = backRect.pivot = Vector2.zero;
        scrollBar = Ui.Fill(Ui.Rect("Scroll", panel), null, Ui.Rounded, 12);
        scrollBar.color = new Color(1, 1, 1, .3f);
        scrollBar.raycastTarget = false;
        var barRect = scrollBar.rectTransform;
        barRect.anchorMin = barRect.anchorMax = barRect.pivot = Vector2.zero;
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
        field = Ui.Fill(inputRow, null, Ui.Rounded, 5);
        var area = Ui.Rect("Text Area", inputRow);
        Ui.Stretch(area, 8, 8);
        area.gameObject.AddComponent<RectMask2D>();
        var typed = Ui.Label(area, "", 15);
        Painted.Clear(typed);
        typed.color = Color.white;
        typed.richText = false;
        Ui.Stretch(typed.rectTransform, 0, 0);
        hint = Ui.Label(area, placeholder, 15);
        Painted.Clear(hint);
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
        input.transition = Selectable.Transition.None;
        input.onFocusSelectAll = false;   // a prefilled "@Name " stays; typing continues after it
        input.onSubmit.AddListener(Submit);
        inputRow.gameObject.AddComponent<TextCursor>();
        inputRow.gameObject.SetActive(false);
    }

    // The input line has the keyboard.
    public bool Typing => typing;
    // Another text field (a game or mod window) has the keyboard.
    public static bool TypingElsewhere
    {
        get
        {
            var selected = EventSystem.current != null ? EventSystem.current.currentSelectedGameObject : null;
            return selected != null && selected.GetComponent<TMP_InputField>() != null;
        }
    }

    // A new line; with repeatable, the same text as the last line adds "×2", "×3"… instead. A reader scrolled back keeps
    // their place.
    public void Add(string text, Sprite? icon = null, bool repeatable = false)
    {
        text = text.Replace('\n', ' ').Replace('\r', ' ').Trim();
        var last = entries.LastOrDefault();
        if (repeatable && last != null && last.Text == text && last.Icon == icon) { last.Count++; last.Time = Time.unscaledTime; return; }
        entries.Add(new Entry { Text = text, Icon = icon, Time = Time.unscaledTime });
        if (entries.Count > History) entries.RemoveAt(0);
        else if (scroll > 0) scroll++;
    }

    public void Clear() { entries.Clear(); scroll = 0; }

    // Scrolls a focused feed back (positive) or forward, in lines.
    public void ScrollBy(int lines) => scroll += lines;

    // Hovering this element (for example the mod's bottom-bar entry) also focuses the feed, after a short delay.
    public void FocusOn(RectTransform target) { if (!focusTargets.Contains(target)) focusTargets.Add(target); }

    // Starts typing on the next frame (a key that started it must not also type into it); a prefill replaces the draft.
    public void OpenInput(string prefill = "")
    {
        if (!Enabled) return;
        if (prefill.Length > 0) input.text = prefill;
        openFrame = Time.frameCount + 1;
    }

    // Stops typing; the draft stays.
    public void CloseInput()
    {
        if (!typing && openFrame < 0) return;
        openFrame = -1;
        typing = false;
        input.DeactivateInputField();
        if (!InputAlwaysVisible) inputRow.gameObject.SetActive(false);
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
        if (!Enabled) { if (typing || openFrame >= 0) CloseInput(); panel.gameObject.SetActive(false); targetHoverSince = -1; focused = false; return; }
        if (!panel.gameObject.activeSelf) panel.gameObject.SetActive(true);
        if (openFrame == frame) Activate();
        // A click into the visible input line.
        if (!typing && openFrame < 0 && input.isFocused) { typing = true; activeFrame = frame; }
        if (typing)
        {
            if (UnityEngine.Input.GetKeyDown(KeyCode.Escape)) CloseInput();
            else if (UnityEngine.Input.GetKeyDown(KeyCode.PageUp)) ScrollBy(FocusLines - 1);
            else if (UnityEngine.Input.GetKeyDown(KeyCode.PageDown)) ScrollBy(1 - FocusLines);
            else if (frame > activeFrame + 2 && !input.isFocused) CloseInput();   // clicked elsewhere; the draft stays
        }
        else if (EnterOpens && openFrame < 0 && frame - closedFrame > 1 && (UnityEngine.Input.GetKeyDown(KeyCode.Return) || UnityEngine.Input.GetKeyDown(KeyCode.KeypadEnter)) && !TypingElsewhere)
            OpenInput();
        Layout();
    }

    private void Activate()
    {
        openFrame = -1;
        typing = true;
        activeFrame = Time.frameCount;
        inputRow.gameObject.SetActive(true);
        EventSystem.current?.SetSelectedGameObject(input.gameObject);
        input.ActivateInputField();
        input.caretPosition = input.text.Length;
    }

    // One of the FocusOn elements has been under the mouse for HoverDelay.
    private bool TargetHovered(Vector2 mouse)
    {
        var over = false;
        foreach (var target in focusTargets)
        {
            if (target == null || !target.gameObject.activeInHierarchy) continue;
            target.GetWorldCorners(corners);   // screen pixels on an overlay canvas
            var rect = new Rect(corners[0] / overlay.Scale, (corners[2] - corners[0]) / overlay.Scale);
            if (rect.Contains(mouse)) { over = true; break; }
        }
        if (!over) { targetHoverSince = -1; return false; }
        if (targetHoverSince < 0) targetHoverSince = Time.unscaledTime;
        return Time.unscaledTime - targetHoverSince >= HoverDelay;
    }

    private void Layout()
    {
        var now = Time.unscaledTime;
        var mouse = overlay.Mouse;
        var width = Mathf.Clamp(overlay.Width - 40, 250, MaxWidth);
        var origin = RightSide ? new Vector2(overlay.Width - Side - width, Bottom) : new Vector2(Side, Bottom);
        var withInput = InputAlwaysVisible || typing;
        // Focus starts on the input line or a FocusOn element (on the lines only for a feed with neither), never by passing
        // over lines that may lie on a game window; once focused, the whole history area keeps it.
        var hovered = TargetHovered(mouse) || focused && zone.Contains(mouse)
            || (withInput ? new Rect(origin, new Vector2(width, InputHeight)).Contains(mouse) : focusTargets.Count == 0 && zone.Contains(mouse));
        var focus = typing || hovered;
        focused = focus;
        var maxScroll = Mathf.Max(0, entries.Count - FocusLines);
        if (hovered)
        {
            var wheel = UnityEngine.Input.mouseScrollDelta.y;
            if (wheel != 0) scroll += wheel > 0 ? 1 : -1;
        }
        scroll = focus ? Mathf.Clamp(scroll, 0, maxScroll) : 0;
        var shown = focus
            ? entries.Take(entries.Count - scroll).Reverse().Take(FocusLines).ToList()
            : entries.Where(e => now - e.Time < Lifetime).Reverse().Take(IdleLines).ToList();
        if (inputRow.gameObject.activeSelf != withInput) inputRow.gameObject.SetActive(withInput);
        var y = withInput ? InputHeight + 4 : 0f;
        var linesBottom = y;
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
        if (withInput)
        {
            inputRow.sizeDelta = new Vector2(width, InputHeight);
            inputRow.anchoredPosition = Vector2.zero;
            contentWidth = width;
            // Dim while waiting, clearer on hover, solid while typing.
            field.color = new Color(.1f, .1f, .1f, typing ? .85f : focus ? .7f : .45f);
            hint.text = typing || IdleHint.Length == 0 ? placeholder : IdleHint;
            hint.color = new Color(1, 1, 1, typing ? .45f : focus ? .6f : .5f);
            input.textComponent.color = new Color(1, 1, 1, typing ? 1f : .6f);
        }
        panel.sizeDelta = new Vector2(width, y);
        panel.anchoredPosition = new Vector2(RightSide ? -Side : Side, Bottom);
        // Focus area: the lines and the input line, a little padded; the backdrop covers it while focused.
        var left = RightSide ? width - contentWidth : 0;
        var back = backdrop.rectTransform;
        back.anchoredPosition = new Vector2(left - 8, -6);
        back.sizeDelta = new Vector2(contentWidth + 16, y + 10);
        backdrop.enabled = focus && shown.Count > 0;
        // Where the shown lines are in the history, when it is longer than they are.
        var scrollable = focus && maxScroll > 0 && shown.Count > 0;
        scrollBar.enabled = scrollable;
        if (scrollable)
        {
            var track = y - linesBottom - Gap;
            var bar = scrollBar.rectTransform;
            bar.sizeDelta = new Vector2(3, Mathf.Max(12, track * shown.Count / entries.Count));
            bar.anchoredPosition = new Vector2(RightSide ? left - 5 : contentWidth + 2, linesBottom + (track - bar.sizeDelta.y) * scroll / maxScroll);
        }
        var any = shown.Count > 0 || withInput;
        zone = any ? new Rect(origin.x + left - 8, origin.y - 6, contentWidth + 16, y + 10) : default;
    }
}
