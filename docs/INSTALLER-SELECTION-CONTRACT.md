# Explicit installer selection

## Release boundary and 1.7.5 compatibility

This is a consumer contract for the local launcher changes, not a capability of
the stable 1.7.5 launcher currently named by manifest.json. Shipping only an
installer that emits these fields will NOT make 1.7.5 switch an existing root.
No launcher release or publication is authorized by this document.

The 1.7.5 LoadOrCreate implementation reads launcher.json only when creating
settings. Its command line has no root override. The agreed narrow compatibility
patch belongs to the installer: capture the original user's LauncherSettings.json
path before elevation and carry it in the elevation plan. After verifying the
selected npm root and Node executable, update an existing settings JSON structurally,
preserving unknown fields and preferences. Back up the old JSON in the same directory
before a root change, then atomically replace it. Update dshRoot/nodePath and reset
only the active cached API key and validation fields on a root change; mark legacy
API-key migration complete. Keep old DSH_HOME identity and files untouched. Ensure
the old launcher has exited before writing, so its in-memory settings cannot restore
the old root. This compatibility patch is owned by the installer agent and is not
implemented in the launcher changes described here.

If the original profile is unavailable, concurrent changes cannot be resolved, or
the installer cannot safely write that user's settings, use explicit guidance:

1. Read only the saved DshRoot and compare it with the explicitly selected target.
   Do not read or log credentials, and do not rewrite LauncherSettings.json.
2. On a mismatch, leave immediate launcher startup unchecked and state that the
   existing launcher still uses its saved directory. Display the newly selected
   npm target path so the user can identify it.
3. Ask the user to exit any running launcher, then select the new npm directory in
   launcher Settings / Service / DSH directory / Browse. The user must also review
   the API configuration because the old launcher's picker does not itself implement
   the new root-bound credential reset.
4. Do not report an automatic switch. Keep all global identity files untouched.

1.7.5 supports DAFEIYU_LAUNCHER_SETTINGS_DIRECTORY, but process-only overrides are
insufficient for a durable installation: shortcuts and autostart must consistently
set the override, and the launcher has a global single-instance mutex. Such isolation
is not part of this change. Installer completion UI and stable-settings compatibility
changes belong to the installer agent and are not modifications included here.

## Consumer contract

`installer-defaults.json` stays in the selected launcher directory. Existing
source-default behavior is unchanged. To hand an explicit user selection to the
launcher, ConfigStore should additionally write:

```json
{
  "SchemaVersion": 2,
  "SelectionId": "unique-guid-for-this-confirmed-installation",
  "LauncherRoot": "G:\\Tools\\Dafeiyu-Go",
  "DshRoot": "G:\\Tools\\DeepSeek Harness",
  "SourcePreference": "backend"
}
```

- Generate SelectionId only for a confirmed installation target. Reuse it for
  retries of that operation. Do not generate a new ID at launcher startup,
  source-preference saves, discovery, or routine maintenance without an explicit
  target selection.
- SchemaVersion must be 2. LauncherRoot and DshRoot must be fully qualified. LauncherRoot must match the
  directory passed to LoadOrCreate after Windows path normalization, ignoring
  case and a trailing separator. IDs are nonblank strings of at most 128 characters.
- The launcher records consumed IDs per launcher directory in
  LauncherSettings.json / appliedInstallerSelections. Previously consumed IDs
  never override a subsequent user root choice, including replay after another
  installer selection. Missing or invalid markers do not override an existing root.
- An accepted marker updates DshRoot and its consumption record. On a root
  change it also clears ApiKeyProtected, ApiKeyValidatedUtc and validation success,
  and marks legacy API-key migration complete. It never copies credentials,
  identity files, profiles, or any other data from the previous DSH home.
- On a root change, NodePath is read from the same launcher directory's
  launcher.json. It must be fully qualified and reference an existing file. If
  invalid or absent, retain a still-existing saved NodePath, otherwise try the
  existing FindNode locator; an unavailable runtime leaves NodePath empty rather
  than retaining a deleted component. A consumed ID never overwrites a subsequent
  user NodePath choice.
- Appearance, startup, update-source, proxy and other user preferences remain.
  GitHub/DeveloperCenter credentials and the launcher's independent Aries identity
  remain launcher-scoped. SourcePreference continues to seed newly created settings
  under the existing source-default rules.
- Persist the marker atomically after a successful confirmed installation. The
  launcher reads it without modifying installation-local metadata. Each Windows
  user consumes it independently in their own settings.

Installer UI, detection, migration, and ConfigStore writes are outside the launcher
consumer change. ConfigStore must emit the marker for this contract to take effect
with existing launcher settings.
