using System.Collections.Generic;
using System.Linq;
using ProcessorTycoonModApi;
using UnityEngine;

namespace ProcessorTycoonMod;

// The About window (Mod API AboutWindow, the same as the Multiplayer mod's): version and links, what the mod does,
// credits with the Claude mark, what it is built with and the disclaimer.
internal sealed class AgentAbout
{
    private readonly AboutWindow window;

    public AgentAbout(Overlay overlay, Sprite icon)
    {
        var credits = new Credits
        {
            Name = AgentInfo.Name, Version = AgentInfo.Version, Release = AgentInfo.Release, License = AgentInfo.License, Description = AgentInfo.Description,
            Repository = AgentInfo.Repository, Issues = AgentInfo.Issues, Discord = AgentInfo.Discord,
            Author = AgentInfo.Author, AuthorUrl = AgentInfo.AuthorUrl, Collaborators = AgentInfo.Collaborators,
            BuiltWith = AgentInfo.ThirdParty.Append(("Processor Tycoon Mod API", "MIT")).ToArray(), Disclaimer = AgentInfo.Disclaimer,
        };
        window = new AboutWindow(overlay, credits, icon, new Dictionary<string, Sprite> { [Marks.ClaudeUrl] = Marks.Claude });
    }

    public bool Visible => window.Visible;
    public void SetVisible(bool visible) { if (visible) window.Show(); else window.Close(); }
    public void Tick() => window.Tick();
}
