# Changelog

## [0.1.0] — unreleased

Initial release.

- Sessions with 30-minute idle semantics matching the AgentHog web tracker (restart-survival,
  background rotation, carry-over of unsent batches under their original ids).
- Automatic scene-view `pageview` events + manual `Screen()` for in-scene UI states.
- Per-screen time via `leave` events with `duration_s`; backgrounded time excluded.
- uGUI click autocapture (label from child text → GameObject name; drag-vs-tap at 8dp).
- Behavior telemetry (mouseMoved / anyScroll / firstInteractionMs) for honest bot scoring.
- `Capture` / `Identify` / `Tag` / `Register` / `SetLandingParams` / `Flush` / `Reset`.
- Config via code (`AgentHog.Init`) or `AgentHogSettings` asset with `.local` override.
- Zero dependencies; Unity 2021.3+; legacy and new Input System supported.
