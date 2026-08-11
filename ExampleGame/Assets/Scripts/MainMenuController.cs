using System.Collections.Generic;
using Brightmotion.AgentHog;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

/// <summary>
/// Menu scene: demonstrates auto scene pageviews (no code needed), manual Screen() for an
/// in-scene panel, Identify/Reset for login/logout, and SetLandingParams for install
/// attribution. Buttons are intentionally a mix of labeled and unlabeled to show the click
/// autocapture label fallback (Text content → GameObject name).
/// </summary>
public class MainMenuController : MonoBehaviour
{
    public GameObject settingsPanel;
    public Text statusText;

    bool loggedIn;

    void Start()
    {
        UpdateStatus();
    }

    public void OnPlay()
    {
        SceneManager.LoadScene("Game");
    }

    public void OnToggleSettings()
    {
        bool opening = !settingsPanel.activeSelf;
        settingsPanel.SetActive(opening);
        // single-scene UI states are screens too — the manual Screen() pattern (plan §5)
        AgentHog.Screen(opening ? "/main-menu/settings" : "/main-menu");
    }

    public void OnLogin()
    {
        loggedIn = true;
        // games rarely have an email — a stable user_id trait is what stitches identity
        AgentHog.Identify(traits: new Dictionary<string, object> { { "user_id", "demo-player-42" } });
        AgentHog.Capture("login", new Dictionary<string, object> { { "method", "demo" } });
        UpdateStatus();
    }

    public void OnLogout()
    {
        loggedIn = false;
        AgentHog.Reset(); // sign-out: this device becomes a new anonymous person
        UpdateStatus();
    }

    public void OnSimulateInstallReferrer()
    {
        // in a real game this comes from the Play Install Referrer API, before first flush
        AgentHog.SetLandingParams(new Dictionary<string, string>
        {
            { "utm_source", "playstore" },
            { "utm_campaign", "example-demo" },
        });
        AgentHog.Capture("simulated_install_referrer");
    }

    void UpdateStatus()
    {
        if (statusText != null)
            statusText.text = (loggedIn ? "logged in as demo-player-42" : "anonymous") +
                              (AgentHog.Enabled ? "" : "  ·  AgentHog DISABLED (no key configured)");
    }
}
