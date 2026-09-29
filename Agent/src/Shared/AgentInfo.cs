using System;
using System.Linq;

namespace ProcessorTycoonShared;

// Release identity, shared by the plugin (Agent window footer and About window) and the CLI (help and status).
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
    public const string Description = "Lets AI agents and scripts play through the visible game with native player actions, and shows what they do.";

    // Credits name the author and collaborators without roles.
    public const string Author = "Critique (Sevastyanoff)";   // nickname (surname)
    public const string AuthorUrl = Discord;   // the author's closest page: their Discord server
    public const string ClaudeUrl = "https://claude.com/claude-code";
    public static readonly (string name, string url)[] Collaborators = { ("Claude Code", ClaudeUrl) };

    // "By Critique (Sevastyanoff), in collaboration with Claude Code.", each name wrapped by link(name, url).
    public static string Byline(Func<string, string, string> link) => $"By {link(Keep(Author), AuthorUrl)}, in collaboration with {string.Join(", ", Collaborators.Select(c => link(Keep(c.name), c.url)))}.";
    private static string Keep(string name) => name.Replace(' ', (char)0xA0);   // a linked name never wraps inside

    public static readonly (string name, string license)[] ThirdParty = { ("BepInEx", "LGPL-2.1"), ("Newtonsoft.Json", "MIT") };
    public static string BuiltWith => "Built with " + string.Join(", ", ThirdParty.Take(ThirdParty.Length - 1).Select(Part)) + " and " + Part(ThirdParty.Last()) + ".";
    private static string Part((string name, string license) p) => $"{p.name} ({p.license})";

    public const string Disclaimer = "Unofficial fan mod. Processor Tycoon belongs to its developers; this mod is not affiliated with or endorsed by them and ships none of the game's files.";
}
