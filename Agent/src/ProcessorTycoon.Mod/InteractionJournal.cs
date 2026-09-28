using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.EventSystems;

namespace ProcessorTycoonMod;

internal sealed class InteractionJournal
{
    private sealed class Baseline { public JObject Data = null!; }
    private readonly Queue<JObject> events = new();
    private readonly Dictionary<string, Baseline> baselines = new();
    private readonly Dictionary<string, long> sessionCursors = new();
    private readonly List<RaycastResult> hits = new();
    private long cursor;
    private float lastKeyboard;
    private JObject? lastPlayerInput;
    public long PlayerInputCursor => lastPlayerInput?["id"]?.Value<long>() ?? 0;

    public void SamplePlayerInput()
    {
        if (!Application.isFocused || EventSystem.current == null) return;
        var button = Input.GetMouseButtonDown(0) ? 0 : Input.GetMouseButtonDown(1) ? 1 : Input.GetMouseButtonDown(2) ? 2 : -1;
        if (button >= 0)
        {
            hits.Clear();
            EventSystem.current.RaycastAll(new PointerEventData(EventSystem.current) { position = Input.mousePosition }, hits);
            var target = hits.Count > 0 ? GenericUi.InputTarget(hits[0].gameObject) : null;
            lastPlayerInput = Add("player_input", "pointer_down", new { button, target });
        }
        else if (Input.anyKeyDown && Time.unscaledTime - lastKeyboard > .5f)
        {
            // Record input activity, not typed characters or clipboard contents.
            lastKeyboard = Time.unscaledTime;
            lastPlayerInput = Add("player_input", "keyboard_activity", new { target = GenericUi.InputTarget(EventSystem.current.currentSelectedGameObject) });
        }
        if (Input.mouseScrollDelta.sqrMagnitude > 0) lastPlayerInput = Add("player_input", "scroll", new { delta = Input.mouseScrollDelta.y });
    }

    public JObject Add(string source, string kind, object detail)
    {
        var item = JObject.FromObject(new { id = ++cursor, atUtc = DateTime.UtcNow, frame = Time.frameCount, source, kind, detail });
        events.Enqueue(item);
        while (events.Count > 128) events.Dequeue();
        return item;
    }

    public object Since(long since) => new { cursor, historyTruncated = events.Count > 0 && since < events.Peek()["id"]!.Value<long>() - 1, events = events.Where(e => e["id"]!.Value<long>() > since).ToArray() };
    public object Status => new { cursor, lastPlayerInput, applicationFocused = Application.isFocused };

    public object? Observe(Request request, object observation)
    {
        var data = JObject.FromObject(observation);
        var key = request.Session + "\n" + request.Scope + "\n" + request.Offset + ":" + request.Limit;
        baselines.TryGetValue(key, out var previous);
        var changes = new List<object>();
        if (request.Changes && previous != null && previous.Data["scene"]?.ToString() == data["scene"]?.ToString())
        {
            Diff(previous.Data, data, "controls", changes);
            Diff(previous.Data, data, "texts", changes);
        }
        var since = sessionCursors.TryGetValue(request.Session, out var seen) ? seen : cursor;
        var playerInputs = events.Where(e => e["id"]!.Value<long>() > since && e["source"]!.ToString() == "player_input").ToArray();
        if (!sessionCursors.ContainsKey(request.Session) && sessionCursors.Count >= 24) sessionCursors.Remove(sessionCursors.Keys.First());
        sessionCursors[request.Session] = cursor;
        var input = playerInputs.Length > 0 ? new { count = playerInputs.Length, last = playerInputs.Last(), meaning = "Local player input observed; re-check the affected UI. This does not attribute every subsequent change to the player." } : null;
        var result = new
        {
            baselineSnapshot = previous?.Data["snapshot"], snapshot = data["snapshot"],
            sceneChanged = previous != null && previous.Data["scene"]?.ToString() != data["scene"]?.ToString(),
            changes = changes.Take(40).ToArray(), omittedChanges = Math.Max(0, changes.Count - 40),
            playerInput = input, eventCursor = cursor,
            attribution = "UI differences are unattributed. Use events for recorded input/commands, not inferred causality."
        };
        if (!baselines.ContainsKey(key) && baselines.Count >= 24) baselines.Remove(baselines.Keys.First());
        baselines[key] = new Baseline { Data = data };
        return request.Changes ? result : input == null ? null : (object)new { playerInput = input, eventCursor = cursor };
    }

    private static void Diff(JObject before, JObject after, string collection, List<object> changes)
    {
        var oldItems = Index(before[collection] as JArray, collection);
        var newItems = Index(after[collection] as JArray, collection);
        foreach (var pair in newItems)
        {
            if (!oldItems.TryGetValue(pair.Key, out var old)) { changes.Add(new { kind = "appeared", collection, after = Summary(pair.Value, collection) }); continue; }
            var previous = Summary(old, collection);
            var current = Summary(pair.Value, collection);
            if (!JToken.DeepEquals(previous, current)) changes.Add(new { kind = "changed", collection, before = previous, after = current });
        }
        foreach (var pair in oldItems.Where(p => !newItems.ContainsKey(p.Key))) changes.Add(new { kind = "disappeared", collection, before = Summary(pair.Value, collection) });
    }

    private static Dictionary<string, JObject> Index(JArray? items, string collection)
    {
        var result = new Dictionary<string, JObject>();
        foreach (var item in (items ?? new JArray()).OfType<JObject>())
        {
            var stem = collection == "controls" ? item["id"]!.ToString() : item["group"] + ":" + item["context"];
            var key = stem;
            for (var suffix = 1; result.ContainsKey(key); suffix++) key = stem + ":" + suffix;
            result[key] = item;
        }
        return result;
    }

    private static JObject Summary(JObject item, string collection)
    {
        var fields = collection == "controls" ? new[] { "id", "label", "context", "value", "displayValue", "blockedReason" } : new[] { "group", "context", "text" };
        var result = new JObject();
        foreach (var field in fields) if (item[field] != null && item[field]!.Type != JTokenType.Null) result[field] = item[field]!.DeepClone();
        return result;
    }
}
