# Actual built managed plugin smoke

This focused 12-case executable loads the actual candidate Core assembly and embedded helper. It downloads the two current production DeveloperCenter packages through the configured localhost:7890 proxy and the launcher's normal HTTPS validation. It installs, inspects, checks for updates, removes both plugins, and verifies read-only startup inspection does not reinstall them. A synthetic conversation and user patch must survive.

Arguments, in order: built-launcher directory, fresh owned fixture root, SDK node_modules, pnpm executable. Prepare the fixture node_modules junction to that exact SDK directory in PowerShell first. The fixture must not already contain .dsh. Both build output and fixture must be outside C:. Use the existing release.ps1 Invoke-LocalReleaseEnvironment wrapper (-FunctionsOnly) to keep temp, NuGet, dotnet and npm caches on G:. Pass BaseIntermediateOutputPath and output directory under the same owned test root.

The test touches only its explicit fixture. It does not discover or modify the user's DSH profile and does not start DSH. The report is written to fixture/managed-built-report.json and records the loaded Core SHA-256. Run only this test for this integration boundary; do not chain the historical full suites.

2026-10-17 result: 12 passed, 0 failed. Evidence: Temp_SetupandLauncher/MP17/managed-built-fixture/managed-built-report.json. Core SHA-256: CEF1CAB51A6EB4DAD4C435A4ACC51491CE3009653AC27DC85966323C4C2BF762. This validates the local candidate, not a newly published launcher.

Additional deterministic metadata mode:

    dotnet ManagedConnectionPlugins.Regression.dll --metadata G:/path/to/candidate G:/path/to/Temp_SetupandLauncher/metadata

This mode executes ReadReleaseAsync in the actual built Core with an in-memory HttpMessageHandler. It covers both plugins' host/platform compatibility, package/protocol/archive identity, hash and size boundaries, Mobile-CSS integrity URL, retry after a missing feed, and cancellation. Its separate managed-metadata-report.json records the actual Core SHA-256. It does not download packages, alter profiles, or replace the original 12-case production HTTPS lifecycle acceptance protocol used by release.ps1.

2026-10-17 rerun: candidate CP17r1 Core SHA-256 539131A6C54DAA1E160BCC850489BC316F8B3380DB35296E9F8BDD9A7DCCEA6E passed the actual HTTPS lifecycle 12/12 and deterministic metadata 21/21. Reports are under Temp_SetupandLauncher/ConnectionPlugins-20261017/built-fixture and metadata. The separate Node managed-lifecycle.mjs suite passed 12/12 using the installed official DSH SDK 0.2.1-rc.1 and pnpm 12.4.1, including failed pnpm snapshot/retry and Mobile-CSS 0.1.5 to 0.1.6 update; its report is under the same evidence root's node-lifecycle directory. This rerun touched only owned G-drive fixtures, without changing the real DSH profile or starting DSH.
