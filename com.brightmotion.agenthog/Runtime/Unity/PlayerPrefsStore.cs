using System;
using Brightmotion.AgentHog.Core;
using UnityEngine;

namespace Brightmotion.AgentHog.Unity
{
    /// <summary>IKeyValueStore over PlayerPrefs. Save() flushes to disk (called on pause/quit).</summary>
    internal sealed class PlayerPrefsStore : IKeyValueStore
    {
        public string Get(string key) => PlayerPrefs.HasKey(key) ? PlayerPrefs.GetString(key) : null;
        public void Set(string key, string value) => PlayerPrefs.SetString(key, value);
        public void Delete(string key) => PlayerPrefs.DeleteKey(key);
        public void Save() => PlayerPrefs.Save();
    }

    internal sealed class SystemClock : IClock
    {
        public long NowMs => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }
}
