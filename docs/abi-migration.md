# ABI upgrade versioning

Release tags use `x.y.z.0`. The .NET 9 / ABI 10.11 artifact uses that exact
version; the .NET 10 / ABI 12 artifact uses `x.y.z.1`. The assembly, file,
package metadata and repository versions agree. Archive filenames retain the
release tag version for both artifacts. The next release increments major,
minor or patch and resets the revision to zero; revision one is reserved for
the ABI migration.

Verified locally against `Emby.Server.Implementations/Updates/InstallationManager.cs`:

- `v10.11.0`, object `877251bcaec3780d44b7657c54684dc28646b1c3`.
- `v12.0`, object `6c073e19ddf604b2369c638716164fdab4c952dc`.

In both versions, `GetCompatibleVersions` filters target ABI against the
application version, applies the installed minimum plugin version, and orders
compatible entries by descending plugin version. `GetAvailablePluginUpdates`
then selects an entry whose version is strictly greater than the installed
plugin version. Both also honor plugin auto-update/disabled settings; 12 skips
pending removals. This scheme assumes an enabled plugin with auto-update on.

For an installed 1.0.0.0 plugin, 10.11 filters out the ABI 12 entry and has no
same-release update. After the server upgrades to 12, 1.0.0.1 is compatible and
strictly greater, so the updater offers the .NET 10 binary. An installed
1.0.0.1 plugin has no further update from this release. Fresh installs select
the corresponding latest compatible build as well.

`scripts/test_release.py` exercises that selection using the actual generated
manifest, package metadata and idempotent manifest updates. The .NET security
regressions read both built DLL assembly versions. These are offline tests;
no real server installation, restart, or auto-update was performed.
