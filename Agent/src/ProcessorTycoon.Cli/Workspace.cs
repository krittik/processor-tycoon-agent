using System.Text.Json.Nodes;

// Window hygiene for CLI compositions. Only read-only workspace views are ever closed here: closing them discards
// no draft, form, dialog, setting or decision. CPU drafts/editors, the release form, negotiations, settings, saves,
// the Pause Menu and every native dialog are never touched. Manage Lines can hold a pending expansion/sale draft,
// so it is closed only when the plugin reports that the agent opened it (window-list agentOpened, plugin >= 0.3.9).
internal static class Workspace
{
    internal static readonly string[] Views = { "AnalysisWindow", "ResearchTreeWindow", "ProductionWindow", "FactoryManagementWindow", "InspectorWindow", "ContractWindow", "ContractManagerWindow", "BusinessWindow", "EmailWindow", "StatisticsWindow" };
    private static readonly string[] agentOnlyViews = { "FactoryManagementWindow" };
    private static readonly string[] chrome = { "DESKTOP -> Bottom", "DESKTOP -> Main", "DesktopButtons", "DesktopWallpaper", "Side Window", "Side Window (dark)", "TopBar", "SpeedButtons" };

    // Visible windows plus (plugin >= 0.3.9) open windows hidden completely behind others (`covered: true`).
    internal static async Task<JsonArray?> Visible(HttpClient client, string session, int timeout)
    {
        var reply = await GameWatch.Call(client, "game.window-list", "", null, session, timeout);
        if (!GameWatch.TryData(reply, out var data) || data!["visibleWindows"] is not JsonArray visible) return null;
        var result = new JsonArray(visible.Select(w => w?.DeepClone()).ToArray());
        foreach (var covered in data["coveredWindows"]?.AsArray() ?? new JsonArray()) if (covered != null) result.Add(covered.DeepClone());
        return result;
    }

    internal static HashSet<string> Scopes(JsonArray? windows) => (windows ?? new JsonArray()).Select(w => w?["scope"]?.ToString() ?? (w?["name"]?.ToString() == "Pause Menu" ? "PauseMenu" : null)).Where(s => s != null).Select(s => s!).ToHashSet(StringComparer.Ordinal);

    internal static JsonObject? Window(JsonArray? windows, string scope) => (windows ?? new JsonArray()).OfType<JsonObject>().FirstOrDefault(w => w["scope"]?.ToString() == scope);
    internal static bool AgentOpened(JsonObject? window) => window?["agentOpened"]?.GetValue<bool>() == true;

    // Views a tidy may close: every read-only view, except agent-only views (Manage Lines) the agent did not open.
    private static bool Closable(JsonObject window)
    {
        var scope = window["scope"]?.ToString() ?? "";
        return Views.Contains(scope) && (!agentOnlyViews.Contains(scope) || AgentOpened(window));
    }

    // Visible windows that are neither desktop chrome nor closable views: drafts, dialogs, menus. Reported, never closed.
    internal static JsonArray Others(JsonArray? windows) => new((windows ?? new JsonArray()).OfType<JsonObject>().Where(w => !chrome.Contains(w["scope"]?.ToString() ?? "") && !Closable(w)).Select(w =>
    {
        var scope = w["scope"]?.ToString();
        var reason = w["dialog"]?.GetValue<bool>() == true ? "native dialog: resolve with dialog-read / dialog-choose" : agentOnlyViews.Contains(scope ?? "") ? "may hold a pending draft and was not opened by the agent" : scope == "PauseMenu" || w["name"]?.ToString() == "Pause Menu" ? "menu: close with window-close \"Pause Menu\"" : w["blocksTime"]?.GetValue<bool>() == true ? $"form or draft that keeps game time paused while open (not a dialog): finish it or close it with window-close \"{w["name"]}\"" : "form, editor, draft or unknown window: never closed automatically";
        var item = new JsonObject { ["name"] = w["name"]?.DeepClone() ?? scope, ["scope"] = scope, ["reason"] = reason };
        if (w["covered"]?.GetValue<bool>() == true) item["covered"] = true;
        if (w["blocksTime"]?.GetValue<bool>() == true) item["blocksTime"] = true;
        return (JsonNode?)item;
    }).ToArray());

    // Closes the given scopes frontmost-first by retrying: a covered window becomes closable once its cover is gone.
    internal static async Task<JsonObject> Close(HttpClient client, string session, int timeout, IEnumerable<string> scopes)
    {
        var pending = scopes.Distinct(StringComparer.Ordinal).ToList();
        var closed = new JsonArray();
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var pass = 0; pass < 4 && pending.Count > 0; pass++)
        {
            var progress = false;
            foreach (var scope in pending.ToArray())
            {
                var reply = await GameWatch.Call(client, "game.window-close", scope, null, session, timeout);
                if (GameWatch.TryData(reply, out var data) && data!["closed"]?.GetValue<bool>() == true) { closed.Add(scope); pending.Remove(scope); errors.Remove(scope); progress = true; }
                else errors[scope] = reply.At("operation", "error", "message").Str() ?? reply.At("error", "message").Str() ?? "close not verified";
            }
            if (!progress) break;
        }
        var failed = new JsonObject();
        foreach (var scope in pending) failed[scope] = errors.GetValueOrDefault(scope, "not attempted");
        return new JsonObject { ["closed"] = closed, ["failed"] = failed };
    }

    // Closes every visible closable read-only view except the named ones. Returns what was closed, what failed and what was kept.
    internal static Task<JsonObject> Tidy(HttpClient client, string session, int timeout, params string[] except) => Tidy(client, session, timeout, null, except);

    internal static async Task<JsonObject> Tidy(HttpClient client, string session, int timeout, JsonArray? visible, params string[] except)
    {
        visible ??= await Visible(client, session, timeout);
        if (visible == null) return new JsonObject { ["closed"] = new JsonArray(), ["failed"] = new JsonObject(), ["error"] = "window-list unavailable" };
        var stale = visible.OfType<JsonObject>().Where(w => Closable(w) && !except.Contains(w["scope"]?.ToString())).Select(w => w["scope"]!.ToString()).ToArray();
        var result = stale.Length == 0 ? new JsonObject { ["closed"] = new JsonArray(), ["failed"] = new JsonObject() } : await Close(client, session, timeout, stale);
        result["keptOpen"] = Others(visible);
        return result;
    }

    // Closes views that became visible since `before` (windows this composition opened).
    internal static async Task<JsonObject?> CloseOpenedSince(HttpClient client, string session, int timeout, HashSet<string> before)
    {
        var now = Scopes(await Visible(client, session, timeout));
        var opened = now.Where(scope => Views.Contains(scope) && !before.Contains(scope)).ToArray();
        return opened.Length == 0 ? null : await Close(client, session, timeout, opened);
    }

    internal static bool Any(JsonObject? cleanup) => cleanup != null && (cleanup["closed"]?.AsArray().Count > 0 || cleanup["failed"]?.AsObject().Count > 0);
}
