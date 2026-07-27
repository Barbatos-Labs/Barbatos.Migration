// Copyright (C) Pham The Hung and Barbatos.Migration Contributors.
// All Rights Reserved.

namespace Barbatos.Migration;

/// <summary>
/// One version bump. Everything a step contains is applied together: the run either reaches
/// <see cref="TargetVersion"/> with every provider done, or the whole run is rolled back.
/// </summary>
public interface IMigrationStep
{
    /// <summary>
    /// A stable identifier for this step, written to the journal and to the applied-steps
    /// ledger. Defaults to the version string; give it something more descriptive
    /// (<c>"2.0.0-split-user-table"</c>) if you ever expect to reorder or rename steps.
    /// <b>Never change it once shipped</b> - it is what tells an installed copy which steps it
    /// has already seen.
    /// </summary>
    string Id { get; }

    /// <summary>
    /// The data version reached once this step completes. Must be unique across all registered
    /// steps, and is what the engine sorts and filters on.
    /// </summary>
    Version TargetVersion { get; }

    /// <summary>A human-readable summary, shown in the progress UI.</summary>
    string Description { get; }

    /// <summary>
    /// The providers to run, in order. They run sequentially, never in parallel: two providers
    /// racing over the same data directory is exactly the kind of corruption this framework
    /// exists to prevent.
    /// </summary>
    IReadOnlyList<IMigrationProvider> Providers { get; }
}

/// <summary>
/// The ready-made <see cref="IMigrationStep"/>. Most applications never need another one.
/// </summary>
public sealed class MigrationStep : IMigrationStep
{
    /// <summary>Creates a step.</summary>
    /// <param name="targetVersion">The data version this step reaches.</param>
    /// <param name="description">A human-readable summary.</param>
    /// <param name="providers">The providers to run, in order.</param>
    public MigrationStep(Version targetVersion, string description, params IMigrationProvider[] providers)
        : this(targetVersion, description, (IEnumerable<IMigrationProvider>)providers, id: null)
    {
    }

    /// <summary>Creates a step with an explicit <see cref="Id"/>.</summary>
    /// <param name="targetVersion">The data version this step reaches.</param>
    /// <param name="description">A human-readable summary.</param>
    /// <param name="providers">The providers to run, in order.</param>
    /// <param name="id">A stable identifier; defaults to the version string.</param>
    public MigrationStep(Version targetVersion, string description, IEnumerable<IMigrationProvider> providers, string? id = null)
    {
        TargetVersion = targetVersion ?? throw new ArgumentNullException(nameof(targetVersion));
        Description = description ?? string.Empty;

        ArgumentNullException.ThrowIfNull(providers);

        List<IMigrationProvider> list = new(providers);
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i] == null)
                throw new ArgumentException($"Provider at index {i} of step '{targetVersion}' is null.", nameof(providers));
            if (list[i].Weight <= 0)
                throw new ArgumentException($"Provider '{list[i].Name}' of step '{targetVersion}' declares a non-positive weight.", nameof(providers));
        }

        if (list.Count == 0)
            throw new ArgumentException($"Step '{targetVersion}' has no providers.", nameof(providers));

        Providers = list;
        Id = id ?? targetVersion.ToString();
    }

    /// <inheritdoc />
    public string Id { get; }

    /// <inheritdoc />
    public Version TargetVersion { get; }

    /// <inheritdoc />
    public string Description { get; }

    /// <inheritdoc />
    public IReadOnlyList<IMigrationProvider> Providers { get; }

    /// <inheritdoc />
    public override string ToString() => $"{TargetVersion} - {Description}";
}
