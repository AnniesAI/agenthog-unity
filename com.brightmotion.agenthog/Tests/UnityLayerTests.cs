using Brightmotion.AgentHog.Core;
using Brightmotion.AgentHog.Unity;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;

namespace Brightmotion.AgentHog.Tests
{
    public class UnityLayerTests
    {
        [Test]
        public void SlugHandlesGameNames()
        {
            Assert.AreEqual("space-miner", Client.Slug("Space Miner"));
            Assert.AreEqual("mainmenu", Client.Slug("MainMenu"));
            Assert.AreEqual("level-2-boss", Client.Slug("Level 2: Boss!"));
            Assert.AreEqual("app", Client.Slug(""));
            Assert.AreEqual("app", Client.Slug("!!!"));
        }

        [Test]
        public void NormalizePathEnsuresLeadingSlash()
        {
            Assert.AreEqual("/menu", Client.NormalizePath("menu"));
            Assert.AreEqual("/menu", Client.NormalizePath("/menu"));
            Assert.AreEqual("/", Client.NormalizePath(null));
            Assert.AreEqual("/", Client.NormalizePath(""));
        }

        [Test]
        public void CollapseFollowsClickLabelRule()
        {
            Assert.AreEqual("Hello World", UiClickTracker.Collapse("  Hello\n\t World  "));
            Assert.AreEqual("", UiClickTracker.Collapse(null));
            Assert.AreEqual(50, UiClickTracker.Collapse(new string('x', 80)).Length);
        }

        [Test]
        public void SelectorIsCappedHierarchyPath()
        {
            var root = new GameObject("Root");
            try
            {
                var current = root.transform;
                foreach (string name in new[] { "HUD", "ShopPanel", "BuyRow", "Deep1", "Deep2" })
                {
                    var child = new GameObject(name);
                    child.transform.SetParent(current);
                    current = child.transform;
                }
                string selector = UiClickTracker.BuildSelector(current.gameObject, "Button");
                Assert.AreEqual("HUD>ShopPanel>BuyRow>Deep1>Button:Deep2", selector,
                    "≤5 segments, leaf as Type:name");
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void LabelPrefersTextOverGameObjectName()
        {
            var button = new GameObject("BuyButton");
            try
            {
                var textGo = new GameObject("Label");
                textGo.transform.SetParent(button.transform);
                var text = textGo.AddComponent<Text>();
                text.text = "  Buy   100 Gems ";
                Assert.AreEqual("Buy 100 Gems", UiClickTracker.FindLabelText(button));
            }
            finally
            {
                Object.DestroyImmediate(button);
            }
        }

        [Test]
        public void UserAgentNeverLooksLikeAnHttpLibrary()
        {
            string ua = AgentHog.BuildUserAgent("My Game!", "2.0.1");
            StringAssert.StartsWith("My-Game/2.0.1 AgentHogUnity/" + AgentHog.SdkVersion + " (", ua);
            StringAssert.DoesNotContain("UnityPlayer", ua);
        }

        [Test]
        public void UninitializedFacadeIsANoOp()
        {
            AgentHog.ShutdownForTests();
            Assert.IsFalse(AgentHog.Enabled);
            Assert.AreEqual("", AgentHog.AnonId);
            // none of these may throw pre-Init
            AgentHog.Capture("x");
            AgentHog.Screen("/x");
            AgentHog.Identify("a@b.c");
            AgentHog.Tag("t", 1);
            AgentHog.Flush();
            AgentHog.Reset();
        }

        [Test]
        public void DisabledConfigStaysInert()
        {
            AgentHog.ShutdownForTests();
            AgentHog.Init(new AgentHogConfig { Host = "https://h.example", ProjectKey = "", Enabled = true });
            Assert.IsFalse(AgentHog.Enabled, "empty key must leave the SDK inert");
            AgentHog.Init(new AgentHogConfig { Host = "https://h.example", ProjectKey = "ah_x", Enabled = false });
            Assert.IsFalse(AgentHog.Enabled, "Enabled=false must leave the SDK inert");
            AgentHog.ShutdownForTests();
        }

        [Test]
        public void LanguageTagMapsCommonLanguages()
        {
            Assert.AreEqual("en", UnityContextProvider.LanguageTag(SystemLanguage.English));
            Assert.AreEqual("zh-Hans", UnityContextProvider.LanguageTag(SystemLanguage.ChineseSimplified));
            Assert.AreEqual("tr", UnityContextProvider.LanguageTag(SystemLanguage.Turkish));
        }

        [Test]
        public void SettingsAssetMapsToConfig()
        {
            var settings = ScriptableObject.CreateInstance<AgentHogSettings>();
            try
            {
                settings.host = " https://hog.example.com ";
                settings.projectKey = "ah_key1";
                settings.debugLog = true;
                var config = settings.ToConfig();
                Assert.AreEqual("https://hog.example.com", config.Host);
                Assert.AreEqual("ah_key1", config.ProjectKey);
                Assert.IsTrue(config.Enabled);
                Assert.IsTrue(config.Debug);

                settings.projectKey = "";
                Assert.IsFalse(settings.ToConfig().Enabled, "blank key → inert (public-repo default)");
            }
            finally
            {
                Object.DestroyImmediate(settings);
            }
        }
    }
}
