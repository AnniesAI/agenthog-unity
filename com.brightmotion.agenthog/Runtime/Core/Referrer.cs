using System;
using System.Collections.Generic;

namespace Brightmotion.AgentHog.Core
{
    /// <summary>
    /// Pure install-referrer parsing. Extracts only the plaintext utm_* params for the
    /// landing-params pipe; the full raw referrer ships to the server, which owns
    /// classification and the Meta decrypt.
    /// </summary>
    internal static class Referrer
    {
        static readonly string[] UtmKeys =
            { "utm_source", "utm_medium", "utm_campaign", "utm_content", "utm_term" };

        const int MinEnvelopeDataChars = 32; // the AES-GCM auth tag alone, hex-encoded

        /// <summary>
        /// Does this utm_content carry Meta's encrypted {source:{data,nonce}} envelope?
        /// Must match the server's extractMetaEncryptedSource (and the RN SDK's isMetaEnvelope)
        /// exactly — if the predicates disagree, the blob leaks into the landing params or
        /// real utm_content is stripped. Mirror any change there into here (and its tests).
        /// </summary>
        internal static bool IsMetaEnvelope(string content)
        {
            if (!(Json.Parse(content) is Dictionary<string, object> parsed)) return false;
            if (!(parsed.TryGetValue("source", out var s) && s is Dictionary<string, object> source)) return false;
            return source.TryGetValue("data", out var d) && d is string data
                && source.TryGetValue("nonce", out var n) && n is string nonce
                && IsHex(data) && IsHex(nonce)
                && data.Length > MinEnvelopeDataChars;
        }

        static bool IsHex(string s)
        {
            if (s.Length == 0) return false;
            foreach (char c in s)
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')))
                    return false;
            return true;
        }

        /// <summary>
        /// The plaintext utm_* params in a raw referrer string, in stable utm-key order.
        /// Meta's encrypted utm_content blob is omitted — it would bloat the landing URL and
        /// is only decryptable server-side. Tolerates already-decoded input.
        /// </summary>
        internal static List<KeyValuePair<string, string>> UtmParams(string referrer)
        {
            var parsed = new Dictionary<string, string>();
            foreach (string part in referrer.Split('&'))
            {
                int eq = part.IndexOf('=');
                if (eq <= 0) continue;
                string key = part.Substring(0, eq);
                string value = part.Substring(eq + 1);
                try { value = Uri.UnescapeDataString(value); }
                catch (UriFormatException) { /* a stray % is data, not an error */ }
                if (value.Length > 0) parsed[key] = value;
            }
            var utms = new List<KeyValuePair<string, string>>();
            foreach (string key in UtmKeys)
            {
                if (!parsed.TryGetValue(key, out string value)) continue;
                if (key == "utm_content" && IsMetaEnvelope(value)) continue;
                utms.Add(new KeyValuePair<string, string>(key, value));
            }
            return utms;
        }
    }
}
