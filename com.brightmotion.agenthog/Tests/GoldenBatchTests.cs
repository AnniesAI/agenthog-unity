using System;
using System.Collections.Generic;
using System.IO;
using Brightmotion.AgentHog.Core;
using NUnit.Framework;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace Brightmotion.AgentHog.Tests
{
    /// <summary>
    /// Wire-parity anchor: a fully deterministic batch must serialize byte-identically to
    /// Tests/Fixtures/golden-batch.json. The agent-hog repo's scripts/check-unity-contract.ts
    /// runs the same fixture through the real ingest validation — together they pin this SDK
    /// to the contract. Regenerate deliberately with AGENTHOG_UPDATE_GOLDEN=1 after intended
    /// wire changes, then re-run the server-side check.
    /// </summary>
    public class GoldenBatchTests
    {
        [Test]
        public void SerializedBatchMatchesGoldenFixture()
        {
            string json = BuildDeterministicBatch();
            string path = FixturePath();

            if (!File.Exists(path) || Environment.GetEnvironmentVariable("AGENTHOG_UPDATE_GOLDEN") == "1")
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, json + "\n");
                Assert.Ignore("golden fixture (re)generated at " + path + " — commit it and re-run");
            }

            string expected = File.ReadAllText(path).TrimEnd('\n');
            Assert.AreEqual(expected, json, "wire format drifted from the golden fixture");
        }

        internal static string BuildDeterministicBatch()
        {
            var rig = new Rig();
            var client = rig.NewClient();
            client.Screen("/main-menu", "Main Menu");
            rig.Clock.Advance(1500);
            client.RecordInteraction();
            client.EmitClick("Play", "MainMenu>Canvas>Button:PlayButton", "Play");
            client.Register(new Dictionary<string, object> { { "build_channel", "beta" } });
            rig.Clock.Advance(2000);
            client.Screen("/game", "Game");
            client.Capture("level_complete", new Dictionary<string, object>
            {
                { "level", 12 }, { "stars", 3 }, { "accuracy", 0.875 },
            });
            client.Tag("ab_test", "variant_b");
            client.Identify("player@example.com", new Dictionary<string, object> { { "user_id", 42 } });
            client.SetLandingParams(new Dictionary<string, string>
            {
                { "utm_source", "playstore" }, { "utm_campaign", "launch" },
            });
            client.Flush();
            return rig.Transport.Sent[0].Json;
        }

        static string FixturePath()
        {
            var package = PackageInfo.FindForAssembly(typeof(Client).Assembly);
            Assert.NotNull(package, "package info must resolve for the SDK assembly");
            return Path.Combine(package.resolvedPath, "Tests", "Fixtures", "golden-batch.json");
        }
    }
}
