# Populated profile cold-cache integration regression

This explicit integration test uses the installed official DSH SDK and an explicit
pnpm executable. It creates all writable state beneath --temp, never discovers a
user DSH home, and only references SDK files through a read-only directory junction.
Use a project temporary directory on a drive other than C. No real profile, setting,
pairing identity, or application process is changed. Registry traffic uses the proxy
provided by --proxy (default http://127.0.0.1:7890).

From this repository, with appropriate absolute SDK/archive/tool locations:

    & 'G:/DeepSeek DSH/node_modules/node/bin/node.exe' tests/ConnectionPluginPopulatedProfile/verify.mjs '--sdk=G:/DeepSeek DSH/node_modules' '--pnpm=C:/Users/KitamaruRamito/AppData/Roaming/npm/node_modules/pnpm/pnpm.exe' '--archive=G:/path/to/frozen-connect/archive.json' '--temp=G:/path/to/project/Temp_SetupandLauncher/plugin-install-dependencies'

--archive is the existing frozen archive metadata JSON: path, version, size,
sha256, and files containing each shipped file's path, size, and sha256.
The actual TGZ is verified by the production helper, never generated or bypassed.
The official SDK must match the helper's required version (currently 0.2.1-alpha.1).
The 2026-10-17 execution used Node 24.21.0 and pnpm 12.4.1.

The baseline is a test-only copy of the current helper with --offline=false
changed back to --offline, plus the previous offline registry environment. It
reproduces the metadata mirror failure for an existing debug@4.4.3 dependency.
This is an isolated reproduction of the former boundary, not a historical source
snapshot. The fixed production helper runs against a separate cold cache and profile.

Assertions cover:

- Old offline mode fails resolving existing dependency metadata and retains recovery.
- Current online mode installs the verified Connect archive through the official SDK
  service invocation and explicit proxy/environment forwarding.
- Existing dependency, arbitrary manifest field, and user patch survive installation.
- A profile postinstall marker never executes (--ignore-scripts remains active).
- Repeated startup is unchanged and successful recovery snapshots are removed.
- Existing lockfile plus installed debug/ms packages with a separate cold cache
  fails the old supply-chain check (missing offline tarball) and passes the fixed
  installation. The no-lock fixture reproduces the exact missing metadata diagnostic.

dependency-report.json records check outcomes, the isolated run directory, baseline
diagnostic, elapsed times, and SHA-256 of the production helper tested. Runs are kept
under --temp for inspection and can be removed after evidence review.

The prior 11-case suite used pnpm 10 and empty successful profile fixtures. A local
Connect TGZ could pass those cases without resolving existing registry dependencies.
This regression adds the missing populated-profile boundary; it does not replace the
existing archive ownership, compatibility, policy, or recovery tests. Cache directory
pnpm/v11 identifies cache/store format, not the pnpm CLI major version.
