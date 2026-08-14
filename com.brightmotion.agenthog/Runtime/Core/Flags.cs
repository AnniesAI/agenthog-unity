using System.Collections.Generic;
using System.Globalization;

namespace Brightmotion.AgentHog.Core
{
    /// <summary>One arm of a feature flag. Weights are integers summing to 100; the bucket
    /// walks the variants in ARRAY ORDER, so order is part of the assignment.</summary>
    internal sealed class FlagVariantDef
    {
        public string Key;
        public int Weight;
    }

    internal sealed class FlagDef
    {
        public string Key;
        public string Type;       // "boolean" | "multivariate"
        public bool Enabled;
        public string Salt;
        public int TrafficBps;    // 0..10000
        public List<FlagVariantDef> Variants;
    }

    /// <summary>Parsed GET /sdk/flags payload: { rev, flags: [...] }.</summary>
    internal sealed class FlagsConfig
    {
        public long Rev;
        public List<FlagDef> Flags;

        public FlagDef Find(string key)
        {
            for (int i = 0; i < Flags.Count; i++)
                if (Flags[i].Key == key)
                    return Flags[i];
            return null;
        }

        /// <summary>Tolerant parse of the ruleset JSON; null on anything malformed.</summary>
        public static FlagsConfig Parse(string json)
        {
            if (!(Json.Parse(json) is Dictionary<string, object> root)) return null;
            if (!(root.TryGetValue("rev", out var revObj) && revObj is long rev)) return null;
            if (!(root.TryGetValue("flags", out var flagsObj) && flagsObj is List<object> list)) return null;
            var flags = new List<FlagDef>(list.Count);
            foreach (var item in list)
            {
                if (!(item is Dictionary<string, object> f)) return null;
                var def = new FlagDef
                {
                    Key = f.TryGetValue("key", out var k) ? k as string : null,
                    Type = f.TryGetValue("type", out var t) ? t as string : null,
                    Enabled = f.TryGetValue("enabled", out var e) && e is bool eb && eb,
                    Salt = f.TryGetValue("salt", out var s) ? s as string : null,
                    TrafficBps = f.TryGetValue("trafficBps", out var tb) && tb is long tbl ? (int)tbl : 0,
                    Variants = new List<FlagVariantDef>(),
                };
                if (def.Key == null || def.Salt == null) return null;
                if (f.TryGetValue("variants", out var vs) && vs is List<object> vlist)
                    foreach (var vitem in vlist)
                        if (vitem is Dictionary<string, object> vd)
                            def.Variants.Add(new FlagVariantDef
                            {
                                Key = vd.TryGetValue("key", out var vk) ? vk as string : "",
                                Weight = vd.TryGetValue("weight", out var vw) && vw is long vwl ? (int)vwl : 0,
                            });
                flags.Add(def);
            }
            return new FlagsConfig { Rev = rev, Flags = flags };
        }
    }

    /// <summary>
    /// The CONTRACTS.md bucketing spec, ported from the normative TS implementation
    /// (agent-hog packages/tracker/src/flags.ts) and pinned to the same canonical test
    /// vectors (Tests/FlagsTests.cs). Any drift here reshuffles users on Unity but not
    /// on web/RN — treat this file as frozen alongside the spec.
    ///
    ///   enrolled = fnv1a32(key + "." + salt + ".t." + anonId) % 10000 &lt; trafficBps
    ///   bucket   = fnv1a32(key + "." + salt + ".v." + anonId) % 10000
    ///   variant  = first range containing bucket, cumulative weight * 100, array order
    ///
    /// Two independent hashes so ramping trafficBps up only ADDS users.
    /// </summary>
    internal static class FlagEval
    {
        /// <summary>FNV-1a 32-bit over the string's UTF-16 code units — identical to the TS
        /// reference (charCodeAt) and to UTF-8 bytes for the ASCII inputs the contract
        /// guarantees (key regex, hex salt, uuid anonId).</summary>
        public static uint Fnv1a32(string s)
        {
            unchecked
            {
                uint h = 2166136261;
                for (int i = 0; i < s.Length; i++)
                {
                    h ^= s[i];
                    h *= 16777619;
                }
                return h;
            }
        }

        /// <summary>The assigned variant key, or null when the caller's code fallback applies
        /// (disabled, outside the traffic allocation, or malformed weights).</summary>
        public static string Evaluate(FlagDef f, string anonId)
        {
            if (f == null || !f.Enabled || f.Variants == null) return null;
            string basis = f.Key + "." + f.Salt;
            if (Fnv1a32(basis + ".t." + anonId) % 10000 >= (uint)f.TrafficBps) return null;
            uint bucket = Fnv1a32(basis + ".v." + anonId) % 10000;
            long cum = 0;
            for (int i = 0; i < f.Variants.Count; i++)
            {
                cum += f.Variants[i].Weight * 100L;
                if (bucket < cum) return f.Variants[i].Key;
            }
            return null;
        }

        public static long ParseRev(string s)
            => long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out long v) ? v : -1;
    }
}
