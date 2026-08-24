using UnityEngine;

/// <summary>Tiny cross-scene state for the example game.</summary>
public static class GameState
{
    public static bool LastWin;
    public static int LastHits;
    public static int LastReward;

    const string CoinsKey = "example_coins";

    /// <summary>
    /// Soft-currency balance
    /// </summary>
    public static int Coins
    {
        get => PlayerPrefs.GetInt(CoinsKey, 0);
        set { PlayerPrefs.SetInt(CoinsKey, value); PlayerPrefs.Save(); }
    }
}
