# Local Launcher Releases

This is the current release procedure. Historical CI release instructions are superseded.
The GitHub workflow has only `workflow_dispatch`, read-only repository permissions,
local acceptance checks and artifact upload. Pushing a tag does not start it.
It does not publish npm packages or GitHub Releases, update manifests or sign binaries.

## Prerequisites

Use Windows, PowerShell 7.4+, .NET 8 SDK, Git, Windows tar, Node/npm and the inbox
x64 .NET Framework C# compiler. Authenticate npm beforehand using your normal account
configuration. GitHub authentication uses `GH_TOKEN` / `GITHUB_TOKEN`, existing
`gh auth`, or `git credential fill` for github.com. No token file or npmrc is created
by these scripts. HTTP errors omit response bodies and signed redirect URLs.

Explicitly commit source, tests, scripts and release notes before acceptance.
Tracked modifications or staged changes stop the process. Untracked files are never
included in the build snapshot: add intended new source files to your explicit commit.
Scripts never stage or commit files, stash, rebase, force push, move tags or delete assets.

## Acceptance And Publication

```powershell
# Acceptance only. -NoManifest is an optional compatibility spelling of this default.
./release.ps1 -NoManifest

# Portable SDK, project scratch space, optional non-C NuGet cache and custom proxy.
./release.ps1 `
  -DotnetPath 'G:/DeepSeek DSH/.tools/dotnet/dotnet.exe' `
  -WorkRoot 'G:/DeepSeek DSH/DSH Works/Project/Dafeiyu-Go/Temp_SetupandLauncher/LocalRelease' `
  -NuGetCache 'G:/DeepSeek DSH/DSH Works/Project/Dafeiyu-Go/Temp_SetupandLauncher/LocalRelease/nuget/packages' `
  -Proxy 'http://127.0.0.1:7890'

# Full acceptance and external publication, using a source commit already on GitHub.
./release.ps1 -Publish

# Explicitly opt in to pushing the existing accepted commit to origin's current branch.
# Requires an attached branch and origin matching -Repository; uses no force flags.
./release.ps1 -Publish -PushCommit

# Direct networking. NO_PROXY=* also neutralizes the build entry's legacy proxy default.
./release.ps1 -Proxy ''
```

The default WorkRoot is `Temp_SetupandLauncher/LocalRelease` beside the main checkout,
derived from the Git common directory so isolated worktrees use the same project area.
An explicit WorkRoot must be on a non-C drive under `Temp_SetupandLauncher`.
TEMP/TMP, .NET CLI home, NuGet HTTP/plugin caches, npm cache and all build intermediates
stay in this area. An explicitly supplied NuGet package cache must also be outside C:.
Environment variables, including proxy, credential interaction and `GODEBUG`, are
restored in `finally`, including deleting variables that were originally absent.
Without `-NuGetCache`, acceptance reuses an existing non-C `NUGET_PACKAGES` directory
or the existing portable `G:/DeepSeek DSH/.nuget-packages` cache before falling back
to the project run cache. This avoids restoring cached dependencies again.

Acceptance archives exactly HEAD to a disposable snapshot and runs:

1. `check-release-gate.ps1` and the 14 existing suites: PluginUpdateCompare,
   PluginInstallRepair, PatchSystem, SettingsSearch, ClientNotice, DeveloperNotice,
   InfoWindowClient, DshDataImport, DshDataExport, DshDataDiscovery,
   StartupBackgroundUpdates, UpdateMetadata, UpdateIntegrity and ComponentRuntime.
2. `source/build-winui.ps1`, including its InfoHost publish target and both bootstrap
   and WindowsAppRuntimeDetector sources. Only the two legacy SDK/cache defaults in
   the disposable copy are adapted to the supplied parameters; build logic is retained.
3. StartupUpdateOwnership against that dist, RuntimeBootstrap against the compiled
   executable, and `verify.ps1` in a child PowerShell process so its exit code is enforced.
4. ZIP creation and `acceptance.json`, binding the exact commit, version, all checks,
   ZIP path and SHA256. No check-skipping or raw-ZIP publication option exists.

Snapshot/source archive and npm staging are cleaned even on failure. The versioned run
directory retains dist, ZIP, receipt, publication journal and verified mirror evidence
for inspection/resume. Delete obsolete run directories only after they are no longer needed.

Publication then checks GitHub conflicts and source commit availability, publishes or
verifies npm, downloads and hashes the actual mirrored tarball's embedded ZIP, creates
an immutable tag and draft Release if missing, uploads without replacing assets, verifies
the asset download, publishes the draft, then downloads the public GitHub URL and hashes
those bytes. Only then are `manifest-<version>.json` and finally `manifest.json` written.
Release body is the corresponding CHANGELOG section body, without the version heading
or appended receipt metadata. Existing Release bodies are retained when resuming.

Both manifests remain local for review. The script prints explicit `git add --` and
`git commit --only` commands naming just those two files, followed by a normal push.
Do not move the release tag onto the later manifest commit.

## Resume And Package Checks

Use the same acceptance receipt after an interrupted publication. HEAD and tracked files
must still match it, and the ZIP must still hash identically. No product regressions
are rerun when publishing an already accepted receipt.

```powershell
$receipt = 'G:/DeepSeek DSH/DSH Works/Project/Dafeiyu-Go/Temp_SetupandLauncher/LocalRelease/<run>/acceptance.json'

# External publication/resume, including npm mirror and GitHub validation.
./publish-release.ps1 -ReceiptPath $receipt

# Optional repository, existing-commit push, proxy and npm OTP parameters.
./publish-release.ps1 -ReceiptPath $receipt `
  -Repository 'YunxiRamito/Dafeiyu-Go-DeepSeek-Harness-Click-To-Run' `
  -PushCommit -Proxy 'http://127.0.0.1:7890' -Otp '<current OTP>'

# Offline npm pack and actual embedded ZIP hash check; never publishes or contacts registries.
./publish-npm.ps1 -ReceiptPath $receipt -NoPublish

# Verify an existing public npm version and its mirror, without publishing.
./publish-npm.ps1 -ReceiptPath $receipt -VerifyOnly -MirrorTimeoutMinutes 25
```

npm may process a successful publish for several minutes. The script polls full metadata
with `?cacheBust=<timestamp>` and reads `versions`, avoiding cached version-endpoint 404s.
A journal is written before the publish attempt, so EOF, failure or processing on resume
never immediately repeats npm publication. Preserve that journal and receipt. If the
attempt never reaches the registry, investigate authentication/processing before starting
a genuinely new release attempt. Timeout never updates manifests.

Existing npm versions and GitHub assets are reused only after their downloaded embedded
ZIP / ZIP SHA256 matches acceptance. A conflicting asset or tag stops before publication.
Drafts are also discovered through paginated Release listing when the tag endpoint returns
404. Mirror sync success, HEAD availability and matching version strings do not count
as content verification. The actual cache-busted mirror tarball must contain the expected
package identity and exactly one expected ZIP with the accepted SHA256.

GET/HEAD transport failures and transient server errors have at most three attempts;
mutations are not blindly retried. `GODEBUG=http2client=0` is scoped to the operation for
gh/proxy EOF mitigation. The default proxy is `http://127.0.0.1:7890`.

## Offline Script Verification

```powershell
./tests/LocalRelease.Smoke.ps1
# Or explicitly choose the project test scratch directory:
./tests/LocalRelease.Smoke.ps1 -WorkRoot 'G:/DeepSeek DSH/DSH Works/Project/Dafeiyu-Go/Temp_SetupandLauncher/LocalRelease-Tests'
```

These tests parse all three scripts, use real gzip/tar embedded ZIP hashing, exercise
environment restoration and path guards, mock the complete mandatory acceptance chain,
and mock registry processing and GitHub API boundaries. They cover publication journals,
matching asset/draft resume, asset/tag conflicts and corrupt public downloads. All external
HTTP, npm publication and Git mutations are mocked; no product regression suite is rerun.
