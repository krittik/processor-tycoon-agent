using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using BepInEx;
using UnityEngine;

namespace ProcessorTycoonModApi;

// Bug reports: one zip with the BepInEx log and the files a mod names, then a small window that says where it went,
// what is in it and what to do with it (Open folder, Report an issue).
internal static class BugReport
{
    // Saves BepInEx/<folder>/diagnostics-<time>.zip with LogOutput.log and the given files (absolute paths; entries keep
    // their path relative to the BepInEx folder, or their file name). Returns the zip's path.
    public static string Save(string folder, IEnumerable<string> files)
    {
        var dir = Path.Combine(Paths.BepInExRootPath, folder);
        Directory.CreateDirectory(dir);
        var zipPath = Path.Combine(dir, $"diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
        using var zip = new ZipArchive(File.Create(zipPath), ZipArchiveMode.Create);
        Add(zip, Path.Combine(Paths.BepInExRootPath, "LogOutput.log"));
        foreach (var file in files) Add(zip, file);
        return zipPath;
    }

    // Runs save and shows the result: the saved file with contents in its tooltip, or the error.
    public static Window Show(Overlay overlay, Func<string> save, string contents, string issuesUrl)
    {
        string zip = "", error = "";
        try { zip = save(); }
        catch (Exception e) { error = e.Message; Debug.LogWarning("Mod API: could not save diagnostics: " + e.Message); }
        var window = new Window(overlay, "Diagnostics", 420);
        var row = Ui.Row(window.Body, 6, 24);
        Ui.Label(row.transform, error.Length > 0 ? "Could not save diagnostics" : "Saved " + Path.GetFileName(zip), 15, error.Length > 0 ? Paint.Negative : Paint.Text);
        if (error.Length == 0) Ui.Info(row.transform, "What is in it", contents + " Check it for anything private before you share it.");
        Ui.Spacer(row.transform);
        Ui.Label(window.Body, error.Length > 0 ? error : "Attach it to your bug report.", 14, Paint.TextLow, wrap: true);
        var footer = window.Footer();
        if (error.Length == 0)
        {
            var folder = Path.GetDirectoryName(zip) ?? "";
            Ui.Button(footer.transform, "Open folder", () => Application.OpenURL("file:///" + folder.Replace('\\', '/')));
        }
        if (issuesUrl.Length > 0) Ui.Button(footer.transform, "Report an issue", () => Application.OpenURL(issuesUrl), cta: true);
        window.Show(new Vector2(0, -40));
        return window;
    }

    // BepInEx keeps the log open for writing: read with shared access.
    private static void Add(ZipArchive zip, string path)
    {
        if (!File.Exists(path)) return;
        var root = Paths.BepInExRootPath.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        var entry = path.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? path.Substring(root.Length).Replace('\\', '/') : Path.GetFileName(path);
        using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var target = zip.CreateEntry(entry, System.IO.Compression.CompressionLevel.Optimal).Open();
        source.CopyTo(target);
    }
}
