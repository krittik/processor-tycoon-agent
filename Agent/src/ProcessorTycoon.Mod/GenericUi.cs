using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace ProcessorTycoonMod;

internal sealed class UiNode
{
    public string handle = "";
    public string id = "";
    public string role = "";
    public string label = "";
    public string name = "";
    public string context = "";
    public string window = "";
    public string? group;
    public string[]? details;
    public object? value;
    public string[] actions = Array.Empty<string>();
    public string[]? options;
    public object? range;
    public string? displayValue;
    public string? blockedReason;
    public bool? catalog;
    internal float X;
    internal float Y;
}

internal sealed class UiTarget
{
    public Component Component = null!;
    public string Fingerprint = "";
    public string Identity = "";
    public UiNode Node = null!;
    public long Seen;
}

internal sealed class GenericUi
{
    private readonly Dictionary<string, UiTarget> targets = new();
    private readonly Dictionary<int, string> currentHandles = new();
    private readonly List<RaycastResult> hits = new();
    private readonly string instance = Guid.NewGuid().ToString("N").Substring(0, 8);
    private long sequence;
    private long observation;
    private Component? hovered;
    // Unity-thread-only, synchronous read context. Generic observations never enable this mode.
    private static bool catalogRead;
    private static readonly Regex RichText = new("<[^>]+>", RegexOptions.Compiled);
    public string Scene => SceneManager.GetActiveScene().name;
    internal Action<Vector2?, bool>? VisualAction;

    internal bool TryCloseWorkspace(Button close)
    {
        if (close == null || !Visible(close, out _) || !Enabled(close) || !TryPoint(close, out var point)) return false;
        VisualAction?.Invoke(point, true);
        Click(close, new PointerEventData(EventSystem.current) { position = point, button = PointerEventData.InputButton.Left, eligibleForClick = true, clickCount = 1 });
        return true;
    }

    internal bool TryVisualPoint(Component? component, out Vector2 point)
    {
        point = default;
        return component != null && component.gameObject.scene.IsValid() && component.gameObject.activeInHierarchy && Visible(component, out _) && Enabled(component) && TryPoint(component, out point);
    }

    internal object ObserveCatalog(string scope)
    {
        if (string.IsNullOrWhiteSpace(scope)) throw new AgentError("invalid_request", "Catalog reads require an explicit player-browsable window scope.");
        var previous = catalogRead;
        catalogRead = true;
        try { return Observe(scope, 0, 10000); }
        finally { catalogRead = previous; }
    }

    internal void ActCatalog(string handle, string command, JToken? value)
    {
        var target = Resolve(handle);
        var component = target.Component;
        var visible = Visible(component, out _);
        Vector2 point = default;
        var visiblePoint = visible && TryPoint(component, out point);
        Vector2? visualPosition = visiblePoint ? point : null;
        // An on-screen covered target is not an offscreen catalog item. Respect foreground UI.
        if (visible && !visiblePoint && !DesktopWorkspaceOverlap(component)) throw OcclusionError(component, "The catalog target is covered by another window. Resolve it before acting.");
        var window = Surface(component);
        var header = window.GetComponentsInChildren<Button>().FirstOrDefault(b => b.transform.parent?.name == "TopBar" && b.name.Contains("Close"));
        if (header != null && !TryPoint(header, out _)) throw OcclusionError(header, "The catalog window is blocked by foreground UI.");
        var previous = catalogRead;
        catalogRead = true;
        try
        {
            if (!Visible(component, out _) || !Enabled(component)) throw new AgentError("not_interactable", "The game disabled or hid this catalog item.");
            if (target.Fingerprint != Fingerprint(component)) throw new AgentError("context_changed", "The catalog item changed; re-read before acting.");
            VisualAction?.Invoke(visualPosition, command == "ui.click");
            var pointer = new PointerEventData(EventSystem.current) { position = ScreenRect((RectTransform)component.transform, CanvasCamera(component)).center, button = PointerEventData.InputButton.Left, eligibleForClick = true, clickCount = 1 };
            switch (command)
            {
                case "ui.click":
                    if (!Describe(component).actions.Contains("click")) throw new AgentError("unsupported_action", "This catalog item is not clickable.");
                    Click(component, pointer);
                    break;
                case "ui.set":
                    if (component is not Slider && component is not Toggle) throw new AgentError("unsupported_action", "Semantic catalog set supports sliders/toggles only; open the native editor for text input.");
                    ValidateValue(component, value);
                    Set(component, value!, pointer);
                    break;
                case "ui.select":
                    var index = ResolveOption(component, value);
                    if (component is TMP_Dropdown tmp) { tmp.value = index; tmp.RefreshShownValue(); tmp.Hide(); }
                    else if (component is Dropdown dropdown) { dropdown.value = index; dropdown.RefreshShownValue(); dropdown.Hide(); }
                    break;
                default: throw new AgentError("unsupported_action", "Catalog adapters support selection/editing, not arbitrary UI commands.");
            }
        }
        finally { catalogRead = previous; }
    }

    public object Observe(string scope, int offset, int limit)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        observation++;
        var nodes = new List<UiNode>();
        var components = new List<Component>();
        var readableComponents = new List<Component>();
        components.AddRange(Resources.FindObjectsOfTypeAll<Selectable>());
        components.AddRange(Resources.FindObjectsOfTypeAll<ScrollRect>());
        // Custom click targets without a standard Selectable remain discoverable, without game type dependencies.
        components.AddRange(Resources.FindObjectsOfTypeAll<MonoBehaviour>().Where(c => c != null && (c is IPointerClickHandler || c is IPointerEnterHandler || c is IDragHandler || c is IPointerDownHandler) && c.GetComponent<Selectable>() == null));
        var seen = new HashSet<int>();
        foreach (var component in components)
        {
            if (component == null || !MatchesScope(component, scope) || !seen.Add(component.gameObject.GetInstanceID()) || !Readable(component)) continue;
            readableComponents.Add(component);
            nodes.Add(Register(component));
        }

        var textComponents = new List<Component>();
        foreach (var text in Resources.FindObjectsOfTypeAll<TMP_Text>())
        {
            if (!MatchesScope(text, scope) || !Visible(text, out _) || text.GetComponentInParent<Selectable>() != null) continue;
            if (TextContent(text).Length > 0 && !BelongsToSlider(text)) textComponents.Add(text);
        }
        foreach (var text in Resources.FindObjectsOfTypeAll<Text>())
        {
            if (!MatchesScope(text, scope) || !Visible(text, out _) || text.GetComponentInParent<Selectable>() != null) continue;
            if (TextContent(text).Length > 0) textComponents.Add(text);
        }
        var texts = textComponents.GroupBy(TextOwner).SelectMany(TextGroups).Select(group => new
        {
            context = Context(group.First()),
            group = TextGroupId(group.First()),
            text = string.Join(" | ", ReadingOrder(group).Select(TextContent))
        }).OrderBy(t => t.context).ToList();
        nodes = nodes.OrderBy(n => n.context).ThenByDescending(n => Mathf.Round(n.Y / 8)).ThenBy(n => n.X).ToList();
        // Unrelated scoped reads must not expire still-live controls. Bound retained historical handles instead.
        foreach (var key in targets.Where(pair => pair.Value.Component == null).Select(pair => pair.Key).ToArray()) targets.Remove(key);
        if (targets.Count > 4096) foreach (var key in targets.OrderBy(pair => pair.Value.Seen).Take(targets.Count - 4096).Select(pair => pair.Key).ToArray()) targets.Remove(key);
        foreach (var id in currentHandles.Where(pair => !targets.ContainsKey(pair.Value)).Select(pair => pair.Key).ToArray()) currentHandles.Remove(id);
        limit = Math.Max(1, Math.Min(catalogRead ? 10000 : 200, limit));
        offset = Math.Max(0, offset);
        return new
        {
            scene = Scene, method = catalogRead ? "ui-catalog" : "generic", snapshot = observation, scope, offset, limit, elapsedMs = timer.ElapsedMilliseconds,
            totalControls = nodes.Count, totalTexts = texts.Count,
            surfaces = readableComponents.Concat(textComponents).Select(Surface).Distinct().Select(s => new { scope = s.name, title = SurfaceTitle(s) }).ToArray(),
            controls = nodes.Skip(offset).Take(limit).ToArray(), texts = texts.Skip(offset).Take(limit).ToArray(),
            more = offset + limit < Math.Max(nodes.Count, texts.Count),
            guidance = "Handles survive unchanged observations; use returned refreshed handles after edits. displayValue is the native label; slider value/range is raw. Text siblings are grouped, not interpreted."
        };
    }

    private bool DesktopWorkspaceOverlap(Component component)
    {
        // Semantic navigation may open a workspace through its desktop button without closing a player's
        // ordinary workspace. Forms, pause menus and unknown/modal blockers are not bypassed.
        if (component is not Button || !Path(component.transform).Contains("/DesktopButtons/") || !Visible(component, out var rect)) return false;
        hits.Clear();
        EventSystem.current.RaycastAll(new PointerEventData(EventSystem.current) { position = rect.center }, hits);
        var top = hits.FirstOrDefault(hit => hit.gameObject != null && hit.gameObject.GetComponentInParent<AgentOverlayMarker>() == null).gameObject;
        if (top == null) return false;
        var surface = Surface(top.transform).name;
        return new[] { "ResearchTreeWindow", "AnalysisWindow", "InspectorWindow", "ProductionWindow", "ContractWindow", "ContractManagerWindow", "BusinessWindow", "ContractInspectorWindow", "EmailWindow", "CreateHardwareWindow" }.Contains(surface);
    }

    private UiNode Register(Component component)
    {
        var node = Describe(component);
        var fingerprint = Fingerprint(component);
        var id = component.GetInstanceID();
        if (currentHandles.TryGetValue(id, out var existing) && targets.TryGetValue(existing, out var target) && target.Component == component && target.Fingerprint == fingerprint)
        {
            node.handle = existing;
            target.Seen = observation;
            target.Node = node;
            return node;
        }
        node.handle = $"{instance}:{++sequence}";
        targets[node.handle] = new UiTarget { Component = component, Node = node, Fingerprint = fingerprint, Identity = Identity(component), Seen = observation };
        currentHandles[id] = node.handle;
        return node;
    }

    public object Inspect(string handle)
    {
        var target = Resolve(handle);
        if (!Readable(target.Component)) throw new AgentError("stale_target", "The target is no longer exposed. Close the foreground window or re-observe.");
        var node = Register(target.Component);
        var rect = ScreenRect((RectTransform)target.Component.transform, CanvasCamera(target.Component));
        return new { scene = Scene, control = node, path = Path(target.Component.transform), bounds = new { x = rect.x, y = rect.y, width = rect.width, height = rect.height }, unchanged = node.handle == handle };
    }

    public string Validate(Request request)
    {
        var target = Resolve(request.Target);
        if (target.Fingerprint != Fingerprint(target.Component))
        {
            // Live labels next to a standalone control (e.g. the CPU designer's unit cost) refresh every game day, and in a
            // Multiplayer session time keeps running while a form is open. The control itself (path, value, state) is
            // unchanged, so the observation still holds. Controls in repeated rows stay strict: pooled rows can switch items.
            if (RepeatedRow(target.Component.transform) != null || Identity(target.Component) != target.Identity) throw new AgentError("context_changed", "The observed control or its context changed. Observe again before acting.");
            target.Fingerprint = Fingerprint(target.Component);
        }
        var node = Describe(target.Component);
        var action = request.Command.Substring(3);
        if (!node.actions.Contains(action)) throw new AgentError("unsupported_action", $"This control supports: {string.Join(", ", node.actions)}.");
        if (!Visible(target.Component, out _)) throw new AgentError("stale_target", "The target is no longer visible. Observe again.");
        if (action != "hover" && !Enabled(target.Component)) throw new AgentError("not_interactable", "The game has disabled this control.");
        if (!TryPoint(target.Component, out _)) throw OcclusionError(target.Component, "The control is covered or cannot receive pointer input. Navigate or scroll first.");
        if (request.Command == "ui.set") ValidateValue(target.Component, request.Value);
        if (request.Command == "ui.select") ResolveOption(target.Component, request.Value);
        if (request.Command == "ui.scroll" && (!float.TryParse(ScalarText(request.Value), NumberStyles.Float, CultureInfo.InvariantCulture, out var delta) || !IsFinite(delta))) throw new AgentError("invalid_value", "scroll requires a finite mouse-wheel delta.");
        if (request.Command == "ui.drag") DragDelta(request.Value);
        return node.label;
    }

    public void Act(Request request)
    {
        Validate(request);
        var component = Resolve(request.Target).Component;
        TryPoint(component, out var position);
        VisualAction?.Invoke(position, request.Command == "ui.click");
        var pointer = new PointerEventData(EventSystem.current) { position = position, button = PointerEventData.InputButton.Left, eligibleForClick = true, clickCount = 1 };
        if (hovered != null && hovered != component) { ExecuteEvents.Execute(hovered.gameObject, pointer, ExecuteEvents.pointerExitHandler); hovered = null; }
        switch (request.Command)
        {
            case "ui.click": Click(component, pointer); break;
            case "ui.hover":
                if (hovered != null) ExecuteEvents.Execute(hovered.gameObject, pointer, ExecuteEvents.pointerExitHandler);
                hovered = component;
                ExecuteEvents.Execute(component.gameObject, pointer, ExecuteEvents.pointerEnterHandler);
                break;
            case "ui.set": Set(component, request.Value!, pointer); break;
            case "ui.select":
                var index = ResolveOption(component, request.Value);
                EventSystem.current.SetSelectedGameObject(component.gameObject);
                if (component is TMP_Dropdown tmp) { tmp.value = index; tmp.RefreshShownValue(); tmp.Hide(); }
                else if (component is Dropdown dropdown) { dropdown.value = index; dropdown.RefreshShownValue(); dropdown.Hide(); }
                break;
            case "ui.scroll":
                pointer.scrollDelta = new Vector2(0, float.Parse(ScalarText(request.Value), CultureInfo.InvariantCulture));
                ExecuteEvents.Execute(component.gameObject, pointer, ExecuteEvents.scrollHandler);
                break;
            case "ui.focus":
                var receiver = ExecuteEvents.ExecuteHierarchy(component.gameObject, pointer, ExecuteEvents.pointerDownHandler);
                if (receiver != null) ExecuteEvents.Execute(receiver, pointer, ExecuteEvents.pointerUpHandler);
                break;
            case "ui.drag":
                var delta = DragDelta(request.Value);
                pointer.pressPosition = position;
                pointer.pointerDrag = component.gameObject;
                ExecuteEvents.Execute(component.gameObject, pointer, ExecuteEvents.pointerDownHandler);
                ExecuteEvents.Execute(component.gameObject, pointer, ExecuteEvents.initializePotentialDrag);
                ExecuteEvents.Execute(component.gameObject, pointer, ExecuteEvents.beginDragHandler);
                pointer.dragging = true;
                for (var step = 1; step <= 4; step++)
                {
                    pointer.delta = delta / 4;
                    pointer.position = position + delta * step / 4;
                    ExecuteEvents.Execute(component.gameObject, pointer, ExecuteEvents.dragHandler);
                }
                ExecuteEvents.Execute(component.gameObject, pointer, ExecuteEvents.endDragHandler);
                ExecuteEvents.Execute(component.gameObject, pointer, ExecuteEvents.pointerUpHandler);
                break;
        }
    }

    public bool PrepareInput(Request request)
    {
        if (request.Command != "ui.set") return false;
        var component = Resolve(request.Target).Component;
        if (component is not TMP_InputField && component is not InputField) return false;
        EventSystem.current.SetSelectedGameObject(component.gameObject);
        if (component is TMP_InputField tmp) tmp.ActivateInputField();
        else ((InputField)component).ActivateInputField();
        return true;
    }

    public object After(string handle)
    {
        if (!targets.TryGetValue(handle, out var target) || target.Component == null || !target.Component.gameObject.activeInHierarchy) return new { targetClosed = true, scene = Scene };
        var node = Register(target.Component);
        return new { scene = Scene, control = node };
    }

    public UiNode Node(string handle) => Resolve(handle).Node;

    public void VerifyPreparedInput(string handle)
    {
        var component = Resolve(handle).Component;
        if (EventSystem.current == null || EventSystem.current.currentSelectedGameObject != component.gameObject) throw new AgentError("context_changed", "Input focus changed while preparing the edit. Re-observe before typing; the field was not changed.");
    }

    public string RegionSignature(string handle)
    {
        if (!targets.TryGetValue(handle, out var target) || target.Component == null) return Scene;
        var region = target.Component.transform;
        // Observe local native form updates, without depending on any game class or simulation internals.
        for (var level = 0; level < 4 && region.parent != null && region.parent.GetComponent<Canvas>() == null; level++) region = region.parent;
        var scrolls = string.Join("|", region.GetComponentsInChildren<ScrollRect>().Select(s => $"{s.content?.anchoredPosition.x:F1},{s.content?.anchoredPosition.y:F1}"));
        var layout = string.Join("|", region.GetComponentsInChildren<RectTransform>().Take(160).Select(t => $"{t.anchoredPosition.x:F1},{t.anchoredPosition.y:F1},{t.localScale.x:F3},{t.localScale.y:F3}"));
        return Scene + ":" + scrolls + ":" + layout + ":" + string.Join("|", region.GetComponentsInChildren<TMP_Text>().Where(t => Visible(t, out _)).Take(160).Select(t => t.text));
    }

    private UiTarget Resolve(string handle)
    {
        if (Regex.IsMatch(handle, @"^[ug]-?\d+$")) throw new AgentError("not_an_action_handle", "That is an identity/group id, not an action target. Pass control.handle (format instance:number) from observe or inspect; id/group only link records.");
        if (!targets.TryGetValue(handle, out var target) || target.Component == null) throw new AgentError("stale_target", "Unknown/expired handle. Run observe and choose a current handle.");
        return target;
    }

    private UiNode Describe(Component component)
    {
        var node = new UiNode { id = "u" + component.GetInstanceID(), label = Label(component), name = HumanName(component.name), context = Context(component), window = SurfaceTitle(Surface(component)) };
        if (catalogRead) node.catalog = true;
        var row = RepeatedRow(component.transform);
        if (row != null) node.group = "g" + row.GetInstanceID();
        var bounds = ScreenRect((RectTransform)component.transform, CanvasCamera(component));
        node.X = bounds.center.x; node.Y = bounds.center.y;
        if (component is Button)
        {
            var content = ReadingOrder(component.GetComponentsInChildren<TMP_Text>().Where(t => Visible(t, out _) && t.GetComponentInParent<Selectable>() == component)).Select(TextContent).Where(t => t.Length > 0 && !node.label.Contains(t)).Distinct().ToArray();
            if (content.Length > 0) node.details = content;
        }
        var actions = new List<string> { "hover" };
        switch (component)
        {
            case TMP_InputField input:
                node.role = "input"; node.value = input.text;
                node.range = new { characterLimit = input.characterLimit, contentType = input.contentType.ToString(), readOnly = input.readOnly };
                if (!input.readOnly) actions.Add("set");
                break;
            case InputField input:
                node.role = "input"; node.value = input.text;
                node.range = new { characterLimit = input.characterLimit, contentType = input.contentType.ToString(), readOnly = input.readOnly };
                if (!input.readOnly) actions.Add("set");
                break;
            case TMP_Dropdown dropdown:
                node.role = "select"; node.options = dropdown.options.Select(o => o.text).ToArray();
                node.value = dropdown.value; actions.AddRange(new[] { "click", "select" });
                break;
            case Dropdown dropdown:
                node.role = "select"; node.options = dropdown.options.Select(o => o.text).ToArray();
                node.value = dropdown.value; actions.AddRange(new[] { "click", "select" });
                break;
            case Toggle toggle: node.role = "toggle"; node.value = toggle.isOn; actions.AddRange(new[] { "click", "set" }); break;
            case Slider slider:
                node.role = "slider"; node.value = slider.value;
                node.displayValue = string.Join(" | ", SliderTexts(slider).Select(TextContent).Where(text => text != node.label));
                node.range = new { rawMin = slider.minValue, rawMax = slider.maxValue, wholeNumbers = slider.wholeNumbers, units = "raw-ui-value" };
                actions.Add("set"); break;
            case Scrollbar scrollbar:
                node.role = "scrollbar"; node.value = scrollbar.value; node.range = new { rawMin = 0, rawMax = 1 };
                actions.Add("set"); break;
            case ScrollRect scroll:
                node.role = "scroll"; node.value = new { x = scroll.horizontalNormalizedPosition, y = scroll.verticalNormalizedPosition };
                actions.Add("scroll"); break;
            case Button _: node.role = "button"; actions.Add("click"); break;
            default:
                node.role = "custom-pointer-control";
                if (component.GetComponents<MonoBehaviour>().Any(c => c is IPointerClickHandler)) actions.Add("click");
                break;
        }
        if (component.GetComponents<MonoBehaviour>().Any(c => c is IPointerDownHandler)) actions.Add("focus");
        if (component.GetComponents<MonoBehaviour>().Any(c => c is IDragHandler)) actions.Add("drag");
        node.actions = actions.ToArray();
        if (!Enabled(component)) node.blockedReason = "disabled_by_game";
        else if (!catalogRead && !TryPoint(component, out _)) node.blockedReason = "covered_or_not_raycastable";
        return node;
    }

    // The control without its surrounding labels: scene, path, own value and enabled state.
    private static string Identity(Component component) => $"{component.gameObject.scene.handle}:{Path(component.transform)}:{ControlValues(component)}:{Enabled(component)}";

    private static string ControlValues(Component component) => component switch
    {
        TMP_InputField x => x.text,
        InputField x => x.text,
        TMP_Dropdown x => $"{x.value}:{string.Join("|", x.options.Select(o => o.text))}",
        Dropdown x => $"{x.value}:{string.Join("|", x.options.Select(o => o.text))}",
        Toggle x => x.isOn.ToString(),
        Slider x => $"{x.value:R}:{x.minValue:R}:{x.maxValue:R}",
        _ => Label(component)
    };

    private static string Fingerprint(Component component)
    {
        var values = ControlValues(component);
        // Include the nearest repeated row so pooled controls cannot silently switch to another item.
        var parent = component.transform.parent;
        var owner = parent;
        owner = RepeatedRow(component.transform) ?? owner;
        var context = owner == null ? "" : string.Join("|", owner.GetComponentsInChildren<TMP_Text>().Where(t => Visible(t, out _) && t.GetComponentInParent<Selectable>() == null && !Regex.IsMatch(Clean(t.text), @"^\d{1,2}:\d{2}$")).Take(24).Select(t => Clean(t.text)));
        return $"{component.gameObject.scene.handle}:{Path(component.transform)}:{values}:{context}:{Enabled(component)}";
    }

    private static void Click(Component component, PointerEventData pointer)
    {
        ExecuteEvents.Execute(component.gameObject, pointer, ExecuteEvents.pointerEnterHandler);
        ExecuteEvents.Execute(component.gameObject, pointer, ExecuteEvents.pointerDownHandler);
        ExecuteEvents.Execute(component.gameObject, pointer, ExecuteEvents.pointerUpHandler);
        ExecuteEvents.Execute(component.gameObject, pointer, ExecuteEvents.pointerClickHandler);
    }

    // Numeric JSON tokens must not inherit the game's current locale (e.g. ru-RU decimal commas).
    private static string ScalarText(JToken? value) => value?.Type is JTokenType.Integer or JTokenType.Float ? value.ToString(Newtonsoft.Json.Formatting.None) : value?.ToString() ?? "";

    private static void Set(Component component, JToken value, PointerEventData pointer)
    {
        switch (component)
        {
            case TMP_InputField input:
                input.text = ""; input.caretPosition = 0;
                foreach (var character in value.ToString()) input.ProcessEvent(new Event { type = EventType.KeyDown, character = character });
                input.ForceLabelUpdate();
                EventSystem.current.SetSelectedGameObject(null);
                break;
            case InputField input:
                input.text = ""; input.caretPosition = 0;
                foreach (var character in value.ToString()) input.ProcessEvent(new Event { type = EventType.KeyDown, character = character });
                input.ForceLabelUpdate();
                EventSystem.current.SetSelectedGameObject(null);
                break;
            case Toggle toggle: if (toggle.isOn != bool.Parse(value.ToString())) Click(toggle, pointer); break;
            case Slider slider: EventSystem.current.SetSelectedGameObject(slider.gameObject); slider.value = float.Parse(ScalarText(value), CultureInfo.InvariantCulture); break;
            case Scrollbar scrollbar: scrollbar.value = float.Parse(ScalarText(value), CultureInfo.InvariantCulture); break;
        }
    }

    private static void ValidateValue(Component component, JToken? value)
    {
        if (value == null) throw new AgentError("invalid_value", "set requires --value.");
        if (component is Toggle && !bool.TryParse(value.ToString(), out _)) throw new AgentError("invalid_value", "Toggle values must be true or false.");
        if (component is Slider || component is Scrollbar)
        {
            var min = component is Slider slider ? slider.minValue : 0;
            var max = component is Slider slider2 ? slider2.maxValue : 1;
            if (!float.TryParse(ScalarText(value), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || !IsFinite(number) || number < min || number > max) throw new AgentError("invalid_value", $"Use a raw value between {min} and {max}.");
        }
    }

    private static int ResolveOption(Component component, JToken? value)
    {
        var options = component is TMP_Dropdown tmp ? tmp.options.Select(o => o.text).ToArray() : component is Dropdown dropdown ? dropdown.options.Select(o => o.text).ToArray() : Array.Empty<string>();
        if (value == null) throw new AgentError("invalid_value", "select requires an exact option label or zero-based index.");
        var matches = options.Select((text, index) => new { text, index }).Where(o => o.text == value.ToString()).ToArray();
        if (matches.Length == 1) return matches[0].index;
        if (matches.Length > 1) throw new AgentError("ambiguous_option", "Several options have that label; use the zero-based index.");
        if (int.TryParse(value.ToString(), out var selected) && selected >= 0 && selected < options.Length) return selected;
        throw new AgentError("invalid_value", "No such option. Inspect the control for available values.");
    }

    private bool TryPoint(Component component, out Vector2 point)
    {
        point = default;
        if (EventSystem.current == null || !Visible(component, out var rect)) return false;
        foreach (var candidate in CandidatePoints(component, rect))
        {
            point = candidate;
            hits.Clear();
            EventSystem.current.RaycastAll(new PointerEventData(EventSystem.current) { position = point }, hits);
            // Transient notification toasts (PopupManager) pass over windows while time runs, which in a Multiplayer
            // session is also while forms are open. Input is dispatched to the target itself, so they do not block it;
            // neither does this mod's own overlay (settings panel, About window, feed).
            var first = hits.FirstOrDefault(h => !InTransientPopup(h.gameObject.transform) && h.gameObject.GetComponentInParent<AgentOverlayMarker>() == null);
            if (first.gameObject == null) continue;
            var hit = first.gameObject.transform;
            if (hit == component.transform || hit.IsChildOf(component.transform)) return true;
        }
        return false;
    }

    private static bool InTransientPopup(Transform transform)
    {
        for (var t = transform; t != null; t = t.parent) if (t.name == "PopupManager") return true;
        return false;
    }

    private static IEnumerable<Vector2> CandidatePoints(Component component, Rect rect)
    {
        var candidates = component.GetComponentsInChildren<Graphic>().Where(g => g.raycastTarget && Visible(g, out _)).Select(g => ScreenRect(g.rectTransform, CanvasCamera(g)).center).Take(24).ToList();
        candidates.Insert(0, rect.center);
        foreach (var fraction in new[] { new Vector2(.15f, .5f), new Vector2(.85f, .5f), new Vector2(.5f, .15f), new Vector2(.5f, .85f) }) candidates.Add(new Vector2(Mathf.Lerp(rect.xMin, rect.xMax, fraction.x), Mathf.Lerp(rect.yMin, rect.yMax, fraction.y)));
        return candidates.Distinct();
    }

    private AgentError OcclusionError(Component component, string fallback)
    {
        // The release modal's full-screen blocker is a sibling of its content, so ancestor raycasts alone miss it.
        if (ResearchGameApi.ReadyReleaseName() != null) return new AgentError("not_interactable", "The native CPU release dialog (scope ProjectReleaseWindow) is active. Use game projects-release-read, then explicitly game projects-release with the chosen draft name when ready. No CPU was released or dialog dismissed; retry this action after resolving it.");
        var pauseScopes = TimeAdvanceController.PauseTriggerScopes();
        var blockers = BlockingWindows(component, pauseScopes);
        if (blockers.Length == 0 && TimeAdvanceController.PauseMenuOpen()) return new AgentError("not_interactable", fallback + " The native Pause Menu is open (scope PauseMenu). Close it with game window-close \"Pause Menu\" (game time stays paused), then retry; nothing was changed.");
        if (blockers.Length == 0) return new AgentError("not_interactable", fallback + " Use game dialog-read for native decisions or game window-list to identify foreground windows; no window was closed.");
        var dialog = blockers.FirstOrDefault(surface => SessionGameApi.IsDialogSurface(surface.name, pauseScopes));
        if (dialog != null)
        {
            var next = dialog.name == "ProjectReleaseWindow" ? "Use game projects-release-read, then explicitly projects-release with the chosen draft name when ready." : "Use game dialog-read, then game dialog-choose with an exact returned choice and --dialog scope.";
            return new AgentError("not_interactable", $"Native dialog '{SurfaceTitle(dialog)}' (scope {dialog.name}) blocks this action. {next} No dialog was dismissed; retry the original action after resolving it.");
        }
        var windows = string.Join(", ", blockers.Select(surface => $"'{SurfaceTitle(surface)}' (scope {surface.name})"));
        var recovery = string.Join("; ", blockers.Select(surface => $"`game window-close \"{surface.name}\"`"));
        return new AgentError("not_interactable", $"The target is covered by visible window(s): {windows}. They were left open because they may contain a player or agent draft. If you intend to close those exact blockers, use these commands as needed, frontmost first: {recovery}. Then retry.");
    }

    private Transform[] BlockingWindows(Component component, HashSet<string> pauseScopes)
    {
        if (EventSystem.current == null || !Visible(component, out var rect)) return Array.Empty<Transform>();
        var targetSurface = Surface(component);
        var blockers = new List<Transform>();
        var seen = new HashSet<int>();
        foreach (var candidate in CandidatePoints(component, rect))
        {
            hits.Clear();
            EventSystem.current.RaycastAll(new PointerEventData(EventSystem.current) { position = candidate }, hits);
            var targetIndex = hits.FindIndex(hit => hit.gameObject != null && (hit.gameObject.transform == component.transform || hit.gameObject.transform.IsChildOf(component.transform)));
            var foreground = targetIndex >= 0 ? hits.Take(targetIndex) : hits.Take(1);
            foreach (var hit in foreground.Where(hit => hit.gameObject != null && hit.gameObject.GetComponentInParent<AgentOverlayMarker>() == null))
            {
                var surface = Surface(hit.gameObject.transform);
                for (var parent = hit.gameObject.transform; parent != null; parent = parent.parent)
                    if (SessionGameApi.IsDialogSurface(parent.name, pauseScopes)) { surface = parent; break; }
                if (surface == targetSurface || !seen.Add(surface.GetInstanceID()) || !CloseableWindow(surface) && !SessionGameApi.IsDialogSurface(surface.name, pauseScopes)) continue;
                blockers.Add(surface);
            }
        }
        return blockers.OrderByDescending(ReachableClose).ToArray();
    }

    private static bool CloseableWindow(Transform surface) => surface.GetComponentsInChildren<Button>().Any(button => button.gameObject.activeInHierarchy && button.transform.parent?.name == "TopBar" && button.name.Contains("Close"));
    private bool ReachableClose(Transform surface) => surface.GetComponentsInChildren<Button>().Where(button => button.transform.parent?.name == "TopBar" && button.name.Contains("Close")).Any(button => TryPoint(button, out _));

    private static bool Enabled(Component component) => component is Selectable selectable ? selectable.IsActive() && selectable.IsInteractable() : component is Behaviour behaviour && behaviour.isActiveAndEnabled;
    private static bool IsFinite(float number) => !float.IsNaN(number) && !float.IsInfinity(number);

    private bool Readable(Component component)
    {
        if (!Visible(component, out _)) return false;
        if (catalogRead) return true;
        if (component is TMP_Text text) return TextContent(text).Length > 0;
        // Some visible labels use a transparent sibling hit area rather than a drawn button background.
        return component.GetComponentsInChildren<Graphic>().Any(g => Visible(g, out var clip) && UiReadability.Exposed(g, clip)) || component is not Graphic && TryPoint(component, out _);
    }

    internal static bool Visible(Component component, out Rect rect)
    {
        rect = default;
        if (component == null || !component.gameObject.scene.IsValid() || !component.gameObject.activeInHierarchy || component.GetComponentInParent<AgentOverlayMarker>() != null) return false;
        var canvas = component.GetComponentInParent<Canvas>();
        if (canvas == null || !canvas.isActiveAndEnabled || component.transform is not RectTransform transform) return false;
        if (component is Graphic graphic && (!graphic.enabled || graphic.color.a < .01f || (!catalogRead && (graphic.canvasRenderer.cull || graphic.canvasRenderer.GetColor().a * graphic.canvasRenderer.GetInheritedAlpha() < .01f)))) return false;
        var alpha = 1f;
        for (var current = component.transform; current != null; current = current.parent)
        {
            var groups = current.GetComponents<CanvasGroup>();
            foreach (var group in groups) alpha *= group.alpha;
            if (groups.Any(g => g.ignoreParentGroups)) break;
        }
        if (alpha < .01f) return false;
        var camera = canvas.rootCanvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.rootCanvas.worldCamera;
        rect = ScreenRect(transform, camera);
        if (catalogRead) return true;
        rect = Intersect(rect, ClipRect(component));
        return rect.width > 1 && rect.height > 1;
    }

    internal static Rect ClipRect(Component component)
    {
        var camera = CanvasCamera(component);
        var rect = new Rect(0, 0, Screen.width, Screen.height);
        foreach (var mask in component.GetComponentsInParent<RectMask2D>()) if (mask.isActiveAndEnabled) rect = Intersect(rect, ScreenRect(mask.rectTransform, camera));
        foreach (var mask in component.GetComponentsInParent<Mask>()) if (mask.isActiveAndEnabled) rect = Intersect(rect, ScreenRect(mask.rectTransform, camera));
        return rect;
    }

    private static Rect ScreenRect(RectTransform transform, Camera? camera)
    {
        var corners = new Vector3[4];
        transform.GetWorldCorners(corners);
        var points = corners.Select(p => RectTransformUtility.WorldToScreenPoint(camera, p)).ToArray();
        return Rect.MinMaxRect(points.Min(p => p.x), points.Min(p => p.y), points.Max(p => p.x), points.Max(p => p.y));
    }

    private static Rect Intersect(Rect a, Rect b) => Rect.MinMaxRect(Math.Max(a.xMin, b.xMin), Math.Max(a.yMin, b.yMin), Math.Min(a.xMax, b.xMax), Math.Min(a.yMax, b.yMax));
    private static bool MatchesScope(Component component, string scope) => string.IsNullOrEmpty(scope) || Path(component.transform).IndexOf(scope, StringComparison.OrdinalIgnoreCase) >= 0;
    private static string Clean(string? text) => RichText.Replace(text ?? "", "").Replace("\u200B", "").Trim();

    private static string Label(Component component)
    {
        if (component is TMP_InputField tmp)
        {
            var titles = component.GetComponentsInChildren<TMP_Text>().Where(t => t != tmp.textComponent && t != tmp.placeholder).Select(TextContent).Where(t => t.Length > 0 && !Regex.IsMatch(t, @"^\d+/\d+$")).ToArray();
            if (titles.Length > 0) return string.Join(" | ", titles);
            if (tmp.placeholder is TMP_Text placeholder && !string.IsNullOrWhiteSpace(placeholder.text)) return Clean(placeholder.text);
            return component.name;
        }
        if (component is InputField input && input.placeholder is Text placeholder2 && !string.IsNullOrWhiteSpace(placeholder2.text)) return Clean(placeholder2.text);
        if (component is Slider slider)
        {
            var sliderTexts = SliderTexts(slider);
            var title = sliderTexts.FirstOrDefault(t => t.name.IndexOf("title", StringComparison.OrdinalIgnoreCase) >= 0) ?? sliderTexts.FirstOrDefault(t => !Regex.IsMatch(Clean(t.text), @"^[\d+\-.$€]") && Clean(t.text) != "n/a");
            return title != null ? TextContent(title) : HumanName(component.transform.parent?.name ?? component.name);
        }
        if (component is Scrollbar || component is ScrollRect) return HumanName(component.name);
        if (component is not Selectable && component.GetComponentsInChildren<Selectable>().Length > 0) return SurfaceTitle(component.transform);
        var labels = ReadingOrder(component.GetComponentsInChildren<TMP_Text>().Where(t => Visible(t, out _))).Select(TextContent).Where(t => t.Length > 0).Distinct().Take(3).ToArray();
        if (labels.Length > 0) return string.Join(" | ", labels);
        var text = component.GetComponentInChildren<Text>();
        return text != null && TextContent(text).Length > 0 ? TextContent(text) : HumanName(component.name);
    }

    private static TMP_Text[] SliderTexts(Component slider) => slider.transform.parent == null ? Array.Empty<TMP_Text>() : slider.transform.parent.GetComponentsInChildren<TMP_Text>().Where(t => Visible(t, out _) && t.GetComponentInParent<Selectable>() == null && TextContent(t).Length > 0).ToArray();
    private static bool BelongsToSlider(Component text) => text.transform.parent != null && text.transform.parent.GetComponentsInChildren<Slider>().Length == 1 && text.transform.parent.GetComponentsInChildren<Selectable>().Length == 1;
    private static string TextContent(Component text) => catalogRead ? Clean(text is TMP_Text catalogText ? catalogText.text : ((Text)text).text) : text is TMP_Text tmp ? UiReadability.Text(tmp) : Visible(text, out var clip) && UiReadability.Exposed((Text)text, clip) ? Clean(((Text)text).text) : "";
    private static IEnumerable<T> ReadingOrder<T>(IEnumerable<T> texts) where T : Component
    {
        var items = texts.Select(t => new { Item = t, Rect = ScreenRect((RectTransform)t.transform, CanvasCamera(t)) }).OrderByDescending(t => t.Rect.center.y).ToArray();
        var index = 0;
        while (index < items.Length)
        {
            var start = index++;
            var tolerance = Mathf.Min(8, items[start].Rect.height * .4f);
            while (index < items.Length && items[start].Rect.center.y - items[index].Rect.center.y <= tolerance) index++;
            foreach (var item in items.Skip(start).Take(index - start).OrderBy(t => t.Rect.xMin)) yield return item.Item;
        }
    }
    private static IEnumerable<Component[]> TextGroups(IEnumerable<Component> texts)
    {
        var row = new List<Component>();
        Rect previous = default;
        foreach (var text in ReadingOrder(texts))
        {
            var rect = ScreenRect((RectTransform)text.transform, CanvasCamera(text));
            if (row.Count > 0 && previous.center.y - rect.center.y > Math.Max(36, Math.Max(previous.height, rect.height) * 1.6f)) { yield return row.ToArray(); row.Clear(); }
            row.Add(text); previous = rect;
        }
        if (row.Count > 0) yield return row.ToArray();
    }
    private static Transform Surface(Component component)
    {
        var root = component.GetComponentInParent<Canvas>()?.rootCanvas.transform;
        var surface = component.transform;
        while (surface.parent != null && surface.parent != root) surface = surface.parent;
        return surface;
    }
    private static string SurfaceTitle(Transform surface)
    {
        // Only shallow header text, never the contents of child controls or off-screen rows.
        var title = surface.GetComponentsInChildren<TMP_Text>().FirstOrDefault(t => Visible(t, out _) && !t.text.Contains(":") && t.GetComponentInParent<Selectable>() == null && (t.transform.parent?.name is "TopBar" or "Header" or "TitleBar") && (t.transform.parent.parent == surface || t.transform.parent.parent?.parent == surface));
        return title != null && TextContent(title).Length > 0 ? TextContent(title) : HumanName(surface.name);
    }
    private static string TextGroupId(Component text)
    {
        var owner = TextOwner(text);
        if (owner != text.transform.parent) return "g" + owner.GetInstanceID();
        var root = text.GetComponentInParent<Canvas>()?.rootCanvas.transform;
        for (var parent = text.transform.parent; parent != null && parent != root; parent = parent.parent)
        {
            if (parent.name.EndsWith("(Clone)") || (parent.parent != null && parent.parent.Cast<Transform>().Count(t => t.name == parent.name) > 1)) return "g" + parent.GetInstanceID();
        }
        return "g" + text.transform.parent.GetInstanceID();
    }
    private static Transform? RepeatedRow(Transform target)
    {
        for (var current = target; current != null && current.GetComponent<Canvas>() == null; current = current.parent)
        {
            if (current.name.EndsWith("(Clone)") || (current.parent != null && current.parent.Cast<Transform>().Count(t => t.name == current.name) > 1)) return current;
            if (current != target && current.GetComponentsInChildren<Selectable>().Length > 8) break;
        }
        return null;
    }
    private static Transform TextOwner(Component text)
    {
        var parent = text.transform.parent;
        var row = parent.parent;
        if (row == null || row.childCount < 2 || parent.GetComponentsInChildren<TMP_Text>().Length != 1) return parent;
        var cells = row.Cast<Transform>().Select(t => t.GetComponentsInChildren<TMP_Text>().Where(label => Visible(label, out _)).ToArray()).ToArray();
        if (cells.Any(c => c.Length != 1)) return parent;
        var centers = cells.Select(c => ScreenRect(c[0].rectTransform, CanvasCamera(c[0])).center.y).ToArray();
        return centers.Max() - centers.Min() < 8 ? row : parent;
    }
    internal static Camera? CanvasCamera(Component component)
    {
        var canvas = component.GetComponentInParent<Canvas>()?.rootCanvas;
        return canvas == null || canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
    }
    private static string HumanName(string name)
    {
        var cleaned = Regex.Replace(Regex.Replace(name.Replace("(Clone)", ""), @"(?<=\w)(Button|Slider|Toggle)$", ""), @"(?<=[a-z])(?=[A-Z])", " ").Trim();
        return cleaned.Length > 0 ? cleaned : name;
    }
    private static Vector2 DragDelta(JToken? value)
    {
        var parts = value?.ToString().Split(',');
        if (parts == null || parts.Length != 2 || !float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x) || !float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y) || !IsFinite(x) || !IsFinite(y)) throw new AgentError("invalid_value", "drag requires --value 'dx,dy' in screen pixels (positive right/up).");
        return new Vector2(x, y);
    }

    private static string Context(Component component)
    {
        var names = new List<string>();
        var root = component.GetComponentInParent<Canvas>()?.rootCanvas.transform;
        for (var parent = component.transform.parent; parent != null && parent != root; parent = parent.parent) names.Add(parent.name);
        names.Reverse();
        return string.Join("/", names);
    }

    private static string Path(Transform transform)
    {
        var names = new List<string>();
        for (var current = transform; current != null; current = current.parent) names.Add(current.name);
        names.Reverse();
        return string.Join("/", names);
    }

    public static object? InputTarget(GameObject? hit)
    {
        if (hit == null) return null;
        var handler = ExecuteEvents.GetEventHandler<IPointerClickHandler>(hit) ?? hit;
        var selectable = handler.GetComponent<Selectable>();
        return new { label = selectable != null ? Label(selectable) : HumanName(handler.name), path = Path(handler.transform), agentOverlay = handler.GetComponentInParent<AgentOverlayMarker>() != null };
    }
}

internal sealed class AgentOverlayMarker : MonoBehaviour { }
