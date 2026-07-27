// Copyright (C) Pham The Hung and Barbatos.Migration Contributors.
// All Rights Reserved.

using System.Text;

namespace Barbatos.Migration;

/// <summary>
/// The ordered list of steps that takes the data from one version to another, plus the
/// direction they run in. Building a plan is pure computation - no I/O, no side effects - so
/// an application can show the user exactly what is about to happen before anything is touched,
/// and tests can assert on the plan without a disk.
/// </summary>
public sealed class MigrationPlan
{
    private MigrationPlan(
        Version fromVersion,
        Version toVersion,
        MigrationDirection direction,
        IReadOnlyList<IMigrationStep> steps)
    {
        FromVersion = fromVersion;
        ToVersion = toVersion;
        Direction = direction;
        Steps = steps;
    }

    /// <summary>The version the data is at now.</summary>
    public Version FromVersion { get; }

    /// <summary>The version the plan ends at.</summary>
    public Version ToVersion { get; }

    /// <summary>Which way the plan runs.</summary>
    public MigrationDirection Direction { get; }

    /// <summary>
    /// The steps to run, already in execution order: ascending for an upgrade, descending for a
    /// downgrade.
    /// </summary>
    public IReadOnlyList<IMigrationStep> Steps { get; }

    /// <summary><see langword="true"/> when there is nothing to do.</summary>
    public bool IsEmpty => Steps.Count == 0;

    /// <summary>
    /// The number of versions skipped over. A user who upgrades from 1.0 straight to 2.0 having
    /// ignored 1.1, 1.2 and 1.3 produces a plan with four steps - they all run inside a single
    /// snapshot, so a failure at 1.3 still returns the data to 1.0 rather than stranding it
    /// halfway.
    /// </summary>
    public int HopCount => Steps.Count;

    /// <summary>
    /// Builds the plan. Pure: no data is read or written.
    /// </summary>
    /// <param name="steps">Every registered step, in any order.</param>
    /// <param name="currentVersion">The version the data is at.</param>
    /// <param name="targetVersion">The version the application needs.</param>
    /// <returns>The plan, possibly empty.</returns>
    /// <exception cref="MigrationPlanException">
    /// Two steps declare the same <see cref="IMigrationStep.TargetVersion"/> or the same
    /// <see cref="IMigrationStep.Id"/>, or a downgrade crosses a step that cannot be undone.
    /// </exception>
    public static MigrationPlan Create(IEnumerable<IMigrationStep> steps, Version currentVersion, Version targetVersion)
    {
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(currentVersion);
        ArgumentNullException.ThrowIfNull(targetVersion);

        List<IMigrationStep> all = ValidateSteps(steps);

        int comparison = currentVersion.CompareTo(targetVersion);
        if (comparison == 0)
            return new MigrationPlan(currentVersion, targetVersion, MigrationDirection.Upgrade, []);

        if (comparison < 0)
        {
            // Upgrade: every step whose target sits in (current, target].
            List<IMigrationStep> up = all
                .Where(s => s.TargetVersion > currentVersion && s.TargetVersion <= targetVersion)
                .OrderBy(s => s.TargetVersion)
                .ToList();

            return new MigrationPlan(currentVersion, targetVersion, MigrationDirection.Upgrade, up);
        }

        // Downgrade: undo every step whose target sits in (target, current], newest first, so
        // 2.2 is undone before 2.1. A step whose providers cannot be undone makes the whole
        // plan impossible - and it is much better to find that out here, before the snapshot
        // has been taken, than three steps into the run.
        List<IMigrationStep> down = all
            .Where(s => s.TargetVersion > targetVersion && s.TargetVersion <= currentVersion)
            .OrderByDescending(s => s.TargetVersion)
            .ToList();

        List<string> irreversible = down
            .SelectMany(s => s.Providers.Where(p => !p.CanDown).Select(p => $"{s.TargetVersion}/{p.Name}"))
            .ToList();

        if (irreversible.Count > 0)
        {
            throw new MigrationPlanException(
                $"Cannot downgrade data from {currentVersion} to {targetVersion}: " +
                $"{irreversible.Count} provider(s) are forward-only ({string.Join(", ", irreversible)}). " +
                "Either implement DownAsync on them, or use InstallationModel.SideBySideMultiFolder " +
                "so the user can simply run the older build against its own data folder.");
        }

        return new MigrationPlan(currentVersion, targetVersion, MigrationDirection.Downgrade, down);
    }

    /// <summary>
    /// Renders the plan as a short multi-line summary, for logs and for "what will happen"
    /// confirmation dialogs.
    /// </summary>
    public string Describe()
    {
        if (IsEmpty)
            return $"Data is already at {FromVersion}; nothing to do.";

        StringBuilder builder = new();
        builder.Append(Direction == MigrationDirection.Upgrade ? "Upgrade " : "Downgrade ")
            .Append(FromVersion).Append(" -> ").Append(ToVersion)
            .Append(" (").Append(Steps.Count).Append(Steps.Count == 1 ? " step" : " steps").AppendLine("):");

        foreach (IMigrationStep step in Steps)
        {
            builder.Append("  ").Append(step.TargetVersion).Append(" - ").Append(step.Description)
                .Append(" [").Append(string.Join(", ", step.Providers.Select(p => p.Name))).AppendLine("]");
        }

        return builder.ToString().TrimEnd();
    }

    /// <inheritdoc />
    public override string ToString() =>
        IsEmpty ? $"{FromVersion} (up to date)" : $"{Direction}: {FromVersion} -> {ToVersion}, {Steps.Count} step(s)";

    /// <summary>
    /// Checks the step set for the mistakes that make <em>every</em> plan invalid - a null step,
    /// a step with no providers, two steps reaching the same version, two steps sharing an id.
    /// Called by <see cref="Create"/> and, independently of any particular version pair, by
    /// <see cref="MigrationEngine"/>'s constructor.
    /// </summary>
    internal static List<IMigrationStep> ValidateSteps(IEnumerable<IMigrationStep> steps)
    {
        List<IMigrationStep> all = [];
        Dictionary<Version, IMigrationStep> byVersion = [];
        Dictionary<string, IMigrationStep> byId = new(StringComparer.Ordinal);

        foreach (IMigrationStep step in steps)
        {
            if (step == null)
                throw new MigrationPlanException("A registered migration step is null.");
            if (step.TargetVersion == null)
                throw new MigrationPlanException($"Migration step '{step.Id}' has no target version.");
            if (step.Providers == null || step.Providers.Count == 0)
                throw new MigrationPlanException($"Migration step '{step.Id}' ({step.TargetVersion}) has no providers.");

            if (byVersion.TryGetValue(step.TargetVersion, out IMigrationStep? duplicateVersion))
            {
                throw new MigrationPlanException(
                    $"Two migration steps target version {step.TargetVersion}: '{duplicateVersion.Id}' and '{step.Id}'. " +
                    "Each version must be reached by exactly one step - merge them, or give one a different version.");
            }

            if (byId.TryGetValue(step.Id, out IMigrationStep? duplicateId))
            {
                throw new MigrationPlanException(
                    $"Two migration steps share the id '{step.Id}' (versions {duplicateId.TargetVersion} and {step.TargetVersion}). " +
                    "Step ids are what an installed copy uses to recognise steps it has already applied, so they must be unique.");
            }

            byVersion.Add(step.TargetVersion, step);
            byId.Add(step.Id, step);
            all.Add(step);
        }

        return all;
    }
}
