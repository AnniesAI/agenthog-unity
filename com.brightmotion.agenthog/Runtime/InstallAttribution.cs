using System;
using System.Collections.Generic;

namespace Brightmotion.AgentHog
{
    /// <summary>
    /// A raw Play Install Referrer read. <see cref="Referrer"/> is the untouched referrer
    /// string (it may carry Meta's encrypted envelope — the server classifies and decrypts);
    /// the timestamps are ReferrerDetails' click/install-begin times in epoch seconds, when
    /// the reader can supply them.
    /// </summary>
    public sealed class InstallReferrerResult
    {
        public string Referrer;
        public long? ClickTs;
        public long? InstallBeginTs;
    }

    /// <summary>
    /// Resolves the device's raw install referrer. Call <paramref name="callback"/> exactly
    /// once, on the Unity main thread: with a result on success, with null when there is no
    /// referrer (iOS, editor, non-Play stores — a permanent answer), or not at all on a
    /// transient failure (the read retries next launch). Set via
    /// <see cref="AgentHogConfig.InstallReferrer"/>.
    /// </summary>
    public delegate void InstallReferrerProvider(Action<InstallReferrerResult> callback);

    /// <summary>
    /// The server-computed install attribution for this install, from the ingest response to
    /// the batch that carried the referrer. Cached durably; survives <see cref="AgentHog.Reset"/>
    /// (it belongs to the install, not the person).
    /// </summary>
    public sealed class InstallAttribution
    {
        /// <summary>"meta_referrer" | "play_referrer" | "organic" | "none".</summary>
        public string Source;

        /// <summary>Plaintext utm_* params the server extracted from the referrer.</summary>
        public Dictionary<string, string> Utm;

        /// <summary>Decrypted Meta campaign fields, or null (no Meta envelope / not yet decrypted).</summary>
        public Dictionary<string, object> Meta;

        /// <summary>True while a Meta envelope awaits the project's decryption key; the SDK
        /// re-asks on later launches until it resolves.</summary>
        public bool Pending;
    }
}
