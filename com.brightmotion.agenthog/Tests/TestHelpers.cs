using System.Collections.Generic;
using NUnit.Framework;

namespace Brightmotion.AgentHog.Tests
{
    internal static class TestHelpers
    {
        public static List<object> Events(this SentBatch batch)
            => batch.Parsed["events"] as List<object>;

        public static Dictionary<string, object> Event(this SentBatch batch, int i)
            => batch.Events()[i] as Dictionary<string, object>;

        public static List<string> EventNames(this SentBatch batch)
        {
            var names = new List<string>();
            foreach (var e in batch.Events())
                names.Add((string)((Dictionary<string, object>)e)["name"]);
            return names;
        }

        public static Dictionary<string, object> Context(this SentBatch batch)
            => batch.Parsed.TryGetValue("context", out var c) ? c as Dictionary<string, object> : null;

        public static Dictionary<string, object> Behavior(this SentBatch batch)
            => batch.Parsed.TryGetValue("behavior", out var b) ? b as Dictionary<string, object> : null;

        public static Dictionary<string, object> Identify(this SentBatch batch)
            => batch.Parsed.TryGetValue("identify", out var i) ? i as Dictionary<string, object> : null;

        public static Dictionary<string, object> Props(this Dictionary<string, object> ev)
            => ev.TryGetValue("props", out var p) ? p as Dictionary<string, object> : null;

        public static string SessionId(this SentBatch batch) => (string)batch.Parsed["sessionId"];
        public static string AnonId(this SentBatch batch) => (string)batch.Parsed["anonId"];

        public static void AssertNoBatchSent(this FakeTransport transport, int expectedCount, string message = null)
            => Assert.AreEqual(expectedCount, transport.Sent.Count, message ?? "unexpected batch count");
    }
}
