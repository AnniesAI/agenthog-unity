using System;
using System.Threading;
using UnityEngine;
using UnityEngine.Scripting;

namespace Brightmotion.AgentHog.InstallReferrer
{
    /// <summary>
    /// Play Install Referrer reader for AgentHog install attribution. <see cref="Attach"/>
    /// wires it up as the config's <see cref="AgentHogConfig.InstallReferrer"/> provider; on
    /// Android it reads ReferrerDetails through the Play installreferrer AIDL client (via the
    /// Gradle dependency this package carries — no bundled .aar), everywhere else it resolves
    /// null (no referrer exists — a permanent answer). The raw referrer string ships to the
    /// AgentHog server untouched; classification and Meta decryption happen there.
    /// </summary>
    public static class PlayInstallReferrer
    {
        /// <summary>Register this reader on the config. Call before <see cref="AgentHog.Init"/>;
        /// a provider the game already set is left in place.</summary>
        public static void Attach(AgentHogConfig config)
        {
            if (config != null && config.InstallReferrer == null)
                config.InstallReferrer = Read;
        }

        /// <summary>
        /// InstallReferrerProvider entry point (call on the Unity main thread — the SDK does).
        /// The callback fires once, marshalled back to the main thread: with the referrer, with
        /// null when there is none (a permanent answer), or never on a transient service
        /// failure so the SDK retries next launch.
        /// </summary>
        public static void Read(Action<InstallReferrerResult> callback)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            ReadAndroid(callback);
#else
            callback(null);
#endif
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        static void ReadAndroid(Action<InstallReferrerResult> callback)
        {
            AndroidJavaObject client = null;
            try
            {
                using (var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                using (var activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity"))
                using (var context = activity.Call<AndroidJavaObject>("getApplicationContext"))
                using (var clientClass = new AndroidJavaClass("com.android.installreferrer.api.InstallReferrerClient"))
                using (var builder = clientClass.CallStatic<AndroidJavaObject>("newBuilder", context))
                {
                    client = builder.Call<AndroidJavaObject>("build");
                }
                client.Call("startConnection",
                    new StateListener(client, SynchronizationContext.Current, callback));
            }
            catch (Exception e)
            {
                // installreferrer classes missing (Gradle dependency not resolved): nothing
                // will ever resolve in this build — a permanent answer
                Debug.LogWarning("[AgentHog] install referrer unavailable: " + e.Message);
                if (client != null) client.Dispose();
                callback(null);
            }
        }

        /// <summary>
        /// InstallReferrerStateListener bridge. Callbacks arrive on a binder thread; results
        /// are posted back to the captured main-thread context.
        /// </summary>
        [Preserve]
        sealed class StateListener : AndroidJavaProxy
        {
            // InstallReferrerClient.InstallReferrerResponse
            const int Ok = 0;
            const int ServiceUnavailable = 1; // transient — no callback, the SDK retries next launch

            readonly AndroidJavaObject client;
            readonly SynchronizationContext mainThread;
            Action<InstallReferrerResult> callback;

            public StateListener(AndroidJavaObject client, SynchronizationContext mainThread,
                                 Action<InstallReferrerResult> callback)
                : base("com.android.installreferrer.api.InstallReferrerStateListener")
            {
                this.client = client;
                this.mainThread = mainThread;
                this.callback = callback;
            }

            [Preserve]
            public void onInstallReferrerSetupFinished(int responseCode)
            {
                var pending = Interlocked.Exchange(ref callback, null);
                if (pending == null) return;
                InstallReferrerResult result = null;
                bool permanent = responseCode != ServiceUnavailable;
                try
                {
                    if (responseCode == Ok)
                    {
                        using (var details = client.Call<AndroidJavaObject>("getInstallReferrer"))
                        {
                            string referrer = details.Call<string>("getInstallReferrer");
                            if (!string.IsNullOrEmpty(referrer))
                            {
                                long clickTs = details.Call<long>("getReferrerClickTimestampSeconds");
                                long installBeginTs = details.Call<long>("getInstallBeginTimestampSeconds");
                                result = new InstallReferrerResult
                                {
                                    Referrer = referrer,
                                    ClickTs = clickTs > 0 ? clickTs : (long?)null,
                                    InstallBeginTs = installBeginTs > 0 ? installBeginTs : (long?)null,
                                };
                            }
                        }
                    }
                }
                catch (Exception)
                {
                    permanent = false; // read blew up mid-flight — let next launch retry
                }
                finally
                {
                    try { client.Call("endConnection"); } catch (Exception) { }
                    client.Dispose();
                }
                if (result != null || permanent)
                    Deliver(pending, result);
            }

            [Preserve]
            public void onInstallReferrerServiceDisconnected()
            {
                // transient; when setup never finishes, the SDK's flush timeout takes over
            }

            void Deliver(Action<InstallReferrerResult> pending, InstallReferrerResult result)
            {
                if (mainThread != null) mainThread.Post(_ => pending(result), null);
                else pending(result);
            }
        }
#endif
    }
}
