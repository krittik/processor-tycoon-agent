using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace ProcessorTycoonMod;

internal sealed class AgentOverlay : IDisposable
{
    private sealed class Entry { public string Text = ""; public string Icon = "read"; public int Count = 1; public float Time; public bool Repeatable; }
    private sealed class FeedRow { public RectTransform Rect = null!; public Image Icon = null!; public TextMeshProUGUI Text = null!; }
    private readonly Plugin plugin;
    private readonly GameObject root;
    private readonly Canvas canvas;
    private readonly AgentCursor cursor;
    private int cursorScene;
    private Canvas? nativeCanvas;
    private readonly RectTransform tray;
    private readonly RectTransform panel;
    private readonly RectTransform about;
    private readonly List<Image> headerSurfaces = new();
    private readonly TextMeshProUGUI status;
    private readonly TextMeshProUGUI pause;
    private readonly TextMeshProUGUI feedToggle;
    private readonly TMP_InputField delayInput;
    private readonly Image connection;
    private TMP_FontAsset font;
    private readonly Dictionary<string, Sprite> icons = new();
    private readonly List<Entry> entries = new();
    private readonly FeedRow[] rows = new FeedRow[6];
    private readonly List<Image> surfaces = new();
    private readonly List<TextMeshProUGUI> panelLabels = new();
    private readonly List<Button> buttons = new();
    private Image? nativePanel;
    private Image? nativeHeader;
    private float nextStyleCheck;
    private int shownDelay = -1;
    private string shownConnection = "";
    private bool shownPause;
    private bool shownFeed;

    public static AgentOverlay? TryCreate(Plugin plugin)
    {
        var text = Resources.FindObjectsOfTypeAll<TMP_Text>().FirstOrDefault(t => t.gameObject.activeInHierarchy && t.font != null && t.GetComponentInParent<AgentOverlayMarker>() == null);
        return text == null ? null : new AgentOverlay(plugin, text.font);
    }

    private AgentOverlay(Plugin plugin, TMP_FontAsset font)
    {
        this.plugin = plugin;
        this.font = font;
        LoadIcons();
        root = new GameObject("AgentOverlay", typeof(RectTransform), typeof(Canvas), typeof(GraphicRaycaster), typeof(AgentOverlayMarker));
        Object.DontDestroyOnLoad(root);
        canvas = root.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 32760;

        tray = Rect("Agent tray", root.transform, 300, 40);
        Dock(tray, 120, 0);
        var hitArea = tray.gameObject.AddComponent<Image>();
        hitArea.color = Color.white;
        var trayButton = tray.gameObject.AddComponent<Button>();
        trayButton.targetGraphic = hitArea;
        var trayColors = ColorBlock.defaultColorBlock;
        trayColors.normalColor = Color.clear;
        trayColors.highlightedColor = new Color(1, 1, 1, .12f);
        trayColors.pressedColor = new Color(1, 1, 1, .21f);
        trayColors.selectedColor = Color.clear;
        trayColors.fadeDuration = .1f;
        trayButton.colors = trayColors;
        trayButton.navigation = new Navigation { mode = Navigation.Mode.None };
        trayButton.onClick.AddListener(TogglePanel);
        var content = Rect("Tray content", tray, 0, 0);
        Stretch(content, 0, 0);
        content.pivot = new Vector2(.5f, .5f);
        tray.gameObject.AddComponent<AgentButtonFeedback>().Target = content;
        connection = Icon(content, "connection", new Vector2(10, 0), 16);
        status = Text(content, "Agent", 15, TextAlignmentOptions.MidlineLeft);
        Stretch(status.rectTransform, 36, 8);

        panel = Rect("Agent settings", root.transform, 280, 181);
        surfaces.Add(Background(panel, new Color(.96f, .96f, .96f)));
        var border = panel.gameObject.AddComponent<Outline>();
        border.effectColor = new Color(.15f, .15f, .15f, .7f);
        border.effectDistance = new Vector2(1, -1);
        var header = Rect("Title bar", panel, 280, 28);
        Top(header, 0, 0);
        surfaces.Add(Background(header, new Color(.86f, .87f, .88f)));
        headerSurfaces.Add(surfaces[surfaces.Count - 1]);
        var title = PanelText(header, "Agent Settings", 16);
        title.alignment = TextAlignmentOptions.MidlineLeft;
        Stretch(title.rectTransform, 10, 36);
        var close = MakeButton(header, 24, 24, 254, 2, "", TogglePanel);
        Icon(close.transform, "close", Vector2.zero, 12, true);

        pause = MakeButton(panel, 126, 30, 10, 38, "Pause", () => plugin.SetPaused(!plugin.Paused)).GetComponentInChildren<TextMeshProUGUI>();
        feedToggle = MakeButton(panel, 124, 30, 146, 38, "Feed: on", plugin.ToggleFeed).GetComponentInChildren<TextMeshProUGUI>();
        var delayLabel = PanelText(panel, "Agent actions delay", 14);
        Position(delayLabel.rectTransform, 10, 79, 142, 28);
        var inputRect = Rect("Delay", panel, 90, 28);
        Top(inputRect, 156, 79);
        surfaces.Add(Background(inputRect, Color.white));
        delayInput = inputRect.gameObject.AddComponent<TMP_InputField>();
        var inputText = PanelText(inputRect, "0", 14);
        inputText.alignment = TextAlignmentOptions.MidlineRight;
        Stretch(inputText.rectTransform, 5, 5);
        delayInput.textComponent = inputText;
        delayInput.textViewport = inputRect;
        delayInput.contentType = TMP_InputField.ContentType.IntegerNumber;
        delayInput.characterLimit = 5;
        delayInput.onEndEdit.AddListener(value =>
        {
            if (int.TryParse(value, out var milliseconds)) plugin.SetDelay(Mathf.Clamp(milliseconds, 0, 60000));
            delayInput.SetTextWithoutNotify(plugin.Delay.ToString());
        });
        var units = PanelText(panel, "ms", 13);
        Position(units.rectTransform, 248, 79, 24, 28);
        var presets = new[] { 0, 100, 250, 500, 1000 };
        for (var index = 0; index < presets.Length; index++)
        {
            var value = presets[index];
            MakeButton(panel, 48, 25, 10 + index * 53, 116, value == 0 ? "Off" : value.ToString(), () => plugin.SetDelay(value));
        }
        // Version and credits stay out of the way: one quiet line at the bottom of this panel opens the About window.
        var aboutLink = LinkButton(panel, $"{AgentInfo.Short}  ·  About and credits", 10, 150, 260, ToggleAbout);
        aboutLink.alignment = TextAlignmentOptions.MidlineLeft;
        panel.gameObject.SetActive(false);
        about = BuildAbout();
        about.gameObject.SetActive(false);

        for (var index = 0; index < rows.Length; index++)
        {
            var rect = Rect("Feed row", root.transform, 760, 22);
            var label = Text(rect, "", 15, TextAlignmentOptions.MidlineRight);
            label.richText = false;
            Stretch(label.rectTransform, 26, 0);
            rows[index] = new FeedRow { Rect = rect, Icon = Icon(rect, "read", Vector2.zero, 16), Text = label };
            rect.gameObject.SetActive(false);
        }
        ApplyStyle();
        cursor = AgentCursor.Create(root.transform);
        cursorScene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().handle;
    }

    internal void ShowAction(Vector2? point, bool click)
    {
        if (point.HasValue && !plugin.Paused) cursor.MoveTo(point.Value, click);
        else cursor.Hide();
    }

    public void TogglePanel() => SetPanel(!panel.gameObject.activeSelf);
    public void ToggleAbout() => SetAbout(!about.gameObject.activeSelf);
    public void SetAbout(bool visible) { about.gameObject.SetActive(visible); if (visible) about.SetAsLastSibling(); }

    private RectTransform BuildAbout()
    {
        const float width = 460;
        var window = Rect("Agent about", root.transform, width, 300);
        window.anchorMin = window.anchorMax = window.pivot = new Vector2(.5f, .5f);
        window.anchoredPosition = Vector2.zero;
        surfaces.Add(Background(window, new Color(.96f, .96f, .96f)));
        var border = window.gameObject.AddComponent<Outline>();
        border.effectColor = new Color(.15f, .15f, .15f, .7f);
        border.effectDistance = new Vector2(1, -1);
        var header = Rect("Title bar", window, width, 28);
        Top(header, 0, 0);
        surfaces.Add(Background(header, new Color(.86f, .87f, .88f)));
        headerSurfaces.Add(surfaces[surfaces.Count - 1]);
        var title = PanelText(header, "About " + AgentInfo.Name, 16);
        title.alignment = TextAlignmentOptions.MidlineLeft;
        Stretch(title.rectTransform, 10, 36);
        var close = MakeButton(header, 24, 24, width - 26, 2, "", ToggleAbout);
        Icon(close.transform, "close", Vector2.zero, 12, true);

        var y = 38f;
        TextMeshProUGUI Line(string text, float size, float lineHeight, bool wrap = false)
        {
            var label = PanelText(window, text, size);
            label.alignment = TextAlignmentOptions.TopLeft;
            if (wrap) { label.textWrappingMode = TextWrappingModes.Normal; label.overflowMode = TextOverflowModes.Overflow; }
            // Wrapped lines take the height they need (a long credit wraps onto a second line).
            if (wrap) lineHeight = Mathf.Max(lineHeight, Mathf.Ceil(label.GetPreferredValues(text, width - 28, 0).y) + 4);
            Position(label.rectTransform, 14, y, width - 28, lineHeight);
            y += lineHeight;
            return label;
        }
        Line($"<b>{AgentInfo.Name}</b>  {AgentInfo.Short}", 17, 26);
        Line("Lets AI agents and scripts play through the visible game with native player actions, and shows what they do.", 14, 40, wrap: true);
        var byline = Line(AgentInfo.Byline((name, url) => $"<link=\"{url}\"><u>{name}</u></link>"), 14, 22, wrap: true);
        byline.raycastTarget = true;
        byline.gameObject.AddComponent<TextLinks>();
        y += 6;
        Line("Uses " + string.Join(", ", AgentInfo.ThirdParty.Select(t => $"{t.name} ({t.note})")) + $". Released under the {AgentInfo.License} license.", 12, 36, wrap: true);
        Line(AgentInfo.Disclaimer, 12, 36, wrap: true);
        y += 6;
        MakeButton(window, 130, 30, width - 14 - 80 - 8 - 84 - 8 - 84 - 8 - 130, y, "Report an issue", () => Application.OpenURL(AgentInfo.Issues));
        MakeButton(window, 84, 30, width - 14 - 80 - 8 - 84 - 8 - 84, y, "Discord", () => Application.OpenURL(AgentInfo.Discord));
        MakeButton(window, 84, 30, width - 14 - 80 - 8 - 84, y, "GitHub", () => Application.OpenURL(AgentInfo.Repository));
        MakeButton(window, 80, 30, width - 14 - 80, y, "Close", ToggleAbout);
        window.sizeDelta = new Vector2(width, y + 30 + 12);
        return window;
    }

    private TextMeshProUGUI LinkButton(Transform parent, string text, float x, float y, float width, Action action)
    {
        var rect = Rect("Link", parent, width, 22);
        Top(rect, x, y);
        var hit = Background(rect, Color.clear);
        var button = rect.gameObject.AddComponent<Button>();
        button.targetGraphic = hit;
        var colors = ColorBlock.defaultColorBlock;
        colors.normalColor = Color.clear; colors.highlightedColor = new Color(.5f, .5f, .5f, .12f); colors.pressedColor = new Color(.5f, .5f, .5f, .22f); colors.selectedColor = Color.clear;
        button.colors = colors;
        button.navigation = new Navigation { mode = Navigation.Mode.None };
        button.onClick.AddListener(() => action());
        var label = PanelText(rect, text, 13);
        Stretch(label.rectTransform, 2, 2);
        return label;
    }
    public void SetPanel(bool visible) { panel.gameObject.SetActive(visible); if (visible) panel.SetAsLastSibling(); else SetAbout(false); }
    public void Add(string message, bool repeatable, string icon = "read")
    {
        message = message.Replace('\n', ' ').Replace('\r', ' ').Trim();
        var last = entries.LastOrDefault();
        if (repeatable && last != null && last.Repeatable && last.Text == message && last.Icon == icon) { last.Count++; last.Time = Time.unscaledTime; }
        else entries.Add(new Entry { Text = message, Icon = icon, Time = Time.unscaledTime, Repeatable = repeatable });
        if (entries.Count > rows.Length) entries.RemoveAt(0);
    }

    public void Refresh()
    {
        var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().handle;
        if (plugin.Paused || scene != cursorScene || plugin.Connection != "Agent Connected") cursor.Hide(resetPosition: true);
        cursorScene = scene;
        if (Time.unscaledTime >= nextStyleCheck) { ApplyStyle(); nextStyleCheck = Time.unscaledTime + 1; }
        SyncScale();
        var connectionText = plugin.Connection.Replace("Agent ", "").Replace("Waiting for agent", "Offline");
        if (shownConnection != connectionText || shownDelay != plugin.Delay)
        {
            status.text = $"Agent · {connectionText}    Actions delay: {(plugin.Delay == 0 ? "off" : plugin.Delay + " ms")}";
            if (!delayInput.isFocused) delayInput.SetTextWithoutNotify(plugin.Delay.ToString());
            connection.sprite = icons[plugin.Paused ? "pause" : "connection"];
            shownConnection = connectionText;
            shownDelay = plugin.Delay;
        }
        if (shownPause != plugin.Paused || pause.text == "Pause") { pause.text = plugin.Paused ? "Resume" : "Pause"; shownPause = plugin.Paused; }
        if (shownFeed != plugin.ShowFeed || feedToggle.text.Length == 0) { feedToggle.text = plugin.ShowFeed ? "Feed: on" : "Feed: off"; shownFeed = plugin.ShowFeed; }
        var uiWidth = Screen.width / canvas.scaleFactor;
        var width = Mathf.Clamp(uiWidth - 40, 250, 760);
        var gameplay = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name != "Main Menu Scene";
        // Reserve native clock/version space. In-game, the tray sits inside the existing bottom bar.
        var right = gameplay ? 90f : 140f;
        var bottom = 4f;
        Dock(tray, right, 0);
        tray.sizeDelta = new Vector2(Mathf.Min(status.GetPreferredValues(status.text).x + 50, uiWidth - right - 20), 40);
        Dock(panel, right, bottom + 40);
        var active = plugin.ShowFeed ? entries.Where(e => Time.unscaledTime - e.Time < 18).Reverse().ToArray() : Array.Empty<Entry>();
        for (var index = 0; index < rows.Length; index++)
        {
            var row = rows[index];
            row.Rect.gameObject.SetActive(index < active.Length);
            if (index >= active.Length) continue;
            var entry = active[index];
            var alpha = Mathf.Clamp01((18 - (Time.unscaledTime - entry.Time)) / 4) * .78f;
            row.Text.text = entry.Text + (entry.Count > 1 ? $" ×{entry.Count}" : "");
            row.Text.color = new Color(.76f, .78f, .8f, alpha);
            row.Icon.sprite = icons.TryGetValue(entry.Icon, out var sprite) ? sprite : icons["read"];
            row.Icon.color = row.Text.color;
            row.Rect.sizeDelta = new Vector2(width, 22);
            Dock(row.Rect, 12, bottom + 42 + index * 23);
            var textWidth = Mathf.Min(width - 26, row.Text.GetPreferredValues(row.Text.text).x);
            // Keep the icon beside its text instead of at the distant left edge of the whole feed.
            row.Icon.rectTransform.anchoredPosition = new Vector2(width - textWidth - 24, 0);
        }
    }

    private void SyncScale()
    {
        var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
        if (nativeCanvas == null || !nativeCanvas.isActiveAndEnabled || nativeCanvas.gameObject.scene != scene)
        {
            // Follow the main native screen canvas, not a separate aspect-ratio approximation.
            nativeCanvas = Resources.FindObjectsOfTypeAll<Canvas>()
                .Where(c => c.isRootCanvas && c.isActiveAndEnabled && c.renderMode != RenderMode.WorldSpace && c.gameObject.scene == scene && c.GetComponent<AgentOverlayMarker>() == null)
                .OrderByDescending(c => c.GetComponentsInChildren<Selectable>(true).Length).FirstOrDefault();
        }
        if (nativeCanvas == null) return;
        canvas.scaleFactor = nativeCanvas.scaleFactor;
        canvas.referencePixelsPerUnit = nativeCanvas.referencePixelsPerUnit;
    }

    private void ApplyStyle()
    {
        if (nativePanel == null)
        {
            var images = Resources.FindObjectsOfTypeAll<Image>().Where(i => i.GetComponentInParent<AgentOverlayMarker>() == null).ToArray();
            nativePanel = images.FirstOrDefault(i => i.name == "Background" && i.transform.parent != null && i.transform.parent.name == "SettingsWindow");
            nativeHeader = images.FirstOrDefault(i => i.name == "TopBar" && i.transform.parent != null && i.transform.parent.name == "SettingsWindow");
        }
        var background = nativePanel != null ? nativePanel.color : new Color(.96f, .96f, .96f);
        var nativeFont = nativePanel != null ? nativePanel.transform.parent.GetComponentsInChildren<TMP_Text>(true).FirstOrDefault(t => t.font != null)?.font : null;
        if (nativeFont != null && nativeFont != font)
        {
            font = nativeFont;
            foreach (var label in root.GetComponentsInChildren<TMP_Text>(true)) label.font = font;
        }
        var dark = background.grayscale < .5f;
        for (var index = 0; index < surfaces.Count; index++)
        {
            var image = surfaces[index];
            image.color = headerSurfaces.Contains(image) ? nativeHeader != null ? nativeHeader.color : dark ? new Color(.2f, .2f, .2f) : new Color(.85f, .86f, .87f) : background;
            if (nativePanel != null && nativePanel.sprite != null) { image.sprite = nativePanel.sprite; image.type = nativePanel.type; }
        }
        foreach (var label in panelLabels) label.color = dark ? new Color(.88f, .88f, .88f) : new Color(.15f, .15f, .15f);
        foreach (var button in buttons)
        {
            button.colors = ColorBlock.defaultColorBlock;
            var target = button.GetComponent<Image>();
            target.color = dark ? new Color(.28f, .28f, .28f) : Color.white;
        }
        foreach (var image in panel.GetComponentsInChildren<Image>(true).Concat(about.GetComponentsInChildren<Image>(true)).Where(i => i.name == "Icon")) image.color = dark ? new Color(.88f, .88f, .88f) : new Color(.2f, .2f, .2f);
        status.color = new Color(.78f, .8f, .82f);
        connection.color = status.color;
    }

    private void LoadIcons()
    {
        var assembly = Assembly.GetExecutingAssembly();
        foreach (var name in new[] { "read", "search", "chart", "edit", "click", "open", "close", "pause", "connection", "message", "error", "picture" })
        {
            using var stream = assembly.GetManifestResourceStream("Icons." + name + ".png") ?? throw new InvalidOperationException("Missing embedded icon: " + name);
            using var bytes = new MemoryStream();
            stream.CopyTo(bytes);
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, name = "Agent " + name };
            ImageConversion.LoadImage(texture, bytes.ToArray());
            icons[name] = Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height), new Vector2(.5f, .5f), 48);
        }
    }

    private Image Icon(Transform parent, string icon, Vector2 position, float size, bool center = false)
    {
        var rect = Rect("Icon", parent, size, size);
        rect.anchorMin = rect.anchorMax = new Vector2(center ? .5f : 0, .5f);
        rect.pivot = new Vector2(center ? .5f : 0, .5f);
        rect.anchoredPosition = position;
        var image = rect.gameObject.AddComponent<Image>();
        image.sprite = icons[icon]; image.raycastTarget = false;
        return image;
    }

    private Button MakeButton(Transform parent, float width, float height, float x, float y, string label, Action action)
    {
        var rect = Rect("Button", parent, width, height); Top(rect, x, y);
        Background(rect, Color.white);
        var outline = rect.gameObject.AddComponent<Outline>();
        outline.effectColor = new Color(.45f, .45f, .45f, .55f); outline.effectDistance = new Vector2(1, -1);
        var button = rect.gameObject.AddComponent<Button>();
        button.onClick.AddListener(() => action());
        PanelText(rect, label, 14);
        buttons.Add(button);
        return button;
    }

    private TextMeshProUGUI PanelText(Transform parent, string text, float size)
    {
        var label = Text(parent, text, size, TextAlignmentOptions.Midline);
        panelLabels.Add(label);
        return label;
    }

    private TextMeshProUGUI Text(Transform parent, string text, float size, TextAlignmentOptions alignment)
    {
        var label = Rect("Text", parent, 0, 0).gameObject.AddComponent<TextMeshProUGUI>();
        label.font = font; label.fontSize = size; label.text = text; label.raycastTarget = false;
        label.alignment = alignment; label.textWrappingMode = TextWrappingModes.NoWrap; label.overflowMode = TextOverflowModes.Ellipsis;
        Stretch(label.rectTransform, 6, 6);
        return label;
    }

    private static Image Background(RectTransform rect, Color color) { var image = rect.gameObject.AddComponent<Image>(); image.color = color; return image; }
    private static RectTransform Rect(string name, Transform parent, float width, float height)
    {
        var rect = new GameObject(name, typeof(RectTransform)).GetComponent<RectTransform>();
        rect.SetParent(parent, false); rect.sizeDelta = new Vector2(width, height); return rect;
    }
    private static void Dock(RectTransform rect, float right, float bottom) { rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(1, 0); rect.anchoredPosition = new Vector2(-right, bottom); }
    private static void Top(RectTransform rect, float x, float y) { rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0, 1); rect.anchoredPosition = new Vector2(x, -y); }
    private static void Position(RectTransform rect, float x, float y, float width, float height) { Top(rect, x, y); rect.sizeDelta = new Vector2(width, height); }
    private static void Stretch(RectTransform rect, float left, float right) { rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one; rect.offsetMin = new Vector2(left, 0); rect.offsetMax = new Vector2(-right, 0); }
    // Opens the web page of a clicked <link="url"> in a text.
    private sealed class TextLinks : MonoBehaviour, UnityEngine.EventSystems.IPointerClickHandler
    {
        public void OnPointerClick(UnityEngine.EventSystems.PointerEventData data)
        {
            var text = GetComponent<TMP_Text>();
            var index = TMP_TextUtilities.FindIntersectingLink(text, data.position, null);
            if (index >= 0) Application.OpenURL(text.textInfo.linkInfo[index].GetLinkID());
        }
    }

    public void Dispose()
    {
        Object.Destroy(root);
        foreach (var sprite in icons.Values) { Object.Destroy(sprite.texture); Object.Destroy(sprite); }
    }
}
