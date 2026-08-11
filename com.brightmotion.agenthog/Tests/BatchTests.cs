using System.Collections.Generic;
using NUnit.Framework;

namespace Brightmotion.AgentHog.Tests
{
    public class BatchTests
    {
        [Test]
        public void ContextSentOnFirstFlushOnly()
        {
            var rig = new Rig();
            var client = rig.NewClient();
            client.Capture("one", null);
            client.Flush();
            client.Capture("two", null);
            client.Flush();

            Assert.NotNull(rig.Transport.Sent[0].Context(), "first flush carries context");
            Assert.IsNull(rig.Transport.Sent[1].Context(), "second flush must not");
        }

        [Test]
        public void RegisterResendsContextAndMergesIntoNewEvents()
        {
            var rig = new Rig();
            var client = rig.NewClient();
            client.Capture("before", null);
            client.Flush();

            client.Register(new Dictionary<string, object> { { "build_channel", "beta" } });
            client.Capture("after", null);
            client.Flush();

            var batch = rig.Transport.Sent[1];
            var registered = batch.Context()["registered"] as Dictionary<string, object>;
            Assert.AreEqual("beta", registered["build_channel"]);
            Assert.AreEqual("ios", registered["platform"], "auto-registered props survive");
            Assert.AreEqual("beta", batch.Event(0).Props()["build_channel"]);
        }

        [Test]
        public void BehaviorIncludedOnEveryFlush()
        {
            var rig = new Rig();
            var client = rig.NewClient();
            client.Capture("a", null);
            client.Flush();
            var b1 = rig.Transport.Sent[0].Behavior();
            Assert.AreEqual(false, b1["mouseMoved"]);
            Assert.AreEqual(false, b1["anyScroll"]);
            Assert.IsNull(b1["firstInteractionMs"]);

            client.RecordMouseMove();
            client.RecordDrag();
            client.Capture("b", null);
            client.Flush();
            var b2 = rig.Transport.Sent[1].Behavior();
            Assert.AreEqual(true, b2["mouseMoved"]);
            Assert.AreEqual(true, b2["anyScroll"]);
        }

        [Test]
        public void EventNamingIsByteExact()
        {
            var rig = new Rig();
            var client = rig.NewClient();
            client.Screen("/main-menu", "Main Menu");
            client.EmitClick("Play", "Canvas>Button:play", "Play");
            client.Capture("level_complete", null);
            client.Tag("ab_test", "b");
            client.Identify("p@example.com", null);
            client.Screen("/game", null);
            client.Flush();

            var names = rig.Transport.Sent[0].EventNames();
            CollectionAssert.AreEqual(new List<string>
            {
                "pageview: /main-menu",
                "click: Play",
                "level_complete",
                "tag: ab_test",
                "identify",
                "leave: /main-menu",
                "pageview: /game",
            }, names);
        }

        [Test]
        public void EventsCarryPathTsAndMergedProps()
        {
            var rig = new Rig();
            var client = rig.NewClient();
            client.Screen("/shop", null);
            client.Capture("purchase", new Dictionary<string, object> { { "sku", "gems_100" } });
            client.Flush();

            var ev = rig.Transport.Sent[0].Event(1);
            Assert.AreEqual("custom", ev["type"]);
            Assert.AreEqual("/shop", ev["path"]);
            Assert.AreEqual(rig.Clock.Now, ev["ts"]);
            Assert.AreEqual("gems_100", ev.Props()["sku"]);
            Assert.AreEqual("ios", ev.Props()["platform"], "registered props merge into event props");
        }

        [Test]
        public void ClickPropsMatchContract()
        {
            var rig = new Rig();
            var client = rig.NewClient();
            client.EmitClick("Buy", "Shop>Button:buy", "Buy");
            client.Flush();
            var props = rig.Transport.Sent[0].Event(0).Props();
            Assert.AreEqual("Shop>Button:buy", props["selector"]);
            Assert.AreEqual("Buy", props["text"]);
            Assert.AreEqual(true, props["interactive"]);
            Assert.AreEqual(true, props["trusted"]);
        }

        [Test]
        public void IdentifyAndTagMergeTraits()
        {
            var rig = new Rig();
            var client = rig.NewClient();
            client.Identify("p@example.com", new Dictionary<string, object> { { "user_id", 42 } });
            client.Tag("ab_test", "variant_b");
            client.Flush();

            var identify = rig.Transport.Sent[0].Identify();
            Assert.AreEqual("p@example.com", identify["email"]);
            var traits = identify["traits"] as Dictionary<string, object>;
            Assert.AreEqual(42L, traits["user_id"]);
            Assert.AreEqual("variant_b", traits["ab_test"]);

            var tagEvent = rig.Transport.Sent[0].Event(1);
            Assert.AreEqual("tag: ab_test", tagEvent["name"]);
            Assert.AreEqual("variant_b", tagEvent.Props()["value"]);
        }

        [Test]
        public void SyntheticLandingUrlUsesAppSlugAndFirstPath()
        {
            var rig = new Rig();
            var client = rig.NewClient();
            client.Screen("/main-menu", null);
            client.Screen("/game", null);
            client.Flush();

            var context = rig.Transport.Sent[0].Context();
            Assert.AreEqual("app://space-miner/main-menu", context["landingUrl"]);
            Assert.AreEqual("", context["referrer"]);
            Assert.AreEqual("2778x1284", context["screen"]);
            Assert.AreEqual("1170x540", context["viewport"]);
            Assert.AreEqual("America/Chicago", context["tz"]);
            Assert.AreEqual("en", context["lang"]);
            Assert.IsFalse(context.ContainsKey("signals"), "browser bot signals must be omitted");
        }

        [Test]
        public void DeepLinkWinsAndLandingExtrasAppendOnlyAbsentKeys()
        {
            var rig = new Rig();
            rig.Context.DeepLink = "https://game.example/start?utm_source=email";
            var client = rig.NewClient();
            client.SetLandingParams(new Dictionary<string, string>
            {
                { "utm_source", "playstore" },   // present in deep link → must NOT override
                { "utm_campaign", "launch" },
            });
            client.Capture("x", null);
            client.Flush();

            var context = rig.Transport.Sent[0].Context();
            Assert.AreEqual("https://game.example/start?utm_source=email&utm_campaign=launch",
                context["landingUrl"]);
            Assert.AreEqual("https://game.example/start?utm_source=email", context["referrer"]);
        }

        [Test]
        public void LandingParamsAfterContextSentAreNeverResent()
        {
            var rig = new Rig();
            var client = rig.NewClient();
            client.Capture("x", null);
            client.Flush();

            client.SetLandingParams(new Dictionary<string, string> { { "utm_source", "late" } });
            client.Capture("y", null);
            client.Flush();
            Assert.IsNull(rig.Transport.Sent[1].Context(),
                "late landing params can't backfill; context must not re-send");
        }

        [Test]
        public void UserAgentAndUrlArePassedThrough()
        {
            var rig = new Rig();
            var client = rig.NewClient();
            client.Capture("x", null);
            client.Flush();
            Assert.AreEqual("https://hog.example.com/ingest", rig.Transport.Sent[0].Url);
            Assert.AreEqual(rig.Config.UserAgent, rig.Transport.Sent[0].UserAgent);
        }
    }
}
