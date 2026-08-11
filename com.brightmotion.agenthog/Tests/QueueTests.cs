using System.Collections.Generic;
using Brightmotion.AgentHog.Core;
using NUnit.Framework;

namespace Brightmotion.AgentHog.Tests
{
    public class QueueTests
    {
        [Test]
        public void MaxQueueTriggersAutoFlush()
        {
            var rig = new Rig();
            var client = rig.NewClient();
            for (int i = 0; i < rig.Config.MaxQueue; i++)
                client.Capture("e" + i, null);
            rig.Transport.AssertNoBatchSent(1);
            Assert.AreEqual(rig.Config.MaxQueue, rig.Transport.Sent[0].Events().Count);
        }

        [Test]
        public void HardCapDropsOldestAtFiveHundred()
        {
            var rig = new Rig();
            rig.Config.MaxQueue = 10_000; // disable auto-flush; exercise only the hard cap
            var client = rig.NewClient();
            for (int i = 0; i < 600; i++)
                client.Capture("e" + i, null);

            var parsed = Core.Json.Parse(client.BuildLiveBatch()) as Dictionary<string, object>;
            var events = parsed["events"] as List<object>;
            Assert.AreEqual(500, events.Count, "hard cap = server per-batch max");
            Assert.AreEqual("e100", ((Dictionary<string, object>)events[0])["name"], "drop-oldest");
        }

        [Test]
        public void RetryableFailureBacksOffExponentially()
        {
            var rig = new Rig();
            rig.Transport.NextStatus = TransportStatus.RetryableError;
            rig.Transport.NextCode = 503;
            var client = rig.NewClient();
            client.Capture("x", null);

            client.Flush();
            rig.Transport.AssertNoBatchSent(1);
            client.Flush(); // inside backoff window → no send
            rig.Transport.AssertNoBatchSent(1);

            rig.Clock.Advance(2_000); // first backoff step
            client.Flush();
            rig.Transport.AssertNoBatchSent(2);

            rig.Clock.Advance(2_000); // second step is 4s — 2s in, still blocked
            client.Flush();
            rig.Transport.AssertNoBatchSent(2);
            rig.Clock.Advance(2_000);
            client.Flush();
            rig.Transport.AssertNoBatchSent(3);

            // success resets the backoff and finally drains the queue
            rig.Transport.NextStatus = TransportStatus.Success;
            rig.Transport.NextCode = 204;
            rig.Clock.Advance(8_000);
            client.Flush();
            rig.Transport.AssertNoBatchSent(4);
            client.Capture("y", null);
            client.Flush();
            rig.Transport.AssertNoBatchSent(5, "no backoff after success");
        }

        [Test]
        public void PermanentFailureDropsBatch()
        {
            var rig = new Rig();
            rig.Transport.NextStatus = TransportStatus.PermanentError;
            rig.Transport.NextCode = 400;
            var client = rig.NewClient();
            client.Capture("bad", null);
            client.Flush();

            rig.Transport.NextStatus = TransportStatus.Success;
            rig.Transport.NextCode = 204;
            client.Flush(); // nothing left to send
            rig.Transport.AssertNoBatchSent(1);
        }

        [Test]
        public void EventsKeepQueuedWhileRequestInFlight()
        {
            var rig = new Rig();
            rig.Transport.AutoComplete = false;
            var client = rig.NewClient();
            client.Capture("first", null);
            client.Flush();
            client.Capture("during_flight", null);
            client.Flush(); // in flight → deferred to completion

            rig.Transport.AssertNoBatchSent(1);
            rig.Transport.CompleteOldest(TransportStatus.Success, 204);
            rig.Transport.AssertNoBatchSent(2);
            CollectionAssert.AreEqual(new List<string> { "during_flight" },
                rig.Transport.Sent[1].EventNames());
        }

        [Test]
        public void CrashCarryOverShipsUnderOriginalIds()
        {
            var rig = new Rig();
            rig.Transport.AutoComplete = false; // nothing ever completes → "crash" with queue on disk
            var client1 = rig.NewClient();
            string oldAnon = client1.AnonId;
            string oldSession = client1.SessionId;
            client1.Screen("/menu", null);
            client1.Capture("orphaned", null);

            rig.Clock.Advance(45 * 60_000); // relaunch after the session died
            rig.Transport.AutoComplete = true;
            rig.Transport.Sent.Clear();
            rig.Transport.Pending.Clear();
            var client2 = rig.NewClient();
            Assert.AreNotEqual(oldSession, client2.SessionId);

            client2.Flush(); // carry-over goes out first
            rig.Transport.AssertNoBatchSent(1);
            var batch = rig.Transport.Sent[0];
            Assert.AreEqual(oldAnon, batch.AnonId());
            Assert.AreEqual(oldSession, batch.SessionId(), "late batches ship under ORIGINAL ids");
            CollectionAssert.AreEqual(new List<string> { "pageview: /menu", "orphaned" },
                batch.EventNames());
            Assert.NotNull(batch.Context(), "unsent session context is regenerated for carry-over");
            StringAssert.Contains("/menu", (string)batch.Context()["landingUrl"]);

            client2.Capture("fresh", null);
            client2.Flush();
            Assert.AreEqual(client2.SessionId, rig.Transport.Sent[1].SessionId());
        }

        [Test]
        public void CrashCarryOverWithinIdleIsAdoptedIntoLiveSession()
        {
            var rig = new Rig();
            rig.Transport.AutoComplete = false;
            var client1 = rig.NewClient();
            string session = client1.SessionId;
            client1.Capture("pre_crash", null);

            rig.Clock.Advance(2 * 60_000); // quick relaunch — same session continues
            rig.Transport.AutoComplete = true;
            rig.Transport.Sent.Clear();
            rig.Transport.Pending.Clear();
            var client2 = rig.NewClient();
            Assert.AreEqual(session, client2.SessionId);

            client2.Capture("post_crash", null);
            client2.Flush();
            rig.Transport.AssertNoBatchSent(1);
            CollectionAssert.AreEqual(new List<string> { "pre_crash", "post_crash" },
                rig.Transport.Sent[0].EventNames());
            Assert.NotNull(rig.Transport.Sent[0].Context(),
                "adopted contextPending: the session never delivered its context");
        }

        [Test]
        public void SuccessfulFlushClearsPersistedQueue()
        {
            var rig = new Rig();
            var client = rig.NewClient();
            client.Capture("x", null);
            Assert.IsTrue(rig.Store.Data.ContainsKey("agh_queue"));
            client.Flush();
            Assert.IsFalse(rig.Store.Data.ContainsKey("agh_queue"),
                "delivered batch must not resurrect on next launch");
        }

        [Test]
        public void TickFlushesOnIntervalNotEveryFrame()
        {
            var rig = new Rig();
            var client = rig.NewClient();
            client.Capture("x", null);
            client.Flush();

            client.Capture("y", null);
            client.Tick();
            rig.Transport.AssertNoBatchSent(1, "within the flush interval → hold");
            rig.Clock.Advance(10_001);
            client.Tick();
            rig.Transport.AssertNoBatchSent(2);
        }
    }
}
