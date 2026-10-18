# Portable state storage

State storage uses managed .NET 9/10 APIs on Linux x64/ARM64 (including Docker),
macOS and Windows. There are no native imports, numeric open flags, stat layouts
or architecture-dependent file code. The server supplies both the plugin state
directory and the trusted `IApplicationPaths.PluginsPath` root. Tests supply
isolated fixture roots. The state directory must be strictly beneath that root.

## Creation, replacement and append

- Snapshot, replacement journal and quarantine copies use random GUID names
  within the plugin directory and `FileMode.CreateNew`. This fails if an entry
  already exists, including a dangling symbolic link. Unix creation permissions
  are 0600; newly created state directories use 0700. Windows uses inherited
  permissions from Jellyfin's data directory.
- Each replacement is written and flushed with `FileStream.Flush(true)` before
  `File.Move(temp, target, overwrite: true)`. The move replaces a destination
  link entry rather than opening or truncating its target. The same-directory
  rename publishes the snapshot before replacing the journal. No target uses
  `FileMode.Create` or `FileMode.Truncate`.
- Before an existing file is opened for reading, or the retained journal stream
  is used for append, `FileSystemInfo.LinkTarget` and `ReparsePoint` checks reject
  symbolic links, dangling links and reparse points. Directory and Device
  attributes are rejected as files. The named journal length must still match
  the writer before append.
- .NET 9/10 have no public managed file link-count API. Consequently hardlink
  counts cannot be inspected on these targets. Existing files are opened read
  only, then journal recovery copies the valid prefix into a fresh exclusively
  created file before any append. Snapshot and quarantine replacements likewise
  unlink destination hardlinks without modifying their other names. This is
  covered by hardlink regression fixtures; it does not claim hardlink rejection.
- Before each path operation, storage resolves the trusted root and checks it
  against the initially resolved root. It rejects linked state directories and
  linked ancestors below the trusted root, and checks resolved containment.
  OS aliases in ancestors of the trusted root are resolved component by
  component. A substituted directory link causes persistence to fail closed.

The retained journal stream originates from exclusive creation; startup never
opens an existing named journal for write. Managed checks do not establish file
identity by inode/handle. Ordinary same-size file substitutions and concurrent
hardlink creation cannot be ruled out. File flushing is explicit, but .NET does
not provide a portable directory-fsync API: rename durability across power loss
also depends on the host filesystem. Snapshot checksums, complete footers and
journal replay still detect incomplete persisted records.

## Bounded I/O and host startup

`NotificationService.ExecuteAsync` starts with `await Task.Yield()` before any
state/configuration path lookup or I/O. [.NET 9 BackgroundService.StartAsync](https://github.com/dotnet/runtime/blob/v9.0.0/src/libraries/Microsoft.Extensions.Hosting.Abstractions/src/BackgroundService.cs)
calls ExecuteAsync directly and returns after it yields an incomplete task, so a
blocked state open cannot hold Jellyfin 10.11 host startup. .NET 10 additionally
dispatches ExecuteAsync in Task.Run. Minimal-host tests exercise both the installed
.NET 10 implementation and the .NET 9 synchronous-start algorithm invoking the
actual service ExecuteAsync; this is a simulation of .NET 9 hosting semantics,
not an execution on a .NET 9 runtime.

Every journal operation that accesses storage (initialization/recovery, commit,
compaction and DiskBytes) is dispatched to one dedicated background thread.
Each call has a ten-second deadline covering the whole operation, a stricter
bound than a separate ten seconds for each open/read. The service's shutdown
token also cancels the caller's wait. Journal handles, including their eventual
disposal, stay on that thread. Timeouts permanently abandon the worker: subsequent
calls fail immediately, the service disables callbacks, removes subscriptions
and ends notification processing without retrying the path. The dashboard is set
to the fixed `stateUnavailable` code even when notifications are disabled. When
configured and not shutting down, the service attempts one ordinary ten-second
heartbeat with that code, using empty state counters rather than accessing state
still owned by the abandoned worker. A failed heartbeat does not overwrite the
dashboard code and is not retried.

Managed .NET APIs cannot reliably establish that a Unix entry is a regular file:
a FIFO can have `FileAttributes.Normal` and no LinkTarget. Device/ReparsePoint/
Directory checks reject identifiable special entries but are not a regular-file
proof. File length and timestamps are not a proof either; a length query after
open is too late to prevent a FIFO's blocking open. The timeout, rather than
these heuristics, provides containment for both startup and later state reads.

Cancellation cannot interrupt a synchronous managed filesystem open/read. An
abandoned background thread can remain blocked until process exit or until the
filesystem call returns. It is never joined on shutdown; cleanup runs on that
thread if it returns. Cancellation checks prevent later path operations after
abandonment, but an already active call may still finish, including a write.
Normal disposal waits at most ten seconds for handles to close; cancellation
releases that wait immediately. Slow regular-file operations exceeding the
deadline also disable notifications and require a server restart after the
storage problem is resolved. There is at most one abandoned I/O thread per
service instance, with no automatic restart or retry loop.

Unix FIFO regressions cover each active filename during construction and later
DiskBytes probes, timeout health on the dashboard/heartbeat, prompt host startup,
and shutdown cancellation before a much longer I/O deadline. FIFO cases are
skipped on Windows. Portable worker tests verify dedicated background execution,
terminal timeout/cancellation and detached cleanup after a blocked call returns.

## Temporary-file recovery

Failed snapshot writes and journal/quarantine rewrites dispose the stream and
attempt to unlink their random temporary entry, including when copy or rename
fails. Cleanup never masks the original persistence error. If permissions or a
directory substitution prevent cleanup, the next startup retries it.

Startup examines at most 128 matching entries across these prefixes:
`snapshot.tmp.`, `journal.ndjson.tmp.` and `snapshot.bin.corrupt.tmp.`. It only
deletes names ending in exactly a 32-hex-digit GUID. It does not read stale
contents or recurse. Deleting a file symlink or hardlink only removes that entry.
Unexpected directories (including directory reparse points) stop recovery
without traversing them. Nonmatching files and fixed names such as `snapshot.tmp`
are left alone. More than 128 matches stops startup before new state is written;
a subsequent startup can resume reclamation. This bounds each startup's recovery
work and prevents continued writes from accumulating additional temporary files.
A filesystem enumeration may still inspect other directory entries internally.

Successful operations leave only the active journal/snapshot and, if applicable,
one quarantine copy. `DiskBytes` reports active journal/snapshot bytes; quarantine
and unreclaimed temporary bytes are not included in that value.

## §8 trust boundary and residual check-then-open window

| Actor | Trust and storage implications |
| --- | --- |
| Relay or ordinary library user | Cannot select a local path or trigger filesystem substitution through a response. Link and directory checks reject pre-existing unsafe state paths. |
| Actor able to write Jellyfin's own data directory (server administrator or installed plugin) | Shares the server process's trust. Can race managed path validation and subsequent open/move/delete, substitute directory entries, or add hardlinks. Storage cannot protect against this actor. |

There is a residual **check-then-open window for the existing journal**: the
link/reparse and containment checks and subsequent managed path use are separate
operations. Although append uses a retained stream from exclusive creation,
checks cannot prevent a local data-directory writer from installing hardlinks or
changing directory entries concurrently. Equivalent windows exist during reads,
creation, rename and cleanup; these APIs are not a race-free directory-handle
sandbox. Exploiting substitution requires write access to Jellyfin's own data
directory, which has the same trust as the server process in the §8 table row.
The data directory must retain Jellyfin's administrator-only permissions.

Regression tests exercise fixed-name links (including dangling links), hardlinks,
quarantine destinations, state-directory/ancestor/root substitution, failed copy
and replacement cleanup, and bounded crash-temporary recovery. They run on macOS
ARM64 here. Compilation of both frameworks is not runtime verification on Linux,
Intel macOS or Windows; filesystem execution on those platforms remains a release
validation step.
