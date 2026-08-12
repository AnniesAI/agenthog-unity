# Changelog

## [0.2.0] — 2026-08-12

Initial release (versioned in lockstep with the core package).

- `PlayInstallReferrer.Attach(config)`: wires the Play Install Referrer AIDL client as the
  core SDK's `InstallReferrer` provider — referrer string plus click/install-begin
  timestamps, read once per install.
- Gradle dependency `com.android.installreferrer:installreferrer:2.2` via EDM4U manifest or
  a documented one-line mainTemplate.gradle edit; no bundled `.aar`, IL2CPP-safe.
- Editor/iOS/standalone resolve null (no referrer); transient Play service failures retry
  on the next launch.
