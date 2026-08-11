using Brightmotion.AgentHog;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

/// <summary>
/// Results scene: the auto scene pageview fires "/results"; the manual Screen() call refines
/// it to "/results/win" or "/results/lose" so funnels can split on outcome.
/// </summary>
public class ResultsController : MonoBehaviour
{
    public Text resultText;

    void Start()
    {
        AgentHog.Screen(GameState.LastWin ? "/results/win" : "/results/lose");
        if (resultText != null)
            resultText.text = (GameState.LastWin ? "YOU WIN" : "TIME'S UP") +
                              $"\n{GameState.LastHits} hits";
    }

    public void OnRetry() => SceneManager.LoadScene("Game");

    public void OnMenu() => SceneManager.LoadScene("MainMenu");
}
