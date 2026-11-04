# Changelog

## 1.1.0.0

- Supervise every state failure, reopening only after the previous writer and
  cleanup have finished. Keep reporting `restartRequired` while ownership is
  uncertain, and reopen automatically when cleanup completes.
- Isolate census and live-item calls on two bounded threads with deadlines and
  a two-second processing budget. Keep ten-minute v3 heartbeats independent
  of library, sender and journal progress, with one monotonic attempt limiter.
- Capture coverage, immutable discovery time and live/recovery provenance.
  Fold recovery and expired additions into one entitled summary per outage;
  keep recovered removals and observed moves quiet.
- Re-probe repaired relays after non-retryable failures, and evaluate pending
  additions immediately on item updates.
- Send ready notices in fixed one-second batches instead of waiting for a
  minute of quiet. Keep relay backoff and the shared request limit, reserving
  ten calls per minute for binding reports.
- Add authenticated session-derived recipient bindings and minimal runtime
  health proof, plus elevated runtime diagnostics with a bounded, redacted ring.
  Runtime status is no longer written into saved configuration.
- Keep items Jellyfin announces again during a rescan quiet: an item already
  in the library, or removed and restored at the same path, no longer alerts.
- Migrate into versioned state while retaining original files for downgrade.
- Jellyfin 10.11 uses 1.1.0.0; Jellyfin 12 uses 1.1.0.1.
