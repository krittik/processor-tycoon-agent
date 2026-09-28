using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ProcessorTycoonMod;

// Capture only text already prepared for native on-screen popups, never their simulation arguments.
internal sealed class GameNotifications : IDisposable
{
    private sealed class Seen { public string Text = ""; }
    private readonly ConditionalWeakTable<MonoBehaviour, Seen> seen = new();
    private readonly Queue<JObject> history = new();
    private readonly Dictionary<string, long> cursors = new();
    private readonly Dictionary<string, long> lastUse = new();
    private long useCounter;
    private readonly Harmony patches = new("local.processortycoon.notifications");
    private static GameNotifications? active;
    private long cursor;
    private readonly string streamId = Guid.NewGuid().ToString("N");
    private string? captureError;
    private const int Capacity = 512;

    public void Initialize()
    {
        active = this;
        try
        {
            var type = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("ProcessorTycoon.PopupSystem.Popup", false)).FirstOrDefault(t => t != null) ?? throw new MissingMemberException("Native Popup type is unavailable.");
            foreach (var field in new[] { "messageText", "secondaryText" }) if (type.GetField(field, BindingFlags.NonPublic | BindingFlags.Instance) == null) throw new MissingFieldException(type.FullName, field);
            var postfix = new HarmonyMethod(typeof(GameNotifications).GetMethod(nameof(Capture), BindingFlags.NonPublic | BindingFlags.Static));
            foreach (var name in new[] { "VisibleAnimation", "AddCompleted" })
            {
                var method = type.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance) ?? throw new MissingMethodException(type.FullName, name);
                patches.Patch(method, postfix: postfix);
            }
        }
        catch (Exception error) { captureError = error.Message; }
    }

    private static void Capture(MonoBehaviour __instance)
    {
        try { active?.Record(__instance); }
        catch (Exception error) { if (active != null) active.captureError = error.Message; }
    }

    private void Record(MonoBehaviour popup)
    {
        if (!popup.gameObject.scene.IsValid() || !popup.gameObject.activeInHierarchy) return;
        string Text(string field) => Regex.Replace((popup.GetType().GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(popup) as TMP_Text)?.text ?? "", "<[^>]+>", "").Trim();
        var message = Text("messageText");
        var detail = Text("secondaryText");
        if (message.Length == 0 && detail.Length == 0) return;
        var signature = message + "\n" + detail;
        var previous = seen.GetValue(popup, _ => new Seen());
        if (previous.Text == signature) return;
        previous.Text = signature;
        string? date = null;
        try { date = (string?)TimeAdvanceController.ReadClock()["date"]; } catch { }
        history.Enqueue(new JObject
        {
            ["id"] = ++cursor, ["gameDate"] = date, ["atUtc"] = DateTime.UtcNow, ["scene"] = SceneManager.GetActiveScene().name,
            ["source"] = "native_popup", ["type"] = message.EndsWith(" has released a new CPU!", StringComparison.Ordinal) ? "competitor_cpu_released" : "game_notification",
            ["text"] = message, ["detail"] = detail
        });
        while (history.Count > Capacity) history.Dequeue();
    }

    public JObject Read(long since) => new()
    {
        ["streamId"] = streamId,
        ["items"] = new JArray(history.Where(item => (long)item["id"]! > since).Select(item => item.DeepClone())), ["cursor"] = cursor,
        ["historyTruncated"] = history.Count > 0 && since < (long)history.Peek()["id"]! - 1,
        ["captureAvailable"] = captureError == null, ["captureError"] = captureError,
        ["scope"] = "Native transient popup text emitted since this mod instance started; disabled/suppressed notifications and blocking dialogs are not synthesized. No causal attribution."
    };

    public JObject Wrap(Request request, object response)
    {
        var result = JObject.FromObject(response);
        if (request.Command == "notifications") return result;
        var since = cursors.TryGetValue(request.Session, out var previous) ? previous : 0;
        result["notifications"] = Read(since);
        // Load/new-game replies are skipped: the native finance panel is not yet populated when loading completes (it read
        // Research $0/m), so advisories are first evaluated on the next command.
        var loadReply = request.Command is "game.save-load" or "game.session-new-start" || (string?)(result["operation"] as JObject)?["command"] is "game.save-load" or "game.session-new-start";
        if (request.Command is not ("status" or "guide" or "capabilities") && !loadReply) { var advisories = CompanyAdvisories.Due(); if (advisories != null) result["advisories"] = advisories; }
        // Unthrottled: every reply says when the company is bankrupt, so a client cannot keep playing a finished company.
        if (request.Command is not ("guide" or "capabilities") && BalanceSnapshot.Bankrupt() is JObject fate) result["companyBankrupt"] = fate;
        // Evict the least recently used session (not the oldest inserted), so an active client never loses its cursor and receives the whole backlog.
        if (!cursors.ContainsKey(request.Session) && cursors.Count >= 64) { var stale = lastUse.OrderBy(pair => pair.Value).First().Key; cursors.Remove(stale); lastUse.Remove(stale); }
        cursors[request.Session] = cursor;
        lastUse[request.Session] = ++useCounter;
        return result;
    }

    public void Dispose() { patches.UnpatchSelf(); if (active == this) active = null; }
}
