using UnityEngine;

namespace Brightmotion.AgentHog
{
    /// <summary>
    /// Inspector-first configuration: create via Assets → Create → AgentHog → Settings, save it
    /// as Resources/AgentHogSettings, and the SDK initializes itself on startup — no code.
    /// A Resources/AgentHogSettings.local asset (conventionally gitignored) overrides the
    /// committed one, so public repos can ship a blank config while devs keep real keys locally.
    /// Calling AgentHog.Init yourself always wins: auto-init skips when already initialized.
    /// </summary>
    [CreateAssetMenu(fileName = "AgentHogSettings", menuName = "AgentHog/Settings")]
    public sealed class AgentHogSettings : ScriptableObject
    {
        [Tooltip("AgentHog host, e.g. https://hog.brightmotion.io (no trailing slash)")]
        public string host = "";

        [Tooltip("Project key, e.g. ah_xxxxxxxx. Empty → SDK stays inert.")]
        public string projectKey = "";

        [Tooltip("Defaults to Application.productName when empty")]
        public string appName = "";

        public bool enabled = true;

        [Header("Automatic tracking")]
        public bool autoTrackScenes = true;
        public bool autoCaptureUiClicks = true;

        [Header("Tuning")]
        public float flushIntervalSeconds = 10f;
        public int maxQueue = 20;

        [Tooltip("Must mirror the server's SESSION_IDLE_MINUTES")]
        public int idleMinutes = 30;

        public bool debugLog = false;

        public AgentHogConfig ToConfig() => new AgentHogConfig
        {
            Host = host == null ? "" : host.Trim(),
            ProjectKey = projectKey == null ? "" : projectKey.Trim(),
            AppName = appName,
            Enabled = enabled && !string.IsNullOrEmpty(projectKey),
            FlushIntervalSeconds = flushIntervalSeconds,
            MaxQueue = maxQueue,
            IdleMinutes = idleMinutes,
            AutoTrackScenes = autoTrackScenes,
            AutoCaptureUiClicks = autoCaptureUiClicks,
            Debug = debugLog,
        };

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void AutoInit()
        {
            if (AgentHog.Enabled) return; // code-first Init already ran
            var settings = Resources.Load<AgentHogSettings>("AgentHogSettings.local")
                           ?? Resources.Load<AgentHogSettings>("AgentHogSettings");
            if (settings == null) return; // no asset → the game initializes in code (or not at all)
            AgentHog.Init(settings.ToConfig());
        }
    }
}
