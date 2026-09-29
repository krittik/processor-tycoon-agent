using System;
using ProcessorTycoon.PopupSystem;
using UnityEngine;

namespace ProcessorTycoonModApi.Game;

// The game's own notification popup (the small cards that announce a finished factory or a competitor's CPU): a message,
// an optional second line (about 40 characters fit on each) and how long it stays, in seconds (−1: the game's default).
internal static class Notify
{
    public static bool Show(string message, string secondary = "", int duration = -1)
    {
        if (PopupManager.Instance == null) return false;
        try { PopupManager.Instance.InstantiateGenericNotification(message, secondary, duration); return true; }
        catch (Exception e) { Debug.LogWarning("Mod API: notification failed: " + e.Message); return false; }
    }
}
