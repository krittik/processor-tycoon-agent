using System;
using System.Globalization;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Paint = ProcessorTycoonMod.AgentUi.Paint;

namespace ProcessorTycoonMod;

// The Agent window, opened from the bottom-bar item (or F8) and docked above it: what the agent is doing, pause, the action
// feed and the delay between agent actions; the footer's version link opens About.
internal sealed class AgentSettings
{
    private readonly Plugin plugin;
    private readonly AgentWindow window;
    private readonly TextMeshProUGUI status, pause, feed;
    private readonly TMP_InputField delay;
    private string shown = "";

    public AgentSettings(Transform parent, Plugin plugin, Action openAbout)
    {
        this.plugin = plugin;
        window = new AgentWindow(parent, "Agent", 330);
        var b = window.Body;
        status = AgentUi.Label(b, "", 15, Paint.TextLow, wrap: true);
        var actions = AgentUi.Row(b, 8, 28);
        pause = Flexible(AgentUi.Button(actions.transform, "Pause", () => plugin.SetPaused(!plugin.Paused)));
        feed = Flexible(AgentUi.Button(actions.transform, "Feed: on", plugin.ToggleFeed));
        var delayRow = AgentUi.Row(b, 8, 28);
        AgentUi.Size(AgentUi.Label(delayRow.transform, "Delay between agent actions", 15), flexWidth: 1);
        delay = AgentUi.Input(delayRow.transform, plugin.Delay.ToString(CultureInfo.InvariantCulture), 5, value =>
        {
            if (int.TryParse(value, out var milliseconds)) plugin.SetDelay(Mathf.Clamp(milliseconds, 0, 60000));
            delay!.SetTextWithoutNotify(plugin.Delay.ToString(CultureInfo.InvariantCulture));
        }, 70, TMP_InputField.ContentType.IntegerNumber);
        AgentUi.Label(delayRow.transform, "ms", 15, Paint.TextLow);
        var presets = AgentUi.Row(b, 6, 26);
        foreach (var value in new[] { 0, 100, 250, 500, 1000 }) Flexible(AgentUi.Button(presets.transform, value == 0 ? "Off" : value.ToString(CultureInfo.InvariantCulture), () => plugin.SetDelay(value), height: 26, size: 14));
        var footer = window.Footer(openAbout);
        AgentUi.Button(footer.transform, "Close", window.Close);
    }

    public bool Visible => window.Visible;
    public void Show(RectTransform dockAbove) { shown = ""; Refresh(); window.Show(dockAbove); }
    public void Close() => window.Close();

    public void Refresh()
    {
        if (!window.Visible) return;
        var connection = plugin.Connection;
        var text = plugin.Paused ? "Paused: agent actions wait until you resume."
            : connection == "Agent Connected" ? "An agent is connected and plays through the visible game."
            : "No agent is connected. Give your agent the prompt from pt-agent prompt (see START-HERE.md).";
        var state = $"{text}|{plugin.Paused}|{plugin.ShowFeed}|{plugin.Delay}";
        if (state == shown) return;
        shown = state;
        status.text = text;
        pause.text = plugin.Paused ? "Resume" : "Pause";
        feed.text = plugin.ShowFeed ? "Feed: on" : "Feed: off";
        if (!delay.isFocused) delay.SetTextWithoutNotify(plugin.Delay.ToString(CultureInfo.InvariantCulture));
    }

    // Equal-width buttons in a row; returns the button's label.
    private static TextMeshProUGUI Flexible(Button button)
    {
        AgentUi.Size(button, flexWidth: 1).minWidth = 0;
        return button.GetComponentInChildren<TextMeshProUGUI>();
    }
}
