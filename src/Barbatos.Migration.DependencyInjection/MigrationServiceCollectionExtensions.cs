// Copyright (C) Pham The Hung and Barbatos.Migration Contributors.
// All Rights Reserved.

using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Barbatos.Migration.DependencyInjection;

/// <summary>
/// Registers Barbatos.Migration in an <see cref="IServiceCollection"/>.
/// </summary>
public static class MigrationServiceCollectionExtensions
{
    /// <summary>
    /// Adds the migration engine and returns a builder for declaring the steps.
    /// </summary>
    /// <remarks>
    /// The engine is a singleton and is <em>not</em> run for you: migrations have to happen at
    /// a point the application chooses, before anything opens the data, and only the
    /// application knows where that point is. Resolve <see cref="MigrationEngine"/> and call
    /// <see cref="MigrationEngine.RunAsync"/> from your startup path - or use
    /// <c>Barbatos.Migration.Wpf</c>, which has a startup path to hook into.
    /// </remarks>
    public static MigrationBuilder AddBarbatosMigration(this IServiceCollection services, Action<MigrationOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (configure != null)
            services.Configure(configure);

        services.TryAddSingleton<IMigrationLogger>(provider =>
            new LoggerMigrationLogger(provider.GetRequiredService<ILoggerFactory>().CreateLogger("Barbatos.Migration")));

        services.TryAddSingleton(provider =>
        {
            MigrationOptions options = provider.GetRequiredService<IOptions<MigrationOptions>>().Value;

            // Wiring the container's logger in here rather than making callers set it means the
            // engine's log ends up in the same place as everything else the application logs -
            // which is where anyone investigating a data-loss report will look first.
            options.Logger = provider.GetRequiredService<IMigrationLogger>();

            return new MigrationEngine(
                options,
                provider.GetServices<IMigrationStep>(),
                provider.GetServices<IInstallationStrategy>() is { } strategies && HasAny(strategies) ? strategies : null,
                provider.GetService<IMigrationJournal>(),
                provider.GetService<IMigrationLock>(),
                provider.GetService<IUpdatePromptService>());
        });

        return new MigrationBuilder(services);
    }

    private static bool HasAny(IEnumerable<IInstallationStrategy> strategies)
    {
        foreach (IInstallationStrategy _ in strategies)
            return true;

        return false;
    }
}

/// <summary>
/// Declares migration steps and services on an <see cref="IServiceCollection"/>.
/// </summary>
public sealed class MigrationBuilder
{
    internal MigrationBuilder(IServiceCollection services)
    {
        Services = services;
    }

    /// <summary>The service collection being configured.</summary>
    public IServiceCollection Services { get; }

    /// <summary>Registers an already-constructed step.</summary>
    public MigrationBuilder AddStep(IMigrationStep step)
    {
        ArgumentNullException.ThrowIfNull(step);

        Services.AddSingleton(step);
        return this;
    }

    /// <summary>Registers a step resolved from the container, so it can take dependencies.</summary>
    public MigrationBuilder AddStep<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TStep>() where TStep : class, IMigrationStep
    {
        Services.AddSingleton<IMigrationStep, TStep>();
        return this;
    }

    /// <summary>Registers a step built from the container.</summary>
    public MigrationBuilder AddStep(Func<IServiceProvider, IMigrationStep> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        Services.AddSingleton(factory);
        return this;
    }

    /// <summary>Registers a step made of the given providers.</summary>
    public MigrationBuilder AddStep(string targetVersion, string description, params IMigrationProvider[] providers) =>
        AddStep(new MigrationStep(Version.Parse(targetVersion), description, providers));

    /// <summary>
    /// Finds every migration step in an assembly and registers it, so each one can live in its
    /// own file without a registration line to keep in sync.
    /// </summary>
    /// <param name="assembly">The assembly to scan; defaults to the caller's own.</param>
    /// <param name="filter">An extra predicate on the discovered types.</param>
    /// <remarks>
    /// Each discovered type is registered with the container rather than constructed here, so a
    /// step can take constructor dependencies like any other service - the connection factory,
    /// an <c>IOptions&lt;T&gt;</c>, a logger. Order does not matter: the engine sorts by
    /// version, and duplicate versions or ids are rejected when it is built.
    /// </remarks>
    /// <example>
    /// <code>
    /// services.AddBarbatosMigration(options => { ... })
    ///         .AddStepsFromAssembly();
    /// </code>
    /// </example>
    [RequiresUnreferencedCode(
        "Scanning an assembly for migration steps requires reflection over its types, which a trimmer cannot follow. " +
        "Use AddStep for each step when publishing trimmed.")]
    public MigrationBuilder AddStepsFromAssembly(Assembly? assembly = null, Func<Type, bool>? filter = null)
    {
        foreach (Type type in MigrationStepScanner.FindStepTypes(assembly ?? Assembly.GetCallingAssembly(), filter))
            Services.AddSingleton(typeof(IMigrationStep), type);

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
    public MigrationBuilder AddStepsFromAssemblyContaining<T>(Func<Type, bool>? filter = null) =>
        AddStepsFromAssembly(typeof(T).Assembly, filter);

    /// <summary>Replaces the built-in installation strategies.</summary>
    public MigrationBuilder AddStrategy<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TStrategy>() where TStrategy : class, IInstallationStrategy
    {
        Services.AddSingleton<IInstallationStrategy, TStrategy>();
        return this;
    }

    /// <summary>
    /// Registers the prompt shown under <see cref="UpdateTriggerMode.ManualInteractive"/>.
    /// </summary>
    public MigrationBuilder UsePrompt<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TPrompt>() where TPrompt : class, IUpdatePromptService
    {
        Services.TryAddSingleton<IUpdatePromptService, TPrompt>();
        return this;
    }

    /// <summary>Replaces the default file-based journal.</summary>
    public MigrationBuilder UseJournal<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TJournal>() where TJournal : class, IMigrationJournal
    {
        Services.TryAddSingleton<IMigrationJournal, TJournal>();
        return this;
    }

    /// <summary>Replaces the default file-based cross-process lock.</summary>
    public MigrationBuilder UseLock<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TLock>() where TLock : class, IMigrationLock
    {
        Services.TryAddSingleton<IMigrationLock, TLock>();
        return this;
    }
}

/// <summary>
/// Forwards <see cref="IMigrationLogger"/> to <see cref="ILogger"/>.
/// </summary>
internal sealed class LoggerMigrationLogger : IMigrationLogger
{
    private readonly ILogger _logger;

    public LoggerMigrationLogger(ILogger logger)
    {
        _logger = logger;
    }

    public void Log(MigrationLogLevel level, string message, Exception? exception = null)
    {
        LogLevel mapped = level switch
        {
            MigrationLogLevel.Debug => LogLevel.Debug,
            MigrationLogLevel.Information => LogLevel.Information,
            MigrationLogLevel.Warning => LogLevel.Warning,
            MigrationLogLevel.Error => LogLevel.Error,
            MigrationLogLevel.Critical => LogLevel.Critical,
            _ => LogLevel.Information,
        };

#pragma warning disable CA2254 // The message is already formatted; there is no template to preserve.
        _logger.Log(mapped, exception, message);
#pragma warning restore CA2254
    }
}
