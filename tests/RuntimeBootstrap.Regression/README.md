# Windows App Runtime Detection Regressions

Run from the repository root on Windows with PowerShell 7:

```powershell
pwsh -NoProfile -File tests/RuntimeBootstrap.Regression/Run.ps1
dotnet run --project tests/ComponentRuntime.Regression/ComponentRuntime.Regression.csproj -c Release
```

The first command uses the inbox x64 .NET Framework compiler, compiles the real
outer bootstrap with its icon and manifest plus `WindowsAppRuntimeDetector.cs`,
and runs 122 assertions against that executable's production detector. It also
loads the actual verification function from `verify.ps1` and invokes it against
the compiled executable. No .NET SDK or NuGet dependency is needed for this suite.

The component suite links the actual `ComponentService.cs` and shared detector
under .NET 8 and runs 47 assertions. Unrelated install/download collaborators are
stubbed to throw if called. It checks Ready/Missing/Unknown mapping, the reported
highest version, and the real component detector entry point.

After CI compiles the release bootstrap, test that artifact directly:

```powershell
pwsh -NoProfile -File tests/RuntimeBootstrap.Regression/Run.ps1 -BootstrapPath 'source/dist-release/DeepSeek Harness.exe'
```

For a read-only check of another source tree, use `-BootstrapSource` pointing to
its `source/RuntimeBootstrap.cs`. The helper, icon, and manifest are read from the
same source directory; all generated files stay in this runner's repository
under a unique `.build-temp/runtime-bootstrap-*` directory and are removed after
the run. This permits checking release 1.7.6 and a backported 1.8.0 source tree
independently without rewriting either assembly's version metadata.

The component suite also accepts an alternate source directory through MSBuild:

```powershell
dotnet run --project tests/ComponentRuntime.Regression/ComponentRuntime.Regression.csproj -c Release '-p:RuntimeDetectionSourceRoot=G:/path/to/other/source'
```

Coverage includes both supported identities, both registry stores, current-user
only installations, denied or failing stores, partial enumeration, x64 and
neutral Main acceptance, wrong architectures, unsupported identities, malformed
or incomplete versions, the minimum version boundary, and numeric maximum
selection across stores. All registry access is read-only. The smoke checks
invoke detector methods without executing the bootstrap entry point, opening
dialogs, starting DSH, running installers, or changing user settings. They do
not simulate a browser installation or verify runtime DLL loading.
