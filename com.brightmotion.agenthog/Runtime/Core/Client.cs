using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Brightmotion.AgentHog.Core
{
    /// <summary>Engine-independent config subset the core runs on.</summary>
    internal sealed class CoreConfig
    {
        public string Host;              // no trailing slash
        public string ProjectKey;
        public string AppName = "";
        public string AppVersion = "";
        public string UserAgent = "";
        public int MaxQueue = 20;
        public long FlushIntervalMs = 10_000;
        public long IdleMs = 30 * 60_000;
        public InstallReferrerProvider InstallReferrerProvider;
        public long InstallReferrerTimeoutMs = 1_500;
        public bool DebugLog;
    }

    internal struct Event
    {
        public long Ts;
        public string Type;
        public string Name;
        public string Path;
        public Dictionary<string, object> Props; // registered props already merged in
    }

    /// <summary>
    /// Pure-C# core: identity, sessions, queue, batch building, retry/backoff, carry-over.
    /// Single-threaded by contract (Unity main thread); the facade marshals cross-thread calls.
    /// Wire shapes follow agent-hog CONTRACTS.md §"Wire format" byte-for-byte.
    ///
    /// Outbound design: live state (queue/identify/context) is PACKAGED — serialized into an
    /// immutable payload string under the ids it belongs to, appended to a persisted outbox —
    /// before anything is sent. Session rotation, Reset(), the hard cap, and crashes therefore
    /// can never corrupt or re-attribute a batch that is (or was) in flight: the payload
    /// already carries its own ids and survives on disk until a 2xx/4xx settles it.
    /// </summary>
    internal sealed class Client
    {
        const string KeyAnonId = "agh_uid";
        const string KeySessionId = "agh_sid";
        const string KeyActivity = "agh_sts";
        const string KeySessionStart = "agh_sstart"; // SDK-internal, not part of the web key set
        const string KeyQueue = "agh_queue";         // live (unpackaged) state snapshot
        const string KeyOutbox = "agh_outbox";       // packaged-but-unsettled payloads, FIFO
        const string KeyFlags = "agh_flags";         // cached flag ruleset json (same key set as web/RN)
        const string KeyExposed = "agh_exp";         // $exposure dedupe: { sid, keys } — one per flag per session
        const string KeyOverrides = "agh_flag_ovr";  // dev/test overrides: { flagKey: variant }
        const string KeyReferrerDone = "agh_ref";    // install-referrer delivered ('1') — once per install
        const string KeyAttribution = "agh_attr";    // cached attribution result (json wrapper)
        const int HardQueueCap = 500;                // server per-batch max
        const int OutboxCap = 20;                    // packaged batches kept for retry, drop-oldest
        const long BackoffStartMs = 2_000;
        const long BackoffCapMs = 60_000;
        const long SendTimeoutMs = 90_000;           // watchdog: a lost transport callback must not wedge us

        readonly CoreConfig cfg;
        readonly IKeyValueStore store;
        readonly IClock clock;
        readonly ITransport transport;
        readonly IContextProvider ctx;
        readonly Func<string> newId;
        readonly Action<string> log;
        readonly string ingestUrl;

        string anonId;
        string sessionId;
        long sessionStartMs;
        long lastActivityMs;
        bool contextPending;
        string firstPath;                                  // first screen path of the session
        string currentPath;
        long screenEnteredMs;
        readonly Dictionary<string, object> registered = new Dictionary<string, object>();
        readonly List<KeyValuePair<string, string>> landingExtras = new List<KeyValuePair<string, string>>();

        readonly List<Event> queue = new List<Event>();
        readonly List<string> outbox = new List<string>(); // head = oldest packaged payload
        Dictionary<string, object> pendingIdentify;        // { email?, traits? }

        // install attribution — context.install rides the install session's first context send
        string installReferrer;                            // raw referrer for context.install
        long? installClickTs;
        long? installBeginTs;
        bool installRequery;                               // cached-pending re-ask; stamps nothing
        List<KeyValuePair<string, string>> installUtms = new List<KeyValuePair<string, string>>();
        bool installReadPending;                           // provider invoked, callback outstanding
        long installGateUntilMs;                           // first flush holds until then (safety valve)
        bool installWindowClosed;                          // session context frozen — late reads retry next launch
        string installReadSessionId;                       // the read belongs to THIS session only
        InstallAttribution attribution;                    // last known result (cached or fresh)
        readonly List<Action<InstallAttribution>> attributionCallbacks = new List<Action<InstallAttribution>>();

        // behavior — cumulative per session, sent on every flush
        bool mouseMoved;
        bool anyScroll;
        long? firstInteractionMs;

        // feature flags (agent-hog docs/EXPERIMENTS_PLAN.md §3; bucketing spec in Flags.cs).
        // The ruleset loads LAZILY — from the store at construction, else fetched on the
        // first Flag()/FlagsReady() call or when an ingest response's x-agh-flags-rev moves.
        // Games that never touch flags generate zero flag traffic.
        FlagsConfig flagsConfig;
        readonly Dictionary<string, string> flagOverrides = new Dictionary<string, string>();
        string exposedSid = "";
        readonly HashSet<string> exposedKeys = new HashSet<string>();
        readonly List<Action> flagsReadyCallbacks = new List<Action>();
        bool flagsFetchInFlight;
        readonly string flagsUrl;

        bool inFlight;
        int sendToken;                                     // stale-callback guard (watchdog re-sends)
        long inFlightStartedMs;
        bool flushRequested;
        long backoffMs;
        long retryAtMs;
        long lastFlushAttemptMs;

        public string AnonId => anonId;
        public string SessionId => sessionId;
        public string CurrentPath => currentPath;
        public InstallAttribution Attribution => attribution;
        internal int AttributionCallbackCount => attributionCallbacks.Count;

        public Client(CoreConfig cfg, IKeyValueStore store, IClock clock, ITransport transport,
                      IContextProvider ctx, Func<string> newId = null, Action<string> log = null)
        {
            this.cfg = cfg;
            this.store = store;
            this.clock = clock;
            this.transport = transport;
            this.ctx = ctx;
            this.newId = newId ?? (() => Guid.NewGuid().ToString());
            this.log = log ?? (_ => { });
            ingestUrl = cfg.Host + "/ingest";
            flagsUrl = cfg.Host + "/sdk/flags?project=" + Uri.EscapeDataString(cfg.ProjectKey ?? "");

            // last-run flag state: cached ruleset (instant evaluation, no flicker), overrides,
            // and the session-scoped exposure dedupe set
            flagsConfig = FlagsConfig.Parse(store.Get(KeyFlags));
            if (Json.Parse(store.Get(KeyOverrides)) is Dictionary<string, object> ovr)
                foreach (var kv in ovr)
                    if (kv.Value is string sv)
                        flagOverrides[kv.Key] = sv;
            if (Json.Parse(store.Get(KeyExposed)) is Dictionary<string, object> exp
                && exp.TryGetValue("sid", out var esid) && esid is string es)
            {
                exposedSid = es;
                if (exp.TryGetValue("keys", out var ek) && ek is List<object> eks)
                    foreach (var item in eks)
                        if (item is string ks)
                            exposedKeys.Add(ks);
            }

            long now = clock.NowMs;
            anonId = store.Get(KeyAnonId);
            if (string.IsNullOrEmpty(anonId))
            {
                anonId = this.newId();
                store.Set(KeyAnonId, anonId);
            }

            // packaged payloads from a previous run ship first, under the ids frozen inside them
            if (Json.Parse(store.Get(KeyOutbox)) is List<object> stored)
                foreach (var item in stored)
                    if (item is string payload)
                        outbox.Add(payload);

            var snapshot = Json.Parse(store.Get(KeyQueue)) as Dictionary<string, object>;

            string storedSid = store.Get(KeySessionId);
            long storedSts = ParseLong(store.Get(KeyActivity));
            if (!string.IsNullOrEmpty(storedSid) && storedSts > 0 && now - storedSts < cfg.IdleMs)
            {
                // continue the persisted session (cold start within the idle window == web)
                sessionId = storedSid;
                long storedStart = ParseLong(store.Get(KeySessionStart));
                sessionStartMs = storedStart > 0 ? storedStart : now;
                lastActivityMs = now;
                store.Set(KeyActivity, now.ToString(CultureInfo.InvariantCulture));
                if (snapshot != null && Str(snapshot, "sessionId") == sessionId)
                    AdoptSnapshot(snapshot);
            }
            else
            {
                StartNewSession(now);
                // a leftover live queue from a dead session becomes a packaged batch under its
                // ORIGINAL ids — ingest upserts the session row and the sweep re-finalizes
                if (snapshot != null && SnapshotHasPayload(snapshot))
                {
                    var payload = BuildBatchFromSnapshot(snapshot);
                    if (payload != null) AppendToOutbox(payload);
                }
                store.Delete(KeyQueue);
            }

            // current device facts always win over anything adopted from disk
            foreach (var kv in ctx.AutoRegistered)
                registered[kv.Key] = kv.Value;

            LoadAttributionCache();
        }

        // ---- public surface (mirrors the RN client one-for-one) ----

        public void Capture(string name, Dictionary<string, object> props)
        {
            if (string.IsNullOrEmpty(name)) return;
            Enqueue("custom", name, props);
        }

        public void Screen(string path, string title)
        {
            long now = clock.NowMs;
            string before = sessionId;
            EnsureSessionFresh(now);
            // after an idle-gap rotation the old screen's stint ended with the OLD session;
            // an idle-inflated leave in the new session would be wrong (mirrors OnResume) —
            // and the navigation target, not the idled-on screen, is the new session's entry
            if (sessionId == before) EmitLeaveIfOnScreen(now);
            else firstPath = null;
            currentPath = NormalizePath(path);
            if (firstPath == null) firstPath = currentPath;
            screenEnteredMs = now;
            Enqueue("pageview", "pageview: " + currentPath,
                title == null ? null : new Dictionary<string, object> { { "title", title } });
        }

        public void Identify(string email, Dictionary<string, object> traits)
        {
            if (pendingIdentify == null) pendingIdentify = new Dictionary<string, object>();
            if (!string.IsNullOrEmpty(email)) pendingIdentify["email"] = email;
            if (traits != null && traits.Count > 0)
            {
                if (!(pendingIdentify.TryGetValue("traits", out var t) && t is Dictionary<string, object> merged))
                {
                    merged = new Dictionary<string, object>();
                    pendingIdentify["traits"] = merged;
                }
                foreach (var kv in traits) merged[kv.Key] = kv.Value;
            }
            Enqueue("identify", "identify", null);
        }

        public void Tag(string name, object value)
        {
            if (string.IsNullOrEmpty(name)) return;
            if (value == null) value = true; // tag("beta_user") == trait true, parity with web/RN
            if (pendingIdentify == null) pendingIdentify = new Dictionary<string, object>();
            if (!(pendingIdentify.TryGetValue("traits", out var t) && t is Dictionary<string, object> traits))
            {
                traits = new Dictionary<string, object>();
                pendingIdentify["traits"] = traits;
            }
            traits[name] = value;
            Enqueue("custom", "tag: " + name, new Dictionary<string, object> { { "value", value } });
        }

        public void Register(Dictionary<string, object> props)
        {
            if (props == null || props.Count == 0) return;
            foreach (var kv in props) registered[kv.Key] = kv.Value;
            contextPending = true; // context re-sends when registered props change (contract)
            PersistQueue();
        }

        public void SetLandingParams(Dictionary<string, string> extras)
        {
            if (extras == null || extras.Count == 0) return;
            bool changed = false;
            foreach (var kv in extras)
            {
                if (string.IsNullOrEmpty(kv.Key) || string.IsNullOrEmpty(kv.Value)) continue;
                bool exists = false;
                for (int i = 0; i < landingExtras.Count; i++)
                    if (landingExtras[i].Key == kv.Key)
                    {
                        exists = true;
                        if (landingExtras[i].Value != kv.Value)
                        {
                            landingExtras[i] = new KeyValuePair<string, string>(kv.Key, kv.Value);
                            changed = true;
                        }
                        break;
                    }
                if (!exists)
                {
                    landingExtras.Add(new KeyValuePair<string, string>(kv.Key, kv.Value));
                    changed = true;
                }
            }
            // extras only ever ship with the session's FIRST context send (the server's session
            // insert is onConflictDoNothing — a late re-send can't backfill utm columns), so
            // this never toggles contextPending; call it before the install session's first flush
            if (changed) PersistQueue();
        }

        public void EmitClick(string label, string selector, string text)
        {
            var props = new Dictionary<string, object> { { "selector", selector ?? "" } };
            if (!string.IsNullOrEmpty(text)) props["text"] = text;
            props["interactive"] = true;
            props["trusted"] = true;
            Enqueue("click", "click: " + label, props);
        }

        // ---- feature flags ----

        /// <summary>Assigned variant key for a flag, or null when the code fallback applies
        /// (no ruleset yet, unknown/disabled flag, outside the traffic allocation). Boolean
        /// flags resolve to "on"; the facade's FlagOn() collapses that to a bool.</summary>
        public string Flag(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            // rotate a stale session BEFORE the exposure dedupe reads sessionId — otherwise
            // the first read after an idle gap compares against the dead session and skips
            // the new session's $exposure (reading a flag is not activity: no Touch here)
            EnsureSessionFresh(clock.NowMs);
            // dev/test overrides FIRST — they exist precisely for the states where evaluation
            // can't answer (no ruleset yet, endpoint down, flag killed), and they never emit
            // $exposure or $ff/ props
            if (flagOverrides.TryGetValue(key, out var ov)) return ov;
            if (flagsConfig == null)
            {
                FetchFlags(); // lazy first load — null now, live once it lands (FlagsReady)
                return null;
            }
            var def = flagsConfig.Find(key);
            if (def == null) return null;
            string variant = FlagEval.Evaluate(def, anonId);
            if (variant != null) RecordExposure(def.Key, variant);
            return variant;
        }

        /// <summary>Invoke cb once a ruleset (cached or fetched) is loaded — or the fetch
        /// failed, so callers fall back to code defaults rather than hanging.</summary>
        public void FlagsReady(Action cb)
        {
            if (cb == null) return;
            if (flagsConfig != null)
            {
                cb();
                return;
            }
            flagsReadyCallbacks.Add(cb);
            FetchFlags();
        }

        /// <summary>Dev/test override, persisted; null variant clears. Never emits exposure data.</summary>
        public void OverrideFlag(string key, string variant)
        {
            if (string.IsNullOrEmpty(key)) return;
            if (variant == null) flagOverrides.Remove(key);
            else flagOverrides[key] = variant;
            if (flagOverrides.Count == 0)
            {
                store.Delete(KeyOverrides);
                return;
            }
            var obj = new JsonObj();
            foreach (var kv in flagOverrides) obj.Add(kv.Key, kv.Value);
            store.Set(KeyOverrides, Json.Serialize(obj));
        }

        void RecordExposure(string flagKey, string variant)
        {
            // $ff/<key> rides every subsequent event via registered props; deduped per value
            // so a Flag() call in an Update() loop doesn't mark context dirty every frame
            string prop = "$ff/" + flagKey;
            if (!(registered.TryGetValue(prop, out var cur) && cur is string cs && cs == variant))
            {
                registered[prop] = variant;
                contextPending = true;
                PersistQueue();
            }
            // one $exposure per flag per session — "code READ the flag", the analysis join key
            if (exposedSid != sessionId)
            {
                exposedSid = sessionId;
                exposedKeys.Clear();
            }
            if (!exposedKeys.Add(flagKey)) return;
            PersistExposed();
            Enqueue("custom", "$exposure", new Dictionary<string, object> { { "flag", flagKey }, { "variant", variant } });
        }

        void PersistExposed()
        {
            var keys = new List<object>(exposedKeys.Count);
            foreach (var k in exposedKeys) keys.Add(k);
            store.Set(KeyExposed, Json.Serialize(new JsonObj().Add("sid", exposedSid).Add("keys", keys)));
        }

        void FetchFlags()
        {
            if (flagsFetchInFlight) return;
            flagsFetchInFlight = true;
            transport.Fetch(flagsUrl, cfg.UserAgent, (code, body) =>
            {
                flagsFetchInFlight = false;
                if (code >= 200 && code < 300 && body != null)
                {
                    var parsed = FlagsConfig.Parse(body);
                    if (parsed != null)
                    {
                        flagsConfig = parsed;
                        store.Set(KeyFlags, body);
                        log("flags ruleset rev " + parsed.Rev + " (" + parsed.Flags.Count + " flag(s))");
                    }
                }
                else
                {
                    log("flags fetch failed (" + code + ")");
                }
                if (flagsReadyCallbacks.Count == 0) return;
                // resolve even on failure — callers fall back to code defaults
                var cbs = flagsReadyCallbacks.ToArray();
                flagsReadyCallbacks.Clear();
                foreach (var cb in cbs)
                {
                    try { cb(); }
                    catch (Exception e) { log("FlagsReady callback threw: " + e.Message); }
                }
            });
        }

        /// <summary>x-agh-flags-rev off an ingest response: refetch when the server moved past
        /// us. Rev "0" with no local ruleset means "project has no flags" — nothing to fetch.</summary>
        void OnFlagsRev(string rev)
        {
            if (rev == null) return;
            long r = FlagEval.ParseRev(rev);
            if (r < 0) return;
            if (flagsConfig == null ? r > 0 : r != flagsConfig.Rev) FetchFlags();
        }

        public void Reset()
        {
            // package the old identity's tail FIRST — its payload freezes the old ids, so
            // nothing of the previous person can ever ship under the new anonId
            PackageLiveState();
            anonId = newId();
            store.Set(KeyAnonId, anonId);
            StartNewSession(clock.NowMs);
            PersistQueue();
            Flush();
            log("reset: new anonId " + anonId);
        }

        /// <summary>force=true bypasses the backoff window (manual flush, backgrounding).</summary>
        public void Flush(bool force = false)
        {
            long now = clock.NowMs;
            if (inFlight) { flushRequested = true; return; }
            if (!force && now < retryAtMs) return;

            // hold the install session's first packaging until the referrer read resolves or
            // the valve blows — already-packaged payloads (carry-over) are free to ship.
            // Deliberately holds even for force (OnPause): packaging now would freeze the
            // context referrer-less and unbackfillable; a kill during the gate delivers the
            // full install batch from the snapshot on the next launch instead
            bool gateHolds = installReadPending && now < installGateUntilMs;
            if (outbox.Count == 0 && (gateHolds || !PackageLiveState())) return;

            string payload = outbox[0];
            int token = ++sendToken;
            inFlight = true;
            inFlightStartedMs = now;
            lastFlushAttemptMs = now;
            log("send " + payload.Length + "B (outbox depth " + outbox.Count + ")");
            transport.Send(ingestUrl, payload, cfg.UserAgent,
                (status, code, body, flagsRev) => OnSendComplete(status, code, body, flagsRev, token));
        }

        /// <summary>Frame/interval driver: interval flush, backoff retries, send watchdog.</summary>
        public void Tick()
        {
            long now = clock.NowMs;
            if (inFlight)
            {
                if (now - inFlightStartedMs > SendTimeoutMs)
                {
                    // transport callback lost (host destroyed mid-coroutine, etc.) — recover;
                    // the payload is still at the outbox head, a stale late callback is
                    // rejected by the token check
                    log("send watchdog fired after " + SendTimeoutMs + "ms — recovering");
                    inFlight = false;
                    backoffMs = backoffMs == 0 ? BackoffStartMs : backoffMs;
                    retryAtMs = now + backoffMs;
                }
                return;
            }
            if (now < retryAtMs) return;
            bool havePayload = outbox.Count > 0 || queue.Count > 0 || pendingIdentify != null;
            if (!havePayload) return;
            if (outbox.Count > 0 || queue.Count >= cfg.MaxQueue ||
                now - lastFlushAttemptMs >= cfg.FlushIntervalMs)
                Flush();
        }

        public void OnPause()
        {
            long now = clock.NowMs;
            string before = sessionId;
            EnsureSessionFresh(now); // a foreground idle gap rotates here, old tail packaged
            if (sessionId == before) EmitLeaveIfOnScreen(now);
            screenEnteredMs = 0; // stint closed (idempotent: quit-after-pause emits no 2nd leave)
            Flush(force: true);  // last chance before suspension — backoff must not block it
            PersistQueue();
            store.Save();
        }

        public void OnResume()
        {
            long now = clock.NowMs;
            string before = sessionId;
            EnsureSessionFresh(now);
            if (sessionId != before && currentPath != null)
            {
                // rotated while backgrounded: the current screen is the new session's entry
                screenEnteredMs = now;
                Enqueue("pageview", "pageview: " + currentPath, null);
            }
            else if (currentPath != null)
            {
                screenEnteredMs = now; // stint restarts; backgrounded time is never counted
            }
            Touch(now);
        }

        // ---- behavior telemetry (bot-scoring inputs) ----

        public void RecordInteraction()
        {
            long now = clock.NowMs;
            EnsureSessionFresh(now);
            if (firstInteractionMs == null) firstInteractionMs = Math.Max(0, now - sessionStartMs);
            Touch(now);
        }

        public void RecordDrag() { anyScroll = true; }
        public void RecordMouseMove() { mouseMoved = true; }
        public void RecordScrollWheel() { anyScroll = true; }

        // ---- install attribution ----

        /// <summary>
        /// Kick off the once-per-install referrer read. Called by the facade right after
        /// construction (never from the ctor — a synchronously-resolving provider must see a
        /// fully built client). The session's first flush waits for the callback, capped at
        /// InstallReferrerTimeoutMs. The done-flag guard fails CLOSED on a storage error
        /// (skip, don't re-read a ~90-day-old referrer onto a return session); the flag itself
        /// is written only after a 2xx confirmed the install context reached the server, so
        /// timeouts, dropped batches, and offline kills all retry next launch.
        /// </summary>
        public void BeginInstallReferrerRead()
        {
            if (cfg.InstallReferrerProvider == null) return;
            if (installReferrer != null) return; // adopted from a crashed run's snapshot, or a requery
            string done;
            try { done = store.Get(KeyReferrerDone); }
            catch (Exception e) { log("referrer done-flag unreadable — skipping read: " + e.Message); return; }
            if (!string.IsNullOrEmpty(done)) return;
            if (OutboxCarriesInstall()) return; // a packaged attempt from a previous launch is still settling
            installReadPending = true;
            installGateUntilMs = clock.NowMs + cfg.InstallReferrerTimeoutMs;
            installReadSessionId = sessionId;
            try
            {
                cfg.InstallReferrerProvider(OnInstallReferrerRead);
            }
            catch (Exception e)
            {
                // provider blew up == transient failure: no flag, retry next launch
                installReadPending = false;
                installGateUntilMs = 0;
                log("install referrer provider failed: " + e.Message);
            }
        }

        public void OnAttribution(Action<InstallAttribution> callback)
        {
            if (callback == null) return;
            if (!AttributionPossible()) return; // no result will ever arrive — don't pin the closure
            attributionCallbacks.Add(callback);
            DeliverAttribution();
        }

        /// <summary>A result is known, or something is still in flight that could produce one.</summary>
        bool AttributionPossible()
            => attribution != null || installReadPending || installReferrer != null || OutboxCarriesInstall();

        void OnInstallReferrerRead(InstallReferrerResult result)
        {
            if (!installReadPending) return; // double-callback guard
            installReadPending = false;
            installGateUntilMs = 0;
            if (result == null || string.IsNullOrEmpty(result.Referrer))
            {
                TrySet(KeyReferrerDone, "1"); // no referrer is a permanent answer, even late
                if (!AttributionPossible()) attributionCallbacks.Clear();
                return;
            }
            if (installWindowClosed || sessionId != installReadSessionId)
            {
                // the install session's context is frozen (or gone) — attribution must not
                // stamp a later session; retry next launch
                log("install referrer read lost the first-batch race — retrying next launch");
                return;
            }
            installReferrer = result.Referrer;
            installClickTs = result.ClickTs;
            installBeginTs = result.InstallBeginTs;
            installRequery = false;
            installUtms = Referrer.UtmParams(result.Referrer);
            contextPending = true;
            PersistQueue(); // a kill before the first flush must carry the referrer with the snapshot
            log("install referrer read (" + result.Referrer.Length + " chars)");
        }

        JsonObj BuildInstallObj()
        {
            var obj = new JsonObj().Add("referrer", installReferrer);
            if (installClickTs.HasValue) obj.Add("clickTs", installClickTs.Value);
            if (installBeginTs.HasValue) obj.Add("installBeginTs", installBeginTs.Value);
            if (installRequery) obj.Add("requery", true);
            return obj;
        }

        void AdoptInstall(Dictionary<string, object> install)
        {
            string referrer = Str(install, "referrer");
            if (string.IsNullOrEmpty(referrer)) return;
            installReferrer = referrer;
            long click = LongOf(install, "clickTs");
            installClickTs = click > 0 ? click : (long?)null;
            long begin = LongOf(install, "installBeginTs");
            installBeginTs = begin > 0 ? begin : (long?)null;
            installRequery = install.TryGetValue("requery", out var rq) && rq is bool rb && rb;
            installUtms = installRequery
                ? new List<KeyValuePair<string, string>>() : Referrer.UtmParams(referrer);
        }

        static JsonObj InstallObjFromDict(Dictionary<string, object> install)
        {
            var obj = new JsonObj().Add("referrer", Str(install, "referrer"));
            long click = LongOf(install, "clickTs");
            if (click > 0) obj.Add("clickTs", click);
            long begin = LongOf(install, "installBeginTs");
            if (begin > 0) obj.Add("installBeginTs", begin);
            if (install.TryGetValue("requery", out var rq) && rq is bool rb && rb) obj.Add("requery", true);
            return obj;
        }

        List<KeyValuePair<string, string>> MergedLandingExtras() => MergeExtras(installUtms, landingExtras);

        /// <summary>Explicit SetLandingParams keys win over referrer UTMs; the deep-link URL's
        /// own params keep top precedence via AppendExtraParams.</summary>
        static List<KeyValuePair<string, string>> MergeExtras(
            List<KeyValuePair<string, string>> utms, List<KeyValuePair<string, string>> extras)
        {
            if (utms.Count == 0) return extras;
            var merged = new List<KeyValuePair<string, string>>(utms.Count + extras.Count);
            foreach (var utm in utms)
            {
                var entry = utm;
                foreach (var kv in extras)
                    if (kv.Key == utm.Key) { entry = kv; break; }
                merged.Add(entry);
            }
            foreach (var kv in extras)
            {
                bool shadowed = false;
                foreach (var utm in utms)
                    if (utm.Key == kv.Key) { shadowed = true; break; }
                if (!shadowed) merged.Add(kv);
            }
            return merged;
        }

        /// <summary>Settle a delivered payload's install context: write the once-per-install
        /// flag (requery re-asks stamp nothing) and adopt the server's attribution answer.</summary>
        void OnInstallDelivered(string payload, string body)
        {
            var install = InstallFromPayload(payload);
            if (install == null) return;
            bool requery = install.TryGetValue("requery", out var rq) && rq is bool rb && rb;
            if (!requery) TrySet(KeyReferrerDone, "1");
            // delivered — later context re-sends this session must not resubmit it (the UTMs
            // stay so a re-sent landingUrl keeps its shape); a pending answer re-asks from
            // the cache next launch
            installReferrer = null;
            installClickTs = null;
            installBeginTs = null;
            installRequery = false;
            var result = ParseAttribution(body);
            if (result != null) AdoptAttribution(result, Str(install, "referrer"));
            else if (!AttributionPossible()) attributionCallbacks.Clear();
        }

        bool OutboxCarriesInstall()
        {
            foreach (string payload in outbox)
                if (InstallFromPayload(payload) != null)
                    return true;
            return false;
        }

        static Dictionary<string, object> InstallFromPayload(string payload)
        {
            if (payload == null || payload.IndexOf("\"install\":", StringComparison.Ordinal) < 0) return null;
            if (!(Json.Parse(payload) is Dictionary<string, object> root)) return null;
            if (!(root.TryGetValue("context", out var c) && c is Dictionary<string, object> context)) return null;
            return context.TryGetValue("install", out var i) ? i as Dictionary<string, object> : null;
        }

        static InstallAttribution ParseAttribution(string body)
        {
            if (string.IsNullOrEmpty(body)) return null;
            if (!(Json.Parse(body) is Dictionary<string, object> root)) return null;
            return root.TryGetValue("attribution", out var a)
                ? AttributionFromDict(a as Dictionary<string, object>) : null;
        }

        static InstallAttribution AttributionFromDict(Dictionary<string, object> dict)
        {
            if (dict == null || !(dict.TryGetValue("source", out var s) && s is string source)) return null;
            var result = new InstallAttribution { Source = source, Utm = new Dictionary<string, string>() };
            if (dict.TryGetValue("utm", out var u) && u is Dictionary<string, object> utm)
                foreach (var kv in utm)
                    if (kv.Value is string sv) result.Utm[kv.Key] = sv;
            if (dict.TryGetValue("meta", out var m)) result.Meta = m as Dictionary<string, object>;
            result.Pending = dict.TryGetValue("pending", out var p) && p is bool pb && pb;
            return result;
        }

        // The referrer is cached alongside a pending result so later launches can requery
        // once the project's decryption key exists; dropped as soon as the result resolves.
        void AdoptAttribution(InstallAttribution result, string referrer)
        {
            attribution = result;
            var wrapper = new JsonObj().Add("result", AttributionToObj(result));
            if (result.Pending && !string.IsNullOrEmpty(referrer)) wrapper.Add("referrer", referrer);
            TrySet(KeyAttribution, Json.Serialize(wrapper));
            DeliverAttribution();
        }

        /// <summary>A throwing store (WebGL quota, …) must never escape into the transport
        /// callback — an unsettled outbox head would re-send an already-delivered batch.</summary>
        void TrySet(string key, string value)
        {
            try { store.Set(key, value); }
            catch (Exception e) { log("store write failed (" + key + "): " + e.Message); }
        }

        static JsonObj AttributionToObj(InstallAttribution result)
        {
            return new JsonObj()
                .Add("source", result.Source)
                .Add("utm", result.Utm)
                .Add("meta", result.Meta)
                .Add("pending", result.Pending);
        }

        void DeliverAttribution()
        {
            if (attribution == null || attributionCallbacks.Count == 0) return;
            var callbacks = attributionCallbacks.ToArray();
            attributionCallbacks.Clear();
            foreach (var callback in callbacks)
            {
                try { callback(attribution); }
                catch (Exception e) { log("attribution callback threw: " + e.Message); }
            }
        }

        void LoadAttributionCache()
        {
            string stored;
            try { stored = store.Get(KeyAttribution); }
            catch (Exception) { return; }
            if (string.IsNullOrEmpty(stored)) return;
            var wrapper = Json.Parse(stored) as Dictionary<string, object>;
            var result = wrapper != null && wrapper.TryGetValue("result", out var r)
                ? AttributionFromDict(r as Dictionary<string, object>) : null;
            if (result == null)
            {
                try { store.Delete(KeyAttribution); } // corrupt — purge, stay unknown
                catch (Exception) { }
                return;
            }
            attribution = result;
            string referrer = wrapper != null ? Str(wrapper, "referrer") : null;
            if (result.Pending && !string.IsNullOrEmpty(referrer))
            {
                // re-ask on this session's first context send — the server may have its
                // decryption key by now; requery computes and returns but stamps nothing
                installReferrer = referrer;
                installRequery = true;
            }
        }

        // ---- internals ----

        void Enqueue(string type, string name, Dictionary<string, object> props)
        {
            long now = clock.NowMs;
            EnsureSessionFresh(now);

            Dictionary<string, object> merged = null;
            if (registered.Count > 0 || (props != null && props.Count > 0))
            {
                merged = new Dictionary<string, object>();
                foreach (var kv in registered) merged[kv.Key] = kv.Value;   // registered first,
                if (props != null) foreach (var kv in props) merged[kv.Key] = kv.Value; // event props win
            }

            queue.Add(new Event { Ts = now, Type = type, Name = name, Path = currentPath ?? "/", Props = merged });
            if (queue.Count > HardQueueCap)
            {
                queue.RemoveAt(0); // drop-oldest; only ever unsent events — in-flight ones live in the outbox
                log("queue cap hit, dropped oldest event");
            }
            Touch(now);
            PersistQueue();
            if (queue.Count >= cfg.MaxQueue) Flush();
        }

        void EmitLeaveIfOnScreen(long now)
        {
            if (currentPath == null || screenEnteredMs == 0) return;
            Enqueue("leave", "leave: " + currentPath, LeaveProps(now));
        }

        Dictionary<string, object> LeaveProps(long now)
        {
            double duration = screenEnteredMs > 0 ? (now - screenEnteredMs) / 1000.0 : 0;
            if (duration < 0) duration = 0;
            return new Dictionary<string, object> { { "duration_s", Math.Round(duration, 1) } };
        }

        void EnsureSessionFresh(long now)
        {
            if (lastActivityMs > 0 && now - lastActivityMs <= cfg.IdleMs) return;
            PackageLiveState(); // the old session's tail freezes under the old ids
            StartNewSession(now);
        }

        /// <summary>
        /// Serialize the live queue/identify/context into an immutable payload appended to the
        /// persisted outbox, and clear the live state. Returns false when there was nothing
        /// batchable (context alone never ships without events, matching web).
        /// </summary>
        bool PackageLiveState()
        {
            if (queue.Count == 0 && pendingIdentify == null) return false;
            if (contextPending) installWindowClosed = true; // context frozen; a late referrer read can't join it
            AppendToOutbox(BuildLiveBatch());
            queue.Clear();
            pendingIdentify = null;
            if (contextPending) contextPending = false; // context rides inside the payload now
            PersistQueue();
            return true;
        }

        void AppendToOutbox(string payload)
        {
            outbox.Add(payload);
            if (outbox.Count > OutboxCap)
            {
                // pathological offline runs: cap disk, keep newest — but never the in-flight
                // head, or OnSendComplete would settle (and stamp install state for) a
                // payload that was never sent
                outbox.RemoveAt(inFlight ? 1 : 0);
                log("outbox cap hit, dropped oldest batch");
            }
            PersistOutbox();
        }

        void StartNewSession(long now)
        {
            sessionId = newId();
            sessionStartMs = now;
            lastActivityMs = now;
            contextPending = true;
            firstPath = currentPath; // null on cold start until the first Screen()
            mouseMoved = false;
            anyScroll = false;
            firstInteractionMs = null;
            landingExtras.Clear();
            // attribution belongs to the install SESSION; later sessions read direct
            installReferrer = null;
            installClickTs = null;
            installBeginTs = null;
            installRequery = false;
            installUtms.Clear();
            installWindowClosed = false;
            store.Set(KeySessionId, sessionId);
            store.Set(KeyActivity, now.ToString(CultureInfo.InvariantCulture));
            store.Set(KeySessionStart, now.ToString(CultureInfo.InvariantCulture));
        }

        void Touch(long now)
        {
            lastActivityMs = now;
            store.Set(KeyActivity, now.ToString(CultureInfo.InvariantCulture));
        }

        void OnSendComplete(TransportStatus status, int code, string body, string flagsRev, int token)
        {
            if (token != sendToken || !inFlight)
            {
                log("stale transport callback ignored (token " + token + ")");
                return;
            }
            inFlight = false;
            switch (status)
            {
                case TransportStatus.Success:
                    backoffMs = 0;
                    retryAtMs = 0;
                    if (outbox.Count > 0)
                    {
                        OnInstallDelivered(outbox[0], body);
                        outbox.RemoveAt(0);
                    }
                    PersistOutbox();
                    OnFlagsRev(flagsRev);
                    break;
                case TransportStatus.PermanentError:
                    log("ingest rejected (" + code + "), dropping batch");
                    if (outbox.Count > 0) outbox.RemoveAt(0);
                    PersistOutbox();
                    break;
                case TransportStatus.RetryableError:
                    backoffMs = backoffMs == 0 ? BackoffStartMs : Math.Min(backoffMs * 2, BackoffCapMs);
                    retryAtMs = clock.NowMs + backoffMs;
                    log("send failed (" + code + "), retry in " + backoffMs + "ms");
                    break;
            }
            bool more = flushRequested || outbox.Count > 0 || queue.Count >= cfg.MaxQueue;
            flushRequested = false;
            if (status != TransportStatus.RetryableError && more) Flush();
        }

        // ---- batch building ----

        internal string BuildLiveBatch()
        {
            var root = new JsonObj()
                .Add("project", cfg.ProjectKey)
                .Add("anonId", anonId)
                .Add("sessionId", sessionId);
            if (contextPending)
            {
                var context = BuildContext(firstPath, registered, MergedLandingExtras());
                if (installReferrer != null) context.Add("install", BuildInstallObj());
                root.Add("context", context);
            }
            root.Add("behavior", new JsonObj()
                .Add("mouseMoved", mouseMoved)
                .Add("anyScroll", anyScroll)
                .Add("firstInteractionMs", firstInteractionMs.HasValue ? (object)firstInteractionMs.Value : null));
            if (pendingIdentify != null)
                root.Add("identify", DictToObj(pendingIdentify));
            var events = new List<object>(queue.Count);
            foreach (var e in queue) events.Add(EventToObj(e));
            root.Add("events", events);
            return Json.Serialize(root);
        }

        static JsonObj EventToObj(Event e)
        {
            var obj = new JsonObj()
                .Add("ts", e.Ts)
                .Add("type", e.Type)
                .Add("name", e.Name)
                .Add("path", e.Path);
            if (e.Props != null && e.Props.Count > 0) obj.Add("props", e.Props);
            return obj;
        }

        JsonObj BuildContext(string forFirstPath, Dictionary<string, object> reg,
                             List<KeyValuePair<string, string>> extras)
        {
            string deepLink = ctx.DeepLinkUrl ?? "";
            string landing = deepLink.Length > 0
                ? AppendExtraParams(deepLink, extras)
                : AppendExtraParams("app://" + Slug(cfg.AppName) + (forFirstPath ?? "/"), extras);
            var obj = new JsonObj()
                .Add("landingUrl", landing)
                .Add("referrer", deepLink)
                .Add("screen", ctx.ScreenSize)
                .Add("viewport", ctx.ViewportSize)
                .Add("tz", ctx.Timezone)
                .Add("lang", ctx.Language);
            if (reg != null && reg.Count > 0) obj.Add("registered", reg);
            // signals omitted: browser bot env-checks don't apply (plan §4)
            return obj;
        }

        /// <summary>Deep-link params keep precedence: extras are appended only for absent keys.</summary>
        static string AppendExtraParams(string url, List<KeyValuePair<string, string>> extras)
        {
            if (extras == null || extras.Count == 0) return url;
            var existing = new HashSet<string>();
            int q = url.IndexOf('?');
            if (q >= 0)
            {
                foreach (var pair in url.Substring(q + 1).Split('&'))
                {
                    int eq = pair.IndexOf('=');
                    existing.Add(Uri.UnescapeDataString(eq >= 0 ? pair.Substring(0, eq) : pair));
                }
            }
            var sb = new StringBuilder(url);
            bool hasQuery = q >= 0;
            foreach (var kv in extras)
            {
                if (existing.Contains(kv.Key)) continue;
                sb.Append(hasQuery ? '&' : '?');
                hasQuery = true;
                sb.Append(Uri.EscapeDataString(kv.Key)).Append('=').Append(Uri.EscapeDataString(kv.Value));
            }
            return sb.ToString();
        }

        // ---- persistence ----

        void PersistQueue()
        {
            if (queue.Count == 0 && pendingIdentify == null && !contextPending)
            {
                store.Delete(KeyQueue);
                return;
            }
            var root = new JsonObj()
                .Add("anonId", anonId)
                .Add("sessionId", sessionId)
                .Add("contextPending", contextPending)
                .Add("firstPath", firstPath)
                .Add("sessionStartMs", sessionStartMs)
                .Add("registered", registered)
                .Add("landingExtras", ExtrasToObj())
                .Add("install", installReferrer != null ? BuildInstallObj() : null)
                .Add("behavior", new JsonObj()
                    .Add("mouseMoved", mouseMoved)
                    .Add("anyScroll", anyScroll)
                    .Add("firstInteractionMs", firstInteractionMs.HasValue ? (object)firstInteractionMs.Value : null))
                .Add("identify", pendingIdentify != null ? DictToObj(pendingIdentify) : null);
            var events = new List<object>(queue.Count);
            foreach (var e in queue) events.Add(EventToObj(e));
            root.Add("events", events);
            store.Set(KeyQueue, Json.Serialize(root));
        }

        void PersistOutbox()
        {
            if (outbox.Count == 0) store.Delete(KeyOutbox);
            else store.Set(KeyOutbox, Json.Serialize(outbox));
        }

        JsonObj ExtrasToObj()
        {
            var obj = new JsonObj();
            foreach (var kv in landingExtras) obj.Add(kv.Key, kv.Value);
            return obj;
        }

        void AdoptSnapshot(Dictionary<string, object> snap)
        {
            contextPending = snap.TryGetValue("contextPending", out var cp) && cp is bool b && b;
            firstPath = Str(snap, "firstPath");
            long start = LongOf(snap, "sessionStartMs");
            if (start > 0) sessionStartMs = start;
            if (snap.TryGetValue("registered", out var reg) && reg is Dictionary<string, object> regd)
                foreach (var kv in regd) registered[kv.Key] = kv.Value;
            if (snap.TryGetValue("landingExtras", out var ex) && ex is Dictionary<string, object> exd)
                foreach (var kv in exd)
                    if (kv.Value is string sv) landingExtras.Add(new KeyValuePair<string, string>(kv.Key, sv));
            // a read from the crashed run whose install batch never flushed — resume it, don't re-read
            if (snap.TryGetValue("install", out var inst) && inst is Dictionary<string, object> instd)
                AdoptInstall(instd);
            if (snap.TryGetValue("behavior", out var beh) && beh is Dictionary<string, object> behd)
            {
                mouseMoved = behd.TryGetValue("mouseMoved", out var mm) && mm is bool mb && mb;
                anyScroll = behd.TryGetValue("anyScroll", out var asv) && asv is bool ab && ab;
                long fi = LongOf(behd, "firstInteractionMs");
                if (fi > 0) firstInteractionMs = fi;
            }
            if (snap.TryGetValue("identify", out var idf) && idf is Dictionary<string, object> idd && idd.Count > 0)
                pendingIdentify = idd;
            if (snap.TryGetValue("events", out var evs) && evs is List<object> list)
                foreach (var item in list)
                    if (item is Dictionary<string, object> ed)
                        queue.Add(SnapshotEvent(ed));
        }

        string BuildBatchFromSnapshot(Dictionary<string, object> snap)
        {
            string snapAnon = Str(snap, "anonId");
            string snapSession = Str(snap, "sessionId");
            if (snapAnon == null || snapSession == null) return null;
            var root = new JsonObj()
                .Add("project", cfg.ProjectKey)
                .Add("anonId", snapAnon)
                .Add("sessionId", snapSession);
            bool snapCtxPending = snap.TryGetValue("contextPending", out var cp) && cp is bool b && b;
            if (snapCtxPending)
            {
                Dictionary<string, object> reg =
                    snap.TryGetValue("registered", out var r) ? r as Dictionary<string, object> : null;
                var extras = new List<KeyValuePair<string, string>>();
                if (snap.TryGetValue("landingExtras", out var ex) && ex is Dictionary<string, object> exd)
                    foreach (var kv in exd)
                        if (kv.Value is string sv) extras.Add(new KeyValuePair<string, string>(kv.Key, sv));
                // the dead session read a referrer it never delivered: it ships here, under the
                // install session's ids — NOT re-read onto the next session
                Dictionary<string, object> instd =
                    snap.TryGetValue("install", out var inst) ? inst as Dictionary<string, object> : null;
                string snapReferrer = instd != null ? Str(instd, "referrer") : null;
                if (string.IsNullOrEmpty(snapReferrer))
                {
                    root.Add("context", BuildContext(Str(snap, "firstPath"), reg, extras));
                }
                else
                {
                    bool snapRequery = instd.TryGetValue("requery", out var rq) && rq is bool rb && rb;
                    var utms = snapRequery
                        ? new List<KeyValuePair<string, string>>() : Referrer.UtmParams(snapReferrer);
                    var context = BuildContext(Str(snap, "firstPath"), reg, MergeExtras(utms, extras));
                    context.Add("install", InstallObjFromDict(instd));
                    root.Add("context", context);
                }
            }
            if (snap.TryGetValue("behavior", out var beh) && beh is Dictionary<string, object> behd)
                root.Add("behavior", behd);
            if (snap.TryGetValue("identify", out var idf) && idf is Dictionary<string, object> idd && idd.Count > 0)
                root.Add("identify", idd);
            var events = snap.TryGetValue("events", out var evs) && evs is List<object> list
                ? list : new List<object>();
            root.Add("events", events);
            return Json.Serialize(root);
        }

        static bool SnapshotHasPayload(Dictionary<string, object> snap)
        {
            if (snap.TryGetValue("events", out var evs) && evs is List<object> list && list.Count > 0) return true;
            return snap.TryGetValue("identify", out var idf) && idf is Dictionary<string, object> idd && idd.Count > 0;
        }

        static Event SnapshotEvent(Dictionary<string, object> ed)
        {
            return new Event
            {
                Ts = LongOf(ed, "ts"),
                Type = Str(ed, "type") ?? "custom",
                Name = Str(ed, "name") ?? "",
                Path = Str(ed, "path") ?? "/",
                Props = ed.TryGetValue("props", out var p) ? p as Dictionary<string, object> : null,
            };
        }

        // ---- small helpers ----

        static JsonObj DictToObj(Dictionary<string, object> dict)
        {
            var obj = new JsonObj();
            foreach (var kv in dict) obj.Add(kv.Key, kv.Value);
            return obj;
        }

        internal static string NormalizePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return "/";
            return path[0] == '/' ? path : "/" + path;
        }

        internal static string Slug(string s)
        {
            if (string.IsNullOrEmpty(s)) return "app";
            var sb = new StringBuilder(s.Length);
            bool lastDash = false;
            foreach (char c in s.Trim().ToLowerInvariant())
            {
                if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9'))
                {
                    sb.Append(c);
                    lastDash = false;
                }
                else if (!lastDash && sb.Length > 0)
                {
                    sb.Append('-');
                    lastDash = true;
                }
            }
            while (sb.Length > 0 && sb[sb.Length - 1] == '-') sb.Length--;
            return sb.Length > 0 ? sb.ToString() : "app";
        }

        static string Str(Dictionary<string, object> d, string key)
            => d.TryGetValue(key, out var v) ? v as string : null;

        static long LongOf(Dictionary<string, object> d, string key)
        {
            if (!d.TryGetValue(key, out var v)) return 0;
            if (v is long l) return l;
            if (v is double dv) return (long)dv;
            return 0;
        }

        static long ParseLong(string s)
            => long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out long v) ? v : 0;
    }
}
