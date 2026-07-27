# Barbatos.Migration

![Barbatos.Migration logo](https://github.com/Barbatos-Labs/Barbatos.Migration/blob/main/build/nuget.png?raw=true)

### *Crash-safe data migration for client applications - upgrade, downgrade, and never lose a byte.*

[![GitHub stars](https://img.shields.io/github/stars/Barbatos-Labs/Barbatos.Migration?style=social)](https://github.com/Barbatos-Labs/Barbatos.Migration/stargazers)
[![License](https://img.shields.io/github/license/Barbatos-Labs/Barbatos.Migration)](https://github.com/Barbatos-Labs/Barbatos.Migration/blob/main/LICENSE.md)

When you ship a new version, the data already sitting on your users' machines has to change
shape with it. That transformation is the one moment an application rewrites data it can never
regenerate - on a machine you cannot see, while the user is free to close the lid at any point.
Barbatos.Migration is built around that fact: a snapshot before anything changes, a journal that
survives the process being killed, a rollback that is a sequence of directory renames rather than
a delete-then-copy, and a result type that makes "the rollback itself failed" impossible to
overlook. It ships as several independent NuGet packages rather than one monolith - install only
the data providers you actually use.

---

## 📖 Documentation Menu

* **[Getting Started](#getting-started)**
  * [Packages](#packages)
  * [Quick Start](#quick-start)
  * [One step per file](#one-step-per-file)
* **[Versions](#versions)**
  * [Data version is not app version](#data-version-is-not-app-version)
  * [Unstamped data: fresh install or legacy install](#unstamped-data-fresh-install-or-legacy-install)
  * [Skipping versions](#skipping-versions)
* **[Installation models](#installation-models)**
  * [In-place](#in-place-single-folder)
  * [Side-by-side](#side-by-side-multi-folder)
* **[Steps and providers](#steps-and-providers)**
  * [Writing a provider](#writing-a-provider)
  * [Progress weights](#progress-weights)
* **[Data providers](#data-providers)**
  * [JSON](#json)
  * [INI](#ini)
  * [CSV](#csv)
  * [File system](#file-system)
  * [Database](#database)
  * [Entity Framework Core](#entity-framework-core)
  * [Database or EF Core?](#database-or-ef-core)
* **[Running a migration](#running-a-migration)**
  * [Reading the result](#reading-the-result)
  * [Progress and cancellation](#progress-and-cancellation)
  * [Asking the user first](#asking-the-user-first)
* **[Safety](#safety)**
  * [What happens when the process is killed](#what-happens-when-the-process-is-killed)
  * [The atomic swap](#the-atomic-swap)
  * [The cross-process lock](#the-cross-process-lock)
  * [Backups and retention](#backups-and-retention)
* **[Hosting](#hosting)**
  * [WPF](#wpf)
  * [Dependency injection](#dependency-injection)
  * [No container at all](#no-container-at-all)
* **[Options reference](#options-reference)**
* **[Samples](#samples)**
* **[API Reference](#api-reference)**
* **[Community](#community)**

---

## Getting Started

### Packages

| Package | What it is |
|---|---|
| **[Barbatos.Migration.Core](https://www.nuget.org/packages/Barbatos.Migration.Core)** [![NuGet](https://img.shields.io/nuget/v/Barbatos.Migration.Core.svg)](https://www.nuget.org/packages/Barbatos.Migration.Core) | The engine, with **zero dependencies**: version planning, journal-based crash recovery, both installation strategies, a cross-process lock, cancellation, rollback and progress. |
| **[Barbatos.Migration.Json](https://www.nuget.org/packages/Barbatos.Migration.Json)** [![NuGet](https://img.shields.io/nuget/v/Barbatos.Migration.Json.svg)](https://www.nuget.org/packages/Barbatos.Migration.Json) | Settings and document files, through the `System.Text.Json` DOM, so keys the migration has never heard of survive. |
| **[Barbatos.Migration.Ini](https://www.nuget.org/packages/Barbatos.Migration.Ini)** [![NuGet](https://img.shields.io/nuget/v/Barbatos.Migration.Ini.svg)](https://www.nuget.org/packages/Barbatos.Migration.Ini) | INI files, through a format-preserving document model that keeps the user's comments, key order and spacing. |
| **[Barbatos.Migration.Csv](https://www.nuget.org/packages/Barbatos.Migration.Csv)** [![NuGet](https://img.shields.io/nuget/v/Barbatos.Migration.Csv.svg)](https://www.nuget.org/packages/Barbatos.Migration.Csv) | Delimited data files, migrated the way a database table is: add, rename, split, merge and drop columns. |
| **[Barbatos.Migration.FileSystem](https://www.nuget.org/packages/Barbatos.Migration.FileSystem)** [![NuGet](https://img.shields.io/nuget/v/Barbatos.Migration.FileSystem.svg)](https://www.nuget.org/packages/Barbatos.Migration.FileSystem) | Moving, renaming, deleting and creating files and directories - reversibly. |
| **[Barbatos.Migration.Database](https://www.nuget.org/packages/Barbatos.Migration.Database)** [![NuGet](https://img.shields.io/nuget/v/Barbatos.Migration.Database.svg)](https://www.nuget.org/packages/Barbatos.Migration.Database) | SQL against **any** ADO.NET provider, with per-dialect foreign keys, integrity checks and file-handle release. No driver dependency of its own. |
| **[Barbatos.Migration.EntityFrameworkCore](https://www.nuget.org/packages/Barbatos.Migration.EntityFrameworkCore)** [![NuGet](https://img.shields.io/nuget/v/Barbatos.Migration.EntityFrameworkCore.svg)](https://www.nuget.org/packages/Barbatos.Migration.EntityFrameworkCore) | EF Core's own migrations, plus data transformations written as LINQ, inside the Barbatos snapshot. |
| **[Barbatos.Migration.DependencyInjection](https://www.nuget.org/packages/Barbatos.Migration.DependencyInjection)** [![NuGet](https://img.shields.io/nuget/v/Barbatos.Migration.DependencyInjection.svg)](https://www.nuget.org/packages/Barbatos.Migration.DependencyInjection) | `IServiceCollection` wiring and an `ILogger` adapter. |
| **[Barbatos.Migration.Wpf](https://www.nuget.org/packages/Barbatos.Migration.Wpf)** [![NuGet](https://img.shields.io/nuget/v/Barbatos.Migration.Wpf.svg)](https://www.nuget.org/packages/Barbatos.Migration.Wpf) | [Barbatos.Wpf.Core](https://www.nuget.org/packages/Barbatos.Wpf.Core) host integration, off-UI-thread execution, splash-screen view model. |

All packages share the `Barbatos.Migration` root C# namespace and target `net8.0`, `net9.0` and
`net10.0` (`Barbatos.Migration.Wpf`: the matching `-windows` flavours). Every satellite depends
only on `Barbatos.Migration.Core` — except `EntityFrameworkCore`, which also builds on
`Database` for its dialects.

```powershell
dotnet add package Barbatos.Migration.Core
dotnet add package Barbatos.Migration.Json
dotnet add package Barbatos.Migration.Ini
dotnet add package Barbatos.Migration.Csv
dotnet add package Barbatos.Migration.FileSystem
dotnet add package Barbatos.Migration.Database
dotnet add package Barbatos.Migration.EntityFrameworkCore
dotnet add package Barbatos.Migration.DependencyInjection
dotnet add package Barbatos.Migration.Wpf
```

### Quick Start

Declare what each version changed, once:

```csharp
var builder = WpfApp.CreateBuilder();

builder.ConfigureMigration()
       .AddStep("1.1.0", "Group the editor settings",
           new JsonMigrationProvider("settings.json",
               up:   json => json.MoveIntoSection("fontSize", "editor"),
               down: json => json.MoveOutOfSection("editor", "fontSize")))
       .AddStep("2.0.0", "Split the full name into first and last",
           DatabaseMigrationProvider.ForFile("app.db",
               path => new SqliteConnection($"Data Source={path};Pooling=False"),
               up: [
                   "ALTER TABLE Users ADD COLUMN FirstName TEXT;",
                   "UPDATE Users SET FirstName = substr(FullName, 1, instr(FullName, ' ') - 1);",
               ],
               dialect: DatabaseDialects.Sqlite));
```

Run it during startup, before anything opens the data:

```csharp
var progress = Services.GetRequiredService<MigrationProgressViewModel>();
var result = await Services.GetRequiredService<IMigrationRunner>()
    .RunAsync(progress, progress.CancellationToken);

await CloseSplashScreenAsync();

if (!result.CanContinue)
{
    // The migration did not finish. result.Outcome says why, and the data is back
    // exactly as it was - unless Outcome is RollbackFailed, in which case
    // result.BackupDirectory holds the last intact copy.
}
```

### One step per file

A registration chain is fine for two-line steps and unreadable by the time one of them runs to
two hundred lines. Declare the step on the class instead, and let it be found:

```csharp
// Migrations/RebuildSearchIndex.cs — one file, one step, however long it needs to be.
[MigrationStep("2.0.0", "Rebuild the search index")]
public sealed class RebuildSearchIndex : CodeMigrationStep
{
    public override double Weight => 8.0;

    public override async Task UpAsync(
        IMigrationContext context,
        IProgress<MigrationProgress>? progress,
        CancellationToken cancellationToken)
    {
        string[] documents = Directory.GetFiles(context.GetWorkingPath("documents"));

        for (int i = 0; i < documents.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await IndexAsync(documents[i], cancellationToken);
            progress?.Report(new MigrationProgress(i * 100.0 / documents.Length, $"Indexed {i + 1}/{documents.Length}"));
        }
    }
}
```

```csharp
builder.ConfigureMigration().AddStepsFromAssembly();
```

`CodeMigrationStep` makes the step its own single provider — the common case when the step *is*
one piece of logic. When it composes several providers, derive from `MigrationStepBase` and
override `CreateProviders()` instead:

```csharp
[MigrationStep("2.0.0", "Split the full name into first and last")]
public sealed class SplitUserName : MigrationStepBase
{
    protected override IEnumerable<IMigrationProvider> CreateProviders()
    {
        yield return DatabaseMigrationProvider.ForFile("app.db", OpenConnection, up: [...], down: [...]);
        yield return new CsvMigrationProvider("licences.csv", up: ..., down: ...);
    }
}
```

Notes on scanning:

- **Order does not matter.** The engine sorts by version, and the scanner sorts its results too,
  because reflection's type order is not guaranteed stable across builds.
- Duplicate versions or ids are rejected when the engine is built, not at migration time.
- Discovered steps need a public parameterless constructor. Register through
  `Barbatos.Migration.DependencyInjection` instead when they need constructor injection — there
  each type is registered with the container rather than constructed by the scanner.
- `Id` defaults to the class name, and the id is what the applied-steps ledger records, so
  **renaming a shipped step class changes its identity**. Set `[MigrationStep(..., Id = "...")]`
  explicitly if you expect to reorganise.
- Scanning uses reflection, so the methods are annotated `[RequiresUnreferencedCode]`. Publish
  trimmed? Use `AddStep` per step instead.

---

## Versions

### Data version is not app version

The **app version** changes every time you build. The **data version** describes the shape of
the data on disk, and only moves when a step moves it. An app can ship 1.4.1, 1.4.2 and 1.4.3
without any data change at all.

That is why the data version is stored rather than derived. `FileDataVersionStore` writes a
`.migration-version` file **inside** the data directory it describes:

```ini
# Barbatos.Migration data version stamp - do not edit by hand.
version=2.0.0
steps=1.1.0|1.2.0|2.0.0
updatedUtc=2026-07-27T09:28:41.1830000+00:00
```

Keeping it inside is deliberate: a directory that gets copied, cloned or restored carries its
version with it. That is what lets the side-by-side strategy clone a folder and have the clone
report the right starting version, and what makes restoring a snapshot restore the version too.

### Unstamped data: fresh install or legacy install

`Read()` returns `null` in exactly two situations, and telling them apart matters:

| Situation | What should happen |
| --- | --- |
| A genuinely fresh install | Nothing to migrate — the app creates its data at the current shape. |
| An install that predates this framework | Real data in an old shape, which every step still has to be applied to. |

`MigrationOptions.InitialDataVersion` decides, defaulting to `0.0.0.0` ("replay every step").
On WPF, `Barbatos.Migration.Wpf` works it out from `IVersionTracking`'s app-version history —
see [WPF](#wpf).

### Skipping versions

A user who installed 1.0, ignored 1.1, 1.2 and 1.3, and only upgrades when 2.0 ships gets all
four steps aggregated into one plan:

```csharp
Console.WriteLine(engine.CreatePlan().Describe());
// Upgrade 1.0.0 -> 2.0.0 (4 steps):
//   1.1.0 - Group the editor settings [JSON (settings.json)]
//   1.2.0 - Move documents into a workspace folder [Reorganise the data folder]
//   1.3.0 - Add the tags index [Tags]
//   2.0.0 - Split the full name [SQLite (app.db), CSV (licences.csv)]
```

All four run inside a **single** snapshot, so a failure at 1.3 returns the data to 1.0 rather
than stranding it at 1.2. `CreatePlan()` is pure computation — nothing is written — so it is
safe to call from a settings screen or a diagnostics command.

---

## Installation models

### In-place (single folder)

Every version shares one data folder. `InPlaceStrategy` snapshots it, lets the providers rewrite
the real thing, and swaps the snapshot back if anything goes wrong.

- **Bi-directional**, when every step involved implements `DownAsync`.
- Costs 2× the data size while a migration runs.
- Right for mobile apps, lightweight desktop apps and SaaS clients.

### Side-by-side (multi folder)

Every version owns a directory under a shared root:

```
AppData/MyApp/1.0.0/
AppData/MyApp/2.0.0/
AppData/MyApp/.migration/     <- journal, lock, staging
```

`SideBySideStrategy` clones the newest existing version, migrates the clone, and publishes it
with a single rename at commit time. Until that rename, the target version has no directory at
all — which is exactly what a version that is not ready yet should look like to the next launch.

- **Forward-only** by nature: downgrading means launching the older build, which finds its own
  directory exactly as it left it.
- A failed migration cannot damage anything: the only directory ever written to is the staging
  clone, which simply gets deleted.
- Costs 2× while migrating plus 1× for every version kept installed.
- Right for professional software — IDEs, CAD, graphics, anything with a real data set.

> Shipping 2.0 with **no** schema change still clones 1.0's data into 2.0's folder. An empty
> plan does not mean an empty data directory.

`MigrationOptions.DataDirectory` means the data directory itself under the in-place model, and
the **parent** that holds the per-version folders under side-by-side.

---

## Steps and providers

A **step** is one version bump. It groups the **providers** that have to move together:

```csharp
new MigrationStep(
    new Version(2, 0, 0),
    "Split the full name into first and last",
    databaseProvider,      // the schema change
    csvProvider,           // the same change on the exported table
    settingsProvider);     // and the setting that goes with it
```

Everything in a step is applied together: the run either reaches the step's version with every
provider done, or the whole run is rolled back. Providers inside a step run **sequentially,
never in parallel** — two of them rewriting the same directory at once is precisely the
corruption this framework exists to prevent.

### Writing a provider

```csharp
public sealed class RebuildIndexProvider : MigrationProvider
{
    public override string Name => "Search index";

    public override double Weight => 6.0;

    public override async Task UpAsync(
        IMigrationContext context,
        IProgress<MigrationProgress>? progress,
        CancellationToken cancellationToken)
    {
        // ... read and write under context.WorkingDirectory only
    }
}
```

Four rules:

1. **Read and write only under `context.WorkingDirectory`.** Never touch `OriginalDirectory` or
   `BackupDirectory`. Under the side-by-side model those are three different places, and
   hard-coding the data path instead of using `WorkingDirectory` is the easiest way to damage
   the version the user can still fall back to.
2. **Check the cancellation token inside every loop.** Cancellation is cooperative; a provider
   that ignores the token makes the Cancel button a lie.
3. **Be safe to run twice.** A crash after your provider finished but before the run committed
   restores the snapshot and re-runs the whole step.
4. You do **not** need to be atomic or reversible yourself — the installation strategy restores
   the whole data directory if any provider throws.

Implement `DownAsync` and return `true` from `CanDown` to make a step reversible. The engine
checks `CanDown` across the whole plan *before* touching any data, so an impossible downgrade
fails immediately instead of halfway through.

### Progress weights

`IMigrationProvider.Weight` is a provider's share of its step's progress, relative to its
siblings. A provider that rewrites a million rows should declare a much larger weight than one
that renames a settings key — otherwise the bar sits at 90% for most of the run. The database
providers default to `4.0`, CSV to `2.0`, everything else to `1.0`.

---

## Data providers

All the file-based providers share three properties, and the first is the one that matters most.

**They preserve what they were not asked to change.** The obvious way to migrate a settings file
is to parse it into a dictionary, edit that, and write the dictionary back. For *reading* that is
right; for *migrating* it is wrong. The file that comes back has lost every comment the user
wrote, every blank line that grouped related settings, the original key order, and whatever
spacing convention it had. A user who opens their carefully annotated `config.ini` after an
update and finds it reduced to a flat alphabetical list has, from their point of view, had their
file damaged by the update.

**Writes are atomic.** Temporary file, flushed to disk, renamed over the original — a settings
file truncated by a power cut is a bricked application. The original encoding, including a BOM,
is carried over.

**Every operation is a no-op when its target is absent.** A step that half-applied before a crash
is re-run from the restored snapshot, so a `RenameKey` whose source key is already renamed must
not throw.

### JSON

```csharp
new JsonMigrationProvider(
    "settings.json",
    up:   json => json.RenameProperty("theme", "appearance")
                      .MoveIntoSection("fontSize", "editor")
                      .SetDefault("language", "vi"),
    down: json => json.MoveOutOfSection("editor", "fontSize")
                      .RenameProperty("appearance", "theme")
                      .RemoveProperty("language"));
```

Works on the `JsonNode` DOM rather than a typed model, because the old shape of the file no
longer *has* a C# type — that type is exactly what the new version deleted. A key written by a
plugin, or by a newer build the user downgraded from, survives untouched.

Helpers: `RenameProperty`, `RemoveProperty`, `Set`, `SetDefault`, `MoveIntoSection`,
`MoveOutOfSection`, `ConvertProperty`, `Section`, `Root`, `ForEachInArray`.

Real settings files nest and hold arrays, so those last three matter more than they look. Chain
`Section` to go down, `Root` to come back up, and `ForEachInArray` for the very common "each
saved entry gains a field":

```csharp
json.MoveIntoSection("fontSize", "editor")
    .Section("editor").Section("minimap").Set("side", "left").Root()
    .ForEachInArray("recentFiles", entry => entry
        .RenameProperty("path", "fullPath")
        .SetDefault("openedAt", DateTimeOffset.UtcNow.ToString("O")));
```

Non-ASCII text is written back **unescaped**. `System.Text.Json` escapes it by default, which
would turn a user's `"Xin chào {0}"` into `"Xin chào {0}"` — still valid JSON, still the
same value, and a file they can no longer read or hand-edit because of an update they did not
ask for.

### INI

```csharp
new IniMigrationProvider(
    "settings.ini",
    up:   ini => ini.RenameSection("Plugins", "Extensions")
                    .RenameKey("Extensions", "ribbonState", "ribbon")
                    .SetDefault("Extensions", "autoUpdate", "true"),
    down: ini => ini.RemoveKey("Extensions", "autoUpdate")
                    .RenameKey("Extensions", "ribbon", "ribbonState")
                    .RenameSection("Extensions", "Plugins"));
```

Given:

```ini
; Cau hinh plugin - nguoi dung tu sua tay
; Dung xoa cac dong ghi chu nay!

[Plugins]
ribbonState = expanded    ; expanded | collapsed
recentLimit = 20
```

you get:

```ini
; Cau hinh plugin - nguoi dung tu sua tay
; Dung xoa cac dong ghi chu nay!

[Extensions]
ribbon = expanded    ; expanded | collapsed
recentLimit = 20
autoUpdate = true
```

`IniDocument` keeps the file as the list of lines it actually is. Each line is classified once —
blank, comment, section header, key/value, or *something else* — and anything not asked to change
is written back byte for byte. A new key lands with its neighbours, not at the bottom of the file.

Three details that only show up on real files:

- **A `;` or `#` starts a comment only when whitespace separates it from the value.** Otherwise
  `ConnectionString=Server=localhost;Db=app` would read as `Server=localhost`, and writing that
  back destroys the rest of the line.
- **Values are quoted only when writing them bare would change what reading them back
  produces** — so that connection string never gains quotes it did not have.
- **`RemoveSection` hands a trailing comment block to the next section.** A comment sitting
  directly above a header documents the section below it, not the one being deleted.

Colons are ordinary characters: `[Messages:Error]` and `Timeout:Max=30` both work.

INI is the format applications that predate a migration framework are most likely to keep their
settings in, which makes it the one a *first* migration most often has to deal with.

### CSV

```csharp
new CsvMigrationProvider(
    "licences.csv",
    up:   csv => csv.SplitColumn("FullName", name => name.Split(' ', 2), "FirstName", "LastName")
                    .AddColumn("Archived", "false"),
    down: csv => csv.RemoveColumn("Archived")
                    .MergeColumns("FullName", parts => string.Join(" ", parts), "FirstName", "LastName"));
```

CSV files in a data folder are tables of real records — exported data, licence lists, index
files — so migrating one is the same work as migrating a database table. Columns:
`AddColumn`, `RemoveColumn`, `RenameColumn`, `MoveColumn`, `TransformColumn`, `SplitColumn`,
`MergeColumns`. Rows: `AddRow`, `RemoveRows`, `UpdateRows`.

The delimiter (`,` `;` tab `|`) is detected, the quoting style and line endings are preserved,
and ragged rows read as empty rather than throwing. For a file big enough to need to stay
cancellable, take the progress reporter and token:

```csharp
new CsvMigrationProvider(
    "history.csv",
    up: (csv, progress, ct) => csv.UpdateRows(
        row => row["Timestamp"] = DateTimeOffset.Parse(row["Timestamp"]).ToString("O"),
        progress,
        ct));
```

A **malformed file is refused**, with the line number. An unterminated quote makes every
following line part of one enormous field; a lenient parser would accept that, and the migration
would then rewrite the file with the user's data silently destroyed.

> `CreateIfMissing` defaults to `false` here, unlike JSON and INI. An absent settings file
> usually means "not configured yet"; an absent data file usually means "no records yet", and
> inventing one would be presumptuous.

### File system

```csharp
new FileSystemMigrationProvider("Reorganise the data folder", operations => operations
    .EnsureDirectory("assets")
    .MoveDirectory("images", "assets/images")
    .RenameFile("data.sqlite", "app.db")
    .DeleteFile("thumbnail.cache"));
```

Operations are declared rather than written so each states its own inverse, and `DownAsync`
walks the list backwards — undoing "create the folder, then move a file into it" has to move the
file back out before the folder can go.

| Operation | Inverse |
| --- | --- |
| `MoveFile` / `RenameFile`, `MoveDirectory` / `RenameDirectory` | moves it back |
| `CopyFile` | deletes the copy |
| `EnsureDirectory` | removes it, but only if the undo left it empty |
| `WriteText` | deletes the file |
| `DeleteFile`, `DeleteDirectory` | **none** — the provider becomes forward-only |

Relative paths only; anything resolving outside the working directory is rejected.

### Database

```csharp
DatabaseMigrationProvider.ForFile(
    "app.db",
    path => new SqliteConnection($"Data Source={path};Pooling=False"),
    up:
    [
        "ALTER TABLE Users RENAME TO Users_old;",
        "CREATE TABLE Users (Id INTEGER PRIMARY KEY, Name TEXT NOT NULL, Email TEXT);",
        "INSERT INTO Users (Id, Name, Email) SELECT Id, Name, Email FROM Users_old;",
        "DROP TABLE Users_old;",
    ],
    dialect: DatabaseDialects.Sqlite);
```

No database driver dependency of its own — it works with a `DbConnection` the application hands
it, so one package covers SQLite, SQL Server, PostgreSQL, MySQL and Oracle. Every statement runs
in one transaction, and a failure names its position (`Statement 3 of 4 failed on 'app.db': ...`)
so you do not have to bisect the list.

What differs between engines lives in an `IDatabaseDialect`:

| Dialect | Transactional DDL | Foreign keys suspended by | Integrity check |
| --- | --- | --- | --- |
| `Sqlite` | yes | `PRAGMA foreign_keys = OFF` | `PRAGMA foreign_key_check` |
| `SqlServer` | yes | `NOCHECK CONSTRAINT ALL` | `WITH CHECK CHECK CONSTRAINT ALL` on the way out |
| `PostgreSql` | yes | *(deferred constraints)* | `SET CONSTRAINTS ALL IMMEDIATE` |
| `MySql` | **no** | `SET FOREIGN_KEY_CHECKS = 0` | — |
| `Generic` | assumed yes | — | — |

MySQL commits DDL implicitly, so a step that fails on its fourth statement leaves the first three
applied. The provider logs a warning rather than quietly promising atomicity it cannot deliver.

`SqliteDialect` is the one that earns its keep, and it is about the *file*, not the SQL. It
checkpoints the write-ahead log (a `-wal` sidecar the snapshot would otherwise miss) and clears
the driver's connection pools, because **a rollback blocked by the very database file it is
restoring is the worst failure this framework has**.

### Entity Framework Core

```csharp
new EfCoreMigrationsProvider<AppDbContext>(
    context => new AppDbContext(
        new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={context.GetWorkingPath("app.db")}")
            .Options))
{
    Dialect = DatabaseDialects.Sqlite,   // required for a file-backed database
};
```

EF Core's `Migrate()` is very good at changing a schema and has no notion of putting anything
back:

| | EF Core migrations alone | Wrapped in a Barbatos step |
| --- | --- | --- |
| Fails on the fourth of six migrations | the first three stay applied | the whole data directory is restored |
| Process killed mid-migration | nobody notices; next launch is undefined | the journal is found and the snapshot restored |
| Settings files and asset folders that must move with the schema | not EF Core's world | ordered into the same plan |
| Progress reporting | none | per-migration, into your splash screen |

`DbContextMigrationProvider<TContext>` covers the other half — the data transformations EF Core
migrations are bad at, written as ordinary LINQ against your entities:

```csharp
new DbContextMigrationProvider<AppDbContext>(
    "Split Users.FullName",
    contextFactory,
    up: async (db, progress, ct) =>
    {
        List<User> users = await db.Users.Where(u => u.FirstName == null).ToListAsync(ct);
        // ... then db.SaveChangesAsync(ct)
    });
```

`EfCoreMigrationsProvider` is forward-only. Use `EfCoreDowngradeMigrationsProvider<TContext>`
with both target migrations named explicitly when you need a reversible schema step.

> **Set `Dialect` for a file-backed database.** Disposing a `DbContext` returns its connection to
> the driver's pool — it does not close the file. A handle still open on the `.db` makes the
> engine's snapshot, restore and directory rename fail.

### Database or EF Core?

They are not alternatives. Use the one that matches how the application already talks to its
data, and the other when it needs the other half:

| | `Database` | `EntityFrameworkCore` |
| --- | --- | --- |
| **Dependency** | none — you bring your driver | `Microsoft.EntityFrameworkCore.Relational` and a provider |
| **You write** | SQL statements | your existing EF migrations, and LINQ over entities |
| **Right when** | the app uses raw ADO.NET, or has no ORM at all | the app already uses EF Core and has migration classes |
| **Per-statement error position** | yes | EF Core reports its own |
| **Dialect handling** | yes | yes — it reuses the same `IDatabaseDialect` |

An app with no ORM should not have to take EF Core and a provider package just to run four
`ALTER TABLE` statements; an app that already has EF migrations should not have to hand-write
them again as strings.

---

## Running a migration

### Reading the result

`RunAsync` never throws for an ordinary failure. A step that blows up produces
`MigrationOutcome.Failed` with the data restored and the exception in `MigrationResult.Error`.

| `Outcome` | The data | `CanContinue` |
| --- | --- | --- |
| `UpToDate` | unchanged, already at the target | `true` |
| `Succeeded` | migrated | `true` |
| `Canceled` | restored to its pre-migration state | `AllowRunningOnOlderData` |
| `Deferred` | untouched — the user postponed | `AllowRunningOnOlderData` |
| `Failed` | restored to its pre-migration state | `AllowRunningOnOlderData` |
| `RollbackFailed` | **may be inconsistent** | `false`, always |
| `Blocked` | untouched — lock held, data newer than the app, invalid plan, disk full | `false` |

`IsSuccess` and `CanContinue` answer different questions. After a clean rollback the data is
intact and self-consistent, but it is still at the old version — whether the app can run against
it is the app's decision, declared through `MigrationOptions.AllowRunningOnOlderData`.

`RollbackFailed` is the one outcome where user data may be damaged. Surface it loudly, point the
user at `result.BackupDirectory`, and do not start normally.

### Progress and cancellation

Overall progress is split into fixed slices, and never moves backwards:

| Phase | Range | Cancellable |
| --- | --- | --- |
| `Planning` | 0 | — |
| `Recovering` | 0–100 (standalone) | no |
| `Preparing` | 0–20 | **yes** |
| `Migrating` | 20–97, split by `Weight` | **yes** |
| `Committing` | 97–100 | no |
| `RollingBack` | 0–100 (standalone) | no |
| `Completed` | 100 | — |

Reports arrive **synchronously**, on whichever thread the work is on. That is deliberate:
`Progress<T>` posts through the captured `SynchronizationContext`, which means reports can arrive
out of order and even after the migration has finished. Marshalling to the UI thread is the
caller's job, done once at the edge — on WPF, `IMigrationRunner` does it with the Barbatos
dispatcher, throttled.

Cancellation is honoured during `Preparing` and `Migrating`, and ignored from `Committing`
onwards — stopping there would cost more than finishing.

### Asking the user first

```csharp
options.TriggerMode = UpdateTriggerMode.ManualInteractive;
```

The engine then calls `IUpdatePromptService.ConfirmAsync` before anything is backed up, so
declining costs nothing. `MigrationPromptContext` carries the plan, the estimated data size (so
the prompt can say "this will take a few minutes" when it actually will), and `CanDefer`.

> `CanDefer` is `false` when the app has declared it cannot run against old data. The dialog must
> then say the choice is between migrating and closing the app, and must **not** offer a "Remind
> me later" that leads nowhere.

This is about the **data** migration, not about downloading a new build. Deferring a download is
always safe; deferring a data migration means running new code against an old schema.

---

## Safety

### What happens when the process is killed

A `try`/`catch` only protects against exceptions. It does nothing about Task Manager, a flat
battery, or Windows restarting for an update — and those are ordinary events during the minutes
a migration occupies.

`IMigrationJournal` is written before the first byte changes and cleared only once the run
commits, in the backup root rather than in the data directory, so it survives the very operations
that replace that directory. Its presence at startup means the previous attempt did not finish:

```ini
sessionId=20260727143012481
model=InPlaceSingleFolder
fromVersion=1.0.0
toVersion=2.0.0
backupDirectory=...\.migration\snapshot-20260727143012481
phase=Migrating
lastCompletedStepId=1.5.0
```

`phase` decides the recovery:

- `Preparing` — the snapshot is incomplete, but no provider had run yet, so the data directory is
  still correct. The partial snapshot is discarded and nothing is restored. (Restoring here would
  be actively wrong.)
- `Migrating` / `Committing` / `RollingBack` — the snapshot is complete, so it is swapped back in
  and the migration is planned again from scratch.

### The atomic swap

The obvious restore — delete the data directory, copy the snapshot back — has a window, lasting
the whole length of the copy, in which the user has neither their old data nor their new data. On
a multi-gigabyte data set that window is minutes long.

Instead the restore is three renames:

```
delete  discard
rename  data      -> discard      (atomic within a volume)
rename  snapshot  -> data         (atomic within a volume)
delete  discard
```

Every state a crash can leave behind is recoverable: either `data` still exists (nothing
happened), or `discard` holds the previous contents and can be renamed back. If the second rename
throws, the original is put back before the exception propagates.

### The cross-process lock

Two copies of the app started from a double-clicked shortcut, or an app racing its own updater,
will both find the same out-of-date data. Single-instance enforcement at the application level is
not enough — an installer is a different executable entirely.

`FileMigrationLock` opens a lock file with `FileShare.None` and `DeleteOnClose`, so the operating
system releases it even if the process is killed. There is no stale-lock timeout to guess at. A
second process gets `MigrationOutcome.Blocked`.

### Backups and retention

`BackupRetentionCount` (default `1`) keeps that many successful-migration snapshots, so a user
who only notices the damage tomorrow can still get their data back. Snapshots left behind by a
**failed rollback** are never pruned.

Before the snapshot starts, the engine checks there is `RequiredFreeSpaceFactor` × the data size
free (default `1.2`). Running out of disk halfway through a copy leaves the engine trying to roll
back on a full volume — the one situation where rollback itself is likely to fail.

`PathGuard` rejects a backup directory that overlaps the data it protects, a drive root, or a
well-known system folder, when the options are validated at construction time.

---

## Hosting

### WPF

```csharp
builder.ConfigureMigration(options => options.BackupRetentionCount = 2)
       .AddStepsFromAssembly();
```

| Option | Source |
| --- | --- |
| `DataDirectory` | `IFileSystem.AppDataDirectory` — the publisher/app-GUID folder the rest of Barbatos.Wpf already writes to |
| `TargetDataVersion` | `IAppInfo.Version` — shipping a new build is all it takes for its steps to become due |
| `InitialDataVersion` | `IVersionTracking.VersionHistory` — see below |
| `Logger` | the container's `ILogger` |

**Legacy installs and `IVersionTracking`.** `IVersionTracking` has been recording which app
versions actually ran on this machine since before any of this existed. The newest entry older
than the current build is the version that last wrote the data:

```csharp
foreach (string entry in versionTracking.VersionHistory)
{
    if (!Version.TryParse(entry, out Version? parsed) || parsed >= current)
        continue;

    if (newestOlder == null || parsed > newestOlder)
        newestOlder = parsed;
}

return newestOlder ?? current;   // no older build ever ran here: a fresh install
```

Reading the *history* rather than `PreviousVersion` is what makes this survive a retry: by the
time a migration runs, version tracking has already recorded the new build, so if the first
attempt is cancelled and the user relaunches, `PreviousVersion` has *become* the new version and
would claim there is nothing to migrate.

`IMigrationRunner` runs the engine on a thread-pool thread — copying a data directory is
synchronous, disk-bound work, and on the UI thread it would freeze the splash screen solid.
Progress comes back through `IDispatcher`, throttled to one report per 50 ms or 0.5%, with
terminal reports always let through.

`MigrationProgressViewModel` is itself an `IProgress<MigrationProgress>`, ready to bind:
`Percentage`, `Status`, `StepDescription`, `IsIndeterminate`, `CanCancel`, `CancelCommand`,
`CancellationToken`. `CanCancel` goes false once the engine reaches `Committing`.

`AskBeforeMigrating()` switches to `ManualInteractive` and registers a built-in message-box
prompt.

### Dependency injection

```csharp
services.AddBarbatosMigration(options =>
        {
            options.DataDirectory = dataDirectory;
            options.TargetDataVersion = new Version(2, 0, 0);
        })
        .AddStepsFromAssembly()
        .AddStep<SplitUserTableStep>()
        .UsePrompt<MyUpdateDialog>();
```

Steps are registered under `IMigrationStep` and collected with `GetServices`, so they can be
spread across feature folders and still end up in one ordered plan — and a step registered this
way can take constructor dependencies. Options bind from the `Barbatos:Migration` configuration
section. The engine's log is adapted onto `ILogger`.

**Nothing runs for you.** A migration has to happen at a point the application chooses, before
anything opens the data:

```csharp
MigrationResult result = await serviceProvider
    .GetRequiredService<MigrationEngine>()
    .RunAsync(progress, cancellationToken);
```

### No container at all

```csharp
MigrationEngine engine = new MigrationEngineBuilder()
    .UseDataDirectory(dataDirectory)
    .UseInPlaceModel()
    .TargetVersion("2.0.0")
    .LogTo((level, message, ex) => Console.WriteLine($"[{level}] {message}"))
    .AddStepsFromAssembly()
    .Build();

MigrationResult result = await engine.RunAsync(progress, cancellationToken);
```

---

## Options reference

| Option | Default | What it does |
| --- | --- | --- |
| `DataDirectory` | *(required)* | The data directory, or the version root under side-by-side. |
| `BackupRootDirectory` | `.migration` beside the data | Snapshots, journal and lock. Never inside the data directory. |
| `Model` | `InPlaceSingleFolder` | Which installation strategy runs. |
| `TargetDataVersion` | `1.0.0.0` | The version this build needs. |
| `InitialDataVersion` | `0.0.0.0` | What unstamped data is assumed to be. |
| `TriggerMode` | `SilentAutoUpdate` | Whether the user is asked first. |
| `BackupRetentionCount` | `1` | How many successful-migration snapshots to keep. |
| `RequiredFreeSpaceFactor` | `1.2` | Free space required before snapshotting, as a multiple of the data size. |
| `SkipFreeSpaceCheck` | `false` | For network shares and container volumes where the figure is meaningless. |
| `AllowRunningOnOlderData` | `false` | Drives `CanContinue` after a cancel, deferral or rollback. |
| `AllowDowngrade` | `false` | Whether newer data may be migrated backwards to match this build. |
| `VersionDirectoryName` | `Major.Minor.Build` | Names the per-version folders under side-by-side. |
| `DataVersionStoreFactory` | `FileDataVersionStore` | Where the data version lives. |
| `Logger` | `NullMigrationLogger` | Where the engine logs. |

Everything binds from the `Barbatos:Migration` configuration section when configuration is
available.

---

## Samples

[`samples/Barbatos.Migration.Wpf.Sample`](https://github.com/Barbatos-Labs/Barbatos.Migration/blob/main/samples/Barbatos.Migration.Wpf.Sample/README.md)
is a WPF playground: seed a 1.0.0 data folder, upgrade it to 2.0.0, downgrade it back, make a
step fail on purpose, cancel one mid-flight, and switch between the two installation models —
each with the file list, the database contents and the engine log side by side.

```powershell
dotnet run --project samples/Barbatos.Migration.Wpf.Sample
```

## Repository layout

- `src/` — the libraries, one folder per package.
- `samples/` — runnable sample applications.
- `tests/` — the unit tests, one project per package that has behaviour worth pinning down.
- `docs/` — [the architecture and the reasoning behind it](https://github.com/Barbatos-Labs/Barbatos.Migration/blob/main/docs/DESIGN.md).
- `build/` — the shared package icon.
- `.github/` — CI (build, test, pack on every push and pull request), the release workflow that
  publishes to NuGet.org, and Dependabot.

```powershell
dotnet test Barbatos.Migration.slnx
```

## API Reference

Every public type and member is documented in
**[API-REFERENCE.md](https://github.com/Barbatos-Labs/Barbatos.Migration/blob/main/API-REFERENCE.md)**.

---

## Community

### Maintainers

- Pham The Hung ([@StHung](https://github.com/StHung))

### Support

For support, please open a [GitHub issue](https://github.com/Barbatos-Labs/Barbatos.Migration/issues/new). We welcome bug reports, feature requests, and questions.

### License

This project is licensed under the terms of the **MIT** open source license. Please refer to the [LICENSE](https://github.com/Barbatos-Labs/Barbatos.Migration/blob/main/LICENSE.md) file for the full terms.

You can use it in private and commercial projects. Keep in mind that you must include a copy of the license in your project.
