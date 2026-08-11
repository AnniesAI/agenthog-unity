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
    /// </summary>
    internal sealed class Client
    {
        const string KeyAnonId = "agh_uid";
        const string KeySessionId = "agh_sid";
        const string KeyActivity = "agh_sts";
        const string KeySessionStart = "agh_sstart"; // SDK-internal, not part of the web key set
        const string KeyQueue = "agh_queue";
        const int HardQueueCap = 500;                // server per-batch max
        const long BackoffStartMs = 2_000;
        const long BackoffCapMs = 60_000;

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
        readonly Queue<string> deferredBatches = new Queue<string>(); // pre-serialized old-session/carry-over payloads
        Dictionary<string, object> pendingIdentify;        // { email?, traits? }

        // behavior — cumulative per session, sent on every flush
        bool mouseMoved;
        bool anyScroll;
        long? firstInteractionMs;

        bool inFlight;
        bool flushRequested;
        long backoffMs;
        long retryAtMs;
        long lastFlushAttemptMs;

        public string AnonId => anonId;
        public string SessionId => sessionId;
        public string CurrentPath => currentPath;

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

            long now = clock.NowMs;
            anonId = store.Get(KeyAnonId);
            if (string.IsNullOrEmpty(anonId))
            {
                anonId = this.newId();
                store.Set(KeyAnonId, anonId);
            }

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
                // a leftover queue from a dead session ships as-is under its ORIGINAL ids —
                // ingest upserts the session row and the sweep re-finalizes, so late is safe
                if (snapshot != null && SnapshotHasPayload(snapshot))
                {
                    var payload = BuildBatchFromSnapshot(snapshot);
                    if (payload != null) deferredBatches.Enqueue(payload);
                }
                store.Delete(KeyQueue);
            }

            // current device facts always win over anything adopted from disk
            foreach (var kv in ctx.AutoRegistered)
                registered[kv.Key] = kv.Value;
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
            EnsureSessionFresh(now);
            EmitLeaveIfOnScreen(now);
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
            if (pendingIdentify == null) pendingIdentify = new Dictionary<string, object>();
            if (!(pendingIdentify.TryGetValue("traits", out var t) && t is Dictionary<string, object> traits))
            {
                traits = new Dictionary<string, object>();
                pendingIdentify["traits"] = traits;
            }
            traits[name] = value;
            Enqueue("custom", "tag: " + name,
                value == null ? null : new Dictionary<string, object> { { "value", value } });
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

        public void Reset()
        {
            Flush();
            anonId = newId();
            store.Set(KeyAnonId, anonId);
            StartNewSession(clock.NowMs);
            store.Delete(KeyQueue);
            log("reset: new anonId " + anonId);
        }

        public void Flush()
        {
            long now = clock.NowMs;
            if (inFlight) { flushRequested = true; return; }
            if (now < retryAtMs) return;

            string payload;
            int sentEvents = 0;
            bool sentContext = false, sentIdentify = false, isDeferred;

            if (deferredBatches.Count > 0)
            {
                payload = deferredBatches.Peek();
                isDeferred = true;
            }
            else
            {
                if (queue.Count == 0 && pendingIdentify == null) return;
                sentEvents = queue.Count;
                sentContext = contextPending;
                sentIdentify = pendingIdentify != null;
                payload = BuildLiveBatch();
                isDeferred = false;
            }

            inFlight = true;
            lastFlushAttemptMs = now;
            log("send " + (isDeferred ? "(carry-over) " : "") + payload.Length + "B");
            transport.Send(ingestUrl, payload, cfg.UserAgent, (status, code) =>
                OnSendComplete(status, code, isDeferred, sentEvents, sentContext, sentIdentify));
        }

        /// <summary>Frame/interval driver: interval flush + backoff retries. Cheap; call often.</summary>
        public void Tick()
        {
            long now = clock.NowMs;
            if (inFlight || now < retryAtMs) return;
            bool havePayload = deferredBatches.Count > 0 || queue.Count > 0 || pendingIdentify != null;
            if (!havePayload) return;
            if (deferredBatches.Count > 0 || queue.Count >= cfg.MaxQueue ||
                now - lastFlushAttemptMs >= cfg.FlushIntervalMs)
                Flush();
        }

        public void OnPause()
        {
            long now = clock.NowMs;
            if (currentPath != null)
            {
                Enqueue("leave", "leave: " + currentPath, LeaveProps(now));
                screenEnteredMs = 0; // stint closed; Resume reopens it
            }
            Flush();
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
                queue.RemoveAt(0); // drop-oldest
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
            if (sessionId != null && (queue.Count > 0 || pendingIdentify != null))
            {
                // the old session's tail ships under the old ids
                deferredBatches.Enqueue(BuildLiveBatch());
                queue.Clear();
                pendingIdentify = null;
            }
            StartNewSession(now);
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
            store.Set(KeySessionId, sessionId);
            store.Set(KeyActivity, now.ToString(CultureInfo.InvariantCulture));
            store.Set(KeySessionStart, now.ToString(CultureInfo.InvariantCulture));
        }

        void Touch(long now)
        {
            lastActivityMs = now;
            store.Set(KeyActivity, now.ToString(CultureInfo.InvariantCulture));
        }

        void OnSendComplete(TransportStatus status, int code, bool wasDeferred,
                            int sentEvents, bool sentContext, bool sentIdentify)
        {
            inFlight = false;
            switch (status)
            {
                case TransportStatus.Success:
                    backoffMs = 0;
                    retryAtMs = 0;
                    if (wasDeferred)
                    {
                        deferredBatches.Dequeue();
                    }
                    else
                    {
                        queue.RemoveRange(0, Math.Min(sentEvents, queue.Count));
                        if (sentContext) contextPending = false;
                        if (sentIdentify) pendingIdentify = null;
                        PersistQueue();
                    }
                    break;
                case TransportStatus.PermanentError:
                    log("ingest rejected (" + code + "), dropping batch");
                    if (wasDeferred)
                    {
                        deferredBatches.Dequeue();
                    }
                    else
                    {
                        queue.RemoveRange(0, Math.Min(sentEvents, queue.Count));
                        if (sentIdentify) pendingIdentify = null;
                        // contextPending stays: if a later batch succeeds, the session still
                        // needs its context
                        PersistQueue();
                    }
                    break;
                case TransportStatus.RetryableError:
                    backoffMs = backoffMs == 0 ? BackoffStartMs : Math.Min(backoffMs * 2, BackoffCapMs);
                    retryAtMs = clock.NowMs + backoffMs;
                    log("send failed (" + code + "), retry in " + backoffMs + "ms");
                    break;
            }
            if (status != TransportStatus.RetryableError &&
                (flushRequested || deferredBatches.Count > 0 || queue.Count >= cfg.MaxQueue))
            {
                flushRequested = false;
                Flush();
            }
            else
            {
                flushRequested = false;
            }
        }

        // ---- batch building ----

        internal string BuildLiveBatch()
        {
            var root = new JsonObj()
                .Add("project", cfg.ProjectKey)
                .Add("anonId", anonId)
                .Add("sessionId", sessionId);
            if (contextPending)
                root.Add("context", BuildContext(firstPath, registered, landingExtras));
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

        // ---- carry-over persistence ----

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
                root.Add("context", BuildContext(Str(snap, "firstPath"), reg, extras));
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
