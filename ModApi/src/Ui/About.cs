using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace ProcessorTycoonModApi;

// Who made a mod and where to find it: the data of its About window.
internal sealed class Credits
{
    public string Name = "", Version = "", Release = "", License = "MIT", Description = "";
    public string Repository = "", Issues = "", Discord = "", Disclaimer = "";
    // Credits name people without roles; each name links to its closest page (a Discord server, a web page).
    public string Author = "", AuthorUrl = "";
    public (string name, string url)[] Collaborators = Array.Empty<(string, string)>();
    public (string name, string license)[] BuiltWith = Array.Empty<(string, string)>();

    public string Short => Release.Length > 0 ? $"v{Version} “{Release}”" : "v" + Version;

    // "By Author, in collaboration with A, B." with each name wrapped by link(name, url); names never wrap inside.
    public string Byline(Func<string, string, string> link)
    {
        var by = "By " + link(Keep(Author), AuthorUrl);
        return Collaborators.Length == 0 ? by + "." : $"{by}, in collaboration with {string.Join(", ", Collaborators.Select(c => link(Keep(c.name), c.url)))}.";
    }

    public string BuiltWithText => BuiltWith.Length == 0 ? "" : "Built with " + (BuiltWith.Length == 1 ? Part(BuiltWith[0]) : string.Join(", ", BuiltWith.Take(BuiltWith.Length - 1).Select(Part)) + " and " + Part(BuiltWith.Last())) + ".";

    private static string Part((string name, string license) p) => $"{p.name} ({p.license})";
    private static string Keep(string name) => name.Replace(' ', (char)0xA0);
}

// A mod's About window, the same in every mod built on this API: icon, name, version, license and links (GitHub, Discord)
// on one line, the description, the credits (optional marks before linked names, see Ui.Mark), what it is built with and
// the disclaimer; the footer has Report an issue and any actions added with AddAction.
internal sealed class AboutWindow
{
    public readonly Window Window;
    private readonly Credits credits;
    private readonly TextMeshProUGUI meta, byline;
    private readonly HorizontalLayoutGroup footer;
    private readonly Dictionary<string, Sprite> marks;
    private int revision = -1;

    public AboutWindow(Overlay overlay, Credits credits, Sprite? icon, Dictionary<string, Sprite>? marks = null)
    {
        this.credits = credits;
        this.marks = marks ?? new Dictionary<string, Sprite>();
        Window = new Window(overlay, "About " + ShortName(credits.Name), 440);
        var b = Window.Body;
        var head = Ui.Row(b, 12, 46);
        Ui.Icon(head.transform, icon, 40, Paint.Cta);
        var names = Ui.Column(head.transform, 0);
        Ui.Size(names, flexWidth: 1);
        Ui.Label(names.transform, "<b>" + credits.Name + "</b>", 18);
        meta = Ui.Label(names.transform, "", 14, Paint.TextLow);
        Ui.Links(meta);
        if (credits.Description.Length > 0) Ui.Label(b, credits.Description, 15, wrap: true);
        byline = Ui.Label(b, "", 15, wrap: true);
        Ui.Links(byline);
        foreach (var mark in this.marks) Ui.Mark(byline, mark.Key, mark.Value);
        if (credits.BuiltWith.Length > 0) Ui.Label(b, credits.BuiltWithText, 14, Paint.TextLow, wrap: true);
        if (credits.Disclaimer.Length > 0) Ui.Label(b, credits.Disclaimer, 13, Paint.TextLow, wrap: true);
        footer = Window.Footer();
        if (credits.Issues.Length > 0) Ui.Button(footer.transform, "Report an issue", () => Application.OpenURL(credits.Issues));
    }

    public bool Visible => Window.Visible;
    public void Show() { Tick(); Window.Show(); }
    public void Close() => Window.Close();
    public void Toggle() { if (Visible) Close(); else Show(); }

    // An extra footer action, before Report an issue.
    public Button AddAction(string label, Action action)
    {
        var button = Ui.Button(footer.transform, label, action);
        button.transform.SetSiblingIndex(footer.transform.childCount - (credits.Issues.Length > 0 ? 2 : 1));
        return button;
    }

    // Links carry the theme's colour inline: rewritten after a theme change.
    public void Tick()
    {
        if (revision == Theme.Revision) return;
        revision = Theme.Revision;
        var links = new List<string>();
        if (credits.Repository.Length > 0) links.Add(Ui.Link("GitHub", credits.Repository));
        if (credits.Discord.Length > 0) links.Add(Ui.Link("Discord", credits.Discord));
        meta.text = string.Join("  ·  ", new[] { $"{credits.Version}{(credits.Release.Length > 0 ? $" “{credits.Release}”" : "")}", credits.License.Length > 0 ? credits.License + " License" : "" }.Where(s => s.Length > 0).Concat(links));
        byline.text = credits.Byline((name, url) => (marks.ContainsKey(url) ? Ui.MarkSpace : "") + Ui.Link(name, url));
    }

    // "Processor Tycoon Multiplayer" → "Multiplayer" for the title bar.
    private static string ShortName(string name) => name.StartsWith("Processor Tycoon ", StringComparison.Ordinal) ? name.Substring("Processor Tycoon ".Length) : name;
}
