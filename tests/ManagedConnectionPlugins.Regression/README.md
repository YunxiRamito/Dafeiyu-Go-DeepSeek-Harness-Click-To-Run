# Actual built managed plugin smoke

This focused 12-case executable loads the actual candidate Core assembly and embedded helper. It downloads the two current production DeveloperCenter packages through the configured localhost:7890 proxy and the launcher's normal HTTPS validation. It installs, inspects, checks for updates, removes both plugins, and verifies read-only startup inspection does not reinstall them. A synthetic conversation and user patch must survive.

Arguments, in order: built-launcher directory, fresh owned fixture root, SDK node_modules, pnpm executable. Prepare the fixture node_modules junction to that exact SDK directory in PowerShell first. The fixture must not already contain .dsh. Both build output and fixture must be outside C:. Use the existing Release.ps1 Invoke-ReleaseEnvironment wrapper to keep temp, NuGet, dotnet and npm caches on G:. Pass BaseIntermediateOutputPath and output directory under the same owned test root.

The test touches only its explicit fixture. It does not discover or modify the user's DSH profile and does not start DSH. The report is written to fixture/managed-built-report.json and records the loaded Core SHA-256. Run only this test for this integration boundary; do not chain the historical full suites.

2026-10-17 result: 12 passed, 0 failed. Evidence: Temp_SetupandLauncher/MP17/managed-built-fixture/managed-built-report.json. Core SHA-256: CEF1CAB51A6EB4DAD4C435A4ACC51491CE3009653AC27DC85966323C4C2BF762. This validates the local candidate, not a newly published launcher.
