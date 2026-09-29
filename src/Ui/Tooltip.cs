using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace ProcessorTycoonModApi;

// Hover text for a mod's own controls. Header and Text may change while shown.
internal sealed class Tip : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
{
    public string Header = "", Text = "";

    public static Tip On(GameObject target, string header, string text)
    {
        if (target.GetComponent<Graphic>() == null) target.AddComponent<Image>().color = Color.clear;
        var tip = target.AddComponent<Tip>();
        tip.Header = header;
        tip.Text = text;
        return tip;
    }

    public void OnPointerEnter(PointerEventData data) { var canvas = GetComponentInParent<Canvas>(); if (canvas != null) TipView.For(canvas.rootCanvas).Hover(this); }
    public void OnPointerExit(PointerEventData data) => TipView.Leave(this);
    private void OnDisable() => TipView.Leave(this);
}

// The tooltip box of a canvas, drawn like the game's (dark translucent box, white text) and shown after the game's delay
// (0.15 s) next to the mouse. It lives on the same canvas as its controls, so it always shows above them (the game's own
// tooltip canvas can be below a mod's overlay).
internal sealed class TipView : MonoBehaviour
{
    private static readonly Dictionary<Canvas, TipView> views = new();
    private Tip? target;
    private float since;
    private RectTransform box = null!;
    private TextMeshProUGUI header = null!, text = null!;
    private Canvas canvas = null!;

    public static TipView For(Canvas canvas)
    {
        if (views.TryGetValue(canvas, out var view) && view != null) return view;
        var root = Ui.Rect("Tooltip", canvas.transform);
        root.anchorMin = root.anchorMax = Vector2.zero;
        root.sizeDelta = Vector2.zero;
        view = root.gameObject.AddComponent<TipView>();
        view.canvas = canvas;
        view.box = Ui.Rect("Box", root);
        view.box.anchorMin = view.box.anchorMax = Vector2.zero;
        var back = Ui.Fill(view.box, null);
        back.color = new Color(.137f, .137f, .137f, .93f);
        back.raycastTarget = false;
        var column = view.box.gameObject.AddComponent<VerticalLayoutGroup>();
        column.padding = new RectOffset(10, 10, 7, 9);
        column.spacing = 2;
        column.childControlWidth = column.childControlHeight = true;
        column.childForceExpandWidth = true;
        column.childForceExpandHeight = false;
        view.box.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        view.header = Plain(Ui.Label(view.box, "", 17), Color.white);
        view.text = Plain(Ui.Label(view.box, "", 15, wrap: true), new Color(1, 1, 1, .88f));
        view.box.gameObject.SetActive(false);
        return views[canvas] = view;
    }

    public void Hover(Tip tip) { target = tip; since = Time.unscaledTime; }

    public static void Leave(Tip tip) { foreach (var view in views.Values) if (view != null && view.target == tip) view.target = null; }

    private static TextMeshProUGUI Plain(TextMeshProUGUI label, Color color)
    {
        DestroyImmediate(label.GetComponent<Painted>());
        label.color = color;
        label.richText = false;
        return label;
    }

    private void LateUpdate()
    {
        var show = target != null && target.isActiveAndEnabled && Time.unscaledTime - since >= .15f;
        if (box.gameObject.activeSelf != show) box.gameObject.SetActive(show);
        if (!show) return;
        transform.SetAsLastSibling();
        if (header.text != target!.Header || text.text != target.Text)
        {
            if (Theme.Font != null) header.font = text.font = Theme.Font;
            header.text = target.Header;
            text.text = target.Text;
            header.gameObject.SetActive(target.Header.Length > 0);
            var width = Mathf.Max(header.GetPreferredValues(target.Header).x, text.GetPreferredValues(target.Text).x) + 21;
            box.sizeDelta = new Vector2(Mathf.Min(320, width), box.sizeDelta.y);
        }
        // Below-right of the mouse, flipped to stay on screen.
        var scale = Mathf.Max(.01f, canvas.scaleFactor);
        var mouse = (Vector2)Input.mousePosition / scale;
        var size = box.sizeDelta;
        bool left = mouse.x + 14 + size.x > Screen.width / scale, up = mouse.y - 20 - size.y < 0;
        box.pivot = new Vector2(left ? 1 : 0, up ? 0 : 1);
        box.anchoredPosition = mouse + new Vector2(left ? -6 : 14, up ? 12 : -20);
    }
}
