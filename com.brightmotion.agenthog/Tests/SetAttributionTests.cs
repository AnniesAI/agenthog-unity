using System.Collections.Generic;
using Brightmotion.AgentHog.Core;
using NUnit.Framework;

namespace Brightmotion.AgentHog.Tests
{
    /// <summary>
    /// SetAttribution — the client attach for context.attribution (agent-hog CONTRACTS.md
    /// § Wire format; docs/ATTRIBUTION_ATTACH_PLAN.md § "SDK API — client attach"): rides the
    /// next context send on the normal cadence, delivered exactly once per distinct payload
    /// (confirmed by a 2xx, persisted across launches), cleared by Reset().
    /// </summary>
    public class SetAttributionTests
    {
        const string KeyAttach = "agh_cattr";
        const string KeyAttachDone = "agh_cattr_done";

        static AhAttribution Singular() => new AhAttribution
        {
            Provider = "singular",
            UtmSource = "tiktokglobal_int",
            UtmCampaign = "us-launch-aug",
            Params = new Dictionary<string, string>
            {
                { "click_timestamp", "1756100000" }, { "campaign_id", "1204" },
            },
        };

        [Test]
        public void MissingProviderIsANoOp()
        {
            var rig = new Rig();
            var client = rig.NewClient();
            client.SetAttribution(null);
            client.SetAttribution(new AhAttribution { UtmSource = "meta" });
            client.SetAttribution(new AhAttribution { Provider = "" });
            client.Capture("x", null);
            client.Flush();

            Assert.IsNull(rig.Transport.Sent[0].Attribution());
            Assert.IsFalse(rig.Store.Data.ContainsKey(KeyAttach));
        }

        [Test]
        public void PreFlushAttachRidesFirstBatchCanonically()
        {
            var rig = new Rig();
            var client = rig.NewClient();
            client.SetAttribution(Singular());
            client.Capture("app_first_open", null);
            client.Flush();

            var attach = rig.Transport.Sent[0].Attribution();
            Assert.AreEqual("singular", attach["provider"]);
            Assert.AreEqual("tiktokglobal_int", attach["utm_source"]);
            Assert.AreEqual("us-launch-aug", attach["utm_campaign"]);
            Assert.IsFalse(attach.ContainsKey("utm_medium"), "absent utm keys stay off the wire");
            var prms = attach["params"] as Dictionary<string, object>;
            Assert.AreEqual("1204", prms["campaign_id"]);
            Assert.AreEqual("1756100000", prms["click_timestamp"]);
        }

        [Test]
        public void EmptyUtmAndParamValuesAreSkipped()
        {
            var rig = new Rig();
            var client = rig.NewClient();
            client.SetAttribution(new AhAttribution
            {
                Provider = "singular",
                UtmSource = "meta",
                UtmMedium = "",
                Params = new Dictionary<string, string> { { "adset", "" }, { "creative", "c1" } },
            });
            client.Capture("x", null);
            client.Flush();

            var attach = rig.Transport.Sent[0].Attribution();
            Assert.IsFalse(attach.ContainsKey("utm_medium"));
            var prms = attach["params"] as Dictionary<string, object>;
            Assert.AreEqual(1, prms.Count);
            Assert.AreEqual("c1", prms["creative"]);
        }

        [Test]
        public void PostFlushAttachMarksContextPendingWithoutForcingAFlush()
        {
            var rig = new Rig();
            var client = rig.NewClient();
            client.Capture("x", null);
            client.Flush();
            Assert.IsNull(rig.Transport.Sent[0].Attribution());

            client.SetAttribution(Singular());
            rig.Transport.AssertNoBatchSent(1, "attach must not force an immediate flush");

            client.Capture("y", null);
            rig.Clock.Advance(rig.Config.FlushIntervalMs + 1);
            client.Tick();
            var attach = rig.Transport.Sent[1].Attribution();
            Assert.AreEqual("singular", attach["provider"], "next cadence flush re-sends context with the attach");
        }

        [Test]
        public void DeliveredPayloadClearsPendingAndSuppressesRepeats()
        {
            var rig = new Rig();
            var client = rig.NewClient();
            client.SetAttribution(Singular());
            client.Capture("x", null);
            client.Flush(); // 2xx — delivered
            Assert.IsFalse(rig.Store.Data.ContainsKey(KeyAttach), "delivered payload no longer pending");
            Assert.IsTrue(rig.Store.Data.ContainsKey(KeyAttachDone));

            client.SetAttribution(Singular()); // the exact same verdict again
            client.Capture("y", null);
            client.Flush();
            Assert.IsNull(rig.Transport.Sent[1].Context(), "repeat is a full no-op — context not even re-marked");
        }

        [Test]
        public void DeliveredOnceSurvivesRelaunch()
        {
            var rig = new Rig();
            var client = rig.NewClient();
            client.SetAttribution(Singular());
            client.Capture("x", null);
            client.Flush();

            rig.Clock.Advance(31 * 60_000); // idle out — relaunch starts a new session
            var next = rig.NewClient();
            next.SetAttribution(Singular());
            next.Capture("open", null);
            next.Flush();
            Assert.IsNull(rig.Transport.Sent[1].Attribution(), "already-delivered payload must not re-ship");
        }

        [Test]
        public void DifferentPayloadIsANewDelivery()
        {
            var rig = new Rig();
            var client = rig.NewClient();
            client.SetAttribution(Singular());
            client.Capture("x", null);
            client.Flush();

            client.SetAttribution(new AhAttribution { Provider = "singular", UtmSource = "meta" });
            client.Capture("y", null);
            client.Flush();
            var attach = rig.Transport.Sent[1].Attribution();
            Assert.AreEqual("meta", attach["utm_source"], "re-engagement verdict ships as its own delivery");
        }

        [Test]
        public void ReplayedVerdictStaysSuppressedAfterReEngagement()
        {
            // MMPs re-fire the cached install verdict on later launches; after a
            // re-engagement verdict rotated through, the replay must STILL be a no-op
            var rig = new Rig();
            var client = rig.NewClient();
            client.SetAttribution(Singular()); // A — install verdict, delivered
            client.Capture("a", null);
            client.Flush();
            client.SetAttribution(new AhAttribution { Provider = "singular", UtmSource = "meta" }); // B
            client.Capture("b", null);
            client.Flush();

            client.SetAttribution(Singular()); // A replayed
            client.Capture("c", null);
            client.Flush();
            Assert.IsNull(rig.Transport.Sent[2].Attribution(),
                "replayed install verdict must stay delivered-once past re-engagement");
        }

        [Test]
        public void LateSettleAfterResetDoesNotClobberTheNewPersonsAttach()
        {
            var rig = new Rig();
            rig.Transport.AutoComplete = false;
            var client = rig.NewClient();
            client.SetAttribution(Singular());
            client.Capture("x", null);
            client.Flush();                    // in flight, carries the attach under the OLD anonId
            client.Reset();                    // new person: pending + delivered markers cleared
            client.SetAttribution(Singular()); // the new person's own identical verdict
            rig.Transport.CompleteOldest(TransportStatus.Success, 204); // old batch settles late

            Assert.IsTrue(rig.Store.Data.ContainsKey(KeyAttach),
                "old identity's settle must not clear the new person's pending attach");
            Assert.IsFalse(rig.Store.Data.ContainsKey(KeyAttachDone),
                "old identity's settle must not stamp the new person's delivered set");

            client.Capture("y", null);
            client.Flush();
            var last = rig.Transport.Sent[rig.Transport.Sent.Count - 1];
            Assert.AreEqual("singular", last.Attribution()["provider"],
                "the new person's attach still delivers");
        }

        [Test]
        public void IdleGapVerdictStampsTheFreshSessionNotTheExpiredTail()
        {
            var rig = new Rig();
            var client = rig.NewClient();
            client.Capture("x", null);
            rig.Clock.Advance(31 * 60_000);    // foreground idle-out
            client.SetAttribution(Singular()); // MMP callback fires after the gap
            client.Capture("y", null);
            client.Flush(); // ships the packaged old-session tail
            client.Flush(); // ships the fresh session's first batch

            var old = rig.Transport.Sent[0];
            var fresh = rig.Transport.Sent[1];
            Assert.AreNotEqual(old.SessionId(), fresh.SessionId(), "the gap must rotate the session");
            Assert.IsNull(old.Attribution(), "the expired session's tail must not carry the late verdict");
            Assert.AreEqual("singular", fresh.Attribution()["provider"]);
        }

        [Test]
        public void DeliveredPayloadLeftInStoreDoesNotReArm()
        {
            var rig = new Rig();
            var client = rig.NewClient();
            client.SetAttribution(Singular());
            string canonical = rig.Store.Data[KeyAttach];
            client.Capture("x", null);
            client.Flush(); // delivered — KeyAttach cleared

            rig.Store.Data[KeyAttach] = canonical; // simulate a failed post-delivery clear
            rig.Clock.Advance(31 * 60_000);
            var next = rig.NewClient();
            Assert.IsFalse(rig.Store.Data.ContainsKey(KeyAttach),
                "an already-delivered payload must be purged at load, not re-armed");
            next.Capture("open", null);
            next.Flush();
            Assert.IsNull(rig.Transport.Sent[1].Attribution());
        }

        [Test]
        public void UndeliveredPayloadRidesTheNextLaunchFirstFlush()
        {
            var rig = new Rig();
            var client = rig.NewClient();
            client.SetAttribution(Singular());
            // killed before any flush — the attach persisted alongside the queue snapshot

            rig.Clock.Advance(31 * 60_000);
            var next = rig.NewClient();
            next.Capture("open", null);
            next.Flush();

            SentBatch carrying = null;
            foreach (var batch in rig.Transport.Sent)
                if (batch.Attribution() != null) carrying = batch;
            Assert.NotNull(carrying, "undelivered attach must ride the next launch's first flush");
            Assert.AreEqual("singular", carrying.Attribution()["provider"]);
            Assert.IsFalse(rig.Store.Data.ContainsKey(KeyAttach), "2xx settles the pending payload");
        }

        [Test]
        public void OfflineFirstAttemptRetriesUntilDelivered()
        {
            var rig = new Rig();
            rig.Transport.NextStatus = TransportStatus.RetryableError;
            rig.Transport.NextCode = 0;
            var client = rig.NewClient();
            client.SetAttribution(Singular());
            client.Capture("x", null);
            client.Flush();
            Assert.IsTrue(rig.Store.Data.ContainsKey(KeyAttach), "failed send keeps the payload pending");

            rig.Transport.NextStatus = TransportStatus.Success;
            rig.Transport.NextCode = 204;
            rig.Clock.Advance(5_000);
            client.Flush();
            Assert.NotNull(rig.Transport.Sent[1].Attribution());
            Assert.IsFalse(rig.Store.Data.ContainsKey(KeyAttach));
        }

        [Test]
        public void ResetClearsPendingPayload()
        {
            var rig = new Rig();
            var client = rig.NewClient();
            client.SetAttribution(Singular());
            client.Capture("x", null);
            client.Reset();

            Assert.IsFalse(rig.Store.Data.ContainsKey(KeyAttach));
            foreach (var batch in rig.Transport.Sent)
                Assert.IsNull(batch.Attribution(), "cleared attach must not ride the goodbye batch");
            client.Capture("y", null);
            client.Flush();
            Assert.IsNull(rig.Transport.Sent[rig.Transport.Sent.Count - 1].Attribution());
        }

        [Test]
        public void ResetClearsDeliveredFlagSoTheSamePayloadDeliversAgain()
        {
            var rig = new Rig();
            var client = rig.NewClient();
            client.SetAttribution(Singular());
            client.Capture("x", null);
            client.Flush();
            Assert.IsTrue(rig.Store.Data.ContainsKey(KeyAttachDone));

            client.Reset();
            Assert.IsFalse(rig.Store.Data.ContainsKey(KeyAttachDone));
            client.SetAttribution(Singular());
            client.Capture("y", null);
            client.Flush();
            Assert.NotNull(rig.Transport.Sent[rig.Transport.Sent.Count - 1].Attribution(),
                "the new person is a fresh delivery slate");
        }

        [Test]
        public void SetLandingParamsStaysIndependentOfTheAttach()
        {
            var rig = new Rig();
            var client = rig.NewClient();
            client.SetLandingParams(new Dictionary<string, string> { { "utm_source", "deeplink" } });
            client.SetAttribution(Singular());
            client.Capture("x", null);
            client.Flush();

            var batch = rig.Transport.Sent[0];
            StringAssert.Contains("utm_source=deeplink", (string)batch.Context()["landingUrl"],
                "deep-link params keep riding the landing URL");
            Assert.AreEqual("tiktokglobal_int", batch.Attribution()["utm_source"],
                "the attach never leaks into the landing URL pipe");
        }
    }
}
