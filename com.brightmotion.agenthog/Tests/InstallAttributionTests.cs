using System;
using System.Collections.Generic;
using Brightmotion.AgentHog.Core;
using NUnit.Framework;

namespace Brightmotion.AgentHog.Tests
{
    /// <summary>
    /// Install attribution (INSTALL_ATTRIBUTION_PLAN §2b/§3, RN-parity): once-per-install
    /// referrer read gated ahead of the first flush, plaintext UTMs into the landing-params
    /// pipe, raw string into context.install for the server decrypt, and the cached
    /// attribution result API. Envelope fixtures are shared with the RN SDK's
    /// install-referrer.test.ts and the server's attribution tests — all three predicates
    /// must agree.
    /// </summary>
    public class InstallAttributionTests
    {
        const long Minute = 60_000;

        static string Repeat(string s, int count)
        {
            var sb = new System.Text.StringBuilder(s.Length * count);
            for (int i = 0; i < count; i++) sb.Append(s);
            return sb.ToString();
        }

        static readonly string MetaContent =
            "{\"app\":1,\"t\":2,\"source\":{\"data\":\"" + Repeat("ab", 80) + "\",\"nonce\":\"" + Repeat("cd", 6) + "\"}}";
        static readonly string MetaReferrer =
            "utm_source=apps.facebook.com&utm_campaign=fb4a&utm_content=" + Uri.EscapeDataString(MetaContent);

        const string ResolvedBody =
            "{\"attribution\":{\"source\":\"meta_referrer\",\"utm\":{\"utm_source\":\"apps.facebook.com\"},\"meta\":{\"campaign_group_name\":\"us-launch\"},\"pending\":false}}";
        const string PendingBody =
            "{\"attribution\":{\"source\":\"meta_referrer\",\"utm\":{},\"meta\":null,\"pending\":true}}";

        static InstallReferrerProvider Resolving(string referrer, long? clickTs = null, long? installBeginTs = null)
            => callback => callback(referrer == null ? null : new InstallReferrerResult
            {
                Referrer = referrer,
                ClickTs = clickTs,
                InstallBeginTs = installBeginTs,
            });

        static Dictionary<string, string> Utms(string referrer)
        {
            var dict = new Dictionary<string, string>();
            foreach (var kv in Referrer.UtmParams(referrer)) dict[kv.Key] = kv.Value;
            return dict;
        }

        // ---- referrer parsing (fixtures shared with RN + server) ----

        [Test]
        public void PlaintextUtmPassthroughDecoded()
        {
            var utms = Utms("utm_source=web&utm_medium=cpc&utm_campaign=a%20b&other=x");
            Assert.AreEqual(3, utms.Count);
            Assert.AreEqual("web", utms["utm_source"]);
            Assert.AreEqual("cpc", utms["utm_medium"]);
            Assert.AreEqual("a b", utms["utm_campaign"]);
        }

        [Test]
        public void MetaEncryptedUtmContentIsDroppedPlaintextNeighborsSurvive()
        {
            var utms = Utms(Uri.UnescapeDataString(MetaReferrer));
            Assert.AreEqual(2, utms.Count);
            Assert.AreEqual("apps.facebook.com", utms["utm_source"]);
            Assert.AreEqual("fb4a", utms["utm_campaign"]);
        }

        [Test]
        public void OrdinaryUtmContentPassesThrough()
        {
            var utms = Utms("utm_source=web&utm_content=invite-abc");
            Assert.AreEqual("invite-abc", utms["utm_content"]);
        }

        [Test]
        public void NonHexEnvelopeIsNotMetaMatchingServer()
        {
            string content = "{\"source\":{\"data\":\"zz\",\"nonce\":\"gg\"}}";
            var utms = Utms("utm_source=x&utm_content=" + Uri.EscapeDataString(content));
            Assert.AreEqual(content, utms["utm_content"], "non-hex envelope must be kept");
        }

        [Test]
        public void LeadingWhitespaceEnvelopeIsMetaMatchingServer()
        {
            string content = " {\"source\":{\"data\":\"" + Repeat("ab", 20) + "\",\"nonce\":\"" + Repeat("cd", 6) + "\"}}";
            var utms = Utms("utm_source=x&utm_content=" + Uri.EscapeDataString(content));
            Assert.IsFalse(utms.ContainsKey("utm_content"), "leading-whitespace envelope must be dropped");
            Assert.AreEqual("x", utms["utm_source"]);
        }

        // ---- auto collection ----

        [Test]
        public void FirstLaunchShipsRawReferrerAndUtmsOnLandingUrl()
        {
            var rig = new Rig();
            rig.Config.InstallReferrerProvider = Resolving("utm_source=web&utm_campaign=summer");
            var client = rig.NewClient();
            client.BeginInstallReferrerRead();
            client.Capture("app_first_open", null);
            client.Flush();

            var batch = rig.Transport.Sent[0];
            Assert.AreEqual("utm_source=web&utm_campaign=summer", batch.Install()["referrer"]);
            Assert.IsFalse(batch.Install().ContainsKey("requery"));
            Assert.AreEqual("app://space-miner/?utm_source=web&utm_campaign=summer",
                batch.Context()["landingUrl"]);
            Assert.AreEqual("1", rig.Store.Data["agh_ref"], "2xx confirms the once-per-install read");
        }

        [Test]
        public void NativeTimestampsRideContextInstall()
        {
            var rig = new Rig();
            rig.Config.InstallReferrerProvider = Resolving("utm_source=web", 1_700_000_001, 1_700_000_002);
            var client = rig.NewClient();
            client.BeginInstallReferrerRead();
            client.Capture("x", null);
            client.Flush();

            var install = rig.Transport.Sent[0].Install();
            Assert.AreEqual(1_700_000_001L, install["clickTs"]);
            Assert.AreEqual(1_700_000_002L, install["installBeginTs"]);
        }

        [Test]
        public void MetaReferrerShipsRawButBlobStaysOffLandingUrl()
        {
            var rig = new Rig();
            rig.Config.InstallReferrerProvider = Resolving(MetaReferrer);
            var client = rig.NewClient();
            client.BeginInstallReferrerRead();
            client.Capture("x", null);
            client.Flush();

            var batch = rig.Transport.Sent[0];
            Assert.AreEqual(MetaReferrer, batch.Install()["referrer"], "raw string must ship untouched");
            Assert.AreEqual("app://space-miner/?utm_source=apps.facebook.com&utm_campaign=fb4a",
                batch.Context()["landingUrl"]);
        }

        [Test]
        public void ExplicitSetLandingParamsWinsOverReferrerUtms()
        {
            var rig = new Rig();
            rig.Config.InstallReferrerProvider = Resolving("utm_source=referrer-src&utm_medium=cpc");
            var client = rig.NewClient();
            client.BeginInstallReferrerRead();
            client.SetLandingParams(new Dictionary<string, string> { { "utm_source", "app-src" } });
            client.Capture("x", null);
            client.Flush();

            Assert.AreEqual("app://space-miner/?utm_source=app-src&utm_medium=cpc",
                rig.Transport.Sent[0].Context()["landingUrl"]);
        }

        [Test]
        public void SecondLaunchNeverCallsTheReaderAndSendsNoInstall()
        {
            var rig = new Rig();
            rig.Config.InstallReferrerProvider = Resolving("utm_source=web");
            var c1 = rig.NewClient();
            c1.BeginInstallReferrerRead();
            c1.Capture("x", null);
            c1.Flush();

            int calls = 0;
            rig.Config.InstallReferrerProvider = callback => calls++;
            rig.Transport.Sent.Clear();
            var c2 = rig.NewClient();
            c2.BeginInstallReferrerRead();
            c2.Capture("y", null);
            c2.Flush();
            Assert.AreEqual(0, calls);
            Assert.IsNull(rig.Transport.Sent[0].Install());
        }

        [Test]
        public void NullReadMarksDonePermanentlyNoInstallSent()
        {
            var rig = new Rig();
            rig.Config.InstallReferrerProvider = Resolving(null);
            var client = rig.NewClient();
            client.BeginInstallReferrerRead();
            client.Capture("x", null);
            client.Flush();

            Assert.IsNull(rig.Transport.Sent[0].Install());
            Assert.AreEqual("1", rig.Store.Data["agh_ref"], "no referrer is a permanent answer");
        }

        [Test]
        public void HungReadFirstFlushProceedsWithoutInstallAfterTimeout()
        {
            var rig = new Rig();
            rig.Config.InstallReferrerProvider = callback => { /* never resolves */ };
            var client = rig.NewClient();
            client.BeginInstallReferrerRead();
            client.Capture("x", null);

            client.Flush();
            rig.Transport.AssertNoBatchSent(0, "first flush must hold for the referrer read");
            rig.Clock.Advance(1_501);
            client.Flush();
            rig.Transport.AssertNoBatchSent(1);
            Assert.IsNull(rig.Transport.Sent[0].Install());
            Assert.IsFalse(rig.Store.Data.ContainsKey("agh_ref"), "a hung read must retry next launch");
        }

        [Test]
        public void ReadResolvingBeforeTimeoutJoinsTheHeldFirstFlush()
        {
            var rig = new Rig();
            Action<InstallReferrerResult> resolve = null;
            rig.Config.InstallReferrerProvider = callback => resolve = callback;
            var client = rig.NewClient();
            client.BeginInstallReferrerRead();
            client.Capture("x", null);
            client.Flush();
            rig.Transport.AssertNoBatchSent(0);

            resolve(new InstallReferrerResult { Referrer = "utm_source=web" });
            client.Flush();
            Assert.AreEqual("utm_source=web", rig.Transport.Sent[0].Install()["referrer"]);
        }

        [Test]
        public void ReadResolvingAfterFirstBatchIsDiscardedAndRetriedNextLaunch()
        {
            var rig = new Rig();
            Action<InstallReferrerResult> resolve = null;
            rig.Config.InstallReferrerProvider = callback => resolve = callback;
            rig.Config.InstallReferrerTimeoutMs = 5;
            var c1 = rig.NewClient();
            c1.BeginInstallReferrerRead();
            c1.Capture("x", null);
            rig.Clock.Advance(6);
            c1.Flush();
            Assert.IsNull(rig.Transport.Sent[0].Install());

            resolve(new InstallReferrerResult { Referrer = "utm_source=web" }); // lost the race
            Assert.IsFalse(rig.Store.Data.ContainsKey("agh_ref"), "not marked done");
            c1.Capture("y", null);
            c1.Flush();
            Assert.IsNull(rig.Transport.Sent[1].Context(), "no context re-send with install");

            rig.Config.InstallReferrerTimeoutMs = 1_500;
            rig.Config.InstallReferrerProvider = Resolving("utm_source=web");
            rig.Transport.Sent.Clear();
            var c2 = rig.NewClient();
            c2.BeginInstallReferrerRead();
            c2.Capture("z", null);
            c2.Flush();
            Assert.AreEqual("utm_source=web", rig.Transport.Sent[0].Install()["referrer"]);
        }

        [Test]
        public void StorageErrorOnDoneFlagFailsClosed()
        {
            var rig = new Rig();
            rig.Store.GetThrowsFor.Add("agh_ref");
            int calls = 0;
            rig.Config.InstallReferrerProvider = callback => calls++;
            var client = rig.NewClient();
            client.BeginInstallReferrerRead();
            client.Capture("x", null);
            client.Flush();

            Assert.AreEqual(0, calls, "an unreadable flag must skip, not re-read a ~90-day-old referrer");
            Assert.IsNull(rig.Transport.Sent[0].Install());
        }

        [Test]
        public void ThrowingProviderLeavesNoFlagSoNextLaunchRetries()
        {
            var rig = new Rig();
            rig.Config.InstallReferrerProvider = callback => throw new InvalidOperationException("SERVICE_UNAVAILABLE");
            var c1 = rig.NewClient();
            c1.BeginInstallReferrerRead();
            c1.Capture("x", null);
            c1.Flush();
            Assert.IsNull(rig.Transport.Sent[0].Install());
            Assert.IsFalse(rig.Store.Data.ContainsKey("agh_ref"));

            rig.Config.InstallReferrerProvider = Resolving("utm_source=web");
            rig.Transport.Sent.Clear();
            var c2 = rig.NewClient();
            c2.BeginInstallReferrerRead();
            c2.Capture("y", null);
            c2.Flush();
            Assert.AreEqual("utm_source=web", rig.Transport.Sent[0].Install()["referrer"]);
        }

        [Test]
        public void SessionRotationDropsInstallAndItsUtms()
        {
            var rig = new Rig();
            rig.Config.InstallReferrerProvider = Resolving("utm_source=web&utm_campaign=summer");
            // never settles: install must survive in live state until rotation clears it
            rig.Transport.AutoComplete = false;
            var client = rig.NewClient();
            client.BeginInstallReferrerRead();
            client.Capture("x", null);
            client.Flush();
            Assert.NotNull(rig.Transport.Sent[0].Install());
            StringAssert.Contains("utm_source=web", (string)rig.Transport.Sent[0].Context()["landingUrl"]);

            rig.Clock.Advance(31 * Minute);
            client.Capture("y", null); // rotates
            rig.Transport.AutoComplete = true;
            rig.Transport.CompleteOldest(TransportStatus.Success, 204);
            client.Flush();

            var rotated = rig.Transport.Sent[rig.Transport.Sent.Count - 1];
            Assert.NotNull(rotated.Context(), "fresh session re-sends context");
            Assert.IsNull(rotated.Install(), "attribution belongs to the install session");
            Assert.AreEqual("app://space-miner/", rotated.Context()["landingUrl"], "no campaign carry-over");
        }

        [Test]
        public void NoProviderConfiguredNothingHappens()
        {
            var rig = new Rig();
            var client = rig.NewClient();
            client.BeginInstallReferrerRead();
            client.Capture("x", null);
            client.Flush();
            Assert.IsNull(rig.Transport.Sent[0].Install());
            Assert.IsFalse(rig.Store.Data.ContainsKey("agh_ref"));
        }

        [Test]
        public void PackagedInstallInOutboxSuppressesAReReadOnRelaunch()
        {
            var rig = new Rig();
            rig.Config.InstallReferrerProvider = Resolving("utm_source=web");
            rig.Transport.NextStatus = TransportStatus.RetryableError;
            rig.Transport.NextCode = 0;
            var c1 = rig.NewClient();
            c1.BeginInstallReferrerRead();
            c1.Capture("x", null);
            c1.Flush(); // packaged with install, send failed → stays in the outbox on disk

            int calls = 0;
            rig.Config.InstallReferrerProvider = callback => calls++;
            rig.Transport.NextStatus = TransportStatus.Success;
            rig.Transport.NextCode = 204;
            rig.Transport.Sent.Clear();
            var c2 = rig.NewClient();
            c2.BeginInstallReferrerRead();
            Assert.AreEqual(0, calls, "the persisted install payload is the pending attempt");
            c2.Flush(); // carry-over install batch finally delivers
            Assert.NotNull(rig.Transport.Sent[0].Install());
            Assert.AreEqual("1", rig.Store.Data["agh_ref"]);
        }

        // ---- attribution result (OnAttribution / GetAttribution) ----

        static Func<SentBatch, (TransportStatus, int, string)> InstallResponder(string body)
            => batch => batch.Install() != null
                ? (TransportStatus.Success, 200, body)
                : (TransportStatus.Success, 204, null);

        [Test]
        public void InstallBatchResponseFeedsPreRegisteredCallbackAndCache()
        {
            var rig = new Rig();
            rig.Config.InstallReferrerProvider = Resolving("utm_source=apps.facebook.com");
            rig.Transport.Respond = InstallResponder(ResolvedBody);
            var client = rig.NewClient();
            client.BeginInstallReferrerRead();
            var seen = new List<InstallAttribution>();
            client.OnAttribution(a => seen.Add(a));
            client.Capture("x", null);
            client.Flush();

            Assert.AreEqual(1, seen.Count);
            Assert.AreEqual("meta_referrer", seen[0].Source);
            Assert.AreEqual("apps.facebook.com", seen[0].Utm["utm_source"]);
            Assert.AreEqual("us-launch", seen[0].Meta["campaign_group_name"]);
            Assert.IsFalse(seen[0].Pending);
            Assert.AreSame(seen[0], client.Attribution);

            var wrapper = Core.Json.Parse(rig.Store.Data["agh_attr"]) as Dictionary<string, object>;
            var cached = wrapper["result"] as Dictionary<string, object>;
            Assert.AreEqual("meta_referrer", cached["source"]);
            Assert.IsFalse(wrapper.ContainsKey("referrer"), "resolved result must not keep the referrer");
        }

        [Test]
        public void CallbackRegisteredAfterTheResultFiresImmediatelyOnce()
        {
            var rig = new Rig();
            rig.Config.InstallReferrerProvider = Resolving("utm_source=web");
            rig.Transport.Respond = InstallResponder(ResolvedBody);
            var client = rig.NewClient();
            client.BeginInstallReferrerRead();
            client.Capture("x", null);
            client.Flush();

            var seen = new List<InstallAttribution>();
            client.OnAttribution(a => seen.Add(a));
            Assert.AreEqual(1, seen.Count);
            client.Capture("y", null);
            client.Flush();
            Assert.AreEqual(1, seen.Count, "fires once per callback");
        }

        [Test]
        public void LaterLaunchesReplayFromCacheWithNoInstallBatch()
        {
            var rig = new Rig();
            rig.Config.InstallReferrerProvider = Resolving("utm_source=web");
            rig.Transport.Respond = InstallResponder(ResolvedBody);
            var c1 = rig.NewClient();
            c1.BeginInstallReferrerRead();
            c1.Capture("x", null);
            c1.Flush();

            rig.Transport.Sent.Clear();
            rig.Config.InstallReferrerProvider = Resolving("never-used");
            var c2 = rig.NewClient();
            c2.BeginInstallReferrerRead();
            var seen = new List<InstallAttribution>();
            c2.OnAttribution(a => seen.Add(a));
            Assert.AreEqual(1, seen.Count, "replayed from cache");
            Assert.AreEqual("meta_referrer", c2.Attribution.Source);
            rig.Transport.AssertNoBatchSent(0, "nothing sent to produce it");
        }

        [Test]
        public void NoInstallCallbacksNeverFireAndAttributionIsNull()
        {
            var rig = new Rig();
            var client = rig.NewClient();
            var seen = new List<InstallAttribution>();
            client.OnAttribution(a => seen.Add(a));
            client.Capture("x", null);
            client.Flush();
            Assert.AreEqual(0, seen.Count);
            Assert.IsNull(client.Attribution);
        }

        [Test]
        public void CorruptCacheIsPurgedNotDelivered()
        {
            var rig = new Rig();
            rig.Store.Data["agh_attr"] = "{\"nope\":true}";
            var client = rig.NewClient();
            Assert.IsNull(client.Attribution);
            Assert.IsFalse(rig.Store.Data.ContainsKey("agh_attr"), "corrupt cache must be purged");
        }

        [Test]
        public void AttributionSurvivesReset()
        {
            var rig = new Rig();
            rig.Config.InstallReferrerProvider = Resolving("utm_source=web");
            rig.Transport.Respond = InstallResponder(ResolvedBody);
            var client = rig.NewClient();
            client.BeginInstallReferrerRead();
            client.Capture("x", null);
            client.Flush();

            client.Reset();
            Assert.NotNull(client.Attribution, "the result is install-scoped, not person-scoped");
            Assert.IsTrue(rig.Store.Data.ContainsKey("agh_attr"));
            Assert.AreEqual("1", rig.Store.Data["agh_ref"]);
        }

        // ---- delivery confirmation (flag written only on 2xx) ----

        [Test]
        public void DroppedInstallBatchLeavesNoFlagNextLaunchResends()
        {
            var rig = new Rig();
            rig.Config.InstallReferrerProvider = Resolving("utm_source=web");
            rig.Transport.NextStatus = TransportStatus.PermanentError;
            rig.Transport.NextCode = 400;
            var c1 = rig.NewClient();
            c1.BeginInstallReferrerRead();
            c1.Capture("x", null);
            c1.Flush();
            Assert.NotNull(rig.Transport.Sent[0].Install(), "it tried");
            Assert.IsFalse(rig.Store.Data.ContainsKey("agh_ref"), "delivery wasn't confirmed");

            rig.Transport.NextStatus = TransportStatus.Success;
            rig.Transport.NextCode = 204;
            rig.Transport.Sent.Clear();
            var c2 = rig.NewClient();
            c2.BeginInstallReferrerRead();
            c2.Capture("y", null);
            c2.Flush();
            Assert.AreEqual("utm_source=web", rig.Transport.Sent[0].Install()["referrer"]);
            Assert.AreEqual("1", rig.Store.Data["agh_ref"]);
        }

        [Test]
        public void RetryableFailureKeepsTheInstallBatchFlagLandsWithTheRetry()
        {
            var rig = new Rig();
            rig.Config.InstallReferrerProvider = Resolving("utm_source=web");
            rig.Transport.NextStatus = TransportStatus.RetryableError;
            rig.Transport.NextCode = 500;
            var client = rig.NewClient();
            client.BeginInstallReferrerRead();
            client.Capture("x", null);
            client.Flush();
            Assert.IsFalse(rig.Store.Data.ContainsKey("agh_ref"));

            rig.Transport.NextStatus = TransportStatus.Success;
            rig.Transport.NextCode = 204;
            client.Flush(force: true); // bypass backoff
            Assert.NotNull(rig.Transport.Sent[1].Install(), "the packaged install batch retries as-is");
            Assert.AreEqual("1", rig.Store.Data["agh_ref"]);
        }

        // ---- pending requery ----

        const string PendingWrapper =
            "{\"result\":{\"source\":\"meta_referrer\",\"utm\":{},\"meta\":null,\"pending\":true},\"referrer\":\"META_REF\"}";

        [Test]
        public void CachedPendingReferrerReAsksWithoutStampingResolvedReplacesCache()
        {
            var rig = new Rig();
            rig.Store.Data["agh_ref"] = "1";
            rig.Store.Data["agh_attr"] = PendingWrapper;
            rig.Transport.Respond = InstallResponder(ResolvedBody);
            var client = rig.NewClient();
            client.BeginInstallReferrerRead();
            client.Capture("x", null);
            client.Flush();

            var install = rig.Transport.Sent[0].Install();
            Assert.AreEqual("META_REF", install["referrer"]);
            Assert.AreEqual(true, install["requery"]);
            Assert.AreEqual("meta_referrer", client.Attribution.Source);
            Assert.IsFalse(client.Attribution.Pending);
            var wrapper = Core.Json.Parse(rig.Store.Data["agh_attr"]) as Dictionary<string, object>;
            Assert.IsFalse(wrapper.ContainsKey("referrer"), "resolved — referrer dropped");

            client.Register(new Dictionary<string, object> { { "v", 1 } });
            client.Capture("y", null);
            client.Flush();
            Assert.NotNull(rig.Transport.Sent[1].Context());
            Assert.IsNull(rig.Transport.Sent[1].Install(), "resolved — later context re-sends stop asking");
        }

        [Test]
        public void StillPendingAnswerKeepsTheReferrerForTheNextLaunch()
        {
            var rig = new Rig();
            rig.Store.Data["agh_ref"] = "1";
            rig.Store.Data["agh_attr"] = PendingWrapper;
            rig.Transport.Respond = InstallResponder(PendingBody);
            var client = rig.NewClient();
            client.BeginInstallReferrerRead();
            client.Capture("x", null);
            client.Flush();

            var wrapper = Core.Json.Parse(rig.Store.Data["agh_attr"]) as Dictionary<string, object>;
            Assert.AreEqual("META_REF", wrapper["referrer"], "pending keeps the referrer to re-ask");
            var result = wrapper["result"] as Dictionary<string, object>;
            Assert.AreEqual(true, result["pending"]);
        }

        [Test]
        public void ResolvedCacheNeverRequeries()
        {
            var rig = new Rig();
            rig.Store.Data["agh_ref"] = "1";
            rig.Store.Data["agh_attr"] =
                "{\"result\":{\"source\":\"meta_referrer\",\"utm\":{},\"meta\":{\"campaign_group_name\":\"x\"},\"pending\":false}}";
            var client = rig.NewClient();
            client.BeginInstallReferrerRead();
            client.Capture("x", null);
            client.Flush();
            Assert.IsNull(rig.Transport.Sent[0].Install());
        }

        // ---- review-fix regressions ----

        [Test]
        public void CrashBeforeFirstFlushShipsInstallUnderTheOriginalSession()
        {
            var rig = new Rig();
            rig.Config.InstallReferrerProvider = Resolving("utm_source=web&utm_campaign=summer", 111, 222);
            var c1 = rig.NewClient();
            c1.BeginInstallReferrerRead();
            string installSession = c1.SessionId;
            c1.Capture("app_first_open", null);
            // killed before any flush

            rig.Clock.Advance(45 * Minute);
            int calls = 0;
            rig.Config.InstallReferrerProvider = callback => calls++;
            var c2 = rig.NewClient();
            c2.BeginInstallReferrerRead();
            Assert.AreEqual(0, calls, "the snapshot batch is the pending attempt — no re-read");
            c2.Flush();

            var batch = rig.Transport.Sent[0];
            Assert.AreEqual(installSession, batch.SessionId(), "attribution belongs to the install session");
            Assert.AreEqual("utm_source=web&utm_campaign=summer", batch.Install()["referrer"]);
            Assert.AreEqual(111L, batch.Install()["clickTs"]);
            StringAssert.Contains("utm_source=web", (string)batch.Context()["landingUrl"]);
            Assert.AreEqual("1", rig.Store.Data["agh_ref"], "carried delivery confirms the read");
        }

        [Test]
        public void CrashBeforeFirstFlushWithinIdleResumesTheInstall()
        {
            var rig = new Rig();
            rig.Config.InstallReferrerProvider = Resolving("utm_source=web");
            var c1 = rig.NewClient();
            c1.BeginInstallReferrerRead();
            string session = c1.SessionId;
            c1.Capture("x", null);

            rig.Clock.Advance(2 * Minute);
            int calls = 0;
            rig.Config.InstallReferrerProvider = callback => calls++;
            var c2 = rig.NewClient();
            c2.BeginInstallReferrerRead();
            Assert.AreEqual(0, calls, "the adopted snapshot already holds the referrer");
            c2.Flush();

            var batch = rig.Transport.Sent[0];
            Assert.AreEqual(session, batch.SessionId());
            Assert.AreEqual("utm_source=web", batch.Install()["referrer"]);
            StringAssert.Contains("utm_source=web", (string)batch.Context()["landingUrl"]);
        }

        [Test]
        public void DeliveredInstallIsNotResentWhenRegisterReopensContext()
        {
            var rig = new Rig();
            rig.Config.InstallReferrerProvider = Resolving("utm_source=web");
            var client = rig.NewClient();
            client.BeginInstallReferrerRead();
            client.Capture("x", null);
            client.Flush();
            Assert.NotNull(rig.Transport.Sent[0].Install());

            client.Register(new Dictionary<string, object> { { "v", 1 } });
            client.Capture("y", null);
            client.Flush();
            var resent = rig.Transport.Sent[1];
            Assert.NotNull(resent.Context());
            Assert.IsNull(resent.Install(), "a delivered install must not be submitted again");
            StringAssert.Contains("utm_source=web", (string)resent.Context()["landingUrl"],
                "the referrer UTMs keep shaping the landing URL");
        }

        [Test]
        public void LateReadAfterRotationIsDiscarded()
        {
            var rig = new Rig();
            Action<InstallReferrerResult> resolve = null;
            rig.Config.InstallReferrerProvider = callback => resolve = callback;
            rig.Config.InstallReferrerTimeoutMs = 5;
            var client = rig.NewClient();
            client.BeginInstallReferrerRead();
            client.Capture("x", null);
            rig.Clock.Advance(6);
            client.Flush(); // valve blew; install session's context shipped without install

            rig.Clock.Advance(31 * Minute);
            client.Capture("y", null); // rotates — fresh window, but NOT the install session
            resolve(new InstallReferrerResult { Referrer = "utm_source=web" });
            client.Flush();

            var rotated = rig.Transport.Sent[rig.Transport.Sent.Count - 1];
            Assert.NotNull(rotated.Context());
            Assert.IsNull(rotated.Install(), "a late read must never stamp a later session");
            Assert.IsFalse(rig.Store.Data.ContainsKey("agh_ref"), "retries next launch instead");
        }

        [Test]
        public void OutboxCapNeverDropsTheInFlightHead()
        {
            var rig = new Rig();
            rig.Transport.AutoComplete = false;
            var client = rig.NewClient();
            client.Capture("head", null);
            client.Flush(); // "head" is the in-flight outbox head

            for (int i = 0; i < 25; i++)
            {
                rig.Clock.Advance(31 * Minute);
                client.Capture("evt" + i, null); // each rotation packages the previous tail
            }

            rig.Transport.AutoComplete = true;
            rig.Transport.CompleteOldest(TransportStatus.Success, 204); // must settle "head" itself
            client.Flush();

            int headSends = 0;
            foreach (var batch in rig.Transport.Sent)
                foreach (string name in batch.EventNames())
                    if (name == "head") headSends++;
            Assert.AreEqual(1, headSends, "the in-flight head must neither drop nor re-send");
        }

        [Test]
        public void PauseDuringTheGateHoldsAndDeliversNextLaunch()
        {
            var rig = new Rig();
            rig.Config.InstallReferrerProvider = callback => { /* hung read */ };
            var c1 = rig.NewClient();
            c1.BeginInstallReferrerRead();
            c1.Capture("x", null);
            c1.OnPause();
            rig.Transport.AssertNoBatchSent(0,
                "deliberate: packaging now would freeze the context referrer-less");

            rig.Clock.Advance(2 * Minute);
            rig.Config.InstallReferrerProvider = Resolving("utm_source=web");
            var c2 = rig.NewClient();
            c2.BeginInstallReferrerRead();
            c2.Flush();
            Assert.AreEqual("utm_source=web", rig.Transport.Sent[0].Install()["referrer"],
                "the held batch delivers complete on the next launch");
        }

        [Test]
        public void ThrowingStoreOnDeliveryDoesNotResendTheBatch()
        {
            var rig = new Rig();
            rig.Store.SetThrowsFor.Add("agh_ref");
            rig.Store.SetThrowsFor.Add("agh_attr");
            rig.Config.InstallReferrerProvider = Resolving("utm_source=web");
            rig.Transport.Respond = InstallResponder(ResolvedBody);
            var client = rig.NewClient();
            client.BeginInstallReferrerRead();
            client.Capture("x", null);
            client.Flush();
            Assert.AreEqual(1, rig.Transport.Sent.Count);

            client.Capture("y", null);
            client.Flush();
            CollectionAssert.AreEqual(new List<string> { "y" },
                rig.Transport.Sent[1].EventNames(),
                "a failed flag/cache write must not resurrect the settled batch");
        }

        [Test]
        public void UnresolvableAttributionReleasesCallbacks()
        {
            var rig = new Rig();
            Action<InstallReferrerResult> resolve = null;
            rig.Config.InstallReferrerProvider = callback => resolve = callback;
            var client = rig.NewClient();
            client.BeginInstallReferrerRead();
            client.OnAttribution(a => { });
            Assert.AreEqual(1, client.AttributionCallbackCount, "read pending — a result may come");

            resolve(null); // permanent no-referrer
            Assert.AreEqual(0, client.AttributionCallbackCount, "no result can ever arrive");
            client.OnAttribution(a => { });
            Assert.AreEqual(0, client.AttributionCallbackCount, "late registrations are not pinned");
        }

        [Test]
        public void RequeryNeverTouchesTheDoneFlagOn4xx()
        {
            var rig = new Rig();
            rig.Store.Data["agh_ref"] = "1";
            rig.Store.Data["agh_attr"] = PendingWrapper;
            rig.Transport.NextStatus = TransportStatus.PermanentError;
            rig.Transport.NextCode = 400;
            var client = rig.NewClient();
            client.BeginInstallReferrerRead();
            client.Capture("x", null);
            client.Flush();
            Assert.AreEqual("1", rig.Store.Data["agh_ref"], "requery must not re-write install state");
            var wrapper = Core.Json.Parse(rig.Store.Data["agh_attr"]) as Dictionary<string, object>;
            Assert.AreEqual("META_REF", wrapper["referrer"], "pending cache untouched — next launch re-asks");
        }
    }
}
