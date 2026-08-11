using System.Collections.Generic;
using System.Text;
using Brightmotion.AgentHog.Core;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
#if AGENTHOG_INPUTSYSTEM && ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace Brightmotion.AgentHog.Unity
{
    /// <summary>
    /// uGUI click autocapture (plan §5a): pointer-up with gesture travel ≤ ~8dp is raycast
    /// through the EventSystem; the nearest interactive ancestor (Selectable /
    /// IPointerClickHandler) becomes a "click: &lt;label&gt;" event. Larger travel is a drag →
    /// behavior.anyScroll only. Covers Canvas UI; world objects and UI Toolkit stay manual.
    /// Degrades to behavior-only telemetry when no EventSystem exists.
    /// </summary>
    internal sealed class UiClickTracker
    {
        const int MaxAncestorHops = 10;
        const int MaxSelectorSegments = 5;
        const int MaxLabelLength = 50;

        readonly Client client;
        readonly System.Action<string> log;
        readonly float dragThresholdPx;
        readonly List<RaycastResult> raycastResults = new List<RaycastResult>();

        bool tracking;
        Vector2 downPos;
        float travel;
        bool dragReported;
        bool warnedNoEventSystem;

        public UiClickTracker(Client client, System.Action<string> log)
        {
            this.client = client;
            this.log = log;
            // RN's 8px threshold is density-independent; scale by dpi to match its semantics
            float dpi = UnityEngine.Screen.dpi;
            dragThresholdPx = 8f * Mathf.Max(1f, dpi > 0f ? dpi / 160f : 1f);
        }

        public void Update()
        {
            if (!TryReadPointer(out Vector2 pos, out bool went_down, out bool went_up, out bool held))
                return;

            if (went_down)
            {
                tracking = true;
                downPos = pos;
                travel = 0f;
                dragReported = false;
                client.RecordInteraction();
            }
            if (tracking && (held || went_up))
            {
                travel = Mathf.Max(travel, Vector2.Distance(pos, downPos));
                if (!dragReported && travel > dragThresholdPx)
                {
                    dragReported = true;
                    client.RecordDrag();
                }
            }
            if (went_up && tracking)
            {
                tracking = false;
                if (travel <= dragThresholdPx) TryEmitClick(pos);
            }
        }

        static bool TryReadPointer(out Vector2 pos, out bool down, out bool up, out bool held)
        {
#if ENABLE_LEGACY_INPUT_MANAGER
            if (Input.touchCount > 0)
            {
                Touch t = Input.GetTouch(0);
                pos = t.position;
                down = t.phase == TouchPhase.Began;
                up = t.phase == TouchPhase.Ended || t.phase == TouchPhase.Canceled;
                held = !up;
                return true;
            }
            if (Input.mousePresent)
            {
                pos = Input.mousePosition;
                down = Input.GetMouseButtonDown(0);
                up = Input.GetMouseButtonUp(0);
                held = Input.GetMouseButton(0);
                return true;
            }
            pos = default; down = up = held = false;
            return false;
#elif AGENTHOG_INPUTSYSTEM && ENABLE_INPUT_SYSTEM
            var pointer = Pointer.current;
            if (pointer == null)
            {
                pos = default; down = up = held = false;
                return false;
            }
            pos = pointer.position.ReadValue();
            down = pointer.press.wasPressedThisFrame;
            up = pointer.press.wasReleasedThisFrame;
            held = pointer.press.isPressed;
            return true;
#else
            pos = default; down = up = held = false;
            return false;
#endif
        }

        void TryEmitClick(Vector2 screenPos)
        {
            var eventSystem = EventSystem.current;
            if (eventSystem == null)
            {
                if (!warnedNoEventSystem)
                {
                    warnedNoEventSystem = true;
                    log("no EventSystem — click autocapture degraded to behavior-only");
                }
                return;
            }

            var ped = new PointerEventData(eventSystem) { position = screenPos };
            raycastResults.Clear();
            eventSystem.RaycastAll(ped, raycastResults);
            if (raycastResults.Count == 0) return; // world tap, not UI — no event

            GameObject interactive = FindInteractiveAncestor(raycastResults[0].gameObject, out string kind);
            if (interactive == null) return; // non-interactive UI (decor) — kills tap noise

            string text = FindLabelText(interactive);
            string label = !string.IsNullOrEmpty(text) ? text : interactive.name;
            client.EmitClick(label, BuildSelector(interactive, kind), text);
        }

        static GameObject FindInteractiveAncestor(GameObject leaf, out string kind)
        {
            Transform t = leaf.transform;
            for (int hop = 0; t != null && hop < MaxAncestorHops; hop++, t = t.parent)
            {
                var selectable = t.GetComponent<Selectable>();
                if (selectable != null && selectable.interactable)
                {
                    kind = selectable.GetType().Name;
                    return t.gameObject;
                }
                var clickHandler = t.GetComponent<IPointerClickHandler>();
                if (clickHandler != null)
                {
                    kind = clickHandler.GetType().Name;
                    return t.gameObject;
                }
            }
            kind = null;
            return null;
        }

        internal static string FindLabelText(GameObject interactive)
        {
#if AGENTHOG_TMP
            var tmp = interactive.GetComponentInChildren<TMPro.TMP_Text>(true);
            if (tmp != null)
            {
                string collapsed = Collapse(tmp.text);
                if (collapsed.Length > 0) return collapsed;
            }
#endif
            var uiText = interactive.GetComponentInChildren<Text>(true);
            if (uiText != null)
            {
                string collapsed = Collapse(uiText.text);
                if (collapsed.Length > 0) return collapsed;
            }
            return null;
        }

        /// <summary>Whitespace-collapsed, trimmed, ≤50 chars — the CONTRACTS.md click-label rule.</summary>
        internal static string Collapse(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length);
            bool lastSpace = true; // leading whitespace drops
            foreach (char c in s)
            {
                if (char.IsWhiteSpace(c))
                {
                    if (!lastSpace) { sb.Append(' '); lastSpace = true; }
                }
                else
                {
                    sb.Append(c);
                    lastSpace = false;
                }
            }
            while (sb.Length > 0 && sb[sb.Length - 1] == ' ') sb.Length--;
            if (sb.Length > MaxLabelLength) sb.Length = MaxLabelLength;
            return sb.ToString();
        }

        /// <summary>Hierarchy path analog of cssPath, e.g. "ShopPanel>BuyRow>Button:buy-gems".</summary>
        internal static string BuildSelector(GameObject interactive, string kind)
        {
            var segments = new List<string> { (kind ?? "UI") + ":" + interactive.name };
            Transform t = interactive.transform.parent;
            while (t != null && segments.Count < MaxSelectorSegments)
            {
                segments.Add(t.name);
                t = t.parent;
            }
            segments.Reverse();
            return string.Join(">", segments);
        }
    }
}
