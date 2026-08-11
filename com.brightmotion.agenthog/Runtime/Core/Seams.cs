using System;
using System.Collections.Generic;

namespace Brightmotion.AgentHog.Core
{
    /// <summary>Epoch-ms clock. Injected so tests drive time.</summary>
    internal interface IClock
    {
        long NowMs { get; }
    }

    /// <summary>
    /// Synchronous string KV store (PlayerPrefs in production). Save() flushes to disk —
    /// called on pause/quit, not per write.
    /// </summary>
    internal interface IKeyValueStore
    {
        string Get(string key);          // null when absent
        void Set(string key, string value);
        void Delete(string key);
        void Save();
    }

    internal enum TransportStatus
    {
        Success,          // 2xx
        RetryableError,   // network error / 5xx — keep the batch, back off
        PermanentError    // 4xx — drop the batch, it will never succeed
    }

    /// <summary>
    /// Async single-request transport (UnityWebRequest coroutine in production). The callback
    /// must be invoked on the same thread the client runs on (Unity main thread).
    /// </summary>
    internal interface ITransport
    {
        void Send(string url, string json, string userAgent, Action<TransportStatus, int> callback);
    }

    /// <summary>Device/environment facts the Unity layer supplies for session context.</summary>
    internal interface IContextProvider
    {
        string DeepLinkUrl { get; }                       // Application.absoluteURL, or ""
        string ScreenSize { get; }                        // "2778x1284"
        string ViewportSize { get; }                      // "1170x540"
        string Timezone { get; }                          // IANA id where the runtime gives one
        string Language { get; }                          // BCP-47-ish tag
        Dictionary<string, object> AutoRegistered { get; } // platform/app_version/os_version/...
    }
}
