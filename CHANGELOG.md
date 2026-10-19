# Changelog

## 1.0.1.0

- Fix Windows startup stopping with `stateUnavailable`: state byte counts use
  checked file metadata instead of opening a reader against the live journal.
- Close replay readers and old journal writers before overwriting their entries;
  preserve the snapshot-before-journal publication order and atomic move.
- Recover completed sharing/lock failures (including Windows overwrite-move
  access denial) by closing the worker and replaying
  durable state, with cancellable retries from one to sixty seconds. Timeouts
  and failed/stalled cleanup remain terminal.
- Throttle retry-streak outage heartbeats to the first failure and at most every
  fifteen minutes, and retry error logs to the first failure and at most hourly.
  A full healthy iteration resets retry backoff; terminal failures always log.
  Shutdown cancellation does not generate misleading state timeout diagnostics.
- Include only exception type and HResult alongside the fixed state failure log.
- Add sharing, replacement, compaction, quarantine, reopen and retry regressions.
  Preserve CreateNew temporaries, link/containment checks, I/O bounds and caps.
- Validate builds, full .NET tests and Python packaging tests on Windows, Linux
  and macOS, each on x64 and arm64, before release publishing. The retired
  `macos-13` runner is replaced by `macos-15-intel` for x64 coverage.
- Jellyfin 10.11 uses plugin version 1.0.1.0; Jellyfin 12 uses 1.0.1.1 to retain
  the existing strictly newer ABI migration revision.
