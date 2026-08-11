using System;

namespace Brightmotion.AgentHog
{
    /// <summary>Configuration for <see cref="AgentHog.Init"/>.</summary>
    [Serializable]
    public sealed class AgentHogConfig
    {
        /// <summary>AgentHog host, e.g. "https://hog.brightmotion.io" (no trailing slash).</summary>
        public string Host;

        /// <summary>Project key, e.g. "ah_xxxxxxxx".</summary>
        public string ProjectKey;

        /// <summary>Product name used in the User-Agent string and registered props. Defaults to Application.productName.</summary>
        public string AppName;

        /// <summary>Defaults to Application.version.</summary>
        public string AppVersion;

        /// <summary>false → every AgentHog call is an inert no-op (dev/QA gating). Default true.</summary>
        public bool Enabled = true;

        /// <summary>Foreground flush interval. Default 10s.</summary>
        public float FlushIntervalSeconds = 10f;

        /// <summary>Flush when the queue reaches this many events. Default 20.</summary>
        public int MaxQueue = 20;

        /// <summary>Session idle timeout. MUST mirror the server's SESSION_IDLE_MINUTES. Default 30.</summary>
        public int IdleMinutes = 30;

        /// <summary>Emit "pageview: /scene-name" automatically on scene loads. Default true.</summary>
        public bool AutoTrackScenes = true;

        /// <summary>Autocapture uGUI clicks as "click: label" events. Default true.</summary>
        public bool AutoCaptureUiClicks = true;

        /// <summary>Log sends and drops via Debug.Log. Default false.</summary>
        public bool Debug = false;
    }
}
