# Built Connect installer environment integration

This opt-in Windows test loads the actual built launcher Core DLL by reflection,
then calls production EnsureInstalled with its embedded Node helper and the real
official SDK service subprocess. It never discovers a user DSH home.

Use a fresh output directory on the project drive. Build with the repository's
scoped release environment (TEMP/TMP/DOTNET_CLI_HOME/NuGet caches on that drive).
Set BaseIntermediateOutputPath and output directory outside this source folder.

Before execution, prepare three test-owned directories under the selected fixture
root: official, mirror, backend. Each must contain a node_modules junction pointing
to the explicit read-only SDK node_modules input. No .dsh directory may exist in
any fixture. The test verifies the junction target and refuses reused profiles.

Executable arguments, in order:

1. Built launcher directory containing DeepSeek Harness.Core.dll and assets.
2. Fresh prepared fixture root, outside C:.
3. Explicit installed SDK node_modules directory (read only).
4. Explicit pnpm executable (read only).

The official-source fixture executes actual pnpm with an existing debug@4.4.3
dependency and cold launcher cache, using the configured 127.0.0.1:7890 proxy.
The pnpm wrapper records only allowlisted transport variables, then invokes pnpm.
Assertions cover successful install, actual service-child proxy/registry forwarding,
online/ignore-scripts arguments, preservation of existing dependency and patch,
idempotence, and cancellation before process start.

Mirror and backend fixtures deliberately stop their pnpm wrappers after environment
capture. They validate source selection, strict backend CA/Git TLS configuration,
backend proxy exclusions, and recovery evidence; they do not claim a successful
backend network installation. Captured files must contain no inherited credentials.

The report csharp-environment-report.json binds 13 checks to the actual Core DLL
SHA-256. All fixture profiles, settings, cache, and recovery data stay under the
specified owned root. The TGZ is copied only into this test executable's G-drive
output assets, never patched in the production build.
