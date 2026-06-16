# Building & Deploying the MySql Module

> **Golden rule:** after **any** source change, rebuild before you deploy.
> `dist\` is the artifact that actually runs on the TCAdmin server, and it is
> **gitignored** — it is not produced by `git pull`/`git checkout`. A stale
> `dist\` is identical to an un-shipped fix. (This is exactly how a hyphenated
> account-name fix sat in the source for a day while the live server ran an
> older DLL.)

The fastest path:

```powershell
.\build.ps1
```

That one command produces a fresh, deployable `dist\`. Everything below is the
detail behind it.

---

## TL;DR

| Step | Command |
| --- | --- |
| Build | `.\build.ps1` |
| Output | `dist\` (DLLs + `Monitor\` copies + `Module.json` + SQL + `Views\`) **and** `mysqlmanager.zip` (the same contents, zipped, at the repo root) |
| Deploy | Extract `mysqlmanager.zip` (or copy `dist\` contents) onto the TCAdmin server, then **Update** the module in the admin UI |

---

## Prerequisites

### 1. MSBuild (Visual Studio 2022 or Build Tools)
The project is a classic .NET Framework `packages.config` project, so it builds
with **MSBuild**, not `dotnet build`. Install **Visual Studio 2022** or the
standalone **Build Tools for Visual Studio 2022** with the *MSBuild* /
*.NET desktop build tools* component. `build.ps1` locates MSBuild automatically
via `vswhere`.

### 2. .NET Framework 4.7.2 reference assemblies
The module targets `v4.7.2`. You need its reference assemblies one of two ways:

- **Recommended:** install the **.NET Framework 4.7.2 Developer Pack**
  (<https://dotnet.microsoft.com/download/dotnet-framework/net472>). Once
  installed, `build.ps1` needs nothing extra.
- **Workaround (used on this machine — the Developer Pack is *not* installed):**
  extract the `Microsoft.NETFramework.ReferenceAssemblies.net472` NuGet package
  and point the build at it:
  ```powershell
  $env:NET472_REF_DIR = "C:\path\to\refasm472\build\.NETFramework\v4.7.2"
  .\build.ps1
  ```
  If `NET472_REF_DIR` is unset, `build.ps1` falls back to
  `%TEMP%\refasm472\build\.NETFramework\v4.7.2` (the current local extract).
  Because that lives under `%TEMP%`, it can be cleaned up by Windows — if the
  build complains the assemblies are missing, re-extract the package and set
  `NET472_REF_DIR`.

### 3. Restored packages (gitignored)
`packages\` is gitignored and is **not** restored by a plain clone. On this
machine it is already populated. For a fresh clone:

- **TCAdmin SDK (not on nuget.org)** — recover the vendored copy from the
  initial commit:
  ```bash
  git archive 11e2ef6 packages/TCAdmin.2.0.149.5 | tar -x
  ```
- **Public packages** (`Microsoft.AspNet.Mvc.5.2.7`, `Microsoft.AspNet.Razor.3.2.7`,
  `Microsoft.AspNet.WebPages.3.2.7`, `Microsoft.Web.Infrastructure.1.0.0.0`) —
  restore from nuget.org (`nuget restore MySqlModule.csproj`) or also recover
  from git history.
- **MySql.Data / MySqlBackup / Ubiety.Dns.Core** — these are **vendored DLLs**
  tracked in `lib\` and referenced by `HintPath`, so they are present right
  after a clone (no restore needed).

---

## Build

```powershell
.\build.ps1            # Release (default)
.\build.ps1 -Configuration Debug
```

Equivalent manual command (what the script runs):

```powershell
& "<MSBuild.exe>" MySqlModule.csproj /t:Rebuild /p:Configuration=Release `
    /p:FrameworkPathOverride="$env:NET472_REF_DIR"
```

The csproj's **`PackageModule`** target (`AfterTargets=Build`) assembles `dist\`
on every successful build, so you never have to copy files by hand.

### `dist\` layout (the deploy package)
```
dist\
  MySqlModule.dll  MySql.Data.dll  MySqlBackup.dll  Ubiety.Dns.Core.dll
  Monitor\         <-- the same 4 DLLs again (for the TCAdmin agent/Monitor)
  Module.json
  install.sql  update.sql  uninstall.sql
  Views\Default\...
```

The build also zips these contents into **`mysqlmanager.zip`** at the repo root
(gitignored). The archive holds the files at its root — no `dist` wrapper — so it
extracts straight into the module folder on the server.

### Verify the build really contains your change
.NET stores string literals as **UTF-16**, so a plain ASCII `grep`/`Select-String`
over the DLL gives false negatives. Search as Unicode instead:

```powershell
$b = [IO.File]::ReadAllBytes("dist\MySqlModule.dll")
[Text.Encoding]::Unicode.GetString($b).Contains("[^_a-zA-Z0-9]")   # username sanitizer -> True
```

A quick timestamp check is usually enough:

```powershell
Get-Item dist\MySqlModule.dll | Select-Object LastWriteTime
```

---

## Deploy to TCAdmin

1. Stop/ensure the module isn't mid-use, then copy the **contents of `dist\`**
   into the module's folder on the TCAdmin web server (and the `Monitor\` copies
   travel with it for the agent).
2. In the TCAdmin admin UI, **Update** the module so it re-reads `Module.json`,
   re-runs SQL profiles, and reloads the DLL. A web/app-pool recycle picks up
   the new assembly.
3. The module is built against the **TCAdmin 2.0.149.5 SDK** but runs fine on
   **2.0.200.0** — the SDK APIs it uses are stable across that range, so there
   is no need to re-reference a newer SDK package just to deploy. Only bump the
   SDK reference if you start calling APIs introduced after 2.0.149.5.

### Update vs install SQL profiles (resolved)
`Module.json` declares `"UpdateSql": "update.sql"`, which TCAdmin runs when it
**updates** an already-installed module (a fresh install runs `install.sql`).
`update.sql` now exists, mirrors `install.sql`'s idempotent `replace into`
statements, and is in the csproj `PackageMeta` list so it ships in `dist\`. That
means an in-place update re-registers the sitemap page and all four service event
handlers — important for installs first deployed by a build that predated them.
If you add new event handlers or schema later, update **both** `install.sql` and
`update.sql`.

---

## Keeping `dist\` in sync — habit, not magic

`dist\` is intentionally gitignored (it's a build output, not source). There is
no commit that carries it, so the only thing that keeps it current is **running
`build.ps1` after you change code and before you deploy**. Options if you want a
harder guarantee:

- Run `.\build.ps1` as the last step of every change (recommended, simplest).
- Add a Git **pre-commit hook** that warns when any `*.cs`/`*.cshtml` is newer
  than `dist\MySqlModule.dll`, so you can't forget. (Ask and this can be added
  under `.githooks\` with `core.hooksPath`.)
