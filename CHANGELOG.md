# Changelog

## 1.1.2 — 2026-10-27

- Default to English, 80% background transparency and 360 DIP width for new settings.
- Bound lyrics parsing/cache output and move heavy lyric work and update cleanup off the UI thread.
- Offer a smaller framework-dependent Light ZIP alongside full offline distributions.
- Keep previous/next controls visible at 280 DIP and add a 90% transparency option.
- Add manual and daily automatic GitHub update checks, with an opt-out setting.
- Ask before downloading and opening the installer; portable copies use the release page.
- Verify installer identity, size and SHA-256, validate redirects, and support download cancellation.
- Preserve the current startup preference and keep the widget running if the installer wizard is cancelled.
- Add `--self-test --stdout` for native checks without a report file; always close self-tests and return a failing exit code on errors.

## 1.1.1 — 2026-10-27

- Separate presentation state, tray menus, taskbar coordination and infrastructure services.
- Preserve settings, startup, IPC and installer compatibility; build scripts remain developer-only.
- Add isolated MSTest coverage alongside native self-tests.
- Snap dragging to safe left/right edges and show the horizontal cursor only at the border.
- Scroll overflowing titles and details; stop text animations while hidden or when text fits.
- Exclude bundled license documents from GitHub source-language statistics without removing notices.

## 1.1.0 — 2026-10-26

- Self-contained Windows x64 installer and portable ZIP; .NET is bundled.
- Per-user setup, startup and desktop shortcut options, and standard uninstaller.
- Uninstall preserves settings and lyrics cache.
- English and Russian UI, monitor selection, custom width and background transparency.
- Taskbar coverage recovery without taking keyboard focus.
- Complete track/artwork updates across temporary media-session replacement.
- Reliable click/drag seeking with stale timeline reconciliation.
- Empty widget hidden when no media session exists.
- Drag placement, center/custom positions and protection for left-aligned Start/app buttons.
- Event-driven metadata updates, artwork reuse, idle timer suspension and CPU/GPU measurement script.

## 1.0.0 — 2026-10-26

- Initial standalone taskbar music widget, album artwork, playback controls and synchronized lyrics.
- Local LRC import, LRCLIB lookup, focus timer and startup support.
