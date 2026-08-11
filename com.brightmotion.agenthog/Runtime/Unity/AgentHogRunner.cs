using Brightmotion.AgentHog.Core;
using UnityEngine;

namespace Brightmotion.AgentHog.Unity
{
    /// <summary>
    /// Hidden persistent driver: hosts the transport coroutines, pumps the client's flush
    /// interval + input trackers each frame, and forwards app lifecycle to the client.
    /// Created by AgentHog.Init; never add this component yourself.
    /// </summary>
    [DefaultExecutionOrder(-100)]
    internal sealed class AgentHogRunner : MonoBehaviour
    {
        Client client;
        SceneTracker sceneTracker;
        BehaviorTracker behaviorTracker;
        UiClickTracker clickTracker;

        internal void Bind(Client client, AgentHogConfig config)
        {
            this.client = client;
            System.Action<string> log = config.Debug
                ? (System.Action<string>)(m => Debug.Log("[AgentHog] " + m))
                : (_ => { });
            behaviorTracker = new BehaviorTracker(client);
            if (config.AutoCaptureUiClicks) clickTracker = new UiClickTracker(client, log);
            if (config.AutoTrackScenes) sceneTracker = new SceneTracker(client);
        }

        void Update()
        {
            if (client == null) return;
            AgentHog.DrainCrossThreadCalls();
            behaviorTracker?.Update();
            clickTracker?.Update();
            client.Tick();
        }

        void OnApplicationPause(bool paused)
        {
            if (client == null) return;
            if (paused) client.OnPause();   // leave + flush + PlayerPrefs.Save()
            else client.OnResume();         // idle check → maybe rotate session
        }

        void OnApplicationQuit()
        {
            client?.OnPause();
        }

        void OnDestroy()
        {
            sceneTracker?.Dispose();
        }
    }
}
