using System;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace ProcessorTycoonMod;

internal static class GameSessionState
{
    internal static JObject Read(GenericUi native)
    {
        var scene = native.Scene;
        var inGame = scene == "Game Scene";
        var date = inGame ? GameUi.Texts(new GameUi(native).Read("DateAndTimeControl")).Where(t => ((string?)t["context"] ?? "").EndsWith("/DateAndTimeControl", StringComparison.Ordinal)).Select(t => (string?)t["text"]).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t)) : null;
        var state = scene == "Main Menu Scene" ? "main_menu" : inGame ? date != null ? "campaign" : "gameplay_not_ready" : "scene_not_ready";
        var next = state switch
        {
            "main_menu" => "No campaign is loaded. Use game session-new-preview to inspect/create a new campaign, or game save-list then game save-load EXACT_SAVE. Campaign data cannot be read from the main menu.",
            "campaign" => "Use game desktop-read for current money/date, then the relevant Game API. Native dialogs may still block actions; use game dialog-read when needed.",
            _ => "The gameplay UI is not ready in this scene. Check status again after the scene transition; do not repeat a pending new-game/load command. If persistent, use observe for diagnosis."
        };
        var fate = state == "campaign" ? BalanceSnapshot.Bankrupt() : null;
        if (fate != null) next = (string)fate["note"]!;
        return new JObject { ["state"] = state, ["companyBankrupt"] = fate != null, ["scene"] = scene, ["campaignLoaded"] = state == "campaign" ? new JValue(true) : state == "main_menu" ? new JValue(false) : JValue.CreateNull(), ["canReadCampaign"] = state == "campaign", ["dateDisplay"] = date, ["reason"] = state == "campaign" ? null : next, ["next"] = next };
    }

    internal static void RequireCampaign(GenericUi native, string command)
    {
        if (!command.StartsWith("game.", StringComparison.Ordinal)) return;
        // Session/navigation/settings also operate from the menu. Everything else needs a party.
        if (command.StartsWith("game.session-", StringComparison.Ordinal) || command.StartsWith("game.settings-", StringComparison.Ordinal) || command.StartsWith("game.window-", StringComparison.Ordinal) || command.StartsWith("game.dialog-", StringComparison.Ordinal) || command is "game.save-list" or "game.save-load" or "game.save-delete" or "game.cpu-schema" or "game.time-cancel") return;
        var state = Read(native);
        if ((bool?)state["canReadCampaign"] == true) return;
        throw new AgentError((string?)state["state"] == "main_menu" ? "no_campaign_loaded" : "game_not_ready", $"{command}: {(string?)state["state"]} (scene: {native.Scene}). {state["next"]} No gameplay action was dispatched.");
    }
}
