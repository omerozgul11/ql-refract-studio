# Building Refraction Studio

This document describes how to build, run and work with the source tree. Packaging the Windows
installer is covered in [DEPLOYMENT.md](DEPLOYMENT.md); tests in [TESTING.md](TESTING.md).

## 1. Prerequisites

| Tool | Needed for | Notes |
|---|---|---|
| .NET SDK 10.0.401 (or a later patch of the same feature band) | everything | pinned by `global.json` (`"version": "10.0.401", "rollForward": "latestFeature"`) |
| Python 3 | `packaging/package_windows.py`, `tests/RefractionStudio.Formats.Tests/Golden/make_golden.py` | standard library only |
| Docker | `tests/RefractionStudio.Server.Tests` (Testcontainers PostgreSQL) and the server containers | not needed for the desktop application |
| Inno Setup 6 (`ISCC.exe`) | Windows installer | on Linux run through a private Wine; see [DEPLOYMENT.md](DEPLOYMENT.md) |

All projects target `net10.0` (`Directory.Build.props`). The desktop application uses Avalonia
12.1.4 and SkiaSharp 3.119.4 (`Directory.Packages.props`).

## 2. Project-local SDK

The repository can carry its own SDK under `.tools/dotnet` (the `.tools/` folder is listed in
`.gitignore`, so it is not part of a clone). On the build host it contains SDK 10.0.401. To use it in
a shell:

```bash
source .tools/env.sh
dotnet --version        # 10.0.401
```

`.tools/env.sh` sets `DOTNET_ROOT` to `.tools/dotnet`, prepends it to `PATH`, and sets
`DOTNET_CLI_TELEMETRY_OPTOUT=1`, `DOTNET_NOLOGO=1` and `DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1`.

On a fresh clone either install the .NET 10 SDK system-wide or install it into `.tools/dotnet`
(for example with Microsoft's `dotnet-install` script) and then source `.tools/env.sh`.
`packaging/package_windows.py` uses `.tools/dotnet/dotnet` when it exists and `dotnet` from `PATH`
otherwise.

The local tool manifest `.config/dotnet-tools.json` pins `dotnet-ef` 10.0.12, which is only needed
to create or apply licence-server database migrations (`dotnet tool restore`, then
`dotnet ef …`; see [licensing/LICENSE-SERVER.md](licensing/LICENSE-SERVER.md)).

## 3. Build settings

`Directory.Build.props` applies to every project:

| Property | Value | Effect |
|---|---|---|
| `TargetFramework` | `net10.0` | |
| `LangVersion` | `latest` | |
| `Nullable` | `enable` | nullable reference types everywhere |
| `ImplicitUsings` | `enable` | |
| `TreatWarningsAsErrors` | `true` | **any compiler or analyzer warning fails the build** |
| `GenerateDocumentationFile` | `true` | XML documentation is generated (test projects switch it off) |
| `NoWarn` | `CS1591;CS1573` | missing XML comments on members/parameters are not warnings |
| `Deterministic` | `true` | reproducible assemblies |
| `VersionPrefix` | `1.0.0` | default version; packaging overrides it with `-p:Version=…` |
| `ManagePackageVersionsCentrally` | `true` | central package management (below) |
| `SatelliteResourceLanguages` | `en` | no localised satellite assemblies are copied |
| `Company` / `Product` / `Copyright` | QuakeLogic Inc. / Refraction Studio / Copyright © 2026 QuakeLogic Inc. | assembly metadata |

`tests/Directory.Build.props` imports the root file and additionally suppresses `xUnit1051`
(passing `TestContext` cancellation tokens).

Because warnings are errors, a new warning (for example a nullable-flow warning, an obsolete API,
or a missing `await`) must be fixed or explicitly justified with a targeted suppression; it cannot be
left in the build output.

### Central package management

Package versions live only in `Directory.Packages.props` (`<PackageVersion Include=… Version=…/>`).
Project files reference packages without a version:

```xml
<PackageReference Include="Avalonia" />
```

Two properties keep related packages in step: `AvaloniaVersion` (12.1.4; `Avalonia.Controls.DataGrid`
is pinned separately at 12.1.2) and `MsExtVersion` (10.0.12, used for Microsoft.Extensions.*,
Microsoft.Data.Sqlite, EF Core and `Microsoft.AspNetCore.Mvc.Testing`). To add a package, add a
`PackageVersion` entry to `Directory.Packages.props` and a version-less `PackageReference` to the
project. A package that is bundled with the desktop application must also be listed in
`packaging/windows/THIRD-PARTY-NOTICES.txt`; `packaging/package_windows.py` fails otherwise.

## 4. Building

```bash
source .tools/env.sh
dotnet build RefractionStudio.sln                 # Debug
dotnet build RefractionStudio.sln -c Release
```

`RefractionStudio.sln` contains the eleven `src/` libraries and the application (`src/RefractionStudio.App`), the CLI
(`tools/RefractionStudio.Cli`), the three server projects (`server/`) and the test projects
(`tests/`). On the build host an incremental `dotnet build RefractionStudio.sln` finished with
0 warnings and 0 errors.

Embedded resources that the build depends on:

* `src/RefractionStudio.Infrastructure/Keys/*.pem` (the licence-token and release **public** keys) are
  embedded as `RefractionStudio.Keys.<name>.pem`. `AppInfo` (`src/RefractionStudio.Infrastructure/AppEnvironment.cs`)
  reads `license-public` and `release-public` at start-up and throws if one is missing.
* `docs/user-guide/USER-GUIDE.md` is embedded as `RefractionStudio.Help.user-guide.md` and is the
  in-application help (`src/RefractionStudio.Infrastructure/HelpCatalog.cs`). Editing the guide changes
  the help shown in the application at the next build.

## 5. Running the desktop application

```bash
dotnet run --project src/RefractionStudio.App
dotnet run --project src/RefractionStudio.App -- path/to/project.rfsx   # opens a project at start-up
```

The executable is `RefractionStudio` (`AssemblyName` in `src/RefractionStudio.App/RefractionStudio.App.csproj`,
`OutputType` `WinExe`). A command-line argument ending in `.rfsx` that exists on disk is opened after
start-up (`MainWindowViewModel.StartupAsync`); this is how the Explorer file association passes the
project.

Per-user data (settings, SQLite catalogue, licence store, logs, recovery checkpoints, downloaded
updates, crash reports) is kept in `LocalApplicationData/QuakeLogic/RefractionStudio`
(`%LocalAppData%\QuakeLogic\RefractionStudio` on Windows; `AppPaths` in
`src/RefractionStudio.Infrastructure/AppEnvironment.cs`). The environment variable
`REFRACTIONSTUDIO_DATA_DIR` relocates this folder (used by support and by the UI tests;
`App.DataDirectoryVariable`). New projects default to `Documents/Refraction Studio`.

The product targets Windows 10/11 (`app.manifest` declares Windows 10/11 compatibility, per-monitor
V2 DPI awareness and long-path awareness). The application uses Avalonia's platform detection, and
the headless UI tests run it on Linux; interactive use on Linux is not a supported configuration.

Editing requires a licence or trial (see [SECURITY.md](SECURITY.md)); without one the application
opens projects read-only. The licence server URL is a user setting (default
`https://licensing.quakelogic.net/`).

## 6. Command-line tool

`tools/RefractionStudio.Cli` (assembly `refraction-studio-cli`) runs the analysis engine without the
UI. Run it with `dotnet run --project tools/RefractionStudio.Cli -- <command> …`. Without arguments,
or with `-h`/`--help`, it prints the command list (exit code 0); an unknown command prints the list
and returns 2. Expected errors (bad arguments, I/O, damaged project or seismic file) print
`Error: …` and return 1. The CLI runs with the invariant culture.

| Command | Arguments | What it does |
|---|---|---|
| `sample` | `--out <file.rfsx> [--work <dir>]` | Builds the distributed sample project from synthetic data (`SampleProjectBuilder`), printing progress. Without `--work` a temporary work folder is used and deleted afterwards. |
| `analyze` | `<project.rfsx>` | Opens the project, runs the analysis and prints per line the picks used, layer velocities (or the reason there are none) and the cached tomography RMS/χ² (marked "out of date" when stale), the Vs30 values and the quality verdict. |
| `report` | `<project.rfsx> --out <report.pdf> [--units si\|us] [--paper a4\|letter]` | Writes the PDF report. Paper defaults to A4, or Letter when `--units us` is given without `--paper`. US units use ksf for moduli, SI uses MPa. |
| `export` | `<project.rfsx> --out <folder> [--units si\|us]` | Writes `results.xlsx`, a `csv/` folder, `audit.json` and `figures/` (per line: travel times PNG+SVG, tomogram PNG+PDF when a tomography result exists, first included gather PNG). |
| `validate` | `--out <folder>` | Runs the scientific validation suite and writes `<folder>/validation-report.md`; exit code 0 when all cases pass, 3 otherwise. See [SCIENTIFIC-VALIDATION.md](SCIENTIFIC-VALIDATION.md). |
| `keys` | `--out <folder>` | Generates an ES256 (P-256) key pair `signing-<kid>.private.pem` / `signing-<kid>.public.pem`; refuses to overwrite an existing private key; on Linux/macOS sets the private key to mode 0600. |
| `sign-manifest` | `<manifest.json> --key <private.pem> --out <signed.json>` | Signs an update manifest (`ReleaseInfo` JSON) with the release key. See [DEPLOYMENT.md](DEPLOYMENT.md). |

Options are parsed as `--name value`; a `--name` not followed by a value is read as `true`
(`Args` in `tools/RefractionStudio.Cli/Commands.cs`).

## 7. Regenerating the sample project

`samples/Refraction-Studio-Sample.rfsx` is installed with the application and used by the UI and
reporting tests. It is generated deterministically from synthetic data (`src/RefractionStudio.Synthetics/SampleProjectBuilder.cs`:
48 channels at 3 m, seven shots, P and SH lines, topography, trigger errors on three P shots, a
reversed channel; fixed random seeds 2026 and 2027):

```bash
dotnet run --project tools/RefractionStudio.Cli -- sample --out samples/Refraction-Studio-Sample.rfsx
```

If no installed copy exists, the application's "Explore the sample" action generates the same
project into `Documents/Refraction Studio` (`StartPageViewModel.OpenSampleAsync`).

Regenerate the sample after a change to the project schema, the sample builder, or analysis defaults
that should be reflected in the shipped sample, and re-run the UI and reporting tests afterwards.
