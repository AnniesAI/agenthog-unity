using System;
using System.Collections.Generic;
using Brightmotion.AgentHog.Core;

namespace Brightmotion.AgentHog.Tests
{
    internal sealed class FakeClock : IClock
    {
        public long Now;
        public long NowMs => Now;
        public void Advance(long ms) => Now += ms;
    }

    internal sealed class FakeStore : IKeyValueStore
    {
        public readonly Dictionary<string, string> Data = new Dictionary<string, string>();
        public readonly HashSet<string> GetThrowsFor = new HashSet<string>();
        public readonly HashSet<string> SetThrowsFor = new HashSet<string>();
        public int SaveCount;

        public string Get(string key)
        {
            if (GetThrowsFor.Contains(key)) throw new InvalidOperationException("io error: " + key);
            return Data.TryGetValue(key, out var v) ? v : null;
        }

        public void Set(string key, string value)
        {
            if (SetThrowsFor.Contains(key)) throw new InvalidOperationException("io error: " + key);
            Data[key] = value;
        }
        public void Delete(string key) => Data.Remove(key);
        public void Save() => SaveCount++;
    }

    internal sealed class SentBatch
    {
        public string Url;
        public string Json;
        public string UserAgent;
        public Dictionary<string, object> Parsed;
    }

    internal sealed class FakeTransport : ITransport
    {
        public readonly List<SentBatch> Sent = new List<SentBatch>();
        public readonly List<Action<TransportStatus, int, string, string>> Pending =
            new List<Action<TransportStatus, int, string, string>>();

        /// <summary>When true (default), sends complete synchronously with NextStatus.</summary>
        public bool AutoComplete = true;
        public TransportStatus NextStatus = TransportStatus.Success;
        public int NextCode = 204;
        public string NextBody;
        /// <summary>x-agh-flags-rev echoed on successful sends (null = header absent).</summary>
        public string NextFlagsRev;
        /// <summary>Per-batch responder; overrides Next* when set.</summary>
        public Func<SentBatch, (TransportStatus status, int code, string body)> Respond;

        // ---- /sdk/flags fetches ----
        public readonly List<string> FetchUrls = new List<string>();
        public readonly List<Action<int, string>> PendingFetch = new List<Action<int, string>>();
        public bool AutoCompleteFetch = true;
        public int FlagsCode = 200;
        public string FlagsBody; // null + AutoCompleteFetch = fetch fails

        public void Send(string url, string json, string userAgent, Action<TransportStatus, int, string, string> callback)
        {
            var batch = new SentBatch
            {
                Url = url,
                Json = json,
                UserAgent = userAgent,
                Parsed = Json_Parse(json),
            };
            Sent.Add(batch);
            var (status, code, body) = Respond != null ? Respond(batch) : (NextStatus, NextCode, NextBody);
            if (AutoComplete) callback(status, code, body, NextFlagsRev);
            else Pending.Add(callback);
        }

        public void CompleteOldest(TransportStatus status, int code, string body = null, string flagsRev = null)
        {
            var cb = Pending[0];
            Pending.RemoveAt(0);
            cb(status, code, body, flagsRev);
        }

        public void Fetch(string url, string userAgent, Action<int, string> callback)
        {
            FetchUrls.Add(url);
            if (AutoCompleteFetch) callback(FlagsBody != null ? FlagsCode : 0, FlagsBody);
            else PendingFetch.Add(callback);
        }

        public void CompleteOldestFetch(int code, string body)
        {
            var cb = PendingFetch[0];
            PendingFetch.RemoveAt(0);
            cb(code, body);
        }

        static Dictionary<string, object> Json_Parse(string json)
            => Core.Json.Parse(json) as Dictionary<string, object>;
    }

    internal sealed class FakeContext : IContextProvider
    {
        public string DeepLink = "";
        public string DeepLinkUrl => DeepLink;
        public string ScreenSize => "2778x1284";
        public string ViewportSize => "1170x540";
        public string Timezone => "America/Chicago";
        public string Language => "en";

        public Dictionary<string, object> AutoRegistered => new Dictionary<string, object>
        {
            { "platform", "ios" },
            { "app_version", "1.2.3" },
            { "os_version", "iOS 19.1" },
            { "device_model", "iPhone17,2" },
            { "engine", "unity 2021.3.58f1" },
        };
    }

    /// <summary>Shared test rig with sane defaults; individual tests override pieces.</summary>
    internal sealed class Rig
    {
        public readonly FakeClock Clock = new FakeClock { Now = 1_760_000_000_000 };
        public readonly FakeStore Store = new FakeStore();
        public readonly FakeTransport Transport = new FakeTransport();
        public readonly FakeContext Context = new FakeContext();
        public int IdCounter;

        public CoreConfig Config = new CoreConfig
        {
            Host = "https://hog.example.com",
            ProjectKey = "ah_test01",
            AppName = "Space Miner",
            AppVersion = "1.2.3",
            UserAgent = "SpaceMiner/1.2.3 AgentHogUnity/0.1.0 (ios iOS 19.1)",
        };

        public string NextId() => "00000000-0000-4000-8000-" + (++IdCounter).ToString("d12");

        public Client NewClient() =>
            new Client(Config, Store, Clock, Transport, Context, NextId, null);
    }
}
