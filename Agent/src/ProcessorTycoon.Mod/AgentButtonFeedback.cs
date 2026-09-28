using UnityEngine;
using UnityEngine.EventSystems;

namespace ProcessorTycoonMod;

// Matches the native button's short ease-out press without referencing game classes.
internal sealed class AgentButtonFeedback : MonoBehaviour, IPointerDownHandler, IPointerUpHandler, IPointerExitHandler
{
    public RectTransform Target = null!;
    private float from = 1;
    private float to = 1;
    private float started;

    public void OnPointerDown(PointerEventData data) { if (data.button == PointerEventData.InputButton.Left) Animate(.9825f); }
    public void OnPointerUp(PointerEventData data) { if (data.button == PointerEventData.InputButton.Left) Animate(1); }
    public void OnPointerExit(PointerEventData data) => Animate(1);
    private void OnApplicationFocus(bool focused) { if (!focused) Animate(1); }
    private void OnDisable() { if (Target != null) Target.localScale = Vector3.one; from = to = 1; }

    private void Animate(float scale)
    {
        if (Target == null) return;
        from = Target.localScale.x; to = scale; started = Time.unscaledTime;
    }

    private void Update()
    {
        if (Target == null) return;
        var progress = Mathf.Clamp01((Time.unscaledTime - started) / .1f);
        var scale = Mathf.Lerp(from, to, 1 - Mathf.Pow(1 - progress, 4));
        Target.localScale = new Vector3(scale, scale, 1);
    }
}
