using System.Collections.Generic;
using Brightmotion.AgentHog.Core;
using NUnit.Framework;

namespace Brightmotion.AgentHog.Tests
{
    /// <summary>
    /// Pins the failure scenarios from the 2026-08-11 review: outbound batches must be immune
    /// to session rotation / Reset / caps / crashes once built, and lifecycle events must not
    /// duplicate or mis-attribute leaves.
    /// </summary>
    public class RegressionTests
    {
        const long Minute = 60_000;

        [Test]
        public void RotationMidFlightNeitherDuplicatesNorLosesEvents()
        {
            var rig = new Rig();
            rig.Transport.AutoComplete = false;
            var client = rig.NewClient();
            string oldSession = client.SessionId;
            client.Capture("x", null);
            client.Flush(); // packaged + in flight

            rig.Clock.Advance(31 * Minute);
            client.Capture("y", null); // rotates; y belongs to the NEW session

            rig.Transport.CompleteOldest(TransportStatus.Success, 204); // late callback
            client.Flush();

            Assert.AreEqual(2, rig.Transport.Sent.Count);
            Assert.AreEqual(oldSession, rig.Transport.Sent[0].SessionId());
            CollectionAssert.AreEqual(new List<string> { "x" }, rig.Transport.Sent[0].EventNames());
            Assert.AreNotEqual(oldSession, rig.Transport.Sent[1].SessionId());
            CollectionAssert.AreEqual(new List<string> { "y" }, rig.Transport.Sent[1].EventNames(),
                "the new session's events must survive a late in-flight completion");
        }

        [Test]
        public void HardCapDuringFlightCannotTouchTheInFlightBatch()
        {
            var rig = new Rig();
            rig.Config.MaxQueue = 10_000;
            rig.Transport.AutoComplete = false;
            var client = rig.NewClient();
            client.Capture("a", null);
            client.Flush(); // "a" frozen in the outbox

            for (int i = 0; i < 600; i++) client.Capture("e" + i, null); // cap trims e0..e99

            rig.Transport.CompleteOldest(TransportStatus.Success, 204);
            client.Flush();
            var names = rig.Transport.Sent[1].EventNames();
            Assert.AreEqual(500, names.Count);
            Assert.AreEqual("e100", names[0]);
            CollectionAssert.DoesNotContain(names, "a", "the settled batch must not re-send");
        }

        [Test]
        public void CrashWhileRequestInFlightPreservesTheBatch()
        {
            var rig = new Rig();
            rig.Transport.AutoComplete = false;
            var c1 = rig.NewClient();
            string anon = c1.AnonId;
            string session = c1.SessionId;
            c1.Capture("x", null);
            c1.Flush(); // in flight; process dies before the callback

            rig.Transport.Sent.Clear();
            rig.Transport.Pending.Clear();
            rig.Transport.AutoComplete = true;
            var c2 = rig.NewClient();
            c2.Flush();

            Assert.AreEqual(1, rig.Transport.Sent.Count, "packaged batch must survive the crash");
            Assert.AreEqual(anon, rig.Transport.Sent[0].AnonId());
            Assert.AreEqual(session, rig.Transport.Sent[0].SessionId());
            CollectionAssert.AreEqual(new List<string> { "x" }, rig.Transport.Sent[0].EventNames());
        }

        [Test]
        public void CarryOverSurvivesAnOfflineRelaunch()
        {
            var rig = new Rig();
            rig.Transport.AutoComplete = false; // run 1: nothing ever sends
            var c1 = rig.NewClient();
            string session1 = c1.SessionId;
            c1.Capture("orphaned", null);

            // run 2: still offline — carry-over becomes a packaged batch, send fails
            rig.Clock.Advance(45 * Minute);
            rig.Transport.AutoComplete = true;
            rig.Transport.NextStatus = TransportStatus.RetryableError;
            rig.Transport.NextCode = 0;
            var c2 = rig.NewClient();
            c2.Flush();
            Assert.IsTrue(rig.Store.Data.ContainsKey("agh_outbox"),
                "failed carry-over must stay on disk, not evaporate into memory");

            // run 3: back online
            rig.Clock.Advance(45 * Minute);
            rig.Transport.Sent.Clear();
            rig.Transport.NextStatus = TransportStatus.Success;
            rig.Transport.NextCode = 204;
            var c3 = rig.NewClient();
            c3.Flush();
            Assert.AreEqual(session1, rig.Transport.Sent[0].SessionId(),
                "events survive two crashes/offline runs under their ORIGINAL session");
            CollectionAssert.AreEqual(new List<string> { "orphaned" },
                rig.Transport.Sent[0].EventNames());
        }

        [Test]
        public void ResetNeverLeaksTheOldIdentity()
        {
            var rig = new Rig();
            rig.Transport.AutoComplete = false;
            var client = rig.NewClient();
            string oldAnon = client.AnonId;

            client.Capture("secret", null);
            client.Flush(); // [secret] frozen under old ids, in flight
            client.Identify("old@user.com", null); // arrives while in flight
            client.Reset(); // must fence BOTH off from the new identity

            Assert.AreNotEqual(oldAnon, client.AnonId);
            rig.Transport.CompleteOldest(TransportStatus.Success, 204); // settles [secret], chains
            rig.Transport.CompleteOldest(TransportStatus.Success, 204); // settles the identify tail

            client.Capture("fresh", null);
            client.Flush();
            rig.Transport.CompleteOldest(TransportStatus.Success, 204);

            Assert.AreEqual(3, rig.Transport.Sent.Count);
            Assert.AreEqual(oldAnon, rig.Transport.Sent[0].AnonId());
            Assert.AreEqual(oldAnon, rig.Transport.Sent[1].AnonId(),
                "the pending identify must ship under the OLD anonId");
            Assert.NotNull(rig.Transport.Sent[1].Identify());
            var fresh = rig.Transport.Sent[2];
            Assert.AreEqual(client.AnonId, fresh.AnonId());
            Assert.IsNull(fresh.Identify(), "new identity must start with no identify payload");
            CollectionAssert.AreEqual(new List<string> { "fresh" }, fresh.EventNames());
        }

        [Test]
        public void ScreenAfterForegroundIdleGapSkipsTheStaleLeave()
        {
            var rig = new Rig();
            var client = rig.NewClient();
            client.Screen("/a", null);
            rig.Clock.Advance(31 * Minute); // idle in foreground (desktop, OS dialog, …)
            client.Screen("/b", null);

            client.Flush();
            client.Flush();
            CollectionAssert.AreEqual(new List<string> { "pageview: /a" },
                rig.Transport.Sent[0].EventNames(),
                "no idle-inflated leave may ship in either session");
            CollectionAssert.AreEqual(new List<string> { "pageview: /b" },
                rig.Transport.Sent[1].EventNames());
            StringAssert.Contains("/a", (string)rig.Transport.Sent[0].Context()["landingUrl"]);
            StringAssert.Contains("/b", (string)rig.Transport.Sent[1].Context()["landingUrl"],
                "the navigation target is the rotated session's entry");
        }

        [Test]
        public void QuitAfterPauseEmitsExactlyOneLeave()
        {
            var rig = new Rig();
            var client = rig.NewClient();
            client.Screen("/game", null);
            rig.Clock.Advance(5_000);
            client.OnPause(); // backgrounded
            client.OnPause(); // OnApplicationQuit routes here too on mobile

            int leaves = 0;
            foreach (var batch in rig.Transport.Sent)
                foreach (string name in batch.EventNames())
                    if (name == "leave: /game") leaves++;
            Assert.AreEqual(1, leaves, "quit-after-pause must not add a duration-0 leave");
        }

        [Test]
        public void ForcedFlushBypassesBackoff()
        {
            var rig = new Rig();
            rig.Transport.NextStatus = TransportStatus.RetryableError;
            rig.Transport.NextCode = 503;
            var client = rig.NewClient();
            client.Capture("x", null);
            client.Flush();
            rig.Transport.AssertNoBatchSent(1);

            client.Flush(); // plain flush respects backoff
            rig.Transport.AssertNoBatchSent(1);
            client.Flush(force: true); // manual/pause flush must attempt anyway
            rig.Transport.AssertNoBatchSent(2);
        }

        [Test]
        public void PauseAttemptsASendEvenInsideBackoff()
        {
            var rig = new Rig();
            rig.Transport.NextStatus = TransportStatus.RetryableError;
            rig.Transport.NextCode = 503;
            var client = rig.NewClient();
            client.Screen("/game", null);
            client.Flush();
            int sentBefore = rig.Transport.Sent.Count;
            client.OnPause(); // iOS grace period: this is the last chance
            Assert.Greater(rig.Transport.Sent.Count, sentBefore);
        }

        [Test]
        public void WatchdogRecoversFromALostTransportCallback()
        {
            var rig = new Rig();
            rig.Transport.AutoComplete = false;
            var client = rig.NewClient();
            client.Capture("x", null);
            client.Flush(); // callback will never arrive (lost coroutine)

            rig.Clock.Advance(91_000);
            client.Tick(); // watchdog frees the client
            rig.Clock.Advance(2_001);
            client.Tick(); // backoff elapsed → re-send
            Assert.AreEqual(2, rig.Transport.Sent.Count, "client must not wedge forever");
            Assert.AreEqual(rig.Transport.Sent[0].Json, rig.Transport.Sent[1].Json);

            // the original callback finally fires — must be ignored as stale
            rig.Transport.CompleteOldest(TransportStatus.Success, 204);
            rig.Transport.CompleteOldest(TransportStatus.Success, 204); // the live one settles

            client.Capture("y", null);
            client.Flush();
            rig.Transport.CompleteOldest(TransportStatus.Success, 204);
            CollectionAssert.AreEqual(new List<string> { "y" },
                rig.Transport.Sent[2].EventNames(),
                "stale callback must not have double-settled the outbox");
        }

        [Test]
        public void TagWithoutValueRecordsTrue()
        {
            var rig = new Rig();
            var client = rig.NewClient();
            client.Tag("beta_user", null);
            client.Flush();
            var traits = rig.Transport.Sent[0].Identify()["traits"] as Dictionary<string, object>;
            Assert.AreEqual(true, traits["beta_user"], "parity with web/RN tag(name) == true");
            Assert.AreEqual(true, rig.Transport.Sent[0].Event(0).Props()["value"]);
        }
    }
}
