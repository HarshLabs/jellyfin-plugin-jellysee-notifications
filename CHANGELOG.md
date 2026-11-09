# Changelog

## 1.1.1.0

- Never let an older copy of the plugin take over again. On Windows, Jellyfin
  could not fully delete the previous version during an update restart, because
  its DLL stayed locked; only the DLL was left. On a later restart Jellyfin
  gave that leftover a generated plugin id that sorted ahead of the real one,
  deleted the new version as if it were older, and loaded the old one, which
  then sent a false "titles were removed" alert. The plugin now repairs every
  leftover older copy at each start: it marks the copy Disabled under the real
  id, so the newer version always wins, then removes it, or leaves it for
  Jellyfin to delete on the next restart if it is still locked. No admin
  action is needed.
- One-time cleanup only if a leftover already exists before updating (a
  `JellySee Notifications_<older version>` folder in the plugins directory
  holding only a DLL): stop Jellyfin, delete that folder, then update.
- Refuse to open notification state when a newer release has completed its
  migration into a later schema, so a revived older copy never compares
  outdated state. Every future schema keeps this check.
- Jellyfin 10.11 uses 1.1.1.0; Jellyfin 12 uses 1.1.1.1.

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
