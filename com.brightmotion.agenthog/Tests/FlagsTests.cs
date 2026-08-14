using System.Collections.Generic;
using Brightmotion.AgentHog.Core;
using NUnit.Framework;

namespace Brightmotion.AgentHog.Tests
{
    /// <summary>
    /// Bucketing spec (agent-hog CONTRACTS.md §"Feature flags") + client flag behavior.
    /// The vector table is the CROSS-SDK contract, pinned identically by the web tracker's
    /// packages/tracker/test/flags.test.ts — changing either side reshuffles users on one
    /// platform but not the other. Don't.
    /// </summary>
    public class FlagsTests
    {
        // ---- the normative hash ----

        [Test]
        public void Fnv1a32_MatchesPublishedReferenceValues()
        {
            Assert.AreEqual(0x811c9dc5u, FlagEval.Fnv1a32(""));
            Assert.AreEqual(0xe40c292cu, FlagEval.Fnv1a32("a"));
            Assert.AreEqual(0xbf9cf968u, FlagEval.Fnv1a32("foobar"));
        }

        static FlagDef Ab(int trafficBps = 10000, bool enabled = true) => new FlagDef
        {
            Key = "checkout_cta",
            Type = "multivariate",
            Enabled = enabled,
            Salt = "k3v9x2ma",
            TrafficBps = trafficBps,
            Variants = new List<FlagVariantDef>
            {
                new FlagVariantDef { Key = "control", Weight = 50 },
                new FlagVariantDef { Key = "b", Weight = 50 },
            },
        };

        // anonId | traffic hash %10000 | bucket %10000 | variant — CONTRACTS.md canonical vectors
        static readonly object[][] Vectors =
        {
            new object[] { "00000000-0000-4000-8000-000000000001", 2176u, 9446u, "b" },
            new object[] { "00000000-0000-4000-8000-000000000002", 5033u, 1827u, "control" },
            new object[] { "a3f1c9d2-6b4e-4f8a-9c21-5d7e8f901234", 1885u, 7719u, "b" },
            new object[] { "ffffffff-ffff-4fff-8fff-ffffffffffff", 3031u, 3325u, "control" },
            new object[] { "user-42", 2753u, 703u, "control" },
        };

        [Test]
        public void CanonicalVectors_MatchTheWebReferenceImplementation()
        {
            foreach (var v in Vectors)
            {
                string anon = (string)v[0];
                Assert.AreEqual((uint)v[1], FlagEval.Fnv1a32("checkout_cta.k3v9x2ma.t." + anon) % 10000, anon + " traffic");
                Assert.AreEqual((uint)v[2], FlagEval.Fnv1a32("checkout_cta.k3v9x2ma.v." + anon) % 10000, anon + " bucket");
                Assert.AreEqual((string)v[3], FlagEval.Evaluate(Ab(), anon), anon + " variant");
            }
        }

        [Test]
        public void Evaluate_DisabledAndZeroTraffic_ReturnNull()
        {
            Assert.IsNull(FlagEval.Evaluate(Ab(enabled: false), "user-42"));
            for (int i = 0; i < 100; i++)
                Assert.IsNull(FlagEval.Evaluate(Ab(trafficBps: 0), "anon-" + i));
        }

        [Test]
        public void Evaluate_RampingTrafficUp_NeverReshufflesEnrolledUsers()
        {
            int checkedCount = 0;
            for (int i = 0; i < 2000; i++)
            {
                string anon = "ramp-anon-" + i;
                string before = FlagEval.Evaluate(Ab(trafficBps: 2000), anon);
                if (before == null) continue;
                Assert.AreEqual(before, FlagEval.Evaluate(Ab(trafficBps: 8000), anon), anon);
                checkedCount++;
            }
            Assert.Greater(checkedCount, 200, "the 20% slice actually contained users");
        }

        [Test]
        public void Evaluate_SplitAndAllocationLandNearRequestedShares()
        {
            int b = 0, enrolledAtQuarter = 0;
            for (int i = 0; i < 10000; i++)
            {
                if (FlagEval.Evaluate(Ab(), "dist-anon-" + i) == "b") b++;
                if (FlagEval.Evaluate(Ab(trafficBps: 2500), "traffic-anon-" + i) != null) enrolledAtQuarter++;
            }
            Assert.Greater(b, 4700);
            Assert.Less(b, 5300);
            Assert.Greater(enrolledAtQuarter, 2200);
            Assert.Less(enrolledAtQuarter, 2800);
        }

        // ---- ruleset parsing ----

        const string Ruleset = "{\"rev\":3,\"flags\":[" +
            "{\"key\":\"checkout_cta\",\"type\":\"multivariate\",\"enabled\":true,\"salt\":\"k3v9x2ma\",\"trafficBps\":10000," +
            "\"variants\":[{\"key\":\"control\",\"weight\":50},{\"key\":\"b\",\"weight\":50}]}," +
            "{\"key\":\"killed\",\"type\":\"multivariate\",\"enabled\":false,\"salt\":\"k3v9x2ma\",\"trafficBps\":10000," +
            "\"variants\":[{\"key\":\"control\",\"weight\":50},{\"key\":\"b\",\"weight\":50}]}," +
            "{\"key\":\"new_nav\",\"type\":\"boolean\",\"enabled\":true,\"salt\":\"35c64baa\",\"trafficBps\":10000," +
            "\"variants\":[{\"key\":\"on\",\"weight\":100}]}]}";

        [Test]
        public void FlagsConfig_ParsesTheWirePayload_AndRejectsGarbage()
        {
            var cfg = FlagsConfig.Parse(Ruleset);
            Assert.AreEqual(3, cfg.Rev);
            Assert.AreEqual(3, cfg.Flags.Count);
            Assert.AreEqual(2, cfg.Find("checkout_cta").Variants.Count);
            Assert.IsFalse(cfg.Find("killed").Enabled);
            Assert.IsNull(FlagsConfig.Parse(null));
            Assert.IsNull(FlagsConfig.Parse("not json"));
            Assert.IsNull(FlagsConfig.Parse("{\"flags\":[]}")); // no rev
        }

        // ---- client behavior ----

        Rig NewFlagsRig()
        {
            var rig = new Rig();
            rig.Transport.FlagsBody = Ruleset;
            return rig;
        }

        [Test]
        public void Flag_LazyLoads_ThenEvaluates_AndRecordsExposureOnce()
        {
            var rig = NewFlagsRig();
            rig.Transport.AutoCompleteFetch = false;
            var client = rig.NewClient(); // anonId = ...001 → vector says variant 'b'

            Assert.IsNull(client.Flag("checkout_cta"), "no ruleset yet → code fallback");
            Assert.AreEqual(1, rig.Transport.FetchUrls.Count, "first read triggers the lazy fetch");
            StringAssert.Contains("/sdk/flags?project=ah_test01", rig.Transport.FetchUrls[0]);

            bool ready = false;
            client.FlagsReady(() => ready = true);
            rig.Transport.CompleteOldestFetch(200, Ruleset);
            Assert.IsTrue(ready);

            Assert.AreEqual("b", client.Flag("checkout_cta"));
            Assert.AreEqual("b", client.Flag("checkout_cta"), "sticky across reads");
            Assert.IsNull(client.Flag("killed"), "kill switch → fallback");
            Assert.AreEqual("on", client.Flag("new_nav"));
            Assert.IsNull(client.Flag("nonexistent"));

            client.Capture("checkout_started", null);
            client.Flush(force: true);

            var batch = rig.Transport.Sent[0];
            var names = batch.EventNames();
            Assert.AreEqual(2, names.FindAll(n => n == "$exposure").Count, "one $exposure per exposed flag");
            var custom = batch.Event(names.IndexOf("checkout_started"));
            Assert.AreEqual("b", custom.Props()["$ff/checkout_cta"], "$ff prop on later events");
            Assert.AreEqual("on", custom.Props()["$ff/new_nav"]);
            var registered = batch.Context()["registered"] as Dictionary<string, object>;
            Assert.AreEqual("b", registered["$ff/checkout_cta"], "$ff registered into context");
        }

        [Test]
        public void CachedRuleset_EvaluatesOnRelaunch_AndExposureDedupesWithinTheSession()
        {
            var rig = NewFlagsRig();
            var c1 = rig.NewClient();
            c1.FlagsReady(() => { });
            Assert.AreEqual("b", c1.Flag("checkout_cta"));
            c1.Flush(force: true);
            int sentBefore = rig.Transport.Sent.Count;

            // relaunch within the idle window: same store, flags endpoint now DOWN
            rig.Transport.FlagsBody = null;
            int fetchesBefore = rig.Transport.FetchUrls.Count;
            var c2 = new Client(rig.Config, rig.Store, rig.Clock, rig.Transport, rig.Context, rig.NextId, null);
            Assert.AreEqual("b", c2.Flag("checkout_cta"), "cache serves; same anon, same variant");
            Assert.AreEqual(fetchesBefore, rig.Transport.FetchUrls.Count, "cached ruleset → no fetch needed");
            c2.Capture("later", null);
            c2.Flush(force: true);
            var names = rig.Transport.Sent[rig.Transport.Sent.Count - 1].EventNames();
            Assert.AreEqual(0, names.FindAll(n => n == "$exposure").Count,
                "same session across relaunch → exposure already recorded");
            Assert.AreEqual(sentBefore + 1, rig.Transport.Sent.Count);
        }

        [Test]
        public void FlagsReady_FiresImmediatelyWhenCached_AndAfterAFailedFetch()
        {
            var rig = NewFlagsRig();
            var c1 = rig.NewClient();
            bool ready1 = false;
            c1.FlagsReady(() => ready1 = true); // triggers fetch, AutoCompleteFetch succeeds
            Assert.IsTrue(ready1);
            bool ready2 = false;
            c1.FlagsReady(() => ready2 = true); // config present → immediate
            Assert.IsTrue(ready2);

            var rig2 = new Rig(); // FlagsBody null → fetch fails
            var c2 = rig2.NewClient();
            bool ready3 = false;
            c2.FlagsReady(() => ready3 = true);
            Assert.IsTrue(ready3, "resolves on failure too — callers fall back to code defaults");
            Assert.IsNull(c2.Flag("checkout_cta"));
        }

        [Test]
        public void Overrides_WinEvenWithoutARuleset_AndNeverEmitExposure()
        {
            var rig = new Rig(); // flags endpoint down
            var client = rig.NewClient();
            client.OverrideFlag("checkout_cta", "b");
            Assert.AreEqual("b", client.Flag("checkout_cta"), "override answers even with no ruleset");
            client.Flush(force: true);
            foreach (var batch in rig.Transport.Sent)
                Assert.AreEqual(0, batch.EventNames().FindAll(n => n == "$exposure").Count);

            client.OverrideFlag("checkout_cta", null);
            Assert.IsNull(client.Flag("checkout_cta"), "cleared → back to fallback (no ruleset)");

            // persisted across relaunch
            client.OverrideFlag("new_nav", "off");
            var c2 = new Client(rig.Config, rig.Store, rig.Clock, rig.Transport, rig.Context, rig.NextId, null);
            Assert.AreEqual("off", c2.Flag("new_nav"));
        }

        [Test]
        public void IngestRevHeader_TriggersRefetch_OnlyWhenTheRevMoves()
        {
            var rig = NewFlagsRig();
            var client = rig.NewClient();
            client.FlagsReady(() => { }); // loads rev 3; one fetch
            Assert.AreEqual(1, rig.Transport.FetchUrls.Count);

            rig.Transport.NextFlagsRev = "3";
            client.Capture("e1", null);
            client.Flush(force: true);
            Assert.AreEqual(1, rig.Transport.FetchUrls.Count, "rev matches — no refetch");

            rig.Transport.NextFlagsRev = "4";
            client.Capture("e2", null);
            client.Flush(force: true);
            Assert.AreEqual(2, rig.Transport.FetchUrls.Count, "rev moved — refetched");
        }

        [Test]
        public void RevZero_WithNoRuleset_DoesNotFetch()
        {
            var rig = new Rig();
            rig.Store.Delete("agh_flags");
            var client = rig.NewClient();
            rig.Transport.NextFlagsRev = "0"; // project has no flags
            client.Capture("e1", null);
            client.Flush(force: true);
            Assert.AreEqual(0, rig.Transport.FetchUrls.Count);
        }

        [Test]
        public void ExposureDedupe_ResetsOnSessionRotation()
        {
            var rig = NewFlagsRig();
            var client = rig.NewClient();
            client.FlagsReady(() => { });
            Assert.AreEqual("b", client.Flag("checkout_cta"));
            client.Flush(force: true);

            rig.Clock.Advance(31 * 60_000); // past the idle window → next activity rotates
            Assert.AreEqual("b", client.Flag("checkout_cta"), "same anon → same variant in the new session");
            client.Flush(force: true);

            int exposures = 0;
            foreach (var batch in rig.Transport.Sent)
                exposures += batch.EventNames().FindAll(n => n == "$exposure").Count;
            Assert.AreEqual(2, exposures, "one per session, and the rotation opened a new session");
        }
    }
}
