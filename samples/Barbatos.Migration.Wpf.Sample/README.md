# Barbatos.Migration.Wpf.Sample

### *A WPF playground for upgrade, downgrade, failure and cancellation*

A runnable application built on
[Barbatos.Wpf.Core](https://www.nuget.org/packages/Barbatos.Wpf.Core) and every
Barbatos.Migration package. It does two things at once: it migrates its own data folder behind
the splash screen at startup, the way a real application would, and it gives you a sandbox where
every outcome the engine can produce is one click away.

```powershell
dotnet run --project samples/Barbatos.Migration.Wpf.Sample
```

---

## What you can do

| Button | What it demonstrates |
| --- | --- |
| **Reset to 1.0.0** | Writes a data folder as an installation in use for a while would look: `settings.json`, a legacy `plugins.ini` complete with the user's own comments, an exported `licences.csv`, `documents/`, and `app.db` with three users. |
| **Upgrade → 2.0.0** | Runs all three steps in one protected pass. Watch `documents/` become `workspace/documents/`, `fontSize` move into an `editor` section, `[Plugins]` become `[Extensions]`, `licences.csv` gain two columns where `FullName` was, and `Users.FullName` split into `FirstName`/`LastName`. |
| **Downgrade → 1.0.0** | Runs the same steps backwards, newest first. Only possible because every step here implements a downgrade. |
| **Upgrade, then fail** | The 2.0.0 step changes the database and *then* throws. The file list and the database contents both go back to exactly what they were. |
| **Cancel** | Enabled while a migration runs. The engine restores the snapshot and reports `Canceled` - not `Failed`. |
| **In-place / Side-by-side** | Switches installation model. Under side-by-side, upgrading leaves `1.0.0/` completely untouched and publishes a new `2.0.0/`. |

The **data folder** panel shows the files on disk and the live `Users` table; the **engine log**
panel shows every phase, step and provider as the engine reports it.

---

## What is where

| File | What to read it for |
| --- | --- |
| [`Migrations/`](Migrations) | **One step per file**, each declared with `[MigrationStep("1.1.0", "...")]`. Nothing lists them - `AddStepsFromAssembly()` finds them. |
| [`SampleData.cs`](SampleData.cs) | File names, the shared connection factory, and the 1.0.0 seed data. |
| [`WpfProgram.cs`](WpfProgram.cs) | Host composition: `ConfigureMigration().AddStepsFromAssembly()`. Everything else is defaulted from the host. |
| [`App.xaml.cs`](App.xaml.cs) | The startup path: splash screen, `IMigrationRunner.RunAsync`, and what to do when `CanContinue` is false. |
| [`MainViewModel.cs`](MainViewModel.cs) | The playground. Builds engines with `MigrationEngineBuilder` - the **no-container** API, which is how the framework is consumed from a console tool. |

The two paths through the framework are deliberately both here: the host-integrated one in
`App.xaml.cs`, and the standalone builder in `MainViewModel.cs`. Both discover the same steps
from the same folder.

Adding a step to this sample means adding a file to `Migrations/` and nothing else - no
registration line to forget, and no merge conflict when two people add one at the same time.

---

## The steps

```
Migrations/GroupEditorSettings.cs          1.1.0  JsonMigrationProvider + IniMigrationProvider
Migrations/MoveDocumentsIntoWorkspace.cs   1.2.0  FileSystemMigrationProvider
Migrations/SplitFullName.cs                2.0.0  DatabaseMigrationProvider + CsvMigrationProvider
                                                  + JsonMigrationProvider
```

Step 2.0.0 is the one to read: **one** conceptual change - names are now two fields - applied
everywhere it shows up, in a single step that succeeds or is undone as a unit.

`plugins.ini` is the one to *look at* after upgrading. The section and two keys under it are
renamed, a new key is added, and the user's own comments - including the trailing
`; expanded | collapsed` note - are exactly where they were. That is the format-preserving
document model doing its job; a parse-to-dictionary round trip would have thrown all of it away.

Every one of them implements a downgrade, which is what makes the Downgrade button work. Real
applications are usually reversible for a while and then stop being so - dropping a column
throws away data that cannot be reconstructed. When that happens the step declares itself
forward-only and the engine refuses the downgrade **before touching any data**, or the
application moves to the side-by-side model where downgrading is just running the old build.

Step 2.0.0 pairs a schema change with the setting that goes with it. Both providers belong to
the same step, so they are applied - or undone - together.

---

## Things worth noticing in the code

**`Pooling=False` in the connection string.** Microsoft.Data.Sqlite pools connections, so a
handle can outlive the migration and keep the database file open. On Windows that makes the
engine's snapshot, restore and directory rename all fail - a rollback blocked by the very file
it is restoring. See `SampleMigrations.OpenConnection`.

**`Task.Run` around `engine.RunAsync`.** The engine's directory copying is synchronous I/O.
`MainViewModel` does this itself because it uses the standalone builder; the host-integrated
path gets it from `IMigrationRunner`.

**`<Version>2.0.0</Version>` in the `.csproj`.** That becomes `AppInfo.Version`, which
`Barbatos.Migration.Wpf` uses as `TargetDataVersion` - bumping it is all it takes for a new step
to become due.

**`appsettings.json`.** `BackupRetentionCount`, `AllowDowngrade` and `AllowRunningOnOlderData`
come from the `Barbatos:Migration` section, which overrides the code-based defaults.

---

## Where the sandbox lives

`%TEMP%\Barbatos.Migration.Sample` - the **Open sandbox folder** button takes you there. You will
find:

```
in-place/                    the data folder (in-place model)
in-place/.migration-version  the version stamp
side-by-side/1.0.0/          per-version folders (side-by-side model)
side-by-side/2.0.0/
.migration/                  snapshots, journal and lock
.migration/backup-*          retained pre-migration backups
```

Deleting the whole folder resets everything; **Reset to 1.0.0** does it for you.

The application's *own* data folder - the one migrated at startup - is separate, under
`%LOCALAPPDATA%\Barbatos Labs\{8E4C1F6A-...}\Data`.
