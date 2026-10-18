# JellySee Notifications

Jellyfin plugin release 1.0.0.0 for audience-scoped library notifications in JellySee.
Separate binaries support Jellyfin **10.11** (.NET 9, Controller/Model 10.11.8)
and **12** (.NET 10, Controller/Model 12.0.0-rc3). The 10.11 DLL/package version is
**1.0.0.0**; the 12 DLL/package version is **1.0.0.1**, so upgrading the server
also offers a strictly newer compatible plugin. Jellyfin 10.10 is unsupported.
The repository starts with an empty release-version list; nothing here has been
published or deployed.

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
No controller or other inbound API is registered.

## Behaviour

Movie, Series, Episode, MusicVideo, Book and AudioBook additions wait for metadata
readiness, with a fifteen-minute fallback. An empty audience is retried for up to
thirty minutes. Disabled users are excluded. Addition audiences require both
`IsVisibleStandalone(user)` and a visible `CollectionFolder` returned by Jellyfin;
there is no additional interpretation of library allowlists or blocklists.
Removal audiences use current access to the indexed logical libraries.

A single background loop owns all disk, library-query and HTTP work. Callbacks
only put constant-size signals with random occurrence IDs into a bounded channel.
The durable journal records transitions before changing the index. Checksums,
streaming reads, per-record limits, an atomic snapshot rename and a complete
snapshot footer handle crashes and corruption. Journal corruption truncates the
invalid tail; snapshot corruption moves the snapshot to `snapshot.bin.corrupt`.
Every transition and transaction checks aggregate state bounds, including bulk
rewrites; replay rejects the entire overflowing transaction or quarantines an
invalid snapshot. File operations use a held directory handle, no-follow opens,
exclusive random temporary files, handle identity/type/link checks and atomic
replacement. Unix uses `openat`/`renameat`; Windows uses relative NT opens and
handle-based renames. Existing files, including quarantine destinations, are
never opened for truncation. Unsupported filesystem/platform primitives fail
closed. Transient adapter/database failures retain the loop, subscriptions and
scan progress, report `adapterUnavailable`, and retry with exponential delays
from one to sixty seconds. Shutdown cancellation remains separate.
Disk write failure stops the worker and reports `stateUnavailable` without
printing exception text.

An initial baseline indexes existing titles without announcing historical imports.
It resumes from persisted discovery knowledge after a restart. Each library has
one unpaged, server-wide, ids-only `GetItemIds` query using its physical roots;
only logical CollectionFolder IDs appear in events. Passes run at startup and
on a fifteen-minute schedule, with library queries spaced five seconds apart.
Timer, inventory and dirty requests coalesce into a follow-up pass while the
current scan and its unvisited libraries finish, even if a pass exceeds fifteen minutes.
Each tick processes up to 1,000 titles and index writes are committed in groups
of up to 100 changes. Container changes trigger an all-library pass within a
coalesced thirty-second window; an active pass finishes before the follow-up begins. Every regular pass also refreshes folder and
provider fingerprints, covering the daily full-reconcile requirement.

Library inventory is checked every two minutes using `GetVirtualFolders()` plus
strict directory enumeration and shortcut counts. Two consecutive complete empty
polls are required before declaring every library gone. Unique directory renames
with equal location sets preserve continuity; unchanged libraries sharing paths
cannot conceal a deleted library. A library disappearance journals one
`rmlibrary` and removes its route from the index in the same transaction.

Every processed title removal increments its lifecycle counter, including an
upgrade whose removal is suppressed. Tombstones last twenty-four hours; lifecycle counters persist after tombstones expire. A removal
waits ten minutes for an equal, nonempty provider fingerprint, type and folder set
on an addition within ten minutes in either direction. Replacement fingerprints
are persisted before metadata readiness, both in bounded history and on the
pending addition, so the fifteen-minute fallback or history eviction cannot
release a matching upgrade removal. The addition is always retained. More than 200 unsuppressed removals in a ten-minute window produce one
`rmbulk` for each affected library, with its own audience. Bulk windows wait for
their newest member's upgrade hold to finish; this can add up to ten minutes to
the removal delay. Shared-library removals retain each library's route.

The sender flushes after sixty seconds of quiet or five minutes from the oldest
pending occurrence. Batches contain at most fifty events and 48 KiB of encoded
UTF-8 JSON. User hashes are remapped into each request's users table using an
LSB-first bitmap. Audiences older than twenty minutes, or more than two minutes
in the future, are re-evaluated before sending. Only acknowledgement IDs present
in the request retire outbox entries. Response reads are limited to 16 KiB and
acknowledgements have a strict schema. Network errors, 429 and 5xx back off with
jitter and respect Retry-After. Other HTTP failures pause that setup until a
configuration change; its unacknowledged work remains until expiry. Work expires twenty-four hours after queuing.
The sender permits at most twenty event requests per sliding minute. Health is
sent on start/configuration change and every six hours; failed heartbeats retry
no faster than five minutes.

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
Renames and moves otherwise follow Jellyfin's removal/addition semantics.

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
python3 scripts/package.py --version 1.0.0.0 --notes path/to/release-notes.txt
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
regressions ran on macOS arm64; the Linux and Windows native storage backends
compile in both builds but still require execution on those operating systems.
