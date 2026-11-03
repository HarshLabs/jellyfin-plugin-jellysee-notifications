# JellySee Notifications

Jellyfin plugin release 1.1.0.0 for audience-scoped library notifications in JellySee.
Separate binaries support Jellyfin **10.11** (.NET 9, Controller/Model 10.11.8)
and **12** (.NET 10, Controller/Model 12.0.0-rc3). The 10.11 DLL/package version is
**1.1.0.0**; the 12 DLL/package version is **1.1.0.1**, so upgrading the server
also offers a strictly newer compatible plugin. Jellyfin 10.10 is unsupported.
Version 1.1.0 adds supervised recovery, independent v3 health, entitled outage
summaries, quiet moves and authenticated recipient binding.
See [the changelog](CHANGELOG.md).

## Configuration

Install the matching ABI build, restart Jellyfin, and open Dashboard → Plugins →
JellySee Notifications. Enable notifications and paste the same relay secret used
by JellySee's Webhook integration. The password input never displays a saved
secret; leaving it blank preserves it. Settings load/save exclusively through
Jellyfin's elevated plugin configuration API. The embedded page is static because
Jellyfin can serve configuration page resources without authentication.

The only allowlisted origin is compiled into the DLL:
`https://jellysee-relay.harshlabsllc.workers.dev`. The configuration's origin field
cannot redirect traffic elsewhere. HTTPS uses platform TLS validation, redirects
are disabled, and each full request (including its response-body read) has a ten-second deadline.
Authenticated endpoints are registered under `/JellySeeNotifications`:

- `POST /Bind`: `{"registration":"<32 lowercase hex>","profile":"profile-id"}`.
  The user comes only from the signed-in Jellyfin session. Approved tuples persist
  across restarts and are reported through the authenticated setup channel.
  Retention is 64 registrations per user/setup and 500 per setup; admission only
  evicts the requesting user's own oldest registration and rejects cross-user replacement.
- `GET /Health`: exactly `{"health":"healthy"|"not","setupId":"<64 hex>"}`,
  for any signed-in user. Apps cache proof for at most ten minutes and only
  suppress polling alerts for this same setup. Public relay status is informational.
- `GET /Diagnostics`: Jellyfin `RequiresElevation` policy, used by the config
  page for runtime status and the fixed-code, bounded diagnostics ring.

Capable apps register `CAP_SUMMARY = 4` and bind after every successful relay
registration, including weekly refresh and user/profile changes. Summary envelopes
use the contract's unchanged server/profile presentation key and open Recently Added.
Old apps receive no unsupported summary operation or recovery wake.

## Behaviour

Movie, Series, Episode, MusicVideo, Book and AudioBook additions wait for metadata
readiness, with a fifteen-minute fallback. An empty audience is retried for up to
thirty minutes. Disabled users are excluded. Addition audiences require both
`IsVisibleStandalone(user)` and a visible `CollectionFolder` returned by Jellyfin;
there is no additional interpretation of library allowlists or blocklists.
Removal audiences use current access to the indexed logical libraries.

One processing loop owns notification state. Callbacks enqueue bounded signals
with immutable receipt time and coverage. Census and live metadata calls run on
two fixed, bounded threads; Ids has a 120-second deadline and other calls fifteen
seconds. Pending calls yield the processing slice, whose work budget is two seconds.
A separate heartbeat lane continues through blocked library, sender and state work.
The durable journal records transitions before changing the index. Checksums,
streaming reads, per-record limits, an atomic snapshot rename and a complete
snapshot footer handle crashes and corruption. Journal corruption truncates the
invalid tail; snapshot corruption moves the snapshot to `snapshot.bin.corrupt`.
Every transition and transaction checks aggregate state bounds, including bulk
rewrites; replay rejects the entire overflowing transaction or quarantines an
invalid snapshot. Portable managed .NET file operations create random temporary
files with `FileMode.CreateNew`, flush their contents with `Flush(true)`, then
replace target entries with `File.Move(..., overwrite: true)`. Existing files,
including quarantine destinations, are never opened for truncation. Existing
read/append paths reject symbolic links and reparse points; directory checks keep
state inside Jellyfin's plugin data path. Failed writes remove their temporary
files, and bounded startup recovery reclaims abandoned random temporary entries.
Transient adapter/database failures retain the loop, subscriptions and
scan progress, report `adapterUnavailable`, and retry with exponential delays
from one to sixty seconds. Shutdown cancellation remains separate.
State I/O runs on a dedicated thread with a ten-second deadline per operation.
Host startup yields before I/O. Every state failure is supervised: after proven
cleanup, reopen and replay with backoff from one second to five minutes. If an
operation or cleanup remains outstanding, report `restartRequired`, retain the
last-known counters, and reopen automatically only after clean completion. There
is never a second state writer. Shutdown does not join a blocked filesystem call.
Logs contain fixed codes, exception type and HResult only; diagnostics contain
UTC, monotonic time and fixed cause codes, with a 256 KiB cap and independent I/O.
Runtime health and counters are separate from saved configuration.

New state lives under `v4/`; migration keeps the old journal/snapshot untouched
for downgrade and treats missing provenance as recovery. Re-upgrade resumes v4.
See [storage details](docs/state-storage.md) for containment, atomic replacement,
recovery and residual filesystem limits.

An initial baseline indexes existing titles without announcing historical imports.
It resumes from persisted discovery knowledge after a restart. Each library has
one unpaged, server-wide, ids-only `GetItemIds` query using its physical roots;
only logical CollectionFolder IDs appear in events. Passes run at startup and
on a fifteen-minute schedule.
Timer, inventory and dirty requests coalesce into a follow-up pass while the
current scan and its unvisited libraries finish, even if a pass exceeds fifteen minutes.
Each slice processes up to 1,000 titles within its elapsed budget. Container
changes request a coalesced follow-up pass; the active pass and unvisited tail
retain their cursors. Every regular pass also refreshes folder and
provider fingerprints, covering the daily full-reconcile requirement.

Library inventory is checked every two minutes using `GetVirtualFolders()` plus
strict directory enumeration and shortcut counts. Two consecutive complete empty
polls are required before declaring every library gone. Unique directory renames
with equal location sets preserve continuity; unchanged libraries sharing paths
cannot conceal a deleted library. A library disappearance journals one
`rmlibrary` and removes its route from the index in the same transaction.

Coverage requires a complete, successful, durably committed pass in the current
subscribed worker session. Drops, failed/incomplete censuses, required-call
failures and restarts uncover libraries. Callbacks received uncovered are recovery;
reconcile discoveries are live only with covered membership and a previous complete
pass no more than thirty minutes old. `FirstSeenAt` never changes during retries.
Recovery and expired additions produce one generic, currently entitled summary per
outage and bound profile, with no counts or titles. Empty/removals-only recovery is
silent. Summary entitlement is checked again on every submission/retry.

Title removals advance lifecycles, with twenty-four-hour tombstones and retained
lifecycle counters. Equal nonempty type/provider fingerprints become quiet only
when an actual removal is observed within the symmetric ten-minute move window.
Matching additions are held until a running scan ends (at most thirty minutes),
or for the move window when no scan is running. Unremoved copies/editions alert
normally, subject to the thirty-minute audible deadline. ItemUpdated re-evaluates
pending readiness immediately; the thirty-second tick remains the backstop.
Bulk removal windows retain their per-library audiences.

The sender flushes after sixty seconds of quiet or five minutes from the oldest
pending occurrence. Batches contain at most fifty events and 48 KiB of encoded
UTF-8 JSON. User hashes are remapped into each request's users table using an
LSB-first bitmap. Audiences older than twenty minutes, or more than two minutes
in the future, are re-evaluated before sending. Only acknowledgement IDs present
in the request retire outbox entries. Response reads are limited to 16 KiB and
acknowledgements have a strict schema. Network errors, 429 and 5xx back off with
jitter and respect Retry-After. Other HTTP failures re-probe after fifteen minutes, honoring longer relay backoff;
a configuration change or success resets the schedule. Work expires twenty-four hours after queuing.
Events and bindings share twenty requests per sliding minute. Independent v3
heartbeats run every ten minutes with a twenty-minute lease, progress, boot id,
real assembly version, current verified admin hashes and last-known counters.
One monotonic limiter caps all attempts at twelve/hour across worker sessions.
Individual alerts expire thirty minutes after observation for every audience.

## Bounds and known losses

The callback channel holds 5,000 signals. Pending and outbox queues each hold
5,000 occurrences; overflow drops the oldest and increments the heartbeat's
counter. More than 2,000 users pauses audience evaluation and event sending.
The title index stops at 200,000 entries and permanently turns off addition
recovery by reconcile once full, while live callbacks still announce. Removing
an unindexed title relies on the app's checks. Tombstones are bounded at 200,000
and recent upgrade history at 5,000 entries.

Compact title records are **103 bytes**, plus a one-byte snapshot tag: the plan's
97-byte estimate did not include a persisted lifecycle counter, null flag or
folder count. A full index takes about 20.8 MB before other state. Audience hashes
are packed on disk; tombstones use 36 bytes plus a tag. Lifecycle counters use 20 bytes plus a tag and are capped at one million
unique removed IDs; exhausting that safety bound stops the worker rather than
resetting counters. Snapshot and journal reads
are bounded at 192 MiB each and 128 KiB per JSON record. The journal compacts at
4,096 transitions or 96 MiB, and the dashboard/heartbeat report actual state bytes.
In-memory dictionaries have .NET object overhead beyond the raw IDs.

The journal stores only a hash of the setup secret. On rotation, up to twenty
retired credentials in the elevated Jellyfin configuration remain eligible for
twenty-four hours so their pending occurrences keep their original setup identity across a
restart. Each setup has independent retry scheduling; work is never redirected
under a replacement secret. Retired credentials never appear in the page's input
or status, journal, outbox or HTTP bodies. Disabling notifications stops all setups.

Crashes before a callback reaches disk can be recovered by startup reconciliation.
The initial baseline and an irrecoverable corrupt snapshot can lose historical
discovery knowledge. Queue overflow, an outage beyond twenty-four hours, index
capacity, or an unresolvable library configuration can delay or lose alerts; the
app's own library checks remain the catch-up path. A removal shortly after a
library path change can use the old indexed libraries for up to fifteen minutes.
Observed matching moves are quiet; unobserved changes use the recovery rules.

## Trust boundary and privacy

Only server/item/series/library IDs, event IDs, types, lifecycle counters, timestamps,
eight-byte user hashes, audience bitmaps and fixed health values leave the server.
The relay also sees the server's network address. Hashes are pseudonymous, not
secret. Titles, filenames, paths, ratings, tags and usernames are neither sent nor
logged. Library paths and configuration-directory names are fingerprinted before
local persistence. The secret exists in Jellyfin's administrator-protected plugin
configuration and in Authorization headers, never in journal/outbox/heartbeat
records or static page content.

**The server administrator and any installed plugin DLL share Jellyfin's process
trust. This plugin cannot protect secrets or metadata against either actor.**
Managed checks leave a **check-then-open window for the existing journal** and
other path operations: an actor who can already write Jellyfin's own data
directory could substitute an entry between validation and use. That actor has
the same trust as the server process (the §8 trust-boundary table row), rather
than being a relay or ordinary library user. .NET 9/10 expose no public hardlink
count; existing hardlinks are read only and copied into fresh files before use
as a writable journal. Hardlinks introduced afterward share the same trusted
local-writer limitation. See [state storage and the §8 trust table](docs/state-storage.md)
for the exact guarantees, cleanup bounds and residual races.
The relay cannot choose a destination, path or file through a response. A
compromised publisher can ship executable code: SHA-256 release hashes are a
transparency aid, not authentication. Jellyfin's manifest MD5 checks only corruption.

## Build, tests and release preparation

Run `./build.sh` with the .NET 10 SDK (and .NET 9 targeting packs, restored by the
SDK). It performs locked restore, Release builds of both target frameworks, xUnit
checks on .NET 10, and offline packaging/manifest checks. No running server or
relay is needed. Tests use fake transports and library adapters; no HTTP is sent.
To prepare archives after a Release build:

```sh
python3 scripts/package.py --version 1.1.0.0 --notes path/to/release-notes.txt
```

The package contains the plugin DLL, `meta.json` and license; it excludes Jellyfin
and all third-party assemblies. The tag-only release workflow validates the project
version, builds/tests with locked NuGet packages, then publishes both ABI archives
and SHA-256 hashes. Release tags reserve revision zero (`x.y.z.0`); the 12
artifact uses revision one (`x.y.z.1`) in its DLL, metadata and manifest, while
both archive filenames use the release tag version. A separate publishing job
updates the manifest using its own Contents write permission. The offline tests
model the ABI filtering and strictly-greater update selection checked against
both Jellyfin source tags; see [the migration notes](docs/abi-migration.md). Actions are pinned to SHAs
from the local Jellyfin reference workflows. The build job has read permission
and no persisted checkout credentials.

Before an actual release, configure protected tags, required reviews on `main`,
and approval on the `release` environment. Arrange the manifest bot's narrowly
scoped branch-protection exception if required. These remote repository controls
cannot be configured in this local-only checkout. Run the plan's real-server
10.11/12 deletion and reference-browsing visibility matrix, the 10,000-addition /
2,000-deletion ≤5% performance gate, and an independent security review before
publishing. Those integration and performance gates were not exercised here. The security
regressions ran on macOS arm64 using managed storage; Linux x64/ARM64, macOS x64
and Windows execution are configured in both CI and the release validation job.
The matrix is `windows-latest` (x64), `windows-11-arm`, `ubuntu-latest` (x64),
`ubuntu-24.04-arm`, `macos-latest` (arm64) and `macos-15-intel` (x64).
Each runs both-framework builds, the full .NET 10 test suite, and Python packaging
tests. GitHub [retired macos-13](https://github.blog/changelog/2025-09-19-github-actions-macos-13-runner-image-is-closing-down/)
on December 4, 2025; `macos-15-intel` supplies the requested Intel coverage.
[Windows and Linux arm64 runners are available](https://docs.github.com/en/actions/reference/runners/github-hosted-runners)
for public and private repositories. Publishing remains a single Ubuntu job in
the `release` environment and waits for validation. No matrix jobs ran locally;
the local run covers macOS arm64. Both target frameworks compile here without
native storage imports or architecture-specific layouts.
