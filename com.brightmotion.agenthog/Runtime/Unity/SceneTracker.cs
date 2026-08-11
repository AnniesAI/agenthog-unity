using Brightmotion.AgentHog.Core;
using UnityEngine.SceneManagement;

namespace Brightmotion.AgentHog.Unity
{
    /// <summary>
    /// SceneManager.sceneLoaded → pageview: /&lt;scene-name-slug&gt;. Additive loads are ignored
    /// (UI overlays / streamed chunks are not navigation). Single-scene games layer manual
    /// AgentHog.Screen() calls on top for their UI states.
    /// </summary>
    internal sealed class SceneTracker
    {
        readonly Client client;

        public SceneTracker(Client client)
        {
            this.client = client;
            var active = SceneManager.GetActiveScene();
            if (active.IsValid() && active.isLoaded)
                Track(active.name); // Init runs inside an already-loaded scene: count it
            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        public void Dispose() => SceneManager.sceneLoaded -= OnSceneLoaded;

        void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (mode == LoadSceneMode.Single) Track(scene.name);
        }

        void Track(string sceneName) => client.Screen("/" + Client.Slug(sceneName), sceneName);
    }
}
