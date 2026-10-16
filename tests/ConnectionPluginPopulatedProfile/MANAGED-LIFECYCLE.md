# Focused managed plugin lifecycle regression

Run managed-lifecycle.mjs explicitly with --sdk, --pnpm, and --temp, using the same
G-drive SDK/pnpm invocation described in README.md. The script uses the repository's
frozen Connect 0.3.1 and Mobile-CSS 0.1.5/0.1.6 archives. It constructs a disposable
profile with debug@4.4.3, a custom field, patch, and synthetic conversation marker.
It never discovers or edits a real profile. Registry requests use port 7890.

Focused cases (2026-10-17: 10 passed, 0 failed):

1. Inspect absent does not create a profile.
2. Install Connect with an existing dependency and record complete archive ownership.
3. Inspect installed Connect from its receipt with no downloaded archive argument.
4. Repeat installation leaves its manifest unchanged.
5. Install Mobile-CSS 0.1.5, then update to 0.1.6 using the receipt for old ownership.
6. Tampered installed files are reported and cannot be updated or removed.
7. Foreign Mobile-CSS link source is shown but preserved without mutation.
8. Remove Mobile-CSS while retaining Connect, unrelated settings, patch, and DSH data.
9. Remove Connect while retaining data; a following inspect does not reinstall it.
10. Repeat removal is unchanged and successful recovery storage is empty.

The update feed check is implemented by the host service: it reads metadata and
changes the button to Update; package installation remains an explicit subsequent
action. This Node test covers the actual version upgrade and package operations.

All install/remove calls use official DSH SDK runProfilePnpm with execution=service,
offline=false, ignore-scripts, writer lock, and recovery snapshot. The script's
postinstall marker must remain absent throughout. Old unreceipted TGZ installations
can be recognized only through the explicitly provided current/prior archive list;
unknown link/registry sources are never adopted or removed.

Evidence writes to --temp/managed-report.json. This focused suite is sufficient for
this feature; it does not trigger the previous broad regression suites or publishing.
