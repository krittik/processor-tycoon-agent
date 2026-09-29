using System;
using System.Collections.Generic;
using System.Reflection;
using ProcessorTycoonModApi;
using UnityEngine;

namespace ProcessorTycoonMod;

// The mod's own layer above the game, built with the Processor Tycoon Mod API: the bottom-bar item ("Agent · Connected"),
// which opens the Agent window docked above it, the About window, the action feed at the bottom right (it fades; hover it
// or the bottom-bar entry to read the history, the mouse wheel scrolls back) and the decorative action pointer. Nothing here blocks agent commands.
internal sealed class AgentOverlay : IDisposable
{
    private readonly Plugin plugin;
    private readonly Overlay overlay;
    private readonly ActionPointer cursor;
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
        about = new AgentAbout(overlay, icons["connection"], plugin);
        feed = new Feed(overlay, rightSide: true, richText: false);
        feed.FocusOn(tray.Rect);
        cursor = ActionPointer.Create(overlay.Root, "Agent");
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
        // The Agent window docks over the feed; the feed waits until it closes.
        feed.Enabled = plugin.ShowFeed && !settings.Visible;
        feed.Tick();
    }

    private void LoadIcons()
    {
        var assembly = Assembly.GetExecutingAssembly();
        foreach (var name in new[] { "read", "search", "chart", "edit", "click", "open", "close", "pause", "connection", "message", "error", "picture" })
            icons[name] = Sprites.FromResource(assembly, "Icons." + name + ".png", 48);
    }

    public void Dispose()
    {
        tray.Destroy();
        overlay.Destroy();
        foreach (var sprite in icons.Values) Sprites.Destroy(sprite);
    }
}
