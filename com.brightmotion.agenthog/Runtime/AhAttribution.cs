using System.Collections.Generic;

namespace Brightmotion.AgentHog
{
    /// <summary>
    /// A third-party attribution verdict (an MMP result, e.g. from Singular's device
    /// attribution callback) handed to <see cref="AgentHog.SetAttribution"/>. Ships over the
    /// wire as <c>context.attribution</c>; the server maps each utm field to the same-named
    /// session column under its precedence rules (deep-link &gt; attach &gt; install-referrer)
    /// and stores <see cref="Params"/> verbatim, never mapped.
    /// </summary>
    public sealed class AhAttribution
    {
        /// <summary>Freeform slug naming who produced the verdict, e.g. "singular".
        /// Required — the call is a no-op without it. A provider-only payload is valid:
        /// it records that the provider answered "organic".</summary>
        public string Provider;

        public string UtmSource;
        public string UtmMedium;
        public string UtmCampaign;
        public string UtmContent;
        public string UtmTerm;

        /// <summary>Provider extras (campaign_id, click_timestamp, …) — stored verbatim.
        /// Server caps: ≤32 keys, key ≤100 / value ≤500 chars.</summary>
        public Dictionary<string, string> Params;
    }
}
