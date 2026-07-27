// Copyright (C) Pham The Hung and Barbatos.Migration Contributors.
// All Rights Reserved.

using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace Barbatos.Migration;

/// <summary>
/// Builds a <see cref="MigrationEngine"/> without a dependency-injection container.
/// </summary>
/// <remarks>
/// Unity and small utilities have no <c>IServiceCollection</c>, and a migration engine is a
/// singleton with a handful of settings - reaching for a container to configure one would be
/// backwards. Applications that do have a container should use
/// <c>Barbatos.Migration.DependencyInjection</c> instead, which wraps this same shape.
/// </remarks>
public sealed class MigrationEngineBuilder
{
    private readonly List<IMigrationStep> _steps = [];
    private readonly List<IInstallationStrategy> _strategies = [];
    private IMigrationJournal? _journal;
    private IMigrationLock? _lock;
    private IUpdatePromptService? _promptService;

    /// <summary>The options being built. Mutate directly for anything the fluent methods do not cover.</summary>
    public MigrationOptions Options { get; } = new();

    /// <summary>Sets <see cref="MigrationOptions.DataDirectory"/>.</summary>
    public MigrationEngineBuilder UseDataDirectory(string dataDirectory)
    {
        Options.DataDirectory = dataDirectory;
        return this;
    }

    /// <summary>Sets <see cref="MigrationOptions.BackupRootDirectory"/>.</summary>
    public MigrationEngineBuilder UseBackupDirectory(string backupRootDirectory)
    {
        Options.BackupRootDirectory = backupRootDirectory;
        return this;
    }

    /// <summary>Selects <see cref="InstallationModel.InPlaceSingleFolder"/>.</summary>
    public MigrationEngineBuilder UseInPlaceModel()
    {
        Options.Model = InstallationModel.InPlaceSingleFolder;
        return this;
    }

    /// <summary>Selects <see cref="InstallationModel.SideBySideMultiFolder"/>.</summary>
    /// <param name="versionRootDirectory">The parent directory that holds the per-version folders.</param>
    public MigrationEngineBuilder UseSideBySideModel(string? versionRootDirectory = null)
    {
        Options.Model = InstallationModel.SideBySideMultiFolder;
        if (versionRootDirectory != null)
            Options.DataDirectory = versionRootDirectory;

        return this;
    }

    /// <summary>Sets <see cref="MigrationOptions.TargetDataVersion"/>.</summary>
    public MigrationEngineBuilder TargetVersion(Version version)
    {
        Options.TargetDataVersion = version ?? throw new ArgumentNullException(nameof(version));
        return this;
    }

    /// <inheritdoc cref="TargetVersion(Version)"/>
    public MigrationEngineBuilder TargetVersion(string version) => TargetVersion(Version.Parse(version));

    /// <summary>Sets <see cref="MigrationOptions.InitialDataVersion"/>.</summary>
    public MigrationEngineBuilder StartingFromVersion(Version version)
    {
        Options.InitialDataVersion = version ?? throw new ArgumentNullException(nameof(version));
        return this;
    }

    /// <summary>Sets <see cref="MigrationOptions.Logger"/>.</summary>
    public MigrationEngineBuilder LogTo(IMigrationLogger logger)
    {
        Options.Logger = logger ?? NullMigrationLogger.Instance;
        return this;
    }

    /// <inheritdoc cref="LogTo(IMigrationLogger)"/>
    public MigrationEngineBuilder LogTo(Action<MigrationLogLevel, string, Exception?> write) =>
        LogTo(new DelegateMigrationLogger(write));

    /// <summary>Configures anything the fluent methods do not cover.</summary>
    public MigrationEngineBuilder Configure(Action<MigrationOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        configure(Options);
        return this;
    }

    /// <summary>Adds a step.</summary>
    public MigrationEngineBuilder AddStep(IMigrationStep step)
    {
        _steps.Add(step ?? throw new ArgumentNullException(nameof(step)));
        return this;
    }

    /// <summary>Adds a step made of the given providers.</summary>
    public MigrationEngineBuilder AddStep(Version targetVersion, string description, params IMigrationProvider[] providers) =>
        AddStep(new MigrationStep(targetVersion, description, providers));

    /// <inheritdoc cref="AddStep(Version, string, IMigrationProvider[])"/>
    public MigrationEngineBuilder AddStep(string targetVersion, string description, params IMigrationProvider[] providers) =>
        AddStep(new MigrationStep(Version.Parse(targetVersion), description, providers));

    /// <summary>Adds a step whose only work is the given delegate.</summary>
    public MigrationEngineBuilder AddStep(
        string targetVersion,
        string description,
        Func<IMigrationContext, IProgress<MigrationProgress>?, CancellationToken, Task> up,
        Func<IMigrationContext, IProgress<MigrationProgress>?, CancellationToken, Task>? down = null) =>
        AddStep(new MigrationStep(
            Version.Parse(targetVersion),
            description,
            new DelegateMigrationProvider(description, up, down)));

    /// <summary>
    /// Finds every migration step in an assembly and adds it, so each one can live in its own
    /// file without a registration line to keep in sync.
    /// </summary>
    /// <param name="assembly">The assembly to scan; defaults to the caller's own.</param>
    /// <param name="filter">An extra predicate on the discovered types.</param>
    /// <remarks>
    /// Discovered steps need a public parameterless constructor. Use
    /// <c>Barbatos.Migration.DependencyInjection</c> if they need constructor injection. Order
    /// does not matter - the engine sorts by version - and duplicate versions or ids are
    /// rejected when the engine is built.
    /// </remarks>
    /// <example>
    /// <code>
    /// MigrationEngine engine = new MigrationEngineBuilder()
    ///     .UseDataDirectory(dataDirectory)
    ///     .TargetVersion("2.0.0")
    ///     .AddStepsFromAssembly()
    ///     .Build();
    /// </code>
    /// </example>
    [RequiresUnreferencedCode(
        "Scanning an assembly for migration steps requires reflection over its types, which a trimmer cannot follow. " +
        "Use AddStep for each step when publishing trimmed.")]
    public MigrationEngineBuilder AddStepsFromAssembly(Assembly? assembly = null, Func<Type, bool>? filter = null)
    {
        foreach (IMigrationStep step in MigrationStepScanner.Scan(assembly ?? Assembly.GetCallingAssembly(), factory: null, filter))
            _steps.Add(step);

        return this;
    }

    /// <summary>
    /// Finds every migration step in the assembly that contains <typeparamref name="T"/>.
    /// </summary>
    /// <typeparam name="T">Any type from the assembly to scan.</typeparam>
    /// <param name="filter">An extra predicate on the discovered types.</param>
    [RequiresUnreferencedCode(
        "Scanning an assembly for migration steps requires reflection over its types, which a trimmer cannot follow. " +
        "Use AddStep for each step when publishing trimmed.")]
    public MigrationEngineBuilder AddStepsFromAssemblyContaining<T>(Func<Type, bool>? filter = null) =>
        AddStepsFromAssembly(typeof(T).Assembly, filter);

    /// <summary>Replaces the built-in installation strategies.</summary>
    public MigrationEngineBuilder AddStrategy(IInstallationStrategy strategy)
    {
        _strategies.Add(strategy ?? throw new ArgumentNullException(nameof(strategy)));
        return this;
    }

    /// <summary>Replaces the default file-based journal.</summary>
    public MigrationEngineBuilder UseJournal(IMigrationJournal journal)
    {
        _journal = journal;
        return this;
    }

    /// <summary>Replaces the default file-based cross-process lock.</summary>
    public MigrationEngineBuilder UseLock(IMigrationLock migrationLock)
    {
        _lock = migrationLock;
        return this;
    }

    /// <summary>
    /// Switches to <see cref="UpdateTriggerMode.ManualInteractive"/> and registers the prompt.
    /// </summary>
    public MigrationEngineBuilder AskBeforeMigrating(IUpdatePromptService promptService)
    {
        _promptService = promptService ?? throw new ArgumentNullException(nameof(promptService));
        Options.TriggerMode = UpdateTriggerMode.ManualInteractive;
        return this;
    }

    /// <summary>Builds the engine. Validates the options and the whole step set.</summary>
    public MigrationEngine Build() =>
        new(Options, _steps, _strategies.Count > 0 ? _strategies : null, _journal, _lock, _promptService);
}
