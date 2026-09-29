using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;

namespace ProcessorTycoonMod;

// Soft integration with the separate Processor Tycoon Multiplayer mod (GUID processortycoon.multiplayer) through its
// public static API ProcessorTycoonMp.Api.MpApi, found by reflection so this plugin runs with or without it.
internal static class MultiplayerInterop
{
    private const string ApiTypeName = "ProcessorTycoonMp.Api.MpApi";
    private static Type? api;
    private static bool resolved;

    private static Type? Api
    {
        get
        {
            if (resolved) return api;
            resolved = true;
            api = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(ApiTypeName, false)).FirstOrDefault(t => t != null);
            return api;
        }
    }

    public static bool Installed => Api != null;
    public static bool Active => Get("IsActive") is true;
    public static bool IsHost => Get("IsHost") is true;
    // False only on a multiplayer peer: the host controls game time for everyone.
    public static bool CanControlTime => !Active || Get("CanControlTime") is not false;
    // This player's company went bankrupt during the session (the Multiplayer mod keeps the player as a spectator).
    public static bool LocalBankrupt => Active && Get("LocalBankrupt") is true;

    public static JObject? Status()
    {
        if (Api?.GetMethod("Status", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, null) is not IDictionary status) return null;
        var result = new JObject();
        foreach (DictionaryEntry entry in status) result[entry.Key.ToString()!] = entry.Value == null ? JValue.CreateNull() : JToken.FromObject(entry.Value);
        return result;
    }

    // Chat lines, numbered (older Multiplayer versions have no chat reading: 0 and none).
    public static int ChatLast => Get("ChatLast") as int? ?? 0;

    public static JArray ChatSince(int since)
    {
        var result = new JArray();
        if (Api?.GetMethod("ChatSince", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, new object[] { since }) is not IEnumerable lines) return result;
        foreach (IDictionary line in lines)
        {
            var item = new JObject();
            foreach (DictionaryEntry entry in line) item[entry.Key.ToString()!] = entry.Value == null ? JValue.CreateNull() : JToken.FromObject(entry.Value);
            result.Add(item);
        }
        return result;
    }

    public static JObject Require() => Status() ?? throw new AgentError("multiplayer_not_installed", "The Processor Tycoon Multiplayer mod is not installed in this game (BepInEx/plugins/ProcessorTycoon.Mp).");

    public static void Call(string method, params object[] arguments)
    {
        Require();
        var target = Api!.GetMethod(method, BindingFlags.Public | BindingFlags.Static) ?? throw new AgentError("multiplayer_api_mismatch", $"The installed Multiplayer mod has no {method}; update it.");
        try { target.Invoke(null, arguments); }
        catch (TargetInvocationException exception) { throw new AgentError("multiplayer_error", exception.InnerException?.Message ?? exception.Message); }
    }

    public static void RequireTimeControl(string command)
    {
        if (!CanControlTime)
            throw new AgentError("multiplayer_host_controls_time", $"{command}: in a multiplayer session only the host controls game time. Days advance when the host runs the clock; use game time-read, notifications or game time-advance (which then only waits).");
    }

    private static object? Get(string property) => Api?.GetProperty(property, BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
}
