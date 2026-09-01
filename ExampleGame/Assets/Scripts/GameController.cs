using System.Collections.Generic;
using Brightmotion.AgentHog;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

/// <summary>
/// The toy loop: tap the moving target 5 times inside 15 seconds. Emits the custom events a
/// real game would (level_start / level_complete / level_failed with props, plus a
/// level_reward currency grant on wins); every tap on the target is ALSO an autocaptured
/// "click: TAP!" — both show up in AgentHog.
/// </summary>
public class GameController : MonoBehaviour
{
    public RectTransform target;
    public Text scoreText;
    public Text timerText;

    const int HitsToWin = 5;
    const float TimeLimit = 15f;
    const int CoinsPerHit = 20;

    int hits;
    float timeLeft = TimeLimit;
    bool over;

    void Start()
    {
        AgentHog.Capture("level_start", new Dictionary<string, object> { { "level", 1 } });
        UpdateHud();
    }

    void Update()
    {
        if (over) return;
        timeLeft -= Time.deltaTime;
        if (timeLeft <= 0f)
        {
            timeLeft = 0f;
            Finish(false);
        }
        UpdateHud();
    }

    public void OnTargetHit()
    {
        if (over) return;
        hits++;
        MoveTarget();
        UpdateHud();
        if (hits >= HitsToWin) Finish(true);
    }

    void Finish(bool won)
    {
        over = true;
        GameState.LastWin = won;
        GameState.LastHits = hits;
        AgentHog.Capture(won ? "level_complete" : "level_failed", new Dictionary<string, object>
        {
            { "level", 1 },
            { "hits", hits },
            { "duration_s", Mathf.Round((TimeLimit - timeLeft) * 10f) / 10f },
        });
        GrantReward(won);
        SceneManager.LoadScene("Results");
    }

    /// <summary>
    /// In-game economy convention (https://agenthog.io/docs/economy): currency moves
    /// are plain custom events with "currency" (game-defined name), "amount" (always positive
    /// — direction comes from the server-side mapping, not the sign), and "balance" (holding
    /// after the transaction). One aggregated event per run, never one per coin. Turn it into
    /// a report with: ah economy map --event level_reward --source
    /// </summary>
    void GrantReward(bool won)
    {
        GameState.LastReward = won ? hits * CoinsPerHit : 0;
        if (!won) return;
        GameState.Coins += GameState.LastReward;
        AgentHog.Capture("level_reward", new Dictionary<string, object>
        {
            { "currency", "coins" },
            { "amount", GameState.LastReward },
            { "balance", GameState.Coins },
            { "level", 1 },
        });
    }

    void MoveTarget()
    {
        var parent = (RectTransform)target.parent;
        Vector2 half = (parent.rect.size - target.rect.size) * 0.5f;
        target.anchoredPosition = new Vector2(Random.Range(-half.x, half.x), Random.Range(-half.y, half.y));
    }

    void UpdateHud()
    {
        if (scoreText != null) scoreText.text = $"hits {hits}/{HitsToWin}";
        if (timerText != null) timerText.text = timeLeft.ToString("0.0") + "s";
    }
}
