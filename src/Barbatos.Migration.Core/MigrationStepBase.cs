// Copyright (C) Pham The Hung and Barbatos.Migration Contributors.
// All Rights Reserved.

using System.Reflection;

namespace Barbatos.Migration;

/// <summary>
/// Base class for a migration step that lives in its own file and declares itself with
/// <see cref="MigrationStepAttribute"/>.
/// </summary>
/// <remarks>
/// Derive from this when the step composes several providers. When the step <em>is</em> one
/// piece of logic - which is the case the one-file-per-step layout exists for - derive from
/// <see cref="CodeMigrationStep"/> instead and just write the code.
/// </remarks>
/// <example>
/// <code>
/// [MigrationStep("2.0.0", "Split the full name into first and last")]
/// public sealed class SplitUserName : MigrationStepBase
/// {
///     protected override IEnumerable&lt;IMigrationProvider&gt; CreateProviders()
///     {
///         yield return DatabaseMigrationProvider.ForFile("app.db", OpenConnection, up: [...], down: [...]);
///         yield return new CsvMigrationProvider("licences.csv", up: ..., down: ...);
///     }
/// }
/// </code>
/// </example>
public abstract class MigrationStepBase : IMigrationStep
{
    private IReadOnlyList<IMigrationProvider>? _providers;

    /// <summary>
    /// Reads <see cref="MigrationStepAttribute"/> off the derived type.
    /// </summary>
    /// <exception cref="MigrationPlanException">The derived type has no <see cref="MigrationStepAttribute"/>.</exception>
    protected MigrationStepBase()
    {
        Type type = GetType();
        MigrationStepAttribute attribute = type.GetCustomAttribute<MigrationStepAttribute>()
            ?? throw new MigrationPlanException(
                $"'{type.FullName}' derives from {nameof(MigrationStepBase)} but has no [MigrationStep] attribute, " +
                "so there is nothing to say which version it reaches. Add [MigrationStep(\"2.0.0\", \"...\")].");

        TargetVersion = attribute.Version;
        Description = attribute.Description.Length > 0 ? attribute.Description : type.Name;
        Id = attribute.Id ?? type.Name;
    }

    /// <inheritdoc />
    public string Id { get; }

    /// <inheritdoc />
    public Version TargetVersion { get; }

    /// <inheritdoc />
    public string Description { get; }

    /// <inheritdoc />
    /// <remarks>
    /// Built once, on first use. A step that never runs - because the user is already past its
    /// version - never constructs its providers at all, which matters when a provider opens a
    /// connection or reads a file just to exist.
    /// </remarks>
    public IReadOnlyList<IMigrationProvider> Providers => _providers ??= BuildProviders();

    /// <summary>
    /// Creates the providers this step runs, in order. Called at most once.
    /// </summary>
    protected abstract IEnumerable<IMigrationProvider> CreateProviders();

    /// <inheritdoc />
    public override string ToString() => $"{TargetVersion} - {Description}";

    private IReadOnlyList<IMigrationProvider> BuildProviders()
    {
        List<IMigrationProvider> providers = [.. CreateProviders() ?? []];

        if (providers.Count == 0)
        {
            throw new MigrationPlanException(
                $"Migration step '{Id}' ({TargetVersion}) returned no providers from {nameof(CreateProviders)}().");
        }

        if (providers.Any(provider => provider == null))
            throw new MigrationPlanException($"Migration step '{Id}' ({TargetVersion}) returned a null provider.");

        return providers;
    }
}

/// <summary>
/// A migration step that is its own single provider: put the logic straight into
/// <see cref="UpAsync"/> and the whole step is one file with no ceremony around it.
/// </summary>
/// <remarks>
/// This is the shape to reach for when a step's logic is long or intricate enough that it wants
/// a file to itself - which is exactly when burying it in a registration chain hurts most.
/// </remarks>
/// <example>
/// <code>
/// [MigrationStep("2.0.0", "Rebuild the search index")]
/// public sealed class RebuildSearchIndex : CodeMigrationStep
/// {
///     public override double Weight => 8.0;
///
///     public override async Task UpAsync(
///         IMigrationContext context,
///         IProgress&lt;MigrationProgress&gt;? progress,
///         CancellationToken cancellationToken)
///     {
///         string[] documents = Directory.GetFiles(context.GetWorkingPath("documents"));
///
///         for (int i = 0; i &lt; documents.Length; i++)
///         {
///             cancellationToken.ThrowIfCancellationRequested();
///             await IndexAsync(documents[i], cancellationToken);
///             progress?.Report(new MigrationProgress(i * 100.0 / documents.Length, $"Indexed {i + 1}/{documents.Length}"));
///         }
///     }
/// }
/// </code>
/// </example>
public abstract class CodeMigrationStep : MigrationStepBase, IMigrationProvider
{
    /// <inheritdoc />
    /// <remarks>Defaults to the step's description, so logs and progress read the same either way.</remarks>
    public virtual string Name => Description;

    /// <inheritdoc />
    public virtual double Weight => 1.0;

    /// <inheritdoc />
    public virtual bool CanDown => false;

    /// <summary>Applies the change. Honour <paramref name="cancellationToken"/> inside every loop.</summary>
    /// <param name="context">The run this step takes part in. Read and write only under <see cref="IMigrationContext.WorkingDirectory"/>.</param>
    /// <param name="progress">Reports 0-100 for this step alone; may be <see langword="null"/>.</param>
    /// <param name="cancellationToken">Honour this inside every loop.</param>
    public abstract Task UpAsync(IMigrationContext context, IProgress<MigrationProgress>? progress, CancellationToken cancellationToken);

    /// <summary>Undoes the change. Override together with <see cref="CanDown"/>.</summary>
    /// <param name="context">The run this step takes part in.</param>
    /// <param name="progress">Reports 0-100 for this step alone; may be <see langword="null"/>.</param>
    /// <param name="cancellationToken">Honour this inside every loop.</param>
    public virtual Task DownAsync(IMigrationContext context, IProgress<MigrationProgress>? progress, CancellationToken cancellationToken) =>
        throw new NotSupportedException(
            $"'{Id}' is forward-only. Override DownAsync and return true from CanDown to make it reversible.");

    /// <inheritdoc />
    protected sealed override IEnumerable<IMigrationProvider> CreateProviders() => [this];
}
