using System;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace ProcessorTycoonModApi;

// A window like the game's own (its GameSetupWindow as reference): shadow, 1 px dark outline, rounded frame, 30 px title bar
// with the title and the close button, a body column and an optional footer strip. Drag the title bar to move it; a click
// brings it to the front. It can dock above a bottom-bar item until it is dragged, and always stays on screen.
internal sealed class Window
{
    public readonly RectTransform Holder, Frame, Body;
    public readonly TextMeshProUGUI Title;
    public event Action? Closed;
    private readonly Placement placement;

    public Window(Overlay overlay, string title, float width, bool closable = true) : this(overlay.Root, title, width, closable) { }

    public Window(Transform parent, string title, float width, bool closable = true)
    {
        Holder = Ui.Rect("Window " + title, parent);
        Holder.anchorMin = Holder.anchorMax = Holder.pivot = new Vector2(.5f, .5f);
        Holder.sizeDelta = Vector2.zero;
        var shadow = Ui.Fill(Ui.Rect("Shadow", Holder), null, "shadow", 1);
        shadow.color = Color.white;
        shadow.raycastTarget = false;
        var outline = Ui.Fill(Ui.Rect("Outline", Holder), null, Ui.Rounded, 3);
        outline.color = new Color(0, 0, 0, .6f);
        outline.raycastTarget = false;
        Frame = Ui.Fill(Ui.Rect("Frame", Holder), Paint.Window, Ui.Rounded, 3).rectTransform;
        Frame.anchorMin = Frame.anchorMax = Frame.pivot = new Vector2(.5f, .5f);
        Frame.sizeDelta = new Vector2(width, 100);
        Frame.gameObject.AddComponent<Mask>().showMaskGraphic = true;
        var layout = Frame.gameObject.AddComponent<VerticalLayoutGroup>();
        layout.childControlWidth = layout.childControlHeight = true;
        layout.childForceExpandWidth = true;
        layout.childForceExpandHeight = false;
        Frame.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        Frame.gameObject.AddComponent<ToFront>().Holder = Holder;
        placement = Holder.gameObject.AddComponent<Placement>();
        placement.Frame = Frame; placement.Shadow = shadow.rectTransform; placement.Outline = outline.rectTransform;

        var bar = Ui.Fill(Ui.Rect("TopBar", Frame), Paint.TopBar, null);
        Ui.Size(bar, height: 30);
        var barShadow = bar.gameObject.AddComponent<Shadow>();
        barShadow.effectColor = new Color(0, 0, 0, .12f);
        barShadow.effectDistance = new Vector2(0, -1);
        bar.gameObject.AddComponent<Drag>().Owner = placement;
        Title = Ui.Label(bar.transform, title, 18, Paint.TopBarText);
        Ui.Stretch(Title.rectTransform, 10, closable ? 44 : 10);
        if (closable)
        {
            var close = Ui.Fill(Ui.Rect("CloseButton", bar.transform), null, null);
            close.color = Color.clear;
            close.raycastTarget = true;
            var closeRect = close.rectTransform;
            closeRect.anchorMin = new Vector2(1, 0); closeRect.anchorMax = new Vector2(1, 1); closeRect.pivot = new Vector2(1, .5f);
            closeRect.sizeDelta = new Vector2(40, 0); closeRect.anchoredPosition = Vector2.zero;
            var icon = Ui.Icon(close.transform, "close", 22, Paint.CloseIcon);
            icon.rectTransform.anchorMin = icon.rectTransform.anchorMax = icon.rectTransform.pivot = new Vector2(.5f, .5f);
            icon.rectTransform.anchoredPosition = Vector2.zero;
            var button = close.gameObject.AddComponent<Button>();
            button.transition = Selectable.Transition.None;
            button.onClick.AddListener(Close);
            close.gameObject.AddComponent<HandCursor>();
            var hover = close.gameObject.AddComponent<CloseHover>();
            hover.Back = close; hover.Icon = icon;
        }
        Body = (RectTransform)Ui.Column(Frame, 8, 12, "Body").transform;
        Holder.gameObject.SetActive(false);
    }

    public bool Visible => Holder.gameObject.activeSelf;

    // Footer strip like the native windows': a band with actions on the right (add them to the returned row). Main windows
    // start it with their version link (opens About).
    public HorizontalLayoutGroup Footer(string version = "", Action? openAbout = null)
    {
        var strip = Ui.Fill(Ui.Rect("Footer", Frame), Paint.Window2, null);
        var row = strip.gameObject.AddComponent<HorizontalLayoutGroup>();
        row.padding = new RectOffset(12, 12, 8, 8);
        row.spacing = 8;
        row.childAlignment = TextAnchor.MiddleRight;
        row.childControlWidth = row.childControlHeight = true;
        row.childForceExpandWidth = row.childForceExpandHeight = false;
        Ui.Size(row, height: 44);   // as tall with or without buttons
        if (openAbout != null) Ui.VersionLink(row.transform, version, openAbout);
        Ui.Spacer(row.transform);
        return row;
    }

    // Centred (or at an offset from the centre); undocks.
    public void Show(Vector2? position = null)
    {
        placement.DockAbove = null;
        Holder.anchoredPosition = position ?? Vector2.zero;
        Open();
    }

    // Docks above a bottom-bar item (right edges aligned) until the player drags the window.
    public void ShowAbove(RectTransform item)
    {
        placement.DockAbove = item;
        Open();
    }

    private void Open()
    {
        Holder.gameObject.SetActive(true);
        Holder.SetAsLastSibling();
    }

    public void Close()
    {
        if (!Visible) return;
        Holder.gameObject.SetActive(false);
        Closed?.Invoke();
    }

    public void Destroy() => UnityEngine.Object.Destroy(Holder.gameObject);

    private sealed class Placement : MonoBehaviour
    {
        public RectTransform Frame = null!, Shadow = null!, Outline = null!;
        public RectTransform? DockAbove;

        private void LateUpdate()
        {
            var size = Frame.rect.size;
            Place(Outline, size + new Vector2(2, 2), 1);
            Place(Shadow, size + new Vector2(29, 29), 0);
            Frame.anchoredPosition = Vector2.zero;
            var holder = (RectTransform)transform;
            var parent = (RectTransform)holder.parent;
            if (DockAbove != null && DockAbove.gameObject.activeInHierarchy)
            {
                var corner = parent.InverseTransformPoint(DockAbove.TransformPoint(new Vector3(DockAbove.rect.xMax, DockAbove.rect.yMax)));
                holder.anchoredPosition = new Vector2(corner.x - size.x / 2, corner.y + 6 + size.y / 2);
            }
            // Keep the whole window on screen.
            var half = parent.rect.size / 2;
            var p = holder.anchoredPosition;
            holder.anchoredPosition = new Vector2(Mathf.Clamp(p.x, -half.x + size.x / 2, half.x - size.x / 2), Mathf.Clamp(p.y, -half.y + size.y / 2, half.y - size.y / 2));
        }

        private static void Place(RectTransform rect, Vector2 size, int order) { rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(.5f, .5f); rect.sizeDelta = size; rect.anchoredPosition = Vector2.zero; rect.SetSiblingIndex(order); }
    }

    private sealed class Drag : MonoBehaviour, IDragHandler, IBeginDragHandler
    {
        public Placement Owner = null!;
        public void OnBeginDrag(PointerEventData data) { Owner.DockAbove = null; Owner.transform.SetAsLastSibling(); }
        public void OnDrag(PointerEventData data)
        {
            var canvas = Owner.GetComponentInParent<Canvas>();
            ((RectTransform)Owner.transform).anchoredPosition += data.delta / (canvas != null ? canvas.scaleFactor : 1f);
        }
    }

    private sealed class ToFront : MonoBehaviour, IPointerDownHandler
    {
        public RectTransform Holder = null!;
        public void OnPointerDown(PointerEventData data) => Holder.SetAsLastSibling();
    }

    private sealed class CloseHover : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
    {
        public Image Back = null!, Icon = null!;
        public void OnPointerEnter(PointerEventData data) { Painted.Set(Back, Paint.CloseHover); Painted.Set(Icon, Paint.CtaText); }
        public void OnPointerExit(PointerEventData data) { Painted.Clear(Back); Back.color = Color.clear; Painted.Set(Icon, Paint.CloseIcon); }
    }
}
