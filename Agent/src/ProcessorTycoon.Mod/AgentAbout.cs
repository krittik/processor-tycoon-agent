using TMPro;
using UnityEngine;
using Paint = ProcessorTycoonMod.AgentUi.Paint;

namespace ProcessorTycoonMod;

// The About window: version and links, what the mod does, credits, what it is built with and the disclaimer; laid out like
// the Multiplayer mod's About window.
internal sealed class AgentAbout
{
    private readonly AgentWindow window;
    private readonly TextMeshProUGUI meta, byline;
    private int revision = -1;

    public AgentAbout(Transform parent, Sprite icon)
    {
        window = new AgentWindow(parent, "About Agent", 440);
        var b = window.Body;
        var head = AgentUi.Row(b, 12, 46);
        AgentUi.Icon(head.transform, icon, 40, Paint.Cta);
        var names = AgentUi.Column(head.transform, 0);
        AgentUi.Size(names, flexWidth: 1);
        AgentUi.Label(names, "<b>" + AgentInfo.Name + "</b>", 18);
        meta = AgentUi.Label(names, "", 14, Paint.TextLow);
        AgentUi.Links(meta);
        AgentUi.Label(b, AgentInfo.Description, 15, wrap: true);
        byline = AgentUi.Label(b, "", 15, wrap: true);
        AgentUi.Links(byline);
        AgentUi.Mark(byline, AgentInfo.ClaudeUrl, ClaudeMark.Sprite);
        AgentUi.Label(b, AgentInfo.BuiltWith, 14, Paint.TextLow, wrap: true);
        AgentUi.Label(b, AgentInfo.Disclaimer, 13, Paint.TextLow, wrap: true);
        var footer = window.Footer(null);
        AgentUi.Button(footer.transform, "Report an issue", () => Application.OpenURL(AgentInfo.Issues));
    }

    public bool Visible => window.Visible;
    public void SetVisible(bool visible) { if (visible) { Tick(); window.Show(); } else window.Close(); }

    // Links carry the theme's colour inline: rewritten after a theme change.
    public void Tick()
    {
        if (revision == AgentUi.Revision) return;
        revision = AgentUi.Revision;
        meta.text = $"{AgentInfo.Version} “{AgentInfo.Release}”  ·  {AgentInfo.License} License  ·  {AgentUi.Link("GitHub", AgentInfo.Repository)}  ·  {AgentUi.Link("Discord", AgentInfo.Discord)}";
        byline.text = AgentInfo.Byline((name, url) => (url == AgentInfo.ClaudeUrl ? AgentUi.MarkSpace : "") + AgentUi.Link(name, url));
    }
}
