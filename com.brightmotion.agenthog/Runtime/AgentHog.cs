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
        public const string SdkVersion = "0.1.0";

        static Client client;
        static AgentHogRunner runner;
        static int mainThreadId;
        static readonly ConcurrentQueue<Action> crossThreadCalls = new ConcurrentQueue<Action>();
        static bool warnedUninitialized;

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
                DebugLog = config.Debug,
            };
            Action<string> log = config.Debug ? (Action<string>)(m => Debug.Log("[AgentHog] " + m)) : (_ => { });

            var store = new PlayerPrefsStore();
            client = new Client(core, store, new SystemClock(), new WebRequestTransport(runner),
                                new UnityContextProvider(appName, appVersion), null, log);
            runner.Bind(client, config);

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

        /// <summary>Force-send the queue now (fire-and-forget).</summary>
        public static void Flush()
            => Run(() => client.Flush());

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
        }
    }
}
