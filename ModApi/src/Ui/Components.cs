using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace ProcessorTycoonModApi;

// The game's cursors (ProcessorTycoon.CursorController, by name): the hand over clickable things, the text cursor over
// input fields.
internal static class Cursors
{
    private static MethodInfo? set;
    private static object? controller;
    private static Type? cursorType;

    public static void Set(string cursor)
    {
        try
        {
            if (controller == null || controller is Object o && o == null)
            {
                var type = Theme.GameType("ProcessorTycoon.CursorController");
                controller = type?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                set = type?.GetMethod("SetCursorType");
                cursorType = set?.GetParameters().FirstOrDefault()?.ParameterType;
            }
            if (controller != null && set != null && cursorType != null) set.Invoke(controller, new[] { Enum.Parse(cursorType, cursor) });
        }
        catch (Exception) { }
    }
}

internal sealed class HandCursor : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
{
    public void OnPointerEnter(PointerEventData data) { var s = GetComponent<Selectable>(); if (s == null || s.interactable) Cursors.Set("Hand"); }
    public void OnPointerExit(PointerEventData data) => Cursors.Set("Mouse");
    private void OnDisable() => Cursors.Set("Mouse");
}

internal sealed class TextCursor : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
{
    public void OnPointerEnter(PointerEventData data) => Cursors.Set("Text");
    public void OnPointerExit(PointerEventData data) => Cursors.Set("Mouse");
}

// The native button press (the game's UIAnimationButton): a short ease-out shrink while held.
internal sealed class PressFeedback : MonoBehaviour, IPointerDownHandler, IPointerUpHandler, IPointerExitHandler
{
    public RectTransform Target = null!;
    public float Pressed = .9825f;
    private float from = 1, to = 1, started;

    public void OnPointerDown(PointerEventData data) { var s = GetComponent<Selectable>(); if (data.button == PointerEventData.InputButton.Left && (s == null || s.interactable)) Animate(Pressed); }
    public void OnPointerUp(PointerEventData data) { if (data.button == PointerEventData.InputButton.Left) Animate(1); }
    public void OnPointerExit(PointerEventData data) => Animate(1);
    private void OnDisable() { if (Target != null) Target.localScale = Vector3.one; from = to = 1; }
    private void Animate(float scale) { if (Target == null) return; from = Target.localScale.x; to = scale; started = Time.unscaledTime; }

    private void Update()
    {
        if (Target == null) return;
        var progress = Mathf.Clamp01((Time.unscaledTime - started) / .1f);
        var scale = Mathf.Lerp(from, to, 1 - Mathf.Pow(1 - progress, 4));
        Target.localScale = new Vector3(scale, scale, 1);
    }
}

// Opens the web page of a clicked <link="url"> in a text (other link ids go to OnLink); the hand cursor shows over links.
internal sealed class TextLinks : MonoBehaviour, IPointerClickHandler, IPointerMoveHandler, IPointerExitHandler
{
    public Action<string>? OnLink { get; set; }
    private bool overLink;

    public void OnPointerClick(PointerEventData data)
    {
        var index = LinkAt(data);
        if (index < 0) return;
        var id = GetComponent<TMP_Text>().textInfo.linkInfo[index].GetLinkID();
        if (OnLink != null && !id.StartsWith("http", StringComparison.Ordinal)) OnLink(id);
        else Application.OpenURL(id);
    }

    public void OnPointerMove(PointerEventData data) => Hand(LinkAt(data) >= 0);
    public void OnPointerExit(PointerEventData data) => Hand(false);
    private void OnDisable() => Hand(false);
    private int LinkAt(PointerEventData data) => TMP_TextUtilities.FindIntersectingLink(GetComponent<TMP_Text>(), data.position, null);

    private void Hand(bool over)
    {
        if (over == overLink) return;
        overLink = over;
        Cursors.Set(over ? "Hand" : "Mouse");
    }
}

// Version link: underlined in the call-to-action colour while hovered.
internal sealed class LinkHover : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
{
    public TextMeshProUGUI Text = null!;
    public void OnPointerEnter(PointerEventData data) { Painted.Set(Text, Paint.Cta); Text.fontStyle |= FontStyles.Underline; }
    public void OnPointerExit(PointerEventData data) { Painted.Set(Text, Paint.TextLow); Text.fontStyle &= ~FontStyles.Underline; }
}

// Ui.IconButton: the icon turns to the call-to-action colour while hovered.
internal sealed class IconHover : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
{
    public Image Icon = null!;
    public Paint Normal;
    public void OnPointerEnter(PointerEventData data) => Painted.Set(Icon, Paint.Cta);
    public void OnPointerExit(PointerEventData data) => Painted.Set(Icon, Normal);
}

internal sealed class SegmentsView : MonoBehaviour
{
    public Func<int> Selected = () => -1;
    public int Hovered = -1;
    public readonly List<(TextMeshProUGUI text, Image bar)> Options = new();
    private string shown = "";

    private void LateUpdate()
    {
        var selected = Selected();
        var state = $"{selected}|{Hovered}|{Theme.Revision}";
        if (state == shown) return;
        shown = state;
        for (var i = 0; i < Options.Count; i++)
        {
            Painted.Set(Options[i].text, i == selected ? Paint.TabTextOn : i == Hovered ? Paint.TabTextHover : Paint.TabText);
            Options[i].bar.enabled = i == selected;
        }
    }
}

internal sealed class SegmentHover : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
{
    public SegmentsView View = null!;
    public int Index;
    public void OnPointerEnter(PointerEventData data) => View.Hovered = Index;
    public void OnPointerExit(PointerEventData data) { if (View.Hovered == Index) View.Hovered = -1; }
}

internal sealed class CheckView : MonoBehaviour
{
    public Func<bool> Get = () => false;
    public Image Mark = null!;
    private void LateUpdate() { var on = Get(); if (Mark.enabled != on) Mark.enabled = on; }
}

// Keeps Ui.Mark's icon in front of the first character of its link, centred on the capital letters.
internal sealed class InlineMark : MonoBehaviour
{
    public TextMeshProUGUI Text = null!;
    public RectTransform Icon = null!;
    public string LinkId = "";

    private void LateUpdate()
    {
        var info = Text.textInfo;
        for (var i = 0; info != null && i < info.linkCount; i++)
        {
            if (info.linkInfo[i].GetLinkID() != LinkId) continue;
            var c = info.characterInfo[info.linkInfo[i].linkTextfirstCharacterIndex];
            var size = Text.fontSize;
            Icon.anchorMin = Icon.anchorMax = Text.rectTransform.pivot;
            Icon.pivot = new Vector2(1, .5f);
            Icon.sizeDelta = new Vector2(size, size);
            Icon.anchoredPosition = new Vector2(c.bottomLeft.x - size * .12f, (c.topLeft.y + c.bottomLeft.y) / 2);
            if (!Icon.gameObject.activeSelf) Icon.gameObject.SetActive(true);
            return;
        }
        if (Icon.gameObject.activeSelf) Icon.gameObject.SetActive(false);
    }
}
