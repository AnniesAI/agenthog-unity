using System.Collections.Generic;
using NUnit.Framework;

namespace Brightmotion.AgentHog.Tests
{
    public class SessionTests
    {
        const long Minute = 60_000;

        [Test]
        public void ColdStartWithinIdleContinuesSession()
        {
            var rig = new Rig();
            var c1 = rig.NewClient();
            string session = c1.SessionId;
            string anon = c1.AnonId;
            c1.Capture("hello", null);

            rig.Clock.Advance(10 * Minute); // < 30min idle
            var c2 = rig.NewClient();
            Assert.AreEqual(session, c2.SessionId, "session must survive a cold start within idle");
            Assert.AreEqual(anon, c2.AnonId);
        }

        [Test]
        public void ColdStartAfterIdleRotatesSessionKeepsAnonId()
        {
            var rig = new Rig();
            var c1 = rig.NewClient();
            string session = c1.SessionId;
            string anon = c1.AnonId;

            rig.Clock.Advance(31 * Minute);
            var c2 = rig.NewClient();
            Assert.AreNotEqual(session, c2.SessionId);
            Assert.AreEqual(anon, c2.AnonId, "anonId is permanent until Reset()");
        }

        [Test]
        public void IdleGapMidRunFlushesOldTailUnderOldIds()
        {
            var rig = new Rig();
            var client = rig.NewClient();
            string oldSession = client.SessionId;
            client.Screen("/menu", null);

            rig.Clock.Advance(31 * Minute);
            client.Capture("after_gap", null); // triggers rotation; old tail deferred

            client.Flush(); // sends the deferred old-session batch first
            client.Flush(); // then the live batch
            Assert.AreEqual(2, rig.Transport.Sent.Count);
            Assert.AreEqual(oldSession, rig.Transport.Sent[0].SessionId());
            CollectionAssert.Contains(rig.Transport.Sent[0].EventNames(), "pageview: /menu");
            Assert.AreEqual(client.SessionId, rig.Transport.Sent[1].SessionId());
            CollectionAssert.Contains(rig.Transport.Sent[1].EventNames(), "after_gap");
            Assert.AreNotEqual(oldSession, client.SessionId);
        }

        [Test]
        public void ResetRegeneratesAnonIdAndSession()
        {
            var rig = new Rig();
            var client = rig.NewClient();
            string anon = client.AnonId;
            string session = client.SessionId;
            client.Reset();
            Assert.AreNotEqual(anon, client.AnonId);
            Assert.AreNotEqual(session, client.SessionId);
            Assert.AreEqual(client.AnonId, rig.Store.Data["agh_uid"], "new anonId must persist");
        }

        [Test]
        public void PauseEmitsLeaveWithDurationAndSaves()
        {
            var rig = new Rig();
            var client = rig.NewClient();
            client.Screen("/game", null);
            rig.Clock.Advance(12_500);
            client.OnPause();

            Assert.GreaterOrEqual(rig.Store.SaveCount, 1, "OnPause must flush PlayerPrefs");
            var batch = rig.Transport.Sent[rig.Transport.Sent.Count - 1];
            var names = batch.EventNames();
            CollectionAssert.Contains(names, "leave: /game");
            var leave = batch.Event(names.IndexOf("leave: /game"));
            Assert.AreEqual(12.5, leave.Props()["duration_s"]);
        }

        [Test]
        public void ResumeWithinIdleRestartsStintTimer()
        {
            var rig = new Rig();
            var client = rig.NewClient();
            client.Screen("/game", null);
            rig.Clock.Advance(60_000);
            client.OnPause();

            rig.Clock.Advance(5 * Minute); // backgrounded 5 min — session survives
            client.OnResume();
            rig.Clock.Advance(10_000);
            client.Screen("/results", null); // leave for /game covers only the post-resume stint

            var batch = rig.Transport.Sent[rig.Transport.Sent.Count - 1];
            client.Flush();
            var last = rig.Transport.Sent[rig.Transport.Sent.Count - 1];
            var names = last.EventNames();
            int i = names.LastIndexOf("leave: /game");
            Assert.GreaterOrEqual(i, 0);
            Assert.AreEqual(10.0, last.Event(i).Props()["duration_s"],
                "backgrounded time must never count toward screen duration");
        }

        [Test]
        public void ResumeAfterIdleRotatesAndReentersCurrentScreen()
        {
            var rig = new Rig();
            var client = rig.NewClient();
            client.Screen("/game", null);
            client.OnPause();
            string oldSession = client.SessionId;

            rig.Clock.Advance(45 * Minute);
            client.OnResume();
            Assert.AreNotEqual(oldSession, client.SessionId);

            client.Flush();
            var batch = rig.Transport.Sent[rig.Transport.Sent.Count - 1];
            Assert.AreEqual(client.SessionId, batch.SessionId());
            CollectionAssert.Contains(batch.EventNames(), "pageview: /game");
            // rotated session's context reports the current screen as its landing path
            StringAssert.Contains("/game", (string)batch.Context()["landingUrl"]);
        }

        [Test]
        public void FirstInteractionMsIsRelativeToSessionStart()
        {
            var rig = new Rig();
            var client = rig.NewClient();
            rig.Clock.Advance(2_250);
            client.RecordInteraction();
            client.Capture("x", null);
            client.Flush();
            Assert.AreEqual(2250L, rig.Transport.Sent[0].Behavior()["firstInteractionMs"]);
        }
    }
}
