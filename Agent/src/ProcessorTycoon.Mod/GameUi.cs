using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace ProcessorTycoonMod;

// Modules describe native UI workflows; Plugin owns scheduling, pause, input conflicts and feed.
internal interface IGameModule
{
    string[] Commands { get; }
    void Validate(Request request);
    IEnumerable<Request> Prepare(Request request);
    JObject Result(Request request);
}

internal sealed class GameUi
{
    public GenericUi Native { get; }
    public GameUi(GenericUi native) => Native = native;
    public JObject Read(string scope) => scope == "DesktopButtons" ? Catalog(scope) : JObject.FromObject(Native.Observe(scope, 0, 200), JsonSerializer.Create(Wire.Settings));
    // For a mapped, already-open player-browsable catalog, not arbitrary inactive game objects.
    public JObject Catalog(string scope)
    {
        var view = JObject.FromObject(Native.ObserveCatalog(scope), JsonSerializer.Create(Wire.Settings));
        foreach (var node in Controls(view)) node.AddAnnotation(Native);
        return view;
    }
    public static JObject[] Controls(JObject view) => view["controls"]!.OfType<JObject>().ToArray();
    public static JObject[] Texts(JObject view) => view["texts"]!.OfType<JObject>().ToArray();
    public static JObject One(IEnumerable<JObject> candidates, string description)
    {
        var matches = candidates.ToArray();
        if (matches.Length == 0) throw new AgentError("not_found", $"No exposed {description}. Open its window or resolve the foreground dialog.");
        if (matches.Length != 1) throw new AgentError("ambiguous_target", $"Several controls match {description}; use an exact name from the current result.");
        return matches[0];
    }
    public static JObject Find(JObject view, string name, string? role = null) => One(Controls(view).Where(c => (string?)c["name"] == name && (role == null || (string?)c["role"] == role)), name);
    public JObject? CpuDraftShortcut(string window)
    {
        if (window is not ("Inspector" or "Analysis")) return null;
        var buttons = Controls(Read(CpuGameApi.Scope)).Where(c => (string?)c["role"] == "button" && (string?)c["label"] == window).ToArray();
        return buttons.Length == 0 ? null : One(buttons, $"CPU draft {window} shortcut");
    }
    public static Request Action(string command, JObject node, JToken? value = null)
    {
        // Covered targets still go through native reachability validation, which names the actual blocker.
        if (node["blockedReason"] != null && (string?)node["blockedReason"] != "covered_or_not_raycastable") throw new AgentError("not_interactable", $"{node["label"]}: {node["blockedReason"]}. Resolve the native disabled/covered state first.");
        if (node.Annotation<GenericUi>() is GenericUi catalogOwner)
        {
            var step = NativeStep((string)node["label"]!, () => catalogOwner.ActCatalog((string)node["handle"]!, command, value));
            step.NativeVisualHandled = true;
            return step;
        }
        return new Request { Command = command, Target = node["handle"]!.Value<string>()!, Value = value };
    }
    public static Request Click(JObject node) => Action("ui.click", node);
    public static Request Set(JObject node, JToken value) => Action("ui.set", node, value);
    public static Request Select(JObject node, string value) => Action("ui.select", node, new JValue(value));
    public static Request NativeStep(string label, Action action) => new Request { Command = "native.action", Target = label, NativeAction = action };
    public static Request NativeStep(string label, Action action, Component? visualTarget, bool visualClick = false) => new Request { Command = "native.action", Target = label, NativeAction = action, NativeVisualTarget = visualTarget, NativeVisualClick = visualClick };
    public static void Parameters(Request request, params string[] keys)
    {
        foreach (var p in request.Parameters?.Properties() ?? Enumerable.Empty<JProperty>()) if (!keys.Contains(p.Name)) throw new AgentError("unsupported_parameter", $"Unknown parameter '{p.Name}' for {request.Command}. No input dispatched.");
    }
    public static string RequiredTarget(Request request)
    {
        if (string.IsNullOrWhiteSpace(request.Target)) throw new AgentError("invalid_request", $"{request.Command} requires an exact target name from a current list.");
        return request.Target;
    }
    public static JObject Compact(JObject view)
    {
        var fields = new JArray(Controls(view).Where(c => (string?)c["role"] is "input" or "select" or "toggle" or "slider").Select(c =>
        {
            var field = new JObject { ["name"] = c["name"]!.DeepClone(), ["label"] = c["label"]!.DeepClone(), ["type"] = c["role"]!.DeepClone() };
            foreach (var key in new[] { "value", "displayValue", "options", "blockedReason" }) if (c[key] != null) field[key] = c[key]!.DeepClone();
            if ((string?)c["role"] == "slider") field["valueUnit"] = "raw-ui-value";
            return field;
        }));
        return new JObject
        {
            ["method"] = "game", ["scene"] = view["scene"]!.DeepClone(), ["scope"] = view["scope"]!.DeepClone(),
            ["visibleOnly"] = true, ["texts"] = new JArray(Texts(view).Select(t => t["text"]!.DeepClone())), ["fields"] = fields,
            ["actions"] = new JArray(Controls(view).Where(c => (string?)c["role"] == "button").Select(c =>
            {
                var action = new JObject { ["name"] = c["name"]!.DeepClone(), ["label"] = c["label"]!.DeepClone(), ["enabled"] = c["blockedReason"] == null };
                if (c["blockedReason"] != null) action["blockedReason"] = c["blockedReason"]!.DeepClone();
                return action;
            })),
            ["truncated"] = (bool?)view["more"] ?? false, ["precision"] = "display-rounded"
        };
    }
}
