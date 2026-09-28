using System;
using System.Linq;

namespace ProcessorTycoonShared;

// Release identity, shared by the plugin (Agent Settings footer and About window) and the CLI (help and status).
internal static class AgentInfo
{
    public const string Name = "Processor Tycoon Agent";
    public const string Version = "0.5.0";
    public const string Release = "Autopilot";
    public const string Short = "v" + Version + " “" + Release + "”";
    public const string Repository = "https://github.com/krittik/processor-tycoon-agent";
    public const string Releases = Repository + "/releases/latest";
    public const string Issues = Repository + "/issues";
    public const string Discord = "https://discord.gg/YKdTjge7J2";
    public const string License = "MIT";

    // Credits name the author and collaborators without roles.
    public const string Author = "Critique (Sevastyanoff)";   // nickname (surname)
    public const string AuthorUrl = Discord;   // the author's closest page: their Discord server
    public static readonly (string name, string url)[] Collaborators = { ("Claude Code (Anthropic)", "https://claude.com/claude-code") };

    // "By Critique, in collaboration with Claude Code (Anthropic)", each name wrapped by link(name, url).
    public static string Byline(Func<string, string, string> link) => $"By {link(Keep(Author), AuthorUrl)}, in collaboration with {string.Join(", ", Collaborators.Select(c => link(Keep(c.name), c.url)))}.";
    private static string Keep(string name) => name.Replace(' ', (char)0xA0);   // a linked name never wraps inside

    public static readonly (string name, string note)[] ThirdParty =
    {
        ("BepInEx", "plugin loader, LGPL-2.1"),
        ("Newtonsoft.Json", "JSON, MIT"),
    };

    public const string Disclaimer = "Unofficial fan mod. Processor Tycoon belongs to its developers; this mod is not affiliated with or endorsed by them and ships none of the game's files.";
}
