using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using Brightmotion.AgentHog.Core;
using Brightmotion.AgentHog.Unity;
using UnityEngine;

namespace Brightmotion.AgentHog
{
    /// <summary>
    /// AgentHog analytics entry point. Call <see cref="Init"/> once (or configure an
    /// AgentHogSettings asset under Resources/); every other method is a safe no-op until then
    /// — and stays a no-op when the SDK is disabled, so call sites never need conditionals.
    /// All calls are main-thread safe: calls from worker threads are marshalled.
    /// </summary>
    public static class AgentHog
    {
        public const string SdkVersion = "0.2.0";

        static Client client;
        static AgentHogRunner runner;
        static int mainThreadId;
        static readonly ConcurrentQueue<Action> crossThreadCalls = new ConcurrentQueue<Action>();
        static readonly List<Action<InstallAttribution>> preInitAttributionCallbacks =
            new List<Action<InstallAttribution>>();
        static bool warnedUninitialized;

        /// <summary>
        /// Fallback install-referrer provider used when <see cref="AgentHogConfig.InstallReferrer"/>
        /// is unset. The installreferrer companion package registers itself here at load, which
        /// is what makes attribution work with the no-code settings-asset flow. An explicit
        /// config provider always wins.
        /// </summary>
        public static InstallReferrerProvider DefaultInstallReferrer;

        /// <summary>True when Init succeeded with a usable, enabled config.</summary>
        public static bool Enabled => client != null;

        public static string AnonId => client?.AnonId ?? "";
        public static string SessionId => client?.SessionId ?? "";

        public static void Init(AgentHogConfig config)
        {
            if (client != null)
            {
                Debug.LogWarning("[AgentHog] Init called twice; ignoring.");
                return;
            }
            if (config == null || !config.Enabled ||
                string.IsNullOrEmpty(config.Host) || string.IsNullOrEmpty(config.ProjectKey))
            {
                // disabled or unconfigured → inert; this is the supported env-gating path
                if (config != null && config.Debug)
                    Debug.Log("[AgentHog] disabled (no host/key or Enabled=false) — all calls are no-ops");
                return;
            }

            mainThreadId = Thread.CurrentThread.ManagedThreadId;

            string appName = string.IsNullOrEmpty(config.AppName) ? Application.productName : config.AppName;
            string appVersion = string.IsNullOrEmpty(config.AppVersion) ? Application.version : config.AppVersion;

            var go = new GameObject("[AgentHog]");
            UnityEngine.Object.DontDestroyOnLoad(go);
            runner = go.AddComponent<AgentHogRunner>();

            var core = new CoreConfig
            {
                Host = config.Host.TrimEnd('/'),
                ProjectKey = config.ProjectKey,
                AppName = appName,
                AppVersion = appVersion,
                UserAgent = BuildUserAgent(appName, appVersion),
                MaxQueue = Mathf.Max(1, config.MaxQueue),
                FlushIntervalMs = (long)(Mathf.Max(1f, config.FlushIntervalSeconds) * 1000f),
                IdleMs = Mathf.Max(1, config.IdleMinutes) * 60_000L,
                InstallReferrerProvider = config.InstallReferrer ?? DefaultInstallReferrer,
                InstallReferrerTimeoutMs = (long)(Mathf.Max(0f, config.InstallReferrerTimeoutSeconds) * 1000f),
                DebugLog = config.Debug,
            };
            Action<string> log = config.Debug ? (Action<string>)(m => Debug.Log("[AgentHog] " + m)) : (_ => { });

            var store = new PlayerPrefsStore();
            client = new Client(core, store, new SystemClock(), new WebRequestTransport(runner),
                                new UnityContextProvider(appName, appVersion), null, log);
            runner.Bind(client, config);
            client.BeginInstallReferrerRead();
            List<Action<InstallAttribution>> earlyCallbacks = null;
            lock (preInitAttributionCallbacks)
            {
                if (preInitAttributionCallbacks.Count > 0)
                {
                    earlyCallbacks = new List<Action<InstallAttribution>>(preInitAttributionCallbacks);
                    preInitAttributionCallbacks.Clear();
                }
            }
            if (earlyCallbacks != null)
                foreach (var callback in earlyCallbacks)
                    client.OnAttribution(callback);

            if (config.Debug)
                Debug.Log("[AgentHog] initialized: " + core.Host + " anon=" + client.AnonId + " session=" + client.SessionId);
        }

        /// <summary>Track a custom event (name goes over the wire verbatim).</summary>
        public static void Capture(string name, Dictionary<string, object> props = null)
            => Run(() => client.Capture(name, props));

        /// <summary>Manual screen view (emits "pageview: &lt;path&gt;"). Use for in-scene UI states.</summary>
        public static void Screen(string path, string title = null)
            => Run(() => client.Screen(path, title));

        /// <summary>
        /// Attach identity. Games without emails should pass a stable id in traits, e.g.
        /// <c>Identify(traits: new() { ["user_id"] = id })</c> — the server stitches on either.
        /// </summary>
        public static void Identify(string email = null, Dictionary<string, object> traits = null)
            => Run(() => client.Identify(email, traits));

        /// <summary>Set a single trait (sugar over Identify; also emits "tag: &lt;name&gt;").</summary>
        public static void Tag(string name, object value = null)
            => Run(() => client.Tag(name, value));

        /// <summary>Props merged into every subsequent event (e.g. build_channel, ab variant).</summary>
        public static void Register(Dictionary<string, object> props)
            => Run(() => client.Register(props));

        /// <summary>
        /// Append install-attribution params (e.g. Play Install Referrer UTMs) to the session's
        /// landing URL. Must be called before the install session's first flush; deep-link
        /// params keep precedence.
        /// </summary>
        public static void SetLandingParams(Dictionary<string, string> extras)
            => Run(() => client.SetLandingParams(extras));

        /// <summary>
        /// Assigned variant for a feature flag (deterministic per player — agent-hog
        /// CONTRACTS.md bucketing), or null when your code default applies: SDK disabled,
        /// ruleset not loaded yet (see <see cref="FlagsReady"/>), unknown/killed flag, or the
        /// player is outside the traffic allocation. The first read per flag per session
        /// records exposure automatically. Main thread only — worker-thread calls return null.
        /// </summary>
        public static string Flag(string key)
        {
            if (client == null || Thread.CurrentThread.ManagedThreadId != mainThreadId) return null;
            return client.Flag(key);
        }

        /// <summary>Boolean-flag sugar: true iff <see cref="Flag"/> resolves to "on".</summary>
        public static bool FlagOn(string key) => Flag(key) == "on";

        /// <summary>
        /// Runs the callback once the flag ruleset is available (cached from the last launch,
        /// or fetched) — or once the fetch failed, so gate your first read on it instead of
        /// polling. Fires immediately when the SDK is disabled: code defaults apply.
        /// </summary>
        public static void FlagsReady(Action callback)
        {
            if (callback == null) return;
            if (client == null)
            {
                callback();
                return;
            }
            Run(() => client.FlagsReady(callback));
        }

        /// <summary>Dev/test override, persisted across launches (null clears). Overrides win
        /// even before the ruleset loads and never emit exposure data.</summary>
        public static void OverrideFlag(string key, string variant)
            => Run(() => client.OverrideFlag(key, variant));

        /// <summary>
        /// Register a callback for the server-computed install attribution result. Fires once
        /// per callback, on the main thread: immediately when the result is already known
        /// (replayed from cache on later launches), else as soon as the install batch's
        /// response arrives. Never fires when there is no attribution (iOS, organic installs
        /// with no referrer read). Safe before Init — early registrations are queued and
        /// handed to the client when it initializes.
        /// </summary>
        public static void OnAttribution(Action<InstallAttribution> callback)
        {
            if (callback == null) return;
            lock (preInitAttributionCallbacks)
            {
                if (client == null)
                {
                    preInitAttributionCallbacks.Add(callback);
                    return;
                }
            }
            Run(() => client.OnAttribution(callback));
        }

        /// <summary>The cached install attribution result, or null while unknown.</summary>
        public static InstallAttribution GetAttribution() => client?.Attribution;

        /// <summary>Force-send the queue now (fire-and-forget; bypasses retry backoff).</summary>
        public static void Flush()
            => Run(() => client.Flush(force: true));

        /// <summary>Sign-out: new anonymous id + new session. The device becomes a new person.</summary>
        public static void Reset()
            => Run(() => client.Reset());

        static void Run(Action action)
        {
            if (client == null)
            {
                if (!warnedUninitialized)
                {
                    warnedUninitialized = true;
                    if (Debug.isDebugBuild)
                        Debug.Log("[AgentHog] not initialized (or disabled) — calls are no-ops");
                }
                return;
            }
            if (Thread.CurrentThread.ManagedThreadId == mainThreadId) action();
            else crossThreadCalls.Enqueue(action);
        }

        internal static void DrainCrossThreadCalls()
        {
            while (crossThreadCalls.TryDequeue(out var action))
            {
                try { action(); }
                catch (Exception e) { Debug.LogException(e); }
            }
        }

        internal static string BuildUserAgent(string appName, string appVersion)
        {
            // "<app>/<ver> AgentHogUnity/<sdk> (<platform> <os>)" — never ship Unity's default
            // UA: HTTP-library-looking UAs get binned as crawlers server-side (plan §7)
            string product = Regex.Replace(appName ?? "app", @"[^\w.\-]+", "-").Trim('-');
            if (product.Length == 0) product = "app";
            string version = Regex.Replace(appVersion ?? "0", @"[^\w.\-]+", "-");
            return product + "/" + version +
                   " AgentHogUnity/" + SdkVersion +
                   " (" + UnityContextProvider.PlatformName() + " " + SystemInfo.operatingSystem + ")";
        }

        /// <summary>Test hook: tear down the static singleton between editor test runs.</summary>
        internal static void ShutdownForTests()
        {
            if (runner != null) UnityEngine.Object.DestroyImmediate(runner.gameObject);
            runner = null;
            client = null;
            warnedUninitialized = false;
            lock (preInitAttributionCallbacks) preInitAttributionCallbacks.Clear();
        }
    }
}
