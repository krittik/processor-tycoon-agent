using ProcessorTycoon.TimeSystem;
using UnityEngine;

namespace ProcessorTycoonModApi.Game;

// The game's own screens that overlays should step aside for (a mod's layer is drawn above everything the game shows).
internal static class GameScreen
{
    private static PauseMenu? pauseMenu;
    private static float nextSearch;

    // The Pause Menu (the start button at the bottom left, or Esc) is open: it covers the left side of the screen.
    public static bool PauseMenuOpen
    {
        get
        {
            if (pauseMenu == null && Time.unscaledTime >= nextSearch)
            {
                nextSearch = Time.unscaledTime + 1;
                pauseMenu = Object.FindFirstObjectByType<PauseMenu>(FindObjectsInactive.Include);
            }
            return pauseMenu != null && pauseMenu.IsOpen;
        }
    }
}
