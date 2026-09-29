using System;
using System.Globalization;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Paint = ProcessorTycoonMod.AgentUi.Paint;

namespace ProcessorTycoonMod;

// The Agent window, opened from the bottom-bar item (or F8) and docked above it: what the agent is doing, the prompt to give
// an agent, pause, the action feed and the delay between agent actions; the footer's version link opens About.
internal sealed class AgentSettings
{
    private readonly Plugin plugin;
    private readonly AgentWindow window;
    private readonly TextMeshProUGUI status, pause, feed, copy;
    private readonly TMP_InputField delay;
    private string shown = "";
    private float copiedUntil;

    public AgentSettings(Transform parent, Plugin plugin, Action openAbout)
    {
        this.plugin = plugin;
        window = new AgentWindow(parent, "Agent", 330);
        var b = window.Body;
        status = AgentUi.Label(b, "", 15, Paint.TextLow, wrap: true);
        var copyRow = AgentUi.Row(b, 8, 28);
        copy = Flexible(AgentUi.Button(copyRow.transform, "Copy prompt", CopyPrompt, cta: true));
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
            : "No agent is connected. Copy the prompt, paste it to your AI agent (any agent that can run commands on this PC) and add your goal.";
        var copied = Time.unscaledTime < copiedUntil;
        var state = $"{text}|{plugin.Paused}|{plugin.ShowFeed}|{plugin.Delay}|{copied}";
        if (state == shown) return;
        shown = state;
        status.text = text;
        copy.text = copied ? "Copied to the clipboard" : "Copy prompt";
        pause.text = plugin.Paused ? "Resume" : "Pause";
        feed.text = plugin.ShowFeed ? "Feed: on" : "Feed: off";
        if (!delay.isFocused) delay.SetTextWithoutNotify(plugin.Delay.ToString(CultureInfo.InvariantCulture));
    }

    // A short starting message: where this installation's CLI is (worked out on the player's PC) and how to read the full
    // brief from it, so the brief always matches the installed CLI. The player adds their goal after "My goal:".
    private void CopyPrompt()
    {
        var cli = System.IO.Path.Combine(BepInEx.Paths.GameRootPath, "tools", "pt-agent.exe");
        GUIUtility.systemCopyBuffer = $"Play Processor Tycoon on my PC through its command-line tool:\n\"{cli}\"\n\nFirst run it with \"prompt\" and read that whole game brief and its rules before playing; run it again after any context compaction. Then start with --help and status; \"guide\" lists every command.\n\nMy goal: ";
        copiedUntil = Time.unscaledTime + 2.5f;
        Refresh();
    }

    // Equal-width buttons in a row; returns the button's label.
    private static TextMeshProUGUI Flexible(Button button)
    {
        AgentUi.Size(button, flexWidth: 1).minWidth = 0;
        return button.GetComponentInChildren<TextMeshProUGUI>();
    }
}
