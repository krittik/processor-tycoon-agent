using System;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace ProcessorTycoonMod;

// Decorative only: never sends input, changes the OS cursor, or delays an action.
internal sealed class AgentCursor : MonoBehaviour, IDisposable
{
    private RectTransform root = null!;
    private RectTransform pointer = null!;
    private RectTransform badge = null!;
    private RectTransform pulse = null!;
    private AgentCursorGraphic pulseGraphic = null!;
    private CanvasGroup opacity = null!;
    private Canvas canvas = null!;
    private Vector2 position;
    private Vector2 velocity;
    private Vector2 target;
    private Vector2 pulsePoint;
    private float updatedAt;
    private float activeAt;
    private float clickedAt = -10;
    private bool showing;
    private bool positioned;
    private const float PursuitFrequency = 18;

    public static AgentCursor Create(Transform canvasTransform)
    {
        var gameObject = new GameObject("Agent cursor", typeof(RectTransform), typeof(CanvasGroup), typeof(AgentOverlayMarker));
        gameObject.transform.SetParent(canvasTransform, false);
        var cursor = gameObject.AddComponent<AgentCursor>();
        cursor.Initialize();
        return cursor;
    }

    private void Initialize()
    {
        root = (RectTransform)transform;
        root.anchorMin = Vector2.zero; root.anchorMax = Vector2.one;
        root.offsetMin = root.offsetMax = Vector2.zero; root.pivot = new Vector2(.5f, .5f);
        canvas = GetComponentInParent<Canvas>();
        opacity = GetComponent<CanvasGroup>();
        opacity.blocksRaycasts = false; opacity.interactable = false; opacity.alpha = 0;
        pulse = Rect("Action pulse", root, 44, 44);
        pulseGraphic = pulse.gameObject.AddComponent<AgentCursorGraphic>();
        pulseGraphic.Shape = AgentCursorGraphic.Kind.Ring;
        pulseGraphic.raycastTarget = false;
        pulse.gameObject.SetActive(false);
        pointer = Rect("Pointer", root, 24, 30);
        pointer.pivot = new Vector2(0, 1);
        var arrow = pointer.gameObject.AddComponent<AgentCursorGraphic>();
        arrow.raycastTarget = false;
        badge = Rect("Agent badge", pointer, 43, 20);
        badge.anchorMin = badge.anchorMax = new Vector2(0, 1);
        badge.pivot = new Vector2(0, 1); badge.anchoredPosition = new Vector2(16, -23);
        var background = badge.gameObject.AddComponent<AgentCursorGraphic>();
        background.Shape = AgentCursorGraphic.Kind.Badge; background.raycastTarget = false;
        var label = Rect("Agent label", badge, 43, 20).gameObject.AddComponent<TextMeshProUGUI>();
        label.font = canvas.GetComponentsInChildren<TMP_Text>().FirstOrDefault(t => t != label && t.font != null)?.font;
        label.text = "Agent"; label.fontSize = 11; label.color = Color.white;
        label.alignment = TextAlignmentOptions.Center; label.raycastTarget = false;
        label.textWrappingMode = TextWrappingModes.NoWrap;
    }

    public void MoveTo(Vector2 screenPoint, bool click = false)
    {
        // Offscreen semantic actions have no truthful screen position.
        if (!float.IsFinite(screenPoint.x) || !float.IsFinite(screenPoint.y) || screenPoint.x < 0 || screenPoint.y < 0 || screenPoint.x > Screen.width || screenPoint.y > Screen.height) { Hide(); return; }
        var now = Time.unscaledTime;
        if (showing) Advance(now);
        if (!positioned) { position = screenPoint; velocity = Vector2.zero; positioned = true; }
        target = screenPoint;
        updatedAt = activeAt = now; showing = true;
        root.SetAsLastSibling();
        if (click) { pulsePoint = screenPoint; clickedAt = now; }
        else clickedAt = -10;
        Refresh();
    }

    public void Hide(bool resetPosition = false) { showing = false; if (resetPosition) positioned = false; velocity = Vector2.zero; clickedAt = -10; opacity.alpha = 0; pulse.gameObject.SetActive(false); }
    public void Dispose() => Destroy(gameObject);
    private void Update() { if (showing) Refresh(); }

    private void Refresh()
    {
        var now = Time.unscaledTime;
        Advance(now);
        pointer.anchoredPosition = Local(position);
        opacity.alpha = 1 - Mathf.SmoothStep(0, 1, Mathf.Clamp01((now - activeAt - 2) / .8f));
        if (opacity.alpha <= 0) { pulse.gameObject.SetActive(false); return; }
        // Flip only the badge near edges; the arrow tip stays at the real target.
        var right = Screen.width - position.x < 72 * canvas.scaleFactor;
        var bottom = position.y < 48 * canvas.scaleFactor;
        badge.pivot = new Vector2(right ? 1 : 0, bottom ? 0 : 1);
        badge.anchoredPosition = new Vector2(right ? -8 : 16, bottom ? 8 : -23);
        var pulseAge = (now - clickedAt) / .38f;
        pulse.gameObject.SetActive(pulseAge >= 0 && pulseAge < 1);
        if (pulseAge < 0 || pulseAge >= 1) return;
        pulse.anchoredPosition = Local(pulsePoint);
        pulse.localScale = Vector3.one * Mathf.Lerp(.35f, 1, pulseAge);
        pulseGraphic.color = new Color(.47f, .7f, 1, (1 - pulseAge) * .8f);
    }

    private void Advance(float now)
    {
        var elapsed = Mathf.Clamp(now - updatedAt, 0, .05f);
        updatedAt = now;
        if (elapsed <= 0) return;
        var offset = position - target;
        var decay = Mathf.Exp(-PursuitFrequency * elapsed);
        var impulse = (velocity + PursuitFrequency * offset) * elapsed;
        position = target + (offset + impulse) * decay;
        velocity = (velocity - PursuitFrequency * impulse) * decay;
        if ((position - target).sqrMagnitude < .01f && velocity.sqrMagnitude < 1) { position = target; velocity = Vector2.zero; }
    }

    private Vector2 Local(Vector2 point)
    {
        RectTransformUtility.ScreenPointToLocalPointInRectangle(root, point, canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera, out var local);
        return local;
    }

    private static RectTransform Rect(string name, Transform parent, float width, float height)
    {
        var rect = new GameObject(name, typeof(RectTransform)).GetComponent<RectTransform>();
        rect.SetParent(parent, false); rect.sizeDelta = new Vector2(width, height);
        rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(.5f, .5f);
        return rect;
    }
}

internal sealed class AgentCursorGraphic : MaskableGraphic
{
    internal enum Kind { Arrow, Ring, Badge }
    internal Kind Shape;
    private static readonly Vector2[] Arrow = { new(0, 0), new(0, -23), new(6, -17), new(11, -26), new(15, -24), new(10, -15), new(19, -14) };

    protected override void OnPopulateMesh(VertexHelper mesh)
    {
        mesh.Clear();
        if (Shape == Kind.Ring)
        {
            for (var i = 0; i < 40; i++) Segment(mesh, Circle(i, 21), Circle(i + 1, 21), 1.6f, color);
            return;
        }
        if (Shape == Kind.Badge)
        {
            var rect = rectTransform.rect;
            var center = rect.center;
            for (var i = 0; i < 40; i++)
            {
                var a = Circle(i, 1); var b = Circle(i + 1, 1);
                a = center + new Vector2(a.x * 9 + Mathf.Sign(a.x) * (rect.width / 2 - 9), a.y * rect.height / 2);
                b = center + new Vector2(b.x * 9 + Mathf.Sign(b.x) * (rect.width / 2 - 9), b.y * rect.height / 2);
                Triangle(mesh, center, a, b, new Color(.16f, .29f, .47f, .95f));
            }
            return;
        }
        for (var i = 0; i < Arrow.Length; i++) Segment(mesh, Arrow[i] + new Vector2(.7f, -1), Arrow[(i + 1) % Arrow.Length] + new Vector2(.7f, -1), 4, new Color(.04f, .09f, .16f, .35f));
        for (var i = 0; i < Arrow.Length; i++) Segment(mesh, Arrow[i], Arrow[(i + 1) % Arrow.Length], 2.4f, new Color(.94f, .97f, 1));
        var fill = new Color(.24f, .48f, .79f);
        Triangle(mesh, Arrow[0], Arrow[1], Arrow[2], fill);
        Triangle(mesh, Arrow[0], Arrow[2], Arrow[5], fill);
        Triangle(mesh, Arrow[0], Arrow[5], Arrow[6], fill);
        Triangle(mesh, Arrow[2], Arrow[3], Arrow[4], fill);
        Triangle(mesh, Arrow[2], Arrow[4], Arrow[5], fill);
    }

    private static Vector2 Circle(int index, float radius) => new Vector2(Mathf.Cos(index * Mathf.PI / 20), Mathf.Sin(index * Mathf.PI / 20)) * radius;
    private static void Triangle(VertexHelper mesh, Vector2 a, Vector2 b, Vector2 c, Color tint)
    {
        var index = mesh.currentVertCount;
        mesh.AddVert(a, tint, Vector2.zero); mesh.AddVert(b, tint, Vector2.zero); mesh.AddVert(c, tint, Vector2.zero);
        mesh.AddTriangle(index, index + 1, index + 2);
    }

    private static void Segment(VertexHelper mesh, Vector2 a, Vector2 b, float width, Color tint)
    {
        var direction = (b - a).normalized;
        var normal = new Vector2(-direction.y, direction.x) * width / 2;
        Triangle(mesh, a - normal, a + normal, b + normal, tint);
        Triangle(mesh, a - normal, b + normal, b - normal, tint);
    }
}
