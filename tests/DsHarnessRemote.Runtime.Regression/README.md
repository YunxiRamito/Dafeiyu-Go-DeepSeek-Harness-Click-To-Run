# Actual Remote installation regression

This runner loads the accepted built launcher Core and calls its production
`DsHarnessRemoteService` and `DshPluginCliService`. It uses the actual read-only
DSH SDK and a new owned profile. It does not contact Remote accounts or start a
DSH web service.

Arguments: built output directory, fresh fixture directory under
`Temp_SetupandLauncher`, actual SDK `node_modules`, verified standalone pnpm
executable. Prepare `fixture/node_modules` as a junction to the SDK before the
run. The SDK requires its `node/bin/node.exe`. All profile/package/cache writes
stay under the fixture; no real DSH home is selected.

The runner installs `ds-harness-remote@0.6.4` through the actual official CLI,
checks its dependency and active bundle, removes it through the same CLI, and
checks that an unrelated dependency and synthetic credential/legacy files
remain unchanged. It provides pnpm as a launcher component without modifying
the runner's PATH, thereby checking production component selection. The report
records Core and pnpm hashes, SDK version and successful checks.

The test uses the real npm registry via the launcher's configured local proxy
and therefore requires network access. It performs actual package installation
with lifecycle scripts disabled. Test data and logs remain as owned evidence.
