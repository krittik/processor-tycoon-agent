using System.Collections.Generic;
using TMPro;
using UnityEngine;
using Paint = ProcessorTycoonMod.AgentUi.Paint;

namespace ProcessorTycoonMod;

// The About window: version, credits with links, what the mod is built with, disclaimer; laid out like the Multiplayer
// mod's About window.
internal sealed class AgentAbout
{
    private readonly AgentWindow window;
    private readonly TextMeshProUGUI byline;
    private readonly List<(TextMeshProUGUI label, string name, string note)> thirdParty = new();
    private int revision = -1;

    public AgentAbout(Transform parent, Sprite icon)
    {
        window = new AgentWindow(parent, "About Agent", 440);
        var b = window.Body;
        var head = AgentUi.Row(b, 12, 52);
        AgentUi.Icon(head.transform, icon, 44, Paint.Cta);
        var names = AgentUi.Column(head.transform, 0);
        AgentUi.Size(names, flexWidth: 1);
        AgentUi.Label(names, "<b>" + AgentInfo.Name + "</b>", 18);
        AgentUi.Label(names, $"Version {AgentInfo.Version}  “{AgentInfo.Release}”  ·  {AgentInfo.License} License", 15, Paint.TextLow);
        AgentUi.Label(b, "Lets AI agents and scripts play through the visible game with native player actions, and shows what they do.", 15, wrap: true);
        AgentUi.Header(b, "Credits");
        byline = AgentUi.Label(b, "", 16, wrap: true);
        byline.raycastTarget = true;
        byline.gameObject.AddComponent<TextLinks>();
        byline.gameObject.AddComponent<HandCursor>();
        AgentUi.Header(b, "Built with");
        foreach (var (name, note) in AgentInfo.ThirdParty) thirdParty.Add((AgentUi.Label(b, name, 15), name, note));
        AgentUi.Size(AgentUi.Rect("Space", b), height: 2);
        AgentUi.Label(b, AgentInfo.Disclaimer, 13, Paint.TextLow, wrap: true);
        var footer = window.Footer(null);
        AgentUi.Button(footer.transform, "Report an issue", () => Application.OpenURL(AgentInfo.Issues));
        AgentUi.Button(footer.transform, "Discord", () => Application.OpenURL(AgentInfo.Discord));
        AgentUi.Button(footer.transform, "GitHub", () => Application.OpenURL(AgentInfo.Repository), cta: true);
        AgentUi.Button(footer.transform, "Close", window.Close);
    }

    public bool Visible => window.Visible;
    public void SetVisible(bool visible) { if (visible) { Tick(); window.Show(); } else window.Close(); }

    // Texts with inline theme colours (links, notes) follow theme changes.
    public void Tick()
    {
        if (revision == AgentUi.Revision) return;
        revision = AgentUi.Revision;
        var low = ColorUtility.ToHtmlStringRGB(AgentUi.Of(Paint.TextLow));
        foreach (var (label, name, note) in thirdParty) label.text = $"{name}  <color=#{low}>{note}</color>";
        var cta = ColorUtility.ToHtmlStringRGB(AgentUi.Of(Paint.Cta));
        byline.text = AgentInfo.Byline((name, url) => $"<link=\"{url}\"><u><color=#{cta}>{name}</color></u></link>");
    }
}
