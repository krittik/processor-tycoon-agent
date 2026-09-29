using System.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace ProcessorTycoonModApi;

// Marks the canvases of mods built on this API (found by this type name, so every mod's copy recognises the others).
internal sealed class ModApiOverlay : MonoBehaviour { }

// A mod's own layer above the game: a screen-space canvas that survives scene loads and is scaled like the game's main
// canvas, so sizes match the native UI at every resolution. Call Tick every frame (it also refreshes the theme).
internal sealed class Overlay
{
    public readonly Canvas Canvas;
    public readonly RectTransform Root;
    private Canvas? native;

    // Sorting order: the game's windows are below 32000; give each mod its own value (the Multiplayer mod 32600, the Agent
    // mod 32760) so their layers never interleave.
    public Overlay(string name, int sortingOrder)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(Canvas), typeof(GraphicRaycaster), typeof(ModApiOverlay));
        Object.DontDestroyOnLoad(go);
        Canvas = go.GetComponent<Canvas>();
        Canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        Canvas.sortingOrder = sortingOrder;
        Root = (RectTransform)go.transform;
    }

    public GameObject GameObject => Root.gameObject;
    public float Scale => Mathf.Max(.01f, Canvas.scaleFactor);
    public float Width => Screen.width / Scale;
    public float Height => Screen.height / Scale;
    public static bool InMenu => SceneManager.GetActiveScene().name == "Main Menu Scene";

    // The mouse in this canvas's units, from the bottom-left corner.
    public Vector2 Mouse => (Vector2)UnityEngine.Input.mousePosition / Scale;

    public void Tick()
    {
        Theme.Refresh();
        var scene = SceneManager.GetActiveScene();
        if (native == null || !native.isActiveAndEnabled || native.gameObject.scene != scene)
            native = Object.FindObjectsByType<Canvas>(FindObjectsSortMode.None)
                .Where(c => c.isRootCanvas && c.renderMode != RenderMode.WorldSpace && c.gameObject.scene == scene && c.GetComponent(nameof(ModApiOverlay)) == null)
                .OrderByDescending(c => c.GetComponentsInChildren<Selectable>(true).Length).FirstOrDefault();
        if (native == null) return;
        Canvas.scaleFactor = native.scaleFactor;
        Canvas.referencePixelsPerUnit = native.referencePixelsPerUnit;
    }

    public void Destroy() => Object.Destroy(Root.gameObject);
}
