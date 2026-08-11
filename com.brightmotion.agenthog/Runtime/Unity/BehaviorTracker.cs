using Brightmotion.AgentHog.Core;
using UnityEngine;
#if AGENTHOG_INPUTSYSTEM && ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace Brightmotion.AgentHog.Unity
{
    /// <summary>
    /// Feeds the session's behavior flags (mouseMoved / anyScroll / firstInteractionMs) —
    /// bot-scoring inputs that keep real game sessions clear of no_behavior_multi_event.
    /// Polled from the runner's Update. Pointer press/drag flags come from UiClickTracker;
    /// this covers mouse motion, scroll wheel, and keys.
    /// </summary>
    internal sealed class BehaviorTracker
    {
        readonly Client client;
        Vector3 lastMousePos;
        bool hasMousePos;

        public BehaviorTracker(Client client) => this.client = client;

        public void Update()
        {
#if ENABLE_LEGACY_INPUT_MANAGER
            if (Input.mousePresent)
            {
                Vector3 pos = Input.mousePosition;
                if (hasMousePos && (pos - lastMousePos).sqrMagnitude > 0.25f)
                    client.RecordMouseMove();
                lastMousePos = pos;
                hasMousePos = true;
                if (Input.mouseScrollDelta.y != 0f) client.RecordScrollWheel();
            }
            if (Input.anyKeyDown) client.RecordInteraction();
#elif AGENTHOG_INPUTSYSTEM && ENABLE_INPUT_SYSTEM
            var mouse = Mouse.current;
            if (mouse != null)
            {
                if (mouse.delta.ReadValue().sqrMagnitude > 0.25f) client.RecordMouseMove();
                if (mouse.scroll.ReadValue().y != 0f) client.RecordScrollWheel();
            }
            var keyboard = Keyboard.current;
            if (keyboard != null && keyboard.anyKey.wasPressedThisFrame) client.RecordInteraction();
#endif
        }
    }
}
