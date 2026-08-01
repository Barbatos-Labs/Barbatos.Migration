# Barbatos.Migration API Reference

This document provides a comprehensive reference for the Barbatos.Migration libraries, modeled after the official .NET API documentation.

## Namespaces

| Namespace | Description |
|-----------|-------------|
| **[`Barbatos.Migration`](#barbatosmigration-namespace)** | The engine, the contracts every provider implements, the plan, the result, and the crash-safety primitives. |
| **[`Barbatos.Migration.Strategies`](#barbatosmigrationstrategies-namespace)** | The two built-in installation strategies. |
| **[`Barbatos.Migration.Json`](#barbatosmigrationjson-namespace)** | Transforms JSON settings and document files through the `System.Text.Json` DOM. |
| **[`Barbatos.Migration.Ini`](#barbatosmigrationini-namespace)** | Transforms INI files through a format-preserving document model. |
| **[`Barbatos.Migration.Csv`](#barbatosmigrationcsv-namespace)** | Transforms delimited data files. |
| **[`Barbatos.Migration.FileSystem`](#barbatosmigrationfilesystem-namespace)** | Restructures the data folder with reversible operations. |
| **[`Barbatos.Migration.Database`](#barbatosmigrationdatabase-namespace)** | Runs SQL against any ADO.NET provider, with per-engine dialects. |
| **[`Barbatos.Migration.EntityFrameworkCore`](#barbatosmigrationentityframeworkcore-namespace)** | Runs EF Core migrations and `DbContext` data transformations inside the engine's snapshot. |
| **[`Barbatos.Migration.DependencyInjection`](#barbatosmigrationdependencyinjection-namespace)** | `IServiceCollection` registration and the `ILogger` adapter. |
| **[`Barbatos.Migration.Wpf`](#barbatosmigrationwpf-namespace)** | Integration with the Barbatos.Wpf application host. |

---

## `Barbatos.Migration` Namespace

Contains the migration engine and everything a provider or host needs to talk to it.

### Classes

| Class | Description |
|-------|-------------|
| [`MigrationEngine`](#migrationengine-class) | Plans, prepares, runs, commits and rolls back a migration. |
| [`MigrationEngineBuilder`](#migrationenginebuilder-class) | Fluent construction of a `MigrationEngine` without a dependency-injection container. |
| [`MigrationOptions`](#migrationoptions-class) | Everything configurable about how the engine behaves. |
| [`MigrationPlan`](#migrationplan-class) | The ordered list of steps that takes the data from one version to another. |
| [`MigrationResult`](#migrationresult-class) | The outcome of one `RunAsync` call. |
| [`AppliedStep`](#appliedstep-class) | A record of one step that ran. |
| [`MigrationContext`](#migrationcontext-class) | The engine's `IMigrationContext` implementation. |
| [`MigrationStep`](#migrationstep-class) | The ready-made `IMigrationStep`. |
| [`MigrationStepBase`](#migrationstepbase-class) | Base class for a step declared with `[MigrationStep]` and discovered by scanning. |
| [`CodeMigrationStep`](#codemigrationstep-class) | A step that is its own single provider — one file, one step. |
| [`MigrationStepScanner`](#migrationstepscanner-class) | Finds migration steps by scanning an assembly. |
| [`MigrationStepAttribute`](#migrationattribute-class) | Declares a step's version, description and id on the class itself. |
| [`MigrationProvider`](#migrationprovider-class) | Convenience base class for `IMigrationProvider`. |
| [`DelegateMigrationProvider`](#delegatemigrationprovider-class) | An `IMigrationProvider` built from delegates. |
| [`DataLocation`](#datalocation-class) | Where the current data is and what version it claims to be. |
| [`FileDataVersionStore`](#filedataversionstore-class) | The default `IDataVersionStore`: a stamp file inside the data directory. |
| [`MigrationJournalEntry`](#migrationjournalentry-class) | The record of a migration that is currently in flight. |
| [`FileMigrationJournal`](#filemigrationjournal-class) | The default `IMigrationJournal`: one file in the backup root. |
| [`FileMigrationLock`](#filemigrationlock-class) | The default `IMigrationLock`: an exclusively opened lock file. |
| [`MigrationPromptContext`](#migrationpromptcontext-class) | What the user is being asked to agree to. |
| [`AtomicFile`](#atomicfile-class) | Reads and writes a text file so a crash cannot leave it half-written. |
| [`TextFileContent`](#textfilecontent-class) | A file's text together with the encoding it was stored in. |
| [`NullMigrationLogger`](#nullmigrationlogger-class) | An `IMigrationLogger` that discards everything. |
| [`DelegateMigrationLogger`](#delegatemigrationlogger-class) | An `IMigrationLogger` that forwards to a delegate. |
| [`MigrationException`](#migrationexception-class) | Thrown when a migration cannot run at all. |
| [`MigrationPlanException`](#migrationplanexception-class) | Thrown when the registered steps cannot form a valid plan. |
| [`MigrationLockException`](#migrationlockexception-class) | Thrown when another process is already migrating. |

### Structs

| Struct | Description |
|--------|-------------|
| [`MigrationProgress`](#migrationprogress-struct) | A single progress report. |

### Interfaces

| Interface | Description |
|-----------|-------------|
| [`IMigrationStep`](#imigrationstep-interface) | One version bump, composed of providers applied together. |
| [`IMigrationProvider`](#imigrationprovider-interface) | Migrates one kind of data. |
| [`IMigrationContext`](#imigrationcontext-interface) | Everything a provider needs to know about the run it is in. |
| [`IInstallationStrategy`](#iinstallationstrategy-interface) | Where the data is, how it is protected, and what "undo" means. |
| [`IDataVersionStore`](#idataversionstore-interface) | Remembers what version the data on disk is at. |
| [`IMigrationJournal`](#imigrationjournal-interface) | Reads and writes the in-flight run record. |
| [`IMigrationLock`](#imigrationlock-interface) | Guarantees one migration at a time across processes. |
| [`IMigrationLogger`](#imigrationlogger-interface) | The engine's logging sink. |
| [`IUpdatePromptService`](#iupdatepromptservice-interface) | Asks the user whether to migrate now. |

### Enums

| Enum | Description |
|------|-------------|
| [`InstallationModel`](#installationmodel-enum) | `InPlaceSingleFolder`, `SideBySideMultiFolder`. |
| [`MigrationDirection`](#migrationdirection-enum) | `Upgrade`, `Downgrade`. |
| [`MigrationOutcome`](#migrationoutcome-enum) | How a run ended. |
| [`MigrationPhase`](#migrationphase-enum) | The coarse stage a run is in. |
| [`MigrationLogLevel`](#migrationloglevel-enum) | `Debug` … `Critical`. |
| [`UpdateTriggerMode`](#updatetriggermode-enum) | `SilentAutoUpdate`, `ManualInteractive`. |

---

### `MigrationEngine` Class

Runs migrations. One instance per application; call `RunAsync` once during startup, before anything opens the data.

```csharp
public sealed class MigrationEngine
```

#### Constructors

- **`MigrationEngine(MigrationOptions options, IEnumerable<IMigrationStep> steps, IEnumerable<IInstallationStrategy>? strategies = null, IMigrationJournal? journal = null, IMigrationLock? migrationLock = null, IUpdatePromptService? promptService = null)`**
  Validates `options` and the whole step set immediately, so a configuration mistake — a duplicate target version, a duplicate id, a step with no providers, a backup directory nested inside the data directory — surfaces at startup rather than on the one machine that happens to be three versions behind. `strategies` defaults to the two built-in ones.

#### Properties

- **`IReadOnlyList<IMigrationStep> Steps`**
  The steps this engine knows about.

#### Methods

- **`MigrationPlan CreatePlan()`**
  Works out what `RunAsync` would do without touching anything. Reads the version stamp and nothing else, so it is safe from a settings screen or a diagnostics command.

- **`Task<MigrationResult> RunAsync(IProgress<MigrationProgress>? progress = null, CancellationToken cancellationToken = default)`**
  Brings the data up to `MigrationOptions.TargetDataVersion`. **Never throws for an ordinary failure** — a step that blows up produces `MigrationOutcome.Failed` with the data restored and the exception in `MigrationResult.Error`. Inspect `Outcome` and `CanContinue` rather than assuming success.
  `progress` is reported synchronously from whichever thread the work is on; wrap it in a `Progress<T>` (or use `IMigrationRunner` on WPF) if it touches UI. `cancellationToken` is honoured during preparation and while steps run, and ignored once the commit starts.

---

### `MigrationEngineBuilder` Class

Builds a `MigrationEngine` without a dependency-injection container — the shape a console tool, a game or a small utility wants.

```csharp
public sealed class MigrationEngineBuilder
```

#### Properties

- **`MigrationOptions Options`** — the options being built; mutate directly for anything the fluent methods do not cover.

#### Methods

- **`UseDataDirectory(string)`**, **`UseBackupDirectory(string)`** — set the corresponding options.
- **`UseInPlaceModel()`** — selects `InstallationModel.InPlaceSingleFolder`.
- **`UseSideBySideModel(string? versionRootDirectory = null)`** — selects `SideBySideMultiFolder` and optionally sets the version root.
- **`TargetVersion(Version)` / `TargetVersion(string)`** — sets `TargetDataVersion`.
- **`StartingFromVersion(Version)`** — sets `InitialDataVersion`.
- **`LogTo(IMigrationLogger)` / `LogTo(Action<MigrationLogLevel, string, Exception?>)`** — sets `Logger`.
- **`Configure(Action<MigrationOptions>)`** — anything the fluent methods do not cover.
- **`AddStep(IMigrationStep)`** — adds a step.
- **`AddStep(Version, string, params IMigrationProvider[])`** / **`AddStep(string, string, params IMigrationProvider[])`** — builds and adds a `MigrationStep`.
- **`AddStep(string targetVersion, string description, Func<…, Task> up, Func<…, Task>? down = null)`** — adds a step whose only work is the given delegate.
- **`AddStepsFromAssembly(Assembly? assembly = null, Func<Type, bool>? filter = null)`**
  Finds every migration step in an assembly and adds it, so each can live in its own file. Defaults to the calling assembly. Discovered steps need a public parameterless constructor. Annotated `[RequiresUnreferencedCode]`.
- **`AddStepsFromAssemblyContaining<T>(Func<Type, bool>? filter = null)`** — the same, for the assembly containing `T`.
- **`AddStrategy(IInstallationStrategy)`**, **`UseJournal(IMigrationJournal)`**, **`UseLock(IMigrationLock)`** — replace the built-ins.
- **`AskBeforeMigrating(IUpdatePromptService)`** — switches to `ManualInteractive` and registers the prompt.
- **`MigrationEngine Build()`** — validates the options and the whole step set.

---

### `MigrationOptions` Class

How the engine should behave for one application. Binds from the `Barbatos:Migration` configuration section.

```csharp
public sealed class MigrationOptions
```

#### Fields

- **`const string SectionName`** — `"Barbatos:Migration"`.

#### Properties

| Property | Type | Default | Description |
|---|---|---|---|
| `DataDirectory` | `string` | *(required)* | The data directory under the in-place model; the **parent** holding the per-version folders under side-by-side. |
| `BackupRootDirectory` | `string` | `.migration` beside the data | Snapshots, journal and lock. Rejected if it overlaps the data directory. |
| `Model` | `InstallationModel` | `InPlaceSingleFolder` | Which installation strategy runs. |
| `TargetDataVersion` | `Version` | `1.0.0.0` | The data version this build needs. |
| `InitialDataVersion` | `Version` | `0.0.0.0` | What data with no version stamp is assumed to be. |
| `TriggerMode` | `UpdateTriggerMode` | `SilentAutoUpdate` | Whether `IUpdatePromptService` is consulted first. |
| `BackupRetentionCount` | `int` | `1` | How many successful-migration snapshots to keep. `0` deletes on commit. |
| `RequiredFreeSpaceFactor` | `double` | `1.2` | Free space required before snapshotting, as a multiple of the data size. |
| `SkipFreeSpaceCheck` | `bool` | `false` | For network shares and container volumes where the figure is meaningless. |
| `AllowRunningOnOlderData` | `bool` | `false` | Drives `MigrationResult.CanContinue` after a cancel, deferral or clean rollback. |
| `AllowDowngrade` | `bool` | `false` | Whether data newer than this build may be migrated backwards. In-place only. |
| `VersionDirectoryName` | `Func<Version, string>` | `Major.Minor.Build` | Names the per-version folders under side-by-side. Must round-trip through `Version.TryParse`. |
| `DataVersionStoreFactory` | `Func<string, IDataVersionStore>` | `FileDataVersionStore` | Where the data version lives. |
| `Logger` | `IMigrationLogger` | `NullMigrationLogger` | Where the engine logs. |

---

### `MigrationPlan` Class

The ordered list of steps that takes the data from one version to another. Building a plan is pure computation — no I/O and no side effects — so an application can show the user exactly what is about to happen before anything is touched, and tests can assert on it without a disk.

```csharp
public sealed class MigrationPlan
```

#### Properties

- **`Version FromVersion`** — the version the data is at now.
- **`Version ToVersion`** — the version the plan ends at.
- **`MigrationDirection Direction`** — which way it runs.
- **`IReadOnlyList<IMigrationStep> Steps`** — the steps in execution order: ascending for an upgrade, descending for a downgrade.
- **`bool IsEmpty`** — whether there is nothing to do.
- **`int HopCount`** — how many versions are being crossed. A user who ignored three releases produces a plan with four steps, all of which run inside a single snapshot.

#### Methods

- **`static MigrationPlan Create(IEnumerable<IMigrationStep> steps, Version currentVersion, Version targetVersion)`**
  Builds the plan. Throws `MigrationPlanException` when two steps declare the same target version or the same id, or when a downgrade crosses a step whose providers report `CanDown == false` — checked here, before the snapshot is taken, rather than three steps into the run.

- **`string Describe()`**
  A short multi-line summary, for logs and "what will happen" confirmation dialogs.

---

### `MigrationResult` Class

The outcome of one `MigrationEngine.RunAsync` call.

```csharp
public sealed class MigrationResult
```

#### Properties

- **`MigrationOutcome Outcome`** — how the run ended.
- **`bool IsSuccess`** — `true` for `Succeeded` and `UpToDate`.
- **`bool CanContinue`** — whether it is safe to carry on into the application. Deliberately *not* the same question as `IsSuccess`: after a clean rollback the data is intact but still at the old version, so `MigrationOptions.AllowRunningOnOlderData` decides. Always `false` after `RollbackFailed`.
- **`Version FromVersion`**, **`Version CurrentVersion`**, **`Version TargetVersion`** — where the run started, where the data is now, and where it was aiming.
- **`IReadOnlyList<AppliedStep> AppliedSteps`** — the steps that actually ran, in order.
- **`string WorkingDirectory`** — where the application should read its data now. A successful side-by-side upgrade moves this to the new version's folder, so honour it rather than recomputing the path.
- **`string? BackupDirectory`** — where the pre-migration snapshot is, when one was kept.
- **`TimeSpan Duration`** — how long the whole run took, including preparation and rollback.
- **`Exception? Error`** — why the run failed, when it did.
- **`Exception? RollbackError`** — why the rollback failed, when `Outcome` is `RollbackFailed`.

---

### `AppliedStep` Class

A record of one step that ran during a migration.

```csharp
public sealed class AppliedStep
```

- **`string Id`**, **`Version TargetVersion`**, **`string Description`**, **`TimeSpan Duration`**.

---

### `MigrationProgress` Struct

A single progress report. A `readonly struct` on purpose: a long-running provider can report thousands of times, and progress must not add GC pressure to work that is already competing with heavy disk I/O.

```csharp
public readonly struct MigrationProgress
```

#### Constructors

- **`MigrationProgress(double percentage, string? detail = null)`**
- **`MigrationProgress(double percentage, string? detail, bool isIndeterminate)`**
  For work whose remaining duration cannot be measured — a single long call into a third-party library. The UI shows a marquee instead of inventing a percentage.
- **`MigrationProgress(MigrationPhase phase, double percentage, string? detail = null, bool isIndeterminate = false)`**
  Providers do not need this — the engine stamps `Migrating` on their reports. An `IInstallationStrategy` does: it is what runs during `Preparing` and `RollingBack`, and a progress bar that cannot tell those apart from ordinary migration work cannot disable its Cancel button at the right moment.

#### Properties

- **`MigrationPhase Phase`** — what the engine is doing at a coarse level.
- **`double Percentage`** — 0 to 100. On reports the engine hands to the caller this is monotonic.
- **`bool IsIndeterminate`** — whether the remaining work can be measured.
- **`string Detail`** — a human-readable description of the current unit of work.
- **`string StepDescription`**, **`string ProviderName`**, **`Version? TargetVersion`** — the surrounding context, filled in by the engine.

---

### `IMigrationStep` Interface

One version bump. Everything a step contains is applied together: the run either reaches `TargetVersion` with every provider done, or the whole run is rolled back.

```csharp
public interface IMigrationStep
```

- **`string Id`** — a stable identifier, written to the journal and the applied-steps ledger. **Never change it once shipped** — it is what tells an installed copy which steps it has already seen.
- **`Version TargetVersion`** — the data version reached once this step completes. Must be unique across all registered steps.
- **`string Description`** — a human-readable summary, shown in the progress UI.
- **`IReadOnlyList<IMigrationProvider> Providers`** — the providers to run, in order. They run sequentially, never in parallel.

---

### `MigrationStep` Class

The ready-made `IMigrationStep`. Most applications never need another one.

```csharp
public sealed class MigrationStep : IMigrationStep
```

- **`MigrationStep(Version targetVersion, string description, params IMigrationProvider[] providers)`**
- **`MigrationStep(Version targetVersion, string description, IEnumerable<IMigrationProvider> providers, string? id = null)`**
  `id` defaults to the version string. Throws `ArgumentException` for an empty provider list or a provider with a non-positive weight.

---

### `MigrationStepAttribute` Class

Declares the version and description of a migration step, so the class itself carries them instead of a registration call somewhere else. Used as `[MigrationStep("2.0.0", "…")]`.

> Named `MigrationStepAttribute` rather than `MigrationAttribute` on purpose: EF Core's own `[Migration]` is `Microsoft.EntityFrameworkCore.Migrations.MigrationAttribute`, and a project using both this framework and EF Core migrations — which `Barbatos.Migration.EntityFrameworkCore` actively encourages — would otherwise have to disambiguate every usage.

```csharp
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class MigrationStepAttribute : Attribute
```

- **`MigrationStepAttribute(string version, string description = "")`** — throws `ArgumentException` if `version` does not parse.
- **`Version Version`**, **`string Description`**.
- **`string? Id`** — defaults to the class name, which is why **renaming a shipped class changes its identity**. Set it explicitly if you expect to reorganise.

---

### `MigrationStepBase` Class

Base class for a migration step that lives in its own file and declares itself with `[MigrationStep]`. Derive from this when the step composes several providers.

```csharp
public abstract class MigrationStepBase : IMigrationStep
```

- Reads `[MigrationStep]` off the derived type in its constructor; throws `MigrationPlanException` if the attribute is missing.
- **`Providers`** is built once, on first use — a step that never runs never constructs its providers at all, which matters when a provider opens a connection just to exist.
- **`protected abstract IEnumerable<IMigrationProvider> CreateProviders()`** — creates the providers, in order. Called at most once.

---

### `CodeMigrationStep` Class

A migration step that is its own single provider: put the logic straight into `UpAsync` and the whole step is one file with no ceremony around it. The shape to reach for when a step's logic is long enough to want a file to itself.

```csharp
public abstract class CodeMigrationStep : MigrationStepBase, IMigrationProvider
```

- **`virtual string Name`** — defaults to the step's description.
- **`virtual double Weight`** — defaults to `1.0`.
- **`virtual bool CanDown`** — defaults to `false`; override together with `DownAsync`.
- **`abstract Task UpAsync(IMigrationContext, IProgress<MigrationProgress>?, CancellationToken)`**
- **`virtual Task DownAsync(IMigrationContext, IProgress<MigrationProgress>?, CancellationToken)`**

---

### `MigrationStepScanner` Class

Finds migration steps by scanning an assembly, so each one can live in its own file and be picked up without a registration line that has to be kept in sync.

```csharp
public static class MigrationStepScanner
```

- **`[RequiresUnreferencedCode] static IReadOnlyList<IMigrationStep> Scan(Assembly assembly, Func<Type, IMigrationStep>? factory = null, Func<Type, bool>? filter = null)`**
  Finds every concrete `IMigrationStep` and creates one of each. `factory` defaults to the public parameterless constructor, and reports a clear `MigrationPlanException` when there is not one. Results are **sorted by target version, then id** — reflection's type order is not guaranteed stable across builds.
- **`[RequiresUnreferencedCode] static IReadOnlyList<Type> FindStepTypes(Assembly assembly, Func<Type, bool>? filter = null)`**
  The types `Scan` would construct, without constructing them — for registering them in a container instead.

A `ReflectionTypeLoadException` from a missing optional dependency yields the types that did load, rather than turning an unrelated unreferenced package into "no migrations found".

---

### `IMigrationProvider` Interface

Migrates one kind of data — a database, a settings file, a folder of assets.

```csharp
public interface IMigrationProvider
```

- **`string Name`** — a short name for logs and progress UI.
- **`double Weight`** — this provider's share of its step's progress, relative to its siblings. Must be greater than zero.
- **`bool CanDown`** — whether `DownAsync` is implemented. Checked across the whole plan *before* any data is touched.
- **`Task UpAsync(IMigrationContext, IProgress<MigrationProgress>?, CancellationToken)`**
- **`Task DownAsync(IMigrationContext, IProgress<MigrationProgress>?, CancellationToken)`**

Providers need not be atomic or reversible in themselves — the installation strategy restores the whole data directory if any provider throws. They **must** honour the cancellation token inside every loop, stay inside `IMigrationContext.WorkingDirectory`, and be safe to run twice.

---

### `MigrationProvider` Class

Convenience base class: forward-only by default, `Weight` at `1.0`.

```csharp
public abstract class MigrationProvider : IMigrationProvider
```

---

### `DelegateMigrationProvider` Class

An `IMigrationProvider` built from delegates, for one-off transformations that do not deserve a class of their own.

```csharp
public sealed class DelegateMigrationProvider : IMigrationProvider
```

- **`DelegateMigrationProvider(string name, Func<…, Task> up, Func<…, Task>? down = null, double weight = 1.0)`**

---

### `IMigrationContext` Interface

Everything a provider needs to know about the run it is taking part in.

```csharp
public interface IMigrationContext
```

- **`Version CurrentDataVersion`**, **`Version TargetDataVersion`**, **`MigrationDirection Direction`**, **`InstallationModel Model`**.
- **`string WorkingDirectory`** — the directory providers must operate on. For in-place this is the live data directory; for side-by-side it is the freshly cloned new-version directory.
- **`string OriginalDirectory`** — where the data came from. **Read-only** for side-by-side: writing here damages the version the user can still fall back to.
- **`string? BackupDirectory`** — the pre-migration snapshot, or `null` when the strategy does not take one. Never write here.
- **`IMigrationLogger Logger`** — so providers log into the same stream as the engine.
- **`IDictionary<string, object?> Items`** — free-form state shared between the providers of a run.
- **`string GetWorkingPath(string relativePath)`** — resolves against `WorkingDirectory`.

---

### `MigrationContext` Class

The engine's `IMigrationContext` implementation. Created per run; the installation strategy is what moves `WorkingDirectory`.

```csharp
public sealed class MigrationContext : IMigrationContext
```

- **`string SessionId`** — identifies the run, and names its temporary directories.
- **`void SetWorkingDirectory(string directory)`** — points the run at the directory providers should write to. **For `IInstallationStrategy` implementations only**, normally from `PrepareAsync` and `CommitAsync`; the side-by-side strategy points it at its staging clone while migrating and at the published version directory afterwards. A method rather than a settable property so that misuse by a provider — which would redirect every provider after it — is conspicuous.
- **`void SetBackupDirectory(string? directory)`** — records where the snapshot lives, or clears it. A strategy that takes no snapshot must leave it `null`, since that is what tells the engine there is nothing to restore.

---

### `IInstallationStrategy` Interface

Everything that differs between the two installation models: where the data is, how it is protected while it is being rewritten, and what "undo" means.

```csharp
public interface IInstallationStrategy
```

- **`InstallationModel Model`**
- **`DataLocation ResolveCurrentData()`** — read-only; called during planning.
- **`bool RequiresRunWithEmptyPlan(DataLocation currentData)`** — in-place answers `false`; side-by-side answers `true` when this build's directory does not exist yet, since the previous version's data still has to be cloned into it.
- **`Task PrepareAsync(MigrationContext, IProgress<MigrationProgress>?, CancellationToken)`** — snapshot or clone; cancellable.
- **`Task CommitAsync(MigrationContext, IReadOnlyList<string> appliedStepIds, IProgress<MigrationProgress>?)`** — publish and stamp; **not** cancellable.
- **`Task RollbackAsync(MigrationContext, Exception?, IProgress<MigrationProgress>?)`** — restore; not cancellable. Throwing here produces `MigrationOutcome.RollbackFailed`, so implementations must leave the snapshot on disk rather than clean up after themselves.
- **`Task RecoverAsync(MigrationJournalEntry, IProgress<MigrationProgress>?)`** — cleans up after a run that was killed.

---

### `DataLocation` Class

Where the current data is and what version it claims to be.

```csharp
public sealed class DataLocation
```

- **`string Directory`**, **`Version? Version`** (`null` when never stamped), **`bool Exists`**.

---

### `IDataVersionStore` Interface

Remembers what version the data on disk is at, and which steps have been applied to it. Without this there is nothing to compare the application version against, so every launch would either re-run every migration or run none of them.

```csharp
public interface IDataVersionStore
```

- **`Version? Read()`** — `null` when the data has never been stamped.
- **`IReadOnlyList<string> ReadAppliedStepIds()`** — the ledger, oldest first.
- **`void Write(Version version, IReadOnlyList<string> appliedStepIds)`** — must be durable by the time it returns.

---

### `FileDataVersionStore` Class

The default: a single `.migration-version` file **inside** the data directory it describes, so a directory that is copied, cloned or restored carries its version with it.

```csharp
public sealed class FileDataVersionStore : IDataVersionStore
```

- **`const string DefaultFileName`** — `".migration-version"`.
- **`FileDataVersionStore(string dataDirectory, string fileName = DefaultFileName)`**
- **`string FilePath`**

---

### `MigrationJournalEntry` Class

The record of a migration that is currently in flight. Its presence at startup means the previous attempt did not finish.

```csharp
public sealed class MigrationJournalEntry
```

- **`string SessionId`**, **`DateTimeOffset StartedUtc`**, **`InstallationModel Model`**, **`MigrationDirection Direction`**, **`Version FromVersion`**, **`Version ToVersion`**, **`string OriginalDirectory`**, **`string WorkingDirectory`**, **`string? BackupDirectory`**.
- **`MigrationPhase Phase`** — decides the recovery. `Preparing` means no provider had run yet, so the partial snapshot is discarded and nothing is restored; anything later means the snapshot is complete and is swapped back in.
- **`string? LastCompletedStepId`**

---

### `IMigrationJournal` Interface

```csharp
public interface IMigrationJournal
```

- **`MigrationJournalEntry? Read()`**, **`void Write(MigrationJournalEntry entry)`**, **`void Clear()`**.

---

### `FileMigrationJournal` Class

The default: one file in the backup root — deliberately not in the data directory, since the whole point is to survive the operations that replace it.

```csharp
public sealed class FileMigrationJournal : IMigrationJournal
```

- **`const string DefaultFileName`** — `"migration.journal"`.
- **`FileMigrationJournal(string backupRootDirectory, string fileName = DefaultFileName)`**
- **`string FilePath`**

A journal that cannot be parsed is treated as absent: acting on fields that are not trusted is more dangerous than letting the version comparison decide.

---

### `IMigrationLock` Interface

Guarantees that only one process migrates a given data directory at a time.

```csharp
public interface IMigrationLock
```

- **`IDisposable? TryAcquire()`** — `null` when another process holds it.

---

### `FileMigrationLock` Class

The default: a lock file opened with `FileShare.None` and `FileOptions.DeleteOnClose`, so the operating system releases it even if the process is killed — no stale-lock timeout to guess at.

```csharp
public sealed class FileMigrationLock : IMigrationLock
```

- **`const string DefaultFileName`** — `"migration.lock"`.
- **`FileMigrationLock(string backupRootDirectory, string fileName = DefaultFileName)`**
- **`string FilePath`**

---

### `IUpdatePromptService` Interface

Asks the user whether to migrate now. Only consulted when `MigrationOptions.TriggerMode` is `ManualInteractive`, and always before anything is backed up, so declining costs nothing.

```csharp
public interface IUpdatePromptService
```

- **`Task<bool> ConfirmAsync(MigrationPromptContext context, CancellationToken cancellationToken)`** — cancelling the token counts as declining.

---

### `MigrationPromptContext` Class

```csharp
public sealed class MigrationPromptContext
```

- **`MigrationPlan Plan`** — show `Describe()` to power users.
- **`InstallationModel Model`** — decides what "not now" actually costs.
- **`bool CanDefer`** — `false` means the application has declared it cannot run against the old data. The prompt must then explain that the choice is between migrating and closing, and must **not** offer a "Remind me later" that leads nowhere.
- **`long EstimatedDataSizeBytes`** — so the prompt can say "this will take a few minutes" when it actually will. `0` when unknown.
- **`string? ReleaseNotes`**

---

### `IMigrationLogger` Interface

The engine's logging sink. A one-method interface of its own rather than `Microsoft.Extensions.Logging.ILogger`, so `Barbatos.Migration.Core` stays dependency-free.

```csharp
public interface IMigrationLogger
```

- **`void Log(MigrationLogLevel level, string message, Exception? exception = null)`**

### `NullMigrationLogger` Class

Discards everything. **`static readonly NullMigrationLogger Instance`**.

### `DelegateMigrationLogger` Class

Forwards to a delegate — handy for a game engine's own console and for tests.

- **`DelegateMigrationLogger(Action<MigrationLogLevel, string, Exception?> write)`**

---

### `AtomicFile` Class

Reads and writes a text file so that a crash can never leave it half-written: the write goes to a sibling temporary file, is flushed all the way to disk, and is only then renamed over the original.

```csharp
public static class AtomicFile
```

- **`static Encoding DefaultEncoding`** — UTF-8 without a byte-order mark.
- **`static TextFileContent? Read(string path)`** — returns the contents and the detected encoding, or `null` when the file does not exist.
- **`static void Write(string path, string contents, Encoding? encoding = null)`**

### `TextFileContent` Class

- **`string Text`**, **`Encoding Encoding`** — pass the encoding back to `Write` so a file another tool saved as UTF-16 or with a BOM does not silently change format.

---

### Exceptions

#### `MigrationException` Class

Thrown when a migration cannot run at all — as opposed to a step failing mid-run, which is reported through `MigrationResult.Error` after the data has been rolled back.

#### `MigrationPlanException` Class

The registered steps cannot form a valid plan: duplicate target versions, duplicate ids, a step with no providers, a downgrade across a forward-only provider, or a discovered step that cannot be constructed.

#### `MigrationLockException` Class

Another process is already migrating the same data directory.

> A failed rollback is **not** an exception. `RunAsync` returns `MigrationOutcome.RollbackFailed`, with `MigrationResult.RollbackError` and `MigrationResult.BackupDirectory` — the snapshot the engine deliberately leaves on disk — filled in. The case where the application most needs to act deliberately is the worst one to express as something a caller can forget to catch.

---

### Enums

#### `InstallationModel` Enum

| Member | Description |
|---|---|
| `InPlaceSingleFolder` | Every version shares one data folder; the engine snapshots it and swaps the snapshot back on failure. Bi-directional when every step can be undone. |
| `SideBySideMultiFolder` | Each version owns a folder; an upgrade clones the previous one and migrates the clone. Forward-only — "rolling back" is launching the old build. |

#### `MigrationDirection` Enum

`Upgrade` (ascending, `UpAsync`) · `Downgrade` (descending, `DownAsync`).

#### `MigrationOutcome` Enum

| Member | The data | `CanContinue` |
|---|---|---|
| `UpToDate` | unchanged, already at the target | `true` |
| `Succeeded` | migrated | `true` |
| `Canceled` | restored to its pre-migration state | `AllowRunningOnOlderData` |
| `Deferred` | untouched | `AllowRunningOnOlderData` |
| `Failed` | restored to its pre-migration state | `AllowRunningOnOlderData` |
| `RollbackFailed` | **may be inconsistent** | `false` |
| `Blocked` | untouched | `false` |

#### `MigrationPhase` Enum

`Planning` · `Recovering` · `Preparing` (0–20%, cancellable) · `Migrating` (20–97%, cancellable) · `Committing` (97–100%, **not** cancellable) · `RollingBack` · `Completed`.

#### `MigrationLogLevel` Enum

`Debug` · `Information` · `Warning` · `Error` · `Critical` (a rollback failed; user data may be inconsistent).

#### `UpdateTriggerMode` Enum

`SilentAutoUpdate` — migrate as soon as the application starts, showing progress but not asking.
`ManualInteractive` — ask first, through `IUpdatePromptService`.

---

## `Barbatos.Migration.Strategies` Namespace

### `InPlaceStrategy` Class

Takes a full snapshot of the data directory, lets the providers rewrite the real thing, and swaps the snapshot back if anything goes wrong.

```csharp
public sealed class InPlaceStrategy : IInstallationStrategy
```

- **`InPlaceStrategy(MigrationOptions options, Func<string, IDataVersionStore>? versionStoreFactory = null)`**

The restore is three renames rather than a delete-then-copy, so there is never a moment without a complete copy of the data on disk.

### `SideBySideStrategy` Class

Clones the newest installed version, migrates the clone, and publishes it with a single rename at commit time.

```csharp
public sealed class SideBySideStrategy : IInstallationStrategy
```

- **`SideBySideStrategy(MigrationOptions options, Func<string, IDataVersionStore>? versionStoreFactory = null)`**
- **`string TargetVersionDirectory`** — where this build's data will end up.

Until the commit rename, the target version has no directory at all. Abandoned staging clones from earlier crashed runs are swept up at recovery.

---

## `Barbatos.Migration.Json` Namespace

### `JsonMigrationProvider` Class

Transforms a JSON file by handing its DOM to a delegate. Working on `JsonNode` rather than a deserialised object is what makes this usable at all: the old shape of the file no longer has a C# type — that type is exactly what the new version deleted — and every property the migration does not mention survives untouched.

```csharp
public class JsonMigrationProvider : IMigrationProvider
```

- **`JsonMigrationProvider(string relativePath, Action<JsonObject> up, Action<JsonObject>? down = null, bool createIfMissing = true, bool writeIndented = true)`**
- **`bool CreateIfMissing`**, **`bool WriteIndented`**

A file that is not valid JSON fails the run with a message naming it, and is **not** overwritten.

### `JsonMigrationExtensions` Class

Extension methods on `JsonObject`. Each is a no-op when the key is absent, which is what makes a step safe to re-run after an interrupted attempt.

```csharp
public static class JsonMigrationExtensions
```

- **`RenameProperty(this JsonObject, string from, string to)`**
- **`RemoveProperty(this JsonObject, string name)`**
- **`Set(this JsonObject, string name, JsonNode? value)`**
- **`SetDefault(this JsonObject, string name, JsonNode? value)`** — only when missing, so a user's own value is never overwritten.
- **`MoveIntoSection(this JsonObject, string propertyName, string sectionName, string? newName = null)`**
- **`MoveOutOfSection(this JsonObject, string sectionName, string propertyName, string? newName = null)`** — removes the section if it empties.
- **`ConvertProperty(this JsonObject, string name, Func<JsonNode?, JsonNode?> convert)`**
- **`Section(this JsonObject, string name)`** — gets a nested object, creating it if missing. Chain it for depth: `json.Section("editor").Section("font").Set("size", 16)`.
- **`Root(this JsonNode)`** — climbs back to the top of the document, so one chain can edit several depths. Throws `MigrationException` if the root is not an object.
- **`ForEachInArray(this JsonObject, string name, Action<JsonObject> update)`** — applies an update to every object in an array property; the shape a "each saved entry gains a field" migration takes. A no-op when the property is missing, is not an array, or holds non-object entries.

Non-ASCII text is written back **unescaped**. `System.Text.Json` escapes it by default, which is right for embedding JSON in a web page and wrong for a settings file on disk — a migration that renamed one key would otherwise turn `"Xin chào"` into `"Xin chào"` and leave the user a file they can no longer read.

---

## `Barbatos.Migration.Ini` Namespace

### `IniMigrationProvider` Class

Reshapes an INI file by handing its document to a delegate.

```csharp
public class IniMigrationProvider : IMigrationProvider
```

- **`IniMigrationProvider(string relativePath, Action<IniDocument> up, Action<IniDocument>? down = null, bool createIfMissing = true, bool caseSensitive = false)`**
- **`bool CreateIfMissing`**, **`bool CaseSensitive`**

### `IniDocument` Class

An INI file as an editable, **format-preserving** document: the file is kept as the list of lines it actually is, each classified once, and anything not asked to change is written back byte for byte. Editing a value rewrites only the value part of that one line, keeping its indentation, the spacing around its `=`, and any trailing comment.

```csharp
public sealed class IniDocument
```

#### Properties

- **`string NewLine`** — detected when parsing, so a file written on Windows stays CRLF.
- **`bool EndsWithNewLine`**, **`string KeyValueSeparator`** (` = `, used for *new* keys only), **`char CommentPrefix`**.
- **`IReadOnlyList<string> SectionNames`** — in file order; the unnamed leading section is `string.Empty`.

#### Methods

- **`static IniDocument Parse(string text, bool caseSensitive = false)`**
- **`string ToIniString()`**
- Reading: **`ContainsKey`**, **`ContainsSection`**, **`GetValue(section, key)`**, **`GetValue(section, key, defaultValue)`**, **`KeysIn(section)`**.
- Writing: **`Set`**, **`SetDefault`**, **`RenameKey`**, **`RemoveKey`**, **`ConvertValue`**, **`MoveKey`**, **`RenameSection`**, **`RemoveSection`**, **`EnsureSection`**, **`AddComment`**.

A new key is placed below the last line belonging to its section, above any blank line separating it from the next — with its neighbours, not orphaned at the bottom of the file.

Three details that only show up on real files:

- **A `;` or `#` starts an inline comment only when whitespace separates it from the value**, or when it is the whole value. Treating every one as a comment truncates `ConnectionString=Server=localhost;Db=app` to `Server=localhost`, and a migration that then writes the file back has destroyed the rest of it.
- **Values are quoted on the way out only when writing them bare would change what reading them back produces** — the exact inverse of the rule above, so a connection string never gains quotes it did not have.
- **`RemoveSection` keeps a trailing comment block and hands it to the next section.** The parser assigns every line to the header above it, which puts a comment sitting directly on top of the *next* header into the section being removed — and read by a human, that comment documents the section below it. A trailing run of bare blank lines is removed, since separation is not documentation.

Colons are ordinary characters: `[Messages:Error]` and `Timeout:Max=30` both work.

---

## `Barbatos.Migration.Csv` Namespace

### `CsvMigrationProvider` Class

Reshapes a delimited data file by handing its table to a delegate.

```csharp
public class CsvMigrationProvider : IMigrationProvider
```

- **`CsvMigrationProvider(string relativePath, Action<CsvDocument> up, Action<CsvDocument>? down = null, bool hasHeader = true, char? delimiter = null)`**
- **`CsvMigrationProvider(string relativePath, Action<CsvDocument, IProgress<MigrationProgress>?, CancellationToken> up, …)`** — for a file big enough that the migration has to stay responsive and interruptible.
- **`bool HasHeader`**, **`char? Delimiter`**, **`bool CreateIfMissing`** (default **`false`**), **`CsvQuoteStyle QuoteStyle`**.

### `CsvDocument` Class

A delimited data file as an editable table. The delimiter, quoting style, line endings and the presence of a header are detected on the way in and reproduced on the way out.

```csharp
public sealed class CsvDocument
```

#### Properties

- **`char Delimiter`**, **`string NewLine`**, **`bool HasHeader`**, **`bool EndsWithNewLine`**, **`CsvQuoteStyle QuoteStyle`**.
- **`IReadOnlyList<string> Columns`**, **`IReadOnlyList<CsvRow> Rows`**.

#### Methods

- **`static CsvDocument Create(IEnumerable<string> columns, char delimiter = ',')`**
- **`static CsvDocument Parse(string text, bool hasHeader = true, char? delimiter = null)`** — throws `MigrationException` for a malformed file, naming the line. An unterminated quote makes every following line part of one enormous field; accepting that and then rewriting the file would destroy the user's data silently.
- **`string ToCsvString()`**
- Columns: **`IndexOf`**, **`ContainsColumn`**, **`AddColumn(name, Func<CsvRow,string>?, int?)`**, **`AddColumn(name, defaultValue, int?)`**, **`RemoveColumn`**, **`RenameColumn`**, **`MoveColumn`**, **`TransformColumn`**, **`SplitColumn(source, split, params targets)`**, **`MergeColumns(target, merge, params sources)`**.
- Rows: **`AddRow(IEnumerable<KeyValuePair<string,string>>)`**, **`AddRow(params string[])`**, **`RemoveRows(predicate)`**, **`UpdateRows(update, progress?, cancellationToken)`** — checks the token every row and reports every 500.

### `CsvRow` Class

```csharp
public sealed class CsvRow
```

- **`string this[string column]`** — by name; requires a header.
- **`string this[int index]`** — by position.
- **`int FieldCount`**, **`IReadOnlyList<string> Values`**, **`bool IsEmpty`**.

A row can legitimately be shorter or longer than the header. Reading a missing field gives the empty string and writing one pads the row out, so a migration does not have to defend against ragged files.

### `CsvQuoteStyle` Enum

`Minimal` — quote only what must be quoted.
`PreserveOriginal` — quote a value if it was quoted before, or if it now has to be. The default when parsing.
`All` — quote everything.

---

## `Barbatos.Migration.FileSystem` Namespace

### `FileSystemMigrationProvider` Class

Restructures the data directory. Operations are declared rather than written so each states its own inverse, and `DownAsync` walks the list backwards.

```csharp
public class FileSystemMigrationProvider : IMigrationProvider
```

- **`FileSystemMigrationProvider(string name, Action<FileSystemOperationBuilder> configure)`**
- **`bool CanDown`** — `false` once any delete is declared.

### `FileSystemOperationBuilder` Class

```csharp
public sealed class FileSystemOperationBuilder
```

- **`MoveFile(from, to)`** / **`RenameFile(from, to)`**
- **`MoveDirectory(from, to)`** / **`RenameDirectory(from, to)`**
- **`CopyFile(from, to)`**
- **`DeleteFile(path)`**, **`DeleteDirectory(path)`** — irreversible.
- **`EnsureDirectory(path)`** — reversed by deleting it, but only if the undo left it empty.
- **`WriteText(path, content)`**

Relative paths only; an absolute path, or one resolving outside the working directory, throws `MigrationException`.

---

## `Barbatos.Migration.Database` Namespace

### `DatabaseMigrationProvider` Class

Runs SQL migration scripts against any ADO.NET provider. This package has **no database driver dependency**: it works with a `DbConnection` the application hands it.

```csharp
public class DatabaseMigrationProvider : IMigrationProvider
```

#### Constructors

- **`DatabaseMigrationProvider(string name, Func<IMigrationContext, DbConnection> connectionFactory, IEnumerable<string> up, IEnumerable<string>? down = null, IDatabaseDialect? dialect = null)`**
- **`static DatabaseMigrationProvider ForFile(string relativePath, Func<string, DbConnection> connectionFactory, IEnumerable<string> up, IEnumerable<string>? down = null, IDatabaseDialect? dialect = null)`**
  Resolves the database path against `WorkingDirectory`, which is what makes the same step work under both installation models. Defaults to `DatabaseDialects.Sqlite`.

#### Properties

- **`IDatabaseDialect Dialect`**, **`DatabaseMigrationOptions Options`**, **`IsolationLevel IsolationLevel`**, **`double Weight`** (default `4.0`).

Every statement runs inside one transaction, and a failure names its position. The connection is closed and the dialect's `ReleaseResources` runs in a `finally` block on every path out — for a file-backed database that is not tidiness but correctness.

### `IDatabaseDialect` Interface

The parts of running a migration that genuinely differ between database engines. A dialect is a hint, not a driver: nothing here requires the concrete connection type.

```csharp
public interface IDatabaseDialect
```

- **`string Name`**
- **`bool SupportsTransactionalSchemaChanges`** — MySQL and MariaDB say **no**; the provider logs a warning when a dialect that says no is used.
- **`Task PrepareAsync(DbConnection, DatabaseMigrationOptions, CancellationToken)`** — before the transaction opens, for settings a transaction would ignore.
- **`Task VerifyAsync(DbConnection, DbTransaction, DatabaseMigrationOptions, CancellationToken)`** — inside the transaction, so anything it rejects rolls the step back.
- **`Task FinishAsync(DbConnection, DatabaseMigrationOptions, CancellationToken)`** — after the commit, still connected.
- **`void ReleaseResources(DbConnection)`** — after the connection is closed. This exists for embedded, file-backed engines: an open handle makes the engine's snapshot, restore and directory rename fail, and a rollback blocked by the database it is restoring is the worst failure this framework has.

### `DatabaseMigrationOptions` Class

- **`bool SuspendForeignKeys`** (default `true`), **`bool VerifyIntegrity`** (default `true`), **`int CommandTimeoutSeconds`** (default `0` — no timeout), **`int? SchemaVersion`**.

### `DatabaseDialects` Class

```csharp
public static class DatabaseDialects
```

| Member | Transactional DDL | Foreign keys suspended by | Integrity check |
|---|---|---|---|
| `Generic` | assumed yes | — | — |
| `Sqlite` | yes | `PRAGMA foreign_keys = OFF` | `PRAGMA foreign_key_check` |
| `SqlServer` | yes | `NOCHECK CONSTRAINT ALL` | `WITH CHECK CHECK CONSTRAINT ALL` |
| `PostgreSql` | yes | *(deferred constraints)* | `SET CONSTRAINTS ALL IMMEDIATE` |
| `MySql` | **no** | `SET FOREIGN_KEY_CHECKS = 0` | — |

`SqliteDialect` additionally handles `PRAGMA user_version`, checkpoints the write-ahead log (a `-wal` sidecar the snapshot would otherwise miss) and clears the driver's connection pools.

### `DatabaseDialect` Class

Base class for a custom dialect: everything is a no-op unless overridden, with `ExecuteAsync` and `ExecuteScalarAsync` helpers.

```csharp
public abstract class DatabaseDialect : IDatabaseDialect
```

---

## `Barbatos.Migration.EntityFrameworkCore` Namespace

### `EfCoreMigrationsProvider<TContext>` Class

Applies EF Core's own migrations as a Barbatos step, so they run inside the snapshot the engine has already taken. EF Core is very good at changing a schema and has no notion of putting anything back; this gives it the rollback, the crash recovery and the progress reporting it is missing.

```csharp
public class EfCoreMigrationsProvider<TContext> : IMigrationProvider where TContext : DbContext
```

- **`EfCoreMigrationsProvider(Func<IMigrationContext, TContext> contextFactory, string? name = null)`** — the factory is given the run's context, so a file-backed database can point at `WorkingDirectory`. The provider disposes what it returns.
- **`IDatabaseDialect Dialect`** — **set this to `DatabaseDialects.Sqlite` for a file-backed database.** Disposing a `DbContext` returns its connection to the driver's pool rather than closing the file.
- **`DatabaseMigrationOptions DialectOptions`**, **`double Weight`** (default `5.0`).
- **`virtual bool CanDown`** — `false`. A step does not know which migration was current before it ran, and guessing would be worse than refusing.

Progress is reported as indeterminate with the pending migration names: EF Core applies the batch in one call with no per-migration callback, so a percentage would be invented.

### `EfCoreDowngradeMigrationsProvider<TContext>` Class

The reversible counterpart: migrates up to one named EF Core migration and back down to another. Naming both ends explicitly is what makes the downgrade well-defined.

```csharp
public sealed class EfCoreDowngradeMigrationsProvider<TContext> : EfCoreMigrationsProvider<TContext>
```

- **`EfCoreDowngradeMigrationsProvider(Func<IMigrationContext, TContext> contextFactory, string upTargetMigration, string downTargetMigration, string? name = null)`** — pass `"0"` as the downgrade target to undo every migration.

### `DbContextMigrationProvider<TContext>` Class

Transforms *data* through a `DbContext`, as opposed to the schema — splitting a full name into two columns, normalising stored durations, deriving a lookup table. Written as ordinary LINQ against the entities instead of hand-written SQL inside a migration file.

```csharp
public class DbContextMigrationProvider<TContext> : IMigrationProvider where TContext : DbContext
```

- **`DbContextMigrationProvider(string name, Func<IMigrationContext, TContext> contextFactory, Func<TContext, IProgress<MigrationProgress>?, CancellationToken, Task> up, Func<…>? down = null, double weight = 3.0)`**
- **`bool UseTransaction`** (default `true`) — turn it off only for a provider that manages its own batching.
- **`IDatabaseDialect Dialect`**, **`DatabaseMigrationOptions DialectOptions`**.

---

## `Barbatos.Migration.DependencyInjection` Namespace

### `MigrationServiceCollectionExtensions` Class

```csharp
public static class MigrationServiceCollectionExtensions
```

- **`static MigrationBuilder AddBarbatosMigration(this IServiceCollection services, Action<MigrationOptions>? configure = null)`**
  Registers `MigrationEngine` as a singleton and adapts `IMigrationLogger` onto `ILogger`. **Nothing runs for you** — resolve the engine and call `RunAsync` from your startup path.

### `MigrationBuilder` Class

```csharp
public sealed class MigrationBuilder
```

- **`IServiceCollection Services`**
- **`AddStep(IMigrationStep)`** — an already-constructed step.
- **`AddStep<TStep>()`** — resolved from the container, so it can take constructor dependencies.
- **`AddStep(Func<IServiceProvider, IMigrationStep>)`**
- **`AddStep(string targetVersion, string description, params IMigrationProvider[] providers)`**
- **`[RequiresUnreferencedCode] AddStepsFromAssembly(Assembly? assembly = null, Func<Type, bool>? filter = null)`**
  Registers each discovered type with the container rather than constructing it, so a discovered step can take constructor dependencies like any other service.
- **`[RequiresUnreferencedCode] AddStepsFromAssemblyContaining<T>(Func<Type, bool>? filter = null)`**
- **`AddStrategy<TStrategy>()`** — registering any strategy replaces **both** built-ins.
- **`UsePrompt<TPrompt>()`**, **`UseJournal<TJournal>()`**, **`UseLock<TLock>()`**

Steps are registered under `IMigrationStep` and collected with `GetServices`, so they can be spread across feature folders and still end up in one ordered plan.

---

## `Barbatos.Migration.Wpf` Namespace

### `MigrationAppHostBuilderExtensions` Class

```csharp
public static class MigrationAppHostBuilderExtensions
```

- **`static MigrationBuilder ConfigureMigration(this WpfAppBuilder builder, Action<MigrationOptions>? configure = null)`**
  Registers the engine and defaults its options from the host: `DataDirectory` from `IFileSystem.AppDataDirectory`, `TargetDataVersion` from `IAppInfo.Version`, `InitialDataVersion` from `IVersionTracking.VersionHistory`, `Logger` from the container. All overridable from `configure` or the `Barbatos:Migration` configuration section.
- **`static MigrationBuilder AskBeforeMigrating(this MigrationBuilder builder)`**
  Switches to `ManualInteractive` and registers `MessageBoxUpdatePromptService`.

The `InitialDataVersion` heuristic reads the version *history* rather than `PreviousVersion`: by the time a migration runs, version tracking has already recorded the new build, so a cancelled first attempt would otherwise leave `PreviousVersion` claiming there is nothing to migrate.

### `IMigrationRunner` Interface

Runs the migration from a WPF startup path: off the UI thread, with progress marshalled back onto it.

```csharp
public interface IMigrationRunner
```

- **`MigrationPlan CreatePlan()`**
- **`Task<MigrationResult> RunAsync(IProgress<MigrationProgress>? progress = null, CancellationToken cancellationToken = default)`**

### `MigrationRunner` Class

```csharp
public sealed class MigrationRunner : IMigrationRunner
```

- **`MigrationRunner(MigrationEngine engine, IDispatcher dispatcher, IOptions<MigrationOptions> options)`**
- **`MigrationOptions Options`**

Wraps the engine in `Task.Run` — copying a data directory is synchronous, disk-bound work, and on the UI thread it would freeze the splash screen. Progress is marshalled through `IDispatcher` and throttled to one report per 50 ms or 0.5%, with terminal reports always let through.

### `MigrationProgressViewModel` Class

A ready-made view model for a migration splash screen. It is itself an `IProgress<MigrationProgress>`, so it can be handed straight to `RunAsync`.

```csharp
public sealed class MigrationProgressViewModel : IProgress<MigrationProgress>, INotifyPropertyChanged, IDisposable
```

- **`double Percentage`**, **`bool IsIndeterminate`**, **`string Status`**, **`string StepDescription`**, **`MigrationPhase Phase`**, **`bool IsRunning`**.
- **`bool CanCancel`** — goes `false` once the engine reaches `Committing`, because a Cancel button that ignores clicks is worse than one that is greyed out.
- **`ICommand CancelCommand`**, **`void Cancel()`**, **`CancellationToken CancellationToken`**.

### `MessageBoxUpdatePromptService` Class

The built-in `IUpdatePromptService`: a message box explaining what will change, how much data will be copied, and — when `AllowRunningOnOlderData` permits — offering to postpone.

```csharp
public sealed class MessageBoxUpdatePromptService : IUpdatePromptService
```

- **`MessageBoxUpdatePromptService(IDispatcher dispatcher, IAppInfo appInfo)`**

---

## See also

- **[README.md](README.md)** — the guide, with worked examples for each provider.
- **[docs/DESIGN.md](docs/DESIGN.md)** — the architecture and the reasoning behind each safety decision.
