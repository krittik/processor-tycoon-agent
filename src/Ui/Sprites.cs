using System;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace ProcessorTycoonModApi;

// Sprites from a mod's own PNG files, for icons the game does not have.
internal static class Sprites
{
    // A PNG embedded in the mod's assembly (<EmbeddedResource Include="icons\*.png" LogicalName="Icons.%(Filename).png" />).
    public static Sprite FromResource(Assembly assembly, string resourceName, float pixelsPerUnit = 100)
    {
        using var stream = assembly.GetManifestResourceStream(resourceName) ?? throw new InvalidOperationException("Missing embedded resource: " + resourceName);
        using var bytes = new MemoryStream();
        stream.CopyTo(bytes);
        return FromPng(bytes.ToArray(), resourceName, pixelsPerUnit);
    }

    public static Sprite FromPng(byte[] png, string name = "Mod sprite", float pixelsPerUnit = 100)
    {
        var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, name = name };
        ImageConversion.LoadImage(texture, png);
        return Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height), new Vector2(.5f, .5f), pixelsPerUnit);
    }

    // Frees a sprite made here (and its texture).
    public static void Destroy(Sprite sprite)
    {
        UnityEngine.Object.Destroy(sprite.texture);
        UnityEngine.Object.Destroy(sprite);
    }
}
