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

// The mod's own layer above the game: the bottom-bar item ("Agent · Connected"), which opens the Agent window docked above
// it, the About window, the fading action feed and the decorative agent cursor. Nothing here blocks agent commands.
internal sealed class AgentOverlay : IDisposable
{
    private sealed class Entry { public string Text = ""; public string Icon = "read"; public int Count = 1; public float Time; public bool Repeatable; }
    private sealed class FeedRow { public RectTransform Rect = null!; public Image Icon = null!; public TextMeshProUGUI Text = null!; }
    private static readonly Color TrayColor = new(.78f, .8f, .82f);
    private readonly Plugin plugin;
    private readonly GameObject root;
    private readonly Canvas canvas;
    private readonly AgentCursor cursor;
    private int cursorScene;
    private Canvas? nativeCanvas;
    private readonly RectTransform tray;
    private readonly AgentSettings settings;
    private readonly AgentAbout about;
    private readonly TextMeshProUGUI status;
    private readonly Image connection;
    private readonly TMP_FontAsset font;
    private readonly Dictionary<string, Sprite> icons = new();
    private readonly List<Entry> entries = new();
    private readonly FeedRow[] rows = new FeedRow[6];
    private int shownDelay = -1;
    private string shownConnection = "";
    private bool shownPause;

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
        AgentUi.Refresh();
        root = new GameObject("AgentOverlay", typeof(RectTransform), typeof(Canvas), typeof(GraphicRaycaster), typeof(AgentOverlayMarker));
        Object.DontDestroyOnLoad(root);
        canvas = root.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 32760;

        // Bottom-bar item: same look and press as the Multiplayer mod's item next to it.
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
        tray.gameObject.AddComponent<HandCursor>();
        var content = Rect("Tray content", tray, 0, 0);
        Stretch(content, 0, 0);
        content.pivot = new Vector2(.5f, .5f);
        tray.gameObject.AddComponent<AgentButtonFeedback>().Target = content;
        connection = Icon(content, "connection", new Vector2(10, 0), 16);
        status = Text(content, "Agent", 15, TextAlignmentOptions.MidlineLeft);
        Stretch(status.rectTransform, 36, 8);
        status.color = connection.color = TrayColor;

        settings = new AgentSettings(root.transform, plugin, ToggleAbout);
        about = new AgentAbout(root.transform, icons["connection"]);

        for (var index = 0; index < rows.Length; index++)
        {
            var rect = Rect("Feed row", root.transform, 760, 22);
            var label = Text(rect, "", 15, TextAlignmentOptions.MidlineRight);
            label.richText = false;
            Stretch(label.rectTransform, 26, 0);
            rows[index] = new FeedRow { Rect = rect, Icon = Icon(rect, "read", Vector2.zero, 16), Text = label };
            rect.gameObject.SetActive(false);
        }
        cursor = AgentCursor.Create(root.transform);
        cursorScene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().handle;
    }

    internal void ShowAction(Vector2? point, bool click)
    {
        if (point.HasValue && !plugin.Paused) cursor.MoveTo(point.Value, click);
        else cursor.Hide();
    }

    public void TogglePanel() => SetPanel(!settings.Visible);
    public void SetPanel(bool visible) { if (visible) settings.Show(tray); else { settings.Close(); SetAbout(false); } }
    public void ToggleAbout() => SetAbout(!about.Visible);
    public void SetAbout(bool visible) => about.SetVisible(visible);

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
        AgentUi.Refresh();
        SyncScale();
        settings.Refresh();
        about.Tick();
        var connectionText = plugin.Connection.Replace("Agent ", "").Replace("Waiting for agent", "Offline");
        if (shownConnection != connectionText || shownDelay != plugin.Delay || shownPause != plugin.Paused)
        {
            status.text = $"Agent · {(plugin.Paused ? "Paused" : connectionText)}    Actions delay: {(plugin.Delay == 0 ? "off" : plugin.Delay + " ms")}";
            connection.sprite = icons[plugin.Paused ? "pause" : "connection"];
            shownConnection = connectionText;
            shownDelay = plugin.Delay;
            shownPause = plugin.Paused;
        }
        var uiWidth = Screen.width / canvas.scaleFactor;
        var width = Mathf.Clamp(uiWidth - 40, 250, 760);
        var gameplay = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name != "Main Menu Scene";
        // Reserve native clock/version space. In-game, the tray sits inside the existing bottom bar.
        var right = gameplay ? 90f : 140f;
        var bottom = 4f;
        Dock(tray, right, 0);
        tray.sizeDelta = new Vector2(Mathf.Min(status.GetPreferredValues(status.text).x + 50, uiWidth - right - 20), 40);
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

    private Image Icon(Transform parent, string icon, Vector2 position, float size)
    {
        var rect = Rect("Icon", parent, size, size);
        rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0, .5f);
        rect.anchoredPosition = position;
        var image = rect.gameObject.AddComponent<Image>();
        image.sprite = icons[icon]; image.raycastTarget = false;
        return image;
    }

    private TextMeshProUGUI Text(Transform parent, string text, float size, TextAlignmentOptions alignment)
    {
        var label = Rect("Text", parent, 0, 0).gameObject.AddComponent<TextMeshProUGUI>();
        label.font = font; label.fontSize = size; label.text = text; label.raycastTarget = false;
        label.alignment = alignment; label.textWrappingMode = TextWrappingModes.NoWrap; label.overflowMode = TextOverflowModes.Ellipsis;
        Stretch(label.rectTransform, 6, 6);
        return label;
    }

    private static RectTransform Rect(string name, Transform parent, float width, float height)
    {
        var rect = new GameObject(name, typeof(RectTransform)).GetComponent<RectTransform>();
        rect.SetParent(parent, false); rect.sizeDelta = new Vector2(width, height); return rect;
    }
    private static void Dock(RectTransform rect, float right, float bottom) { rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(1, 0); rect.anchoredPosition = new Vector2(-right, bottom); }
    private static void Stretch(RectTransform rect, float left, float right) { rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one; rect.offsetMin = new Vector2(left, 0); rect.offsetMax = new Vector2(-right, 0); }

    public void Dispose()
    {
        Object.Destroy(root);
        foreach (var sprite in icons.Values) { Object.Destroy(sprite.texture); Object.Destroy(sprite); }
    }
}
