using System.Collections.Generic;
using System.Linq;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace ProcessorTycoonMod;

// Read rendered glyphs through foreground graphics, including non-interactive backgrounds.
// Frame-local caches keep repeated label/value/group lookups from rebuilding geometry.
internal static class UiReadability
{
    private static int frame = -1;
    private static readonly Dictionary<int, string> texts = new();
    private sealed class Layer { public Graphic Graphic = null!; public Rect Bounds; }
    private static Layer[] layers = System.Array.Empty<Layer>();
    private sealed class Geometry { public Vector2[] Points = null!; public int[] Triangles = null!; }
    private static readonly Dictionary<int, Geometry> geometry = new();
    private static readonly Dictionary<int, Canvas> sortingCanvases = new();

    private static void BeginFrame()
    {
        if (frame == Time.frameCount) return;
        frame = Time.frameCount; texts.Clear(); geometry.Clear(); sortingCanvases.Clear();
        layers = Resources.FindObjectsOfTypeAll<Graphic>().Where(g => g is Image or RawImage && g.gameObject.scene.IsValid() && g.isActiveAndEnabled && g.canvas != null && g.canvas.isActiveAndEnabled && !g.canvasRenderer.cull && g.depth >= 0 && g.color.a * g.canvasRenderer.GetColor().a * g.canvasRenderer.GetInheritedAlpha() >= .98f && g.GetComponentInParent<AgentOverlayMarker>() == null).Select(g => new Layer { Graphic = g, Bounds = Bounds(g) }).ToArray();
    }

    private static Rect Bounds(Graphic graphic)
    {
        var corners = new Vector3[4];
        graphic.rectTransform.GetWorldCorners(corners);
        var camera = GenericUi.CanvasCamera(graphic);
        var points = corners.Select(p => RectTransformUtility.WorldToScreenPoint(camera, p)).ToArray();
        return Rect.MinMaxRect(points.Min(p => p.x), points.Min(p => p.y), points.Max(p => p.x), points.Max(p => p.y));
    }

    public static bool Exposed(Graphic graphic, Rect clip)
    {
        BeginFrame();
        if (graphic.depth < 0) return false;
        foreach (var fraction in new[] { new Vector2(.5f, .5f), new Vector2(.15f, .5f), new Vector2(.85f, .5f), new Vector2(.5f, .15f), new Vector2(.5f, .85f) })
        {
            var point = new Vector2(Mathf.Lerp(clip.xMin, clip.xMax, fraction.x), Mathf.Lerp(clip.yMin, clip.yMax, fraction.y));
            if (!Covered(graphic, point)) return true;
        }
        return false;
    }

    public static string Text(TMP_Text text)
    {
        BeginFrame();
        if (texts.TryGetValue(text.GetInstanceID(), out var cached)) return cached;
        if (!GenericUi.Visible(text, out _) || text.depth < 0) return "";
        // Overflow text is not clipped to its own RectTransform; only masks/screen clip glyphs.
        var clip = GenericUi.ClipRect(text);
        if (text.havePropertiesChanged) text.ForceMeshUpdate();
        var result = new StringBuilder();
        var hidden = false;
        var visible = false;
        var camera = GenericUi.CanvasCamera(text);
        for (var index = 0; index < text.textInfo.characterCount; index++)
        {
            var character = text.textInfo.characterInfo[index];
            if (!character.isVisible)
            {
                if (visible && !hidden && char.IsWhiteSpace(character.character)) result.Append(character.character);
                continue;
            }
            var local = (character.bottomLeft + character.topRight) * .5f;
            var point = RectTransformUtility.WorldToScreenPoint(camera, text.transform.TransformPoint(local));
            if (!clip.Contains(point) || Covered(text, point)) { hidden = true; continue; }
            if (hidden) result.Append('…');
            hidden = false; visible = true;
            result.Append(character.character);
        }
        if (visible && hidden) result.Append('…');
        var content = visible ? result.ToString().Trim() : "";
        texts[text.GetInstanceID()] = content;
        return content;
    }

    private static bool Covered(Graphic graphic, Vector2 point)
    {
        var control = graphic.GetComponentInParent<Selectable>();
        foreach (var layer in layers)
        {
            var other = layer.Graphic;
            if (other == graphic || !layer.Bounds.Contains(point) || !GenericUi.ClipRect(other).Contains(point)) continue;
            // Native checkbox/dropdown hit graphics can cover the whole label rect while their sprite does not.
            if (control != null && other.GetComponentInParent<Selectable>() == control) continue;
            if (Above(other, graphic) && DrawnAt(other, point)) return true;
        }
        return false;
    }

    private static Canvas SortingCanvas(Graphic graphic)
    {
        var canvas = graphic.canvas;
        if (sortingCanvases.TryGetValue(canvas.GetInstanceID(), out var cached)) return cached;
        var nearest = canvas;
        while (!canvas.isRootCanvas && !canvas.overrideSorting)
        {
            var parent = canvas.transform.parent.GetComponentInParent<Canvas>();
            if (parent == null) break;
            canvas = parent;
        }
        sortingCanvases[nearest.GetInstanceID()] = canvas;
        return canvas;
    }

    private static bool Above(Graphic other, Graphic graphic)
    {
        var front = SortingCanvas(other);
        var back = SortingCanvas(graphic);
        if (front == back) return other.depth > graphic.depth;
        var frontRoot = front.rootCanvas;
        var backRoot = back.rootCanvas;
        var frontOverlay = frontRoot.renderMode == RenderMode.ScreenSpaceOverlay;
        var backOverlay = backRoot.renderMode == RenderMode.ScreenSpaceOverlay;
        if (frontOverlay != backOverlay) return frontOverlay;
        if (!frontOverlay && frontRoot.worldCamera != null && backRoot.worldCamera != null && frontRoot.worldCamera.depth != backRoot.worldCamera.depth) return frontRoot.worldCamera.depth > backRoot.worldCamera.depth;
        // renderOrder alone is not visual order for camera canvases (e.g. native modal order 32767).
        var frontLayer = SortingLayer.GetLayerValueFromID(front.sortingLayerID);
        var backLayer = SortingLayer.GetLayerValueFromID(back.sortingLayerID);
        if (frontLayer != backLayer) return frontLayer > backLayer;
        if (front.sortingOrder != back.sortingOrder) return front.sortingOrder > back.sortingOrder;
        if (frontRoot == backRoot) return other.depth > graphic.depth;
        return frontRoot.renderOrder > backRoot.renderOrder;
    }

    private static bool DrawnAt(Graphic graphic, Vector2 point)
    {
        if (!geometry.TryGetValue(graphic.GetInstanceID(), out var shape))
        {
            var mesh = graphic.canvasRenderer.GetMesh();
            if (mesh == null) return true; // A newly shown blocking layer may not have rebuilt its mesh yet.
            var camera = GenericUi.CanvasCamera(graphic);
            shape = new Geometry { Points = mesh.vertices.Select(v => RectTransformUtility.WorldToScreenPoint(camera, graphic.transform.TransformPoint(v))).ToArray(), Triangles = mesh.triangles };
            geometry[graphic.GetInstanceID()] = shape;
        }
        for (var index = 0; index + 2 < shape.Triangles.Length; index += 3)
        {
            var a = shape.Points[shape.Triangles[index]];
            var b = shape.Points[shape.Triangles[index + 1]];
            var c = shape.Points[shape.Triangles[index + 2]];
            if (Mathf.Abs(Cross(b - a, c - a)) < .001f) continue;
            var ab = Cross(b - a, point - a);
            var bc = Cross(c - b, point - b);
            var ca = Cross(a - c, point - c);
            if ((ab >= 0 && bc >= 0 && ca >= 0) || (ab <= 0 && bc <= 0 && ca <= 0)) return true;
        }
        return false;
    }

    private static float Cross(Vector2 a, Vector2 b) => a.x * b.y - a.y * b.x;

    public static void Dispose() { texts.Clear(); layers = System.Array.Empty<Layer>(); geometry.Clear(); sortingCanvases.Clear(); }
}
