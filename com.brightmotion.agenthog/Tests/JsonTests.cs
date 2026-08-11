using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using Brightmotion.AgentHog.Core;
using NUnit.Framework;

namespace Brightmotion.AgentHog.Tests
{
    public class JsonTests
    {
        [Test]
        public void SerializesPrimitivesAndCollections()
        {
            var obj = new JsonObj()
                .Add("s", "hi")
                .Add("i", 42)
                .Add("l", 9_000_000_000L)
                .Add("d", 0.875)
                .Add("b", true)
                .Add("n", null)
                .Add("arr", new List<object> { 1, "two", false })
                .Add("dict", new Dictionary<string, object> { { "k", "v" } });
            Assert.AreEqual(
                "{\"s\":\"hi\",\"i\":42,\"l\":9000000000,\"d\":0.875,\"b\":true,\"n\":null," +
                "\"arr\":[1,\"two\",false],\"dict\":{\"k\":\"v\"}}",
                Json.Serialize(obj));
        }

        [Test]
        public void EscapesStrings()
        {
            Assert.AreEqual("\"a\\\"b\\\\c\\n\\t\\u0001\"", Json.Serialize("a\"b\\c\n\t"));
        }

        [Test]
        public void WholeDoublesPrintWithoutFraction()
        {
            Assert.AreEqual("3", Json.Serialize(3.0));
            Assert.AreEqual("-2", Json.Serialize(-2.0f));
            Assert.AreEqual("2.5", Json.Serialize(2.5));
        }

        [Test]
        public void NumbersAreCultureInvariant()
        {
            var previous = Thread.CurrentThread.CurrentCulture;
            try
            {
                // tr-TR uses ',' as the decimal separator — the classic corruption bug
                Thread.CurrentThread.CurrentCulture = new CultureInfo("tr-TR");
                Assert.AreEqual("{\"d\":1.5}", Json.Serialize(new JsonObj().Add("d", 1.5)));
                Assert.AreEqual("{\"f\":0.25}", Json.Serialize(new JsonObj().Add("f", 0.25f)));
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = previous;
            }
        }

        [Test]
        public void ParsesRoundTrip()
        {
            const string json = "{\"a\":1,\"b\":2.5,\"c\":\"x\\ny\",\"d\":[true,null],\"e\":{\"f\":-3}}";
            var parsed = Json.Parse(json) as Dictionary<string, object>;
            Assert.NotNull(parsed);
            Assert.AreEqual(1L, parsed["a"]);
            Assert.AreEqual(2.5, parsed["b"]);
            Assert.AreEqual("x\ny", parsed["c"]);
            var arr = parsed["d"] as List<object>;
            Assert.AreEqual(true, arr[0]);
            Assert.IsNull(arr[1]);
            var nested = parsed["e"] as Dictionary<string, object>;
            Assert.AreEqual(-3L, nested["f"]);
        }

        [Test]
        public void ParseOfGarbageReturnsNull()
        {
            Assert.IsNull(Json.Parse(null));
            Assert.IsNull(Json.Parse(""));
            Assert.IsNull(Json.Parse("{broken"));
            Assert.IsNull(Json.Parse("{\"a\":1} trailing"));
        }
    }
}
