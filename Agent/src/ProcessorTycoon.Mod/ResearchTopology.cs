using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace ProcessorTycoonMod;

// Reads only the active research tree's rendered cards, connector nodes and dependency-line geometry.
internal static class ResearchTopology
{
    private const string WindowType = "ProcessorTycoon.ResearchSystem.ResearchTreeWindow";
    private const string CardType = "ProcessorTycoon.ResearchSystem.ResearchButton";
    private const string InfoTextType = "ProcessorTycoon.UI.InfoText";
    private const float MatchTolerance = 0.25f;

    private sealed class Card
    {
        public string Name = "";
        public string Year = "";
        public Vector2 Enter;
        public Vector2 Exit;
        public JObject Identity() => new() { ["name"] = Name, ["year"] = Year };
        public string Label => Name + " — " + Year;
    }

    internal static JObject Read(bool includeGeometry = false)
    {
        try { return ReadActive(includeGeometry); }
        catch (AgentError error) { return Unavailable(error.Message); }
    }

    private static JObject ReadActive(bool includeGeometry)
    {
        var windows = Resources.FindObjectsOfTypeAll<MonoBehaviour>().Where(component => component != null && component.GetType().FullName == WindowType && component.gameObject.scene.IsValid() && component.gameObject.activeInHierarchy).ToArray();
        if (windows.Length != 1) throw new AgentError("game_ui_mismatch", $"Expected one active ResearchTreeWindow, found {windows.Length}.");
        var window = windows[0];
        var linesParent = Field<GameObject>(window, WindowType, "linesParent");
        var cpuTab = Field<GameObject>(window, WindowType, "cpuTab");
        if (!linesParent.activeInHierarchy || cpuTab.transform is not RectTransform common || linesParent.transform.parent != cpuTab.transform) throw new AgentError("game_ui_mismatch", "The active dependency lines are not parented directly under the expected Research cpuTab coordinate space.");

        var cardComponents = Resources.FindObjectsOfTypeAll<MonoBehaviour>().Where(component => component != null && component.GetType().FullName == CardType && component.gameObject.scene.IsValid() && component.gameObject.activeInHierarchy && component.transform.IsChildOf(window.transform)).ToArray();
        if (cardComponents.Length == 0) throw new AgentError("game_ui_mismatch", "The active Research tree exposed no rendered ResearchButton cards.");
        var cards = cardComponents.Select(ReadCard).ToArray();
        var duplicateIdentities = cards.GroupBy(card => card.Label, StringComparer.Ordinal).Where(group => group.Count() > 1).Select(group => group.Key).OrderBy(value => value, StringComparer.Ordinal).ToArray();

        var edges = new JArray();
        var unmapped = new JArray();
        var geometryLines = new JArray();
        var renderedLines = 0;
        for (var index = 0; index < linesParent.transform.childCount; index++)
        {
            var child = linesParent.transform.GetChild(index);
            if (!child.gameObject.activeInHierarchy) continue;
            var image = child.GetComponent<Image>();
            if (child.name != "Line" || image == null || child is not RectTransform line)
            {
                unmapped.Add(new JObject { ["lineIndex"] = index, ["reason"] = "unexpected_active_lines_parent_child" });
                continue;
            }
            if (!image.enabled || image.color.a < .01f)
            {
                unmapped.Add(new JObject { ["lineIndex"] = index, ["reason"] = "inactive_line_graphic" });
                continue;
            }
            renderedLines++;
            var start = LocalPoint(common, line.TransformPoint(new Vector3(line.rect.xMin, line.rect.center.y, 0)));
            var end = LocalPoint(common, line.TransformPoint(new Vector3(line.rect.xMax, line.rect.center.y, 0)));
            var from = cards.Where(card => Vector2.Distance(card.Exit, start) <= MatchTolerance).ToArray();
            var to = cards.Where(card => Vector2.Distance(card.Enter, end) <= MatchTolerance).ToArray();
            if (includeGeometry) geometryLines.Add(new JObject
            {
                ["lineIndex"] = index, ["start"] = Point(start), ["end"] = Point(end),
                ["startMatches"] = Candidates(from, card => card.Exit, start), ["endMatches"] = Candidates(to, card => card.Enter, end),
                ["nearestExitDiagnostic"] = Nearest(cards, card => card.Exit, start), ["nearestEnterDiagnostic"] = Nearest(cards, card => card.Enter, end)
            });
            if (from.Length != 1 || to.Length != 1)
            {
                unmapped.Add(new JObject
                {
                    ["lineIndex"] = index, ["reason"] = from.Length != 1 && to.Length != 1 ? "non_unique_exit_and_enter" : from.Length != 1 ? "non_unique_exit" : "non_unique_enter",
                    ["exitMatches"] = new JArray(from.Select(card => card.Label)), ["enterMatches"] = new JArray(to.Select(card => card.Label))
                });
                continue;
            }
            edges.Add(new JObject { ["prerequisite"] = from[0].Identity(), ["dependent"] = to[0].Identity() });
        }

        var reasons = new JArray();
        if (renderedLines == 0) reasons.Add("no_active_rendered_dependency_lines");
        if (unmapped.Count > 0) reasons.Add("one_or_more_lines_did_not_map_uniquely");
        if (duplicateIdentities.Length > 0) reasons.Add("duplicate_displayed_card_identity");
        var complete = renderedLines > 0 && unmapped.Count == 0 && duplicateIdentities.Length == 0;
        var result = new JObject
        {
            ["method"] = "game-ui-render", ["available"] = true, ["complete"] = complete,
            ["cardCount"] = cards.Length, ["renderedLineCount"] = renderedLines, ["edgeCount"] = edges.Count,
            ["matchToleranceLocalUnits"] = MatchTolerance, ["edges"] = edges, ["unmapped"] = unmapped,
            ["duplicateCardIdentities"] = new JArray(duplicateIdentities), ["reasons"] = reasons,
            ["direction"] = "ResearchButton ExitNode (prerequisite) to EnterNode (dependent)",
            ["boundary"] = "Reads only active rendered ResearchButton name/year/connectors and active Image lines under ResearchTreeWindow.linesParent; no TargetTechnology or research model graph is accessed."
        };
        if (includeGeometry) result["geometry"] = new JObject
        {
            ["coordinateSpace"] = "linesParent parent local UI units",
            ["cards"] = new JArray(cards.Select(card => new JObject { ["name"] = card.Name, ["year"] = card.Year, ["enter"] = Point(card.Enter), ["exit"] = Point(card.Exit) })),
            ["lines"] = geometryLines
        };
        return result;
    }

    private static Card ReadCard(MonoBehaviour component)
    {
        var name = Field<TMP_Text>(component, CardType, "technologyNameText").text.Trim();
        var yearInfo = Field<object>(component, CardType, "yearText");
        if (yearInfo.GetType().FullName != InfoTextType) throw new AgentError("game_ui_mismatch", "ResearchButton.yearText is not the expected rendered InfoText UI component.");
        var yearProperty = yearInfo.GetType().GetProperty("Text", BindingFlags.Instance | BindingFlags.Public);
        if (yearProperty?.GetValue(yearInfo) is not string year) throw new AgentError("game_ui_mismatch", "ResearchButton.yearText has no rendered Text value.");
        var enter = Property<RectTransform>(component, CardType, "EnterNode");
        var exit = Property<RectTransform>(component, CardType, "ExitNode");
        if (enter.parent != component.transform || exit.parent != component.transform || enter.gameObject.activeSelf || exit.gameObject.activeSelf) throw new AgentError("game_ui_mismatch", "ResearchButton connector nodes are not in the settled post-ResetNodes UI state.");
        if (name.Length == 0 || string.IsNullOrWhiteSpace(year)) throw new AgentError("game_ui_mismatch", "A rendered ResearchButton has no usable displayed name/year identity.");
        // ResetNodes reparents with worldPositionStays:false after line creation. The cpuTab-space
        // connector coordinate is therefore retained in localPosition; current world position is invalid.
        return new Card { Name = name, Year = year.Trim(), Enter = enter.localPosition, Exit = exit.localPosition };
    }

    private static Vector2 LocalPoint(RectTransform common, Vector3 world) => common.InverseTransformPoint(world);
    private static JObject Point(Vector2 value) => new() { ["x"] = value.x, ["y"] = value.y };
    private static JArray Candidates(IEnumerable<Card> cards, Func<Card, Vector2> point, Vector2 endpoint) => new(cards.Select(card => new JObject { ["card"] = card.Label, ["distance"] = Vector2.Distance(point(card), endpoint) }));
    private static JObject Nearest(IEnumerable<Card> cards, Func<Card, Vector2> point, Vector2 endpoint)
    {
        var nearest = cards.OrderBy(card => Vector2.Distance(point(card), endpoint)).First();
        return new JObject { ["card"] = nearest.Label, ["distance"] = Vector2.Distance(point(nearest), endpoint), ["usedForMatching"] = false };
    }

    private static T Field<T>(object component, string expectedType, string name) where T : class
    {
        if (component.GetType().FullName != expectedType) throw new AgentError("game_ui_mismatch", $"Expected UI component {expectedType}, found {component.GetType().FullName}.");
        var field = component.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        if (field?.GetValue(component) is not T value) throw new AgentError("game_ui_mismatch", $"{expectedType} UI field '{name}' is unavailable in this game version.");
        return value;
    }

    private static T Property<T>(object component, string expectedType, string name) where T : class
    {
        if (component.GetType().FullName != expectedType) throw new AgentError("game_ui_mismatch", $"Expected UI component {expectedType}, found {component.GetType().FullName}.");
        var property = component.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
        if (property?.GetValue(component) is not T value) throw new AgentError("game_ui_mismatch", $"{expectedType} UI property '{name}' is unavailable in this game version.");
        return value;
    }

    private static JObject Unavailable(string reason) => new()
    {
        ["method"] = "game-ui-render", ["available"] = false, ["complete"] = false, ["edges"] = new JArray(),
        ["unmapped"] = new JArray(), ["reasons"] = new JArray(reason),
        ["boundary"] = "No research model dependency data was used as a fallback."
    };
}
