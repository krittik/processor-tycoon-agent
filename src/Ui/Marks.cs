using UnityEngine;

namespace ProcessorTycoonModApi;

// Small marks and icons drawn in code (no image files to ship): the Claude mark for credits (Ui.Mark) and white icons the
// game lacks, tinted by their Image colour (Ui.Icon, Ui.IconButton).
internal static class Marks
{
    public const string ClaudeUrl = "https://claude.com/claude-code";

    // The Claude mark (Anthropic's spark), for crediting Claude: twelve slightly uneven rays around a small core, in
    // Claude's orange.
    private static readonly (float angle, float length)[] ClaudeRays =
        { (7, .89f), (48, .92f), (81, .89f), (116, 1f), (148, .95f), (180, .94f), (214, .89f), (237, .96f), (266, .93f), (299, .92f), (316, .94f), (346, .91f) };
    private static Sprite? claude;

    public static Sprite Claude => claude != null ? claude : claude = Draw(64, new Color32(0xD9, 0x77, 0x57, 255), InsideClaude);

    // An envelope outline (messages, email), white.
    public static Sprite Envelope => envelope != null ? envelope : envelope = Draw(64, new Color32(255, 255, 255, 255), InsideEnvelope);
    private static Sprite? envelope;

    private static bool InsideEnvelope(float x, float y)
    {
        const float w = .92f, h = .66f, t = .15f;
        if (Mathf.Abs(x) > w || Mathf.Abs(y) > h) return false;
        if (Mathf.Abs(x) > w - t || Mathf.Abs(y) > h - t) return true;
        // The flap: two strokes from the top corners to just below the centre.
        return Segment(x, y, -w + t / 2, h - t / 2, 0, -.08f) < t / 2 || Segment(x, y, w - t / 2, h - t / 2, 0, -.08f) < t / 2;
    }

    private static float Segment(float x, float y, float ax, float ay, float bx, float by)
    {
        float dx = bx - ax, dy = by - ay;
        var along = Mathf.Clamp01(((x - ax) * dx + (y - ay) * dy) / (dx * dx + dy * dy));
        float px = ax + along * dx - x, py = ay + along * dy - y;
        return Mathf.Sqrt(px * px + py * py);
    }

    // Anti-aliased by 4×4 subsamples of inside(x, y) over unit coordinates (−1…1).
    private static Sprite Draw(int size, Color32 color, System.Func<float, float, bool> inside)
    {
        var texture = new Texture2D(size, size, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp, name = "Mark" };
        var pixels = new Color32[size * size];
        float centre = size / 2f, radius = size / 2f - 1;
        const int samples = 4;
        for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
            {
                var hits = 0;
                for (var sy = 0; sy < samples; sy++)
                    for (var sx = 0; sx < samples; sx++)
                        if (inside((x + (sx + .5f) / samples - centre) / radius, (y + (sy + .5f) / samples - centre) / radius)) hits++;
                pixels[y * size + x] = new Color32(color.r, color.g, color.b, (byte)(color.a * hits / (samples * samples)));
            }
        texture.SetPixels32(pixels);
        texture.Apply();
        return Sprite.Create(texture, new Rect(0, 0, size, size), new Vector2(.5f, .5f), 100);
    }

    private static bool InsideClaude(float x, float y)
    {
        if (x * x + y * y < .27f * .27f) return true;
        foreach (var (angle, length) in ClaudeRays)
        {
            float a = angle * Mathf.Deg2Rad, dx = Mathf.Cos(a), dy = Mathf.Sin(a);
            float along = x * dx + y * dy, across = Mathf.Abs(y * dx - x * dy);
            if (along >= 0 && along <= length && across <= Mathf.Lerp(.1f, .065f, along / length)) return true;
            float tx = x - dx * length, ty = y - dy * length;
            if (tx * tx + ty * ty <= .065f * .065f) return true;
        }
        return false;
    }
}
