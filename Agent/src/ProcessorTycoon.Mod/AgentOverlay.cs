using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using ProcessorTycoonModApi;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ProcessorTycoonMod;

// The mod's own layer above the game, built with the Processor Tycoon Mod API: the bottom-bar item ("Agent · Connected"),
// which opens the Agent window docked above it, the About window, the action feed at the bottom right (it fades; hover it
// to read the recent history) and the decorative agent cursor. Nothing here blocks agent commands.
internal sealed class AgentOverlay : IDisposable
{
    private readonly Plugin plugin;
    private readonly Overlay overlay;
    private readonly AgentCursor cursor;
    private int cursorScene;
    private readonly BarItem tray;
    private readonly AgentSettings settings;
    private readonly AgentAbout about;
    private readonly Feed feed;
    private readonly Dictionary<string, Sprite> icons = new();
    private string shownConnection = "";
    private bool shownPause;

    // Waits for the game's font (the first scene has loaded).
    public static AgentOverlay? TryCreate(Plugin plugin)
    {
        Theme.Refresh();
        return Theme.Font == null ? null : new AgentOverlay(plugin);
    }

    private AgentOverlay(Plugin plugin)
    {
        this.plugin = plugin;
        LoadIcons();
        overlay = new Overlay("AgentOverlay", 32760);
        overlay.GameObject.AddComponent<AgentOverlayMarker>();
        overlay.Tick();
        tray = new BarItem(overlay, "agent", 0, icons["connection"], "Agent");
        tray.Clicked += TogglePanel;
        settings = new AgentSettings(overlay, plugin, ToggleAbout);
        about = new AgentAbout(overlay, icons["connection"]);
        feed = new Feed(overlay, rightSide: true, richText: false);
        cursor = AgentCursor.Create(overlay.Root);
        cursorScene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().handle;
    }

    internal void ShowAction(Vector2? point, bool click)
    {
        if (point.HasValue && !plugin.Paused) cursor.MoveTo(point.Value, click);
        else cursor.Hide();
    }

    public void TogglePanel() => SetPanel(!settings.Visible);
    public void SetPanel(bool visible) { if (visible) settings.Show(tray.Rect); else { settings.Close(); SetAbout(false); } }
    public void ToggleAbout() => SetAbout(!about.Visible);
    public void SetAbout(bool visible) => about.SetVisible(visible);

    public void Add(string message, bool repeatable, string icon = "read") => feed.Add(message, icons.TryGetValue(icon, out var sprite) ? sprite : icons["read"], repeatable);

    public void Refresh()
    {
        var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().handle;
        if (plugin.Paused || scene != cursorScene || plugin.Connection != "Agent Connected") cursor.Hide(resetPosition: true);
        cursorScene = scene;
        overlay.Tick();
        settings.Refresh();
        about.Tick();
        var connectionText = plugin.Connection.Replace("Agent ", "").Replace("Waiting for agent", "Offline");
        if (shownConnection != connectionText || shownPause != plugin.Paused)
        {
            tray.Text = $"Agent · {(plugin.Paused ? "Paused" : connectionText)}";
            tray.Icon = icons[plugin.Paused ? "pause" : "connection"];
            shownConnection = connectionText;
            shownPause = plugin.Paused;
        }
        tray.Tick();
        feed.Enabled = plugin.ShowFeed;
        feed.Tick();
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

    public void Dispose()
    {
        tray.Destroy();
        overlay.Destroy();
        foreach (var sprite in icons.Values) { Object.Destroy(sprite.texture); Object.Destroy(sprite); }
    }
}
