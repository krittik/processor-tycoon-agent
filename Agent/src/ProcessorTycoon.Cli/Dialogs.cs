using System.Text.RegularExpressions;
using System.Text.Json.Nodes;

// Detects native blocking dialogs for a clean stop and describes the CPU release form ("Project Completed").
// Read-only: it never chooses a dialog option or closes a window.
internal static class Dialogs
{
    internal const string ReleaseScope = "ProjectReleaseWindow";
    internal const string ReleaseNext = "A finished CPU project opened its native release form (title 'Project Completed', scope ProjectReleaseWindow); time is paused. This is the same state projects-wait reaches. Inspect it with game projects-release-read, then commit with game projects-release \"NAME\" --price P [--sell-on-market true|false] [--available-for-contracts true|false] (NAME may be omitted: the CLI uses the form's current name). Do not use dialog-choose Release without --price: it would release immediately with the form's current price (the CLI refuses it).";

    internal static bool IsRelease(JsonNode? dialog) => dialog?["scope"]?.ToString() == ReleaseScope;

    // Null when nothing blocks. `known` are dialogs already reported by time-advance (pause-trigger popups).
    // `failedReply` is a read that failed; a covering window it names (scope X) is reported even when it is no known dialog.
    internal static async Task<JsonObject?> Blocking(HttpClient client, string session, int timeout, JsonArray? known = null, bool checkPauseMenu = false, JsonObject? failedReply = null)
    {
        var dialogs = new JsonArray();
        bool Listed(string? scope) => scope != null && dialogs.Any(x => x?["scope"]?.ToString() == scope);
        var read = await GameWatch.Call(client, "game.dialog-read", "", null, session, timeout);
        if (GameWatch.TryData(read, out var data)) foreach (var d in data!["dialogs"]?.AsArray() ?? new JsonArray()) if (d is JsonObject dialog) dialogs.Add(Compact(dialog));
        foreach (var d in known ?? new JsonArray())
            if (d?["scope"] != null && !Listed(d["scope"]!.ToString())) dialogs.Add(d["scope"]!.ToString() == "PauseMenu" ? PauseMenu() : IsForm(d) ? Form(d.AsObject()) : await Describe(client, session, timeout, d["scope"]!.ToString(), d["name"]?.ToString(), d["source"]?.ToString() ?? "native_pause_trigger"));
        var blockerScopes = Regex.Matches(failedReply.At("operation", "error", "message").Str() ?? failedReply.At("error", "message").Str() ?? "", @"\(scope ([^)\s]+)\)").Select(m => m.Groups[1].Value).Distinct().ToArray();
        if (dialogs.Count == 0 && (checkPauseMenu || blockerScopes.Length > 0))
        {
            var windows = await Workspace.Visible(client, session, timeout) ?? new JsonArray();
            // Plugins >= 0.3.9 flag dialog surfaces (including any window holding a native time-blocking popup trigger).
            foreach (var w in windows.OfType<JsonObject>().Where(w => w["dialog"]?.GetValue<bool>() == true && !Listed(w["scope"]?.ToString())))
                dialogs.Add(await Describe(client, session, timeout, w["scope"]!.ToString(), w["title"]?.ToString(), "window_list"));
            // Forms that keep game time paused while open (the CPU designer): reported without choices, never operated.
            foreach (var w in windows.OfType<JsonObject>().Where(w => w["blocksTime"]?.GetValue<bool>() == true && !Listed(w["scope"]?.ToString())))
                dialogs.Add(Form(new JsonObject { ["name"] = w["name"]?.DeepClone() ?? w["scope"]!.DeepClone(), ["scope"] = w["scope"]!.DeepClone() }));
            if (checkPauseMenu && windows.Any(w => w?["name"]?.ToString() == "Pause Menu") && !Listed("PauseMenu")) dialogs.Add(PauseMenu());
            foreach (var scope in blockerScopes.Where(scope => !Listed(scope) && !Workspace.Views.Contains(scope)))
            {
                var covering = await Describe(client, session, timeout, scope, windows.OfType<JsonObject>().FirstOrDefault(w => w["scope"]?.ToString() == scope)?["title"]?.ToString(), "covering_window");
                // Not a recognized dialog (dialogs are listed above): a form, editor or draft. Its buttons are not offered as choices.
                covering.Remove("choices");
                covering["note"] = "This window covers the target. It is not a recognized native dialog but a form, editor or draft; it was not closed. Finish it with its own commands, or close it with game window-close SCOPE if it is safe to close.";
                dialogs.Add(covering);
            }
        }
        if (dialogs.Count == 0) return null;
        var result = new JsonObject { ["wakeReasons"] = Reasons(dialogs), ["dialogs"] = dialogs };
        if (dialogs.Any(IsRelease) && await ReleaseForm(client, session, timeout) is JsonObject form) result["releaseForm"] = form;
        result["next"] = Next(dialogs);
        return result;
    }

    internal static bool IsForm(JsonNode? dialog) => dialog?["kind"]?.ToString() == "form";

    // An open form such as the CPU designer: it keeps game time paused, but it is no popup and has no dialog choice.
    internal static JsonObject Form(JsonObject window)
    {
        var name = window["name"]?.ToString() ?? window["scope"]?.ToString() ?? "";
        return new JsonObject
        {
            ["name"] = name, ["scope"] = window["scope"]?.DeepClone(), ["kind"] = "form", ["source"] = window["source"]?.DeepClone() ?? "window_list",
            ["note"] = window["note"]?.DeepClone() ?? $"'{name}' is an open form, not a popup: the game keeps time paused while it is open. It is never closed automatically and dialog-choose does not operate it; finish it or close it with game window-close \"{name}\"."
        };
    }

    internal static JsonObject PauseMenu() => new() { ["name"] = "Pause Menu", ["scope"] = "PauseMenu", ["choices"] = new JsonArray("Continue"), ["note"] = "Close it with game window-close \"Pause Menu\" (time stays paused)." };

    internal static JsonObject Compact(JsonObject dialog)
    {
        var result = new JsonObject { ["name"] = dialog["name"]?.DeepClone(), ["scope"] = dialog["scope"]?.DeepClone() };
        var choices = dialog["choices"]?.AsArray().Select(c => c is JsonObject choice ? choice["name"]?.ToString() : c?.ToString()).Where(c => !string.IsNullOrEmpty(c)).Select(c => (JsonNode?)JsonValue.Create(c)).ToArray();
        if (choices != null) result["choices"] = new JsonArray(choices);
        var text = dialog["text"]?.AsArray().Select(t => t?.ToString() ?? "").Where(t => t.Length > 0).Take(8).Select(t => (JsonNode?)JsonValue.Create(t)).ToArray();
        if (text?.Length > 0) result["text"] = new JsonArray(text);
        if (dialog["source"] != null) result["source"] = dialog["source"]!.DeepClone();
        if (IsRelease(dialog)) result["releaseForm"] = true;
        return result;
    }

    // Name, choices (visible buttons) and a few texts of any window, read through the generic observe (no input).
    internal static async Task<JsonObject> Describe(HttpClient client, string session, int timeout, string scope, string? name, string source)
    {
        var result = new JsonObject { ["name"] = name ?? scope, ["scope"] = scope, ["source"] = source };
        var reply = await GameWatch.Call(client, "observe", "", null, session, timeout, scope);
        if (reply["data"] is not JsonObject view) return result;
        var title = view["surfaces"]?.AsArray().FirstOrDefault(s => s?["scope"]?.ToString() == scope)?["title"]?.ToString();
        if (name == null && title != null) result["name"] = title;
        var buttons = view["controls"]?.AsArray().OfType<JsonObject>().Where(c => c["role"]?.ToString() == "button" && c["blockedReason"] == null).Select(c => c["label"]?.ToString()).Where(label => !string.IsNullOrWhiteSpace(label)).Distinct().Select(label => (JsonNode?)JsonValue.Create(label)).ToArray() ?? Array.Empty<JsonNode?>();
        result["choices"] = new JsonArray(buttons);
        var texts = view["texts"]?.AsArray().Select(t => t?["text"]?.ToString() ?? "").Where(t => t.Length > 0).Take(8).Select(t => (JsonNode?)JsonValue.Create(t)).ToArray() ?? Array.Empty<JsonNode?>();
        if (texts.Length > 0) result["text"] = new JsonArray(texts);
        if (IsRelease(result)) result["releaseForm"] = true;
        return result;
    }

    internal static JsonArray Reasons(JsonArray dialogs)
    {
        var reasons = new JsonArray();
        if (dialogs.Any(d => !IsForm(d) && d?["source"]?.ToString() != "covering_window")) reasons.Add("popup");
        if (dialogs.Any(IsForm)) reasons.Add("form_open");
        foreach (var name in dialogs.Where(d => !IsForm(d)).Select(d => ((d?["name"]?.ToString() ?? "") + " " + (d?["scope"]?.ToString() ?? "")).ToLowerInvariant()))
        {
            if (name.Contains("research") && name.Contains("complete") && !reasons.Any(r => r?.ToString() == "research_completed")) reasons.Add("research_completed");
            if ((name.Contains("project") || name.Contains("release")) && !reasons.Any(r => r?.ToString() == "project_completed")) reasons.Add("project_completed");
            if (name.Contains("pause menu") && !reasons.Any(r => r?.ToString() == "pause_menu")) reasons.Add("pause_menu");
        }
        if (dialogs.Any(d => d?["source"]?.ToString() == "covering_window") && !reasons.Any(r => r?.ToString() == "window_blocking")) reasons.Add("window_blocking");
        return reasons;
    }

    internal static string Next(JsonArray dialogs)
    {
        if (dialogs.Any(IsRelease)) return ReleaseNext;
        var forms = dialogs.Where(IsForm).ToArray();
        var formText = forms.Length == 0 ? "" : $"Open form(s) {string.Join(", ", forms.Select(d => $"'{d?["name"]}' (scope {d?["scope"]})"))} keep game time paused; they are not popups and have no dialog choice. Close them with game window-close NAME (or finish them) before advancing; they are never closed automatically. ";
        var covering = dialogs.Where(d => d?["source"]?.ToString() == "covering_window").ToArray();
        var coveringText = covering.Length == 0 ? "" : $"Window(s) {string.Join(", ", covering.Select(d => $"'{d?["name"]}' (scope {d?["scope"]})"))} cover the target; they are forms, editors or drafts, not dialogs, and were not closed: finish them or close them with game window-close SCOPE if safe. ";
        var others = dialogs.Where(d => d?["scope"]?.ToString() != "PauseMenu" && !IsForm(d) && d?["source"]?.ToString() != "covering_window").ToArray();
        var pauseMenu = dialogs.Any(d => d?["scope"]?.ToString() == "PauseMenu") ? "The native Pause Menu is open: close it with game window-close \"Pause Menu\" (time stays paused). " : "";
        if (others.Length == 0) return "Time is paused. " + formText + coveringText + pauseMenu + "Nothing was chosen automatically.";
        var names = string.Join(", ", others.Select(d => $"'{d?["name"]}' (scope {d?["scope"]})"));
        return $"Time is paused with native dialog(s) {names} open. {formText}{coveringText}{pauseMenu}Read them with game dialog-read and resolve with game dialog-choose CHOICE --dialog NAME (Research Completed: 'Select New Research' keeps time paused; informational popups: their Close/OK choice). Nothing was chosen automatically.";
    }

    // Compact view of the open release form, or null when none is open.
    internal static async Task<JsonObject?> ReleaseForm(HttpClient client, string session, int timeout)
    {
        var reply = await GameWatch.Call(client, "game.projects-release-read", "", null, session, timeout);
        if (!GameWatch.TryData(reply, out var data)) return null;
        var draft = data!["draft"];
        var name = draft?["name"]?.ToString() ?? "";
        return new JsonObject
        {
            ["name"] = name, ["price"] = draft?["price"]?.DeepClone(), ["sellOnMarket"] = draft?["sellOnMarket"]?.DeepClone(), ["availableForContracts"] = draft?["availableForContracts"]?.DeepClone(),
            ["unitCost"] = data["cpu"]?["unitCost"]?.DeepClone(), ["marketBudgets"] = data["marketBudgets"]?.DeepClone(), ["releaseEnabled"] = data["release"]?["enabled"]?.DeepClone(),
            ["commit"] = $"game projects-release \"{name}\" --price P [--sell-on-market true|false] [--available-for-contracts true|false]"
        };
    }
}
