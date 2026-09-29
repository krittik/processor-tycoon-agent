using System;
using System.Globalization;
using System.Linq;
using ProcessorTycoonModApi;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace ProcessorTycoonMod;

// The Agent window, opened from the bottom-bar item (or F8) and docked above it: whether an agent plays, the prompt to give
// one, pause, the delay between agent actions and the on-screen action feed; the footer's version link opens About.
internal sealed class AgentSettings
{
    private static readonly int[] Delays = { 0, 100, 250, 500, 1000 };
    private readonly Plugin plugin;
    private readonly Window window;
    private readonly Image dot;
    private readonly TextMeshProUGUI status, hint, delayLabel;
    private readonly Button pause, copy;
    private string shown = "";
    private float copiedUntil;

    public AgentSettings(Overlay overlay, Plugin plugin, Action openAbout)
    {
        this.plugin = plugin;
        window = new Window(overlay, "Agent", 350);
        var b = window.Body;
        (dot, status, _) = Ui.Status(b);
        hint = Ui.Label(b, "", 14, Paint.TextLow, wrap: true);
        var actions = Ui.Row(b, 8, 28);
        pause = Flexible(Ui.Button(actions.transform, "Pause agent", () => plugin.SetPaused(!plugin.Paused)));
        copy = Flexible(Ui.Button(actions.transform, "Copy prompt", CopyPrompt));
        Tip.On(copy.gameObject, "Copy prompt", "A short starting message for an AI agent that can run commands on this PC, such as Claude Code or Codex: where this game's command-line tool is and how to begin. Paste it to the agent and add your goal.");
        var delayRow = Ui.Row(b, 6, 28);
        delayLabel = Ui.Label(delayRow.transform, "Delay", 15);
        delayLabel.GetComponent<LayoutElement>().minWidth = 40;
        Ui.Info(delayRow.transform, "Delay between actions", "How long the agent waits between its actions, so you can follow what it does. Off plays at full speed.");
        Ui.Spacer(delayRow.transform);
        Ui.Segments(delayRow.transform, new[] { "Off", "0.1 s", "0.25 s", "0.5 s", "1 s" }, () => Array.IndexOf(Delays, plugin.Delay), i => plugin.SetDelay(Delays[i]));
        Ui.Check(b, "Show agent actions on screen", () => plugin.ShowFeed, _ => plugin.ToggleFeed());
        window.Footer(AgentInfo.Short, openAbout);
    }

    public bool Visible => window.Visible;
    public void Show(RectTransform dockAbove) { shown = ""; Refresh(); window.ShowAbove(dockAbove); }
    public void Close() => window.Close();

    public void Refresh()
    {
        if (!window.Visible) return;
        var connection = plugin.Connection;
        var copied = Time.unscaledTime < copiedUntil;
        var state = $"{connection}|{plugin.Paused}|{plugin.Delay}|{copied}|{Theme.Revision}";
        if (state == shown) return;
        shown = state;
        var connected = connection == "Agent Connected";
        var (paint, title, detail) = plugin.Paused ? (Paint.Cta, "Paused", "Agent actions wait until you resume.")
            : connected ? (Paint.Positive, "Agent connected", "")
            : connection == "Bridge error" ? (Paint.Negative, "The agent bridge could not start", "The game's BepInEx log says why.")
            : (Paint.TextLow, "No agent connected", "Copy the prompt and paste it to an AI agent that can run commands on this PC.");
        Painted.Set(dot, paint);
        status.text = title;
        hint.text = detail;
        hint.gameObject.SetActive(detail.Length > 0);
        pause.GetComponentInChildren<TextMeshProUGUI>().text = plugin.Paused ? "Resume agent" : "Pause agent";
        copy.GetComponentInChildren<TextMeshProUGUI>().text = copied ? "Copied" : "Copy prompt";
        // The primary action follows the state: resume when paused, copy the prompt while no agent plays.
        Ui.Style(pause, plugin.Paused);
        Ui.Style(copy, !plugin.Paused && !connected);
        var custom = !Delays.Contains(plugin.Delay);
        delayLabel.text = custom ? $"Delay ({plugin.Delay.ToString(CultureInfo.InvariantCulture)} ms)" : "Delay";
    }

    // A short starting message: where this installation's CLI is (worked out on the player's PC) and how to read the full
    // brief from it, so the brief always matches the installed CLI. The player adds their goal after "My goal:".
    private void CopyPrompt()
    {
        var cli = System.IO.Path.Combine(BepInEx.Paths.GameRootPath, "tools", "pt-agent.exe");
        GUIUtility.systemCopyBuffer = $"Play Processor Tycoon on my PC through its command-line tool:\n\"{cli}\"\n\nFirst run it with \"prompt\" and read that whole game brief and its rules before playing; run it again after any context compaction. Then start with --help and status; \"guide\" lists every command.\n\nIf I leave the goal below empty, learn the game and the CLI first, then ask me what I want (for example a new game or one of my saves, the difficulty, what counts as success) before you start, load or change anything.\n\nMy goal: ";
        copiedUntil = Time.unscaledTime + 2.5f;
        Refresh();
    }

    // Equal-width buttons in a row.
    private static Button Flexible(Button button)
    {
        Ui.Size(button, flexWidth: 1).minWidth = 0;
        return button;
    }
}
