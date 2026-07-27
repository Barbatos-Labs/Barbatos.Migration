// Copyright (C) Pham The Hung and Barbatos.Migration Contributors.
// All Rights Reserved.

namespace Barbatos.Migration;

/// <summary>
/// Declares the version and description of a migration step, so the class itself carries them
/// instead of a registration call somewhere else.
/// </summary>
/// <remarks>
/// <para>
/// Put one step in one file and the file says what it is at the top. That matters most for the
/// steps that actually need the room - a step whose <c>UpAsync</c> runs to two hundred lines is
/// unreadable when it is wedged into a builder chain alongside five others.
/// </para>
/// <para>
/// Read by <see cref="MigrationStepBase"/> and by the assembly scanners; a class that implements
/// <see cref="IMigrationStep"/> by hand does not need it.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// [MigrationStep("2.0.0", "Split the full name into first and last")]
/// public sealed class SplitUserName : CodeMigrationStep
/// {
///     public override async Task UpAsync(IMigrationContext context, IProgress&lt;MigrationProgress&gt;? progress, CancellationToken ct)
///     {
///         // ... however long it needs to be, alone in its own file
///     }
/// }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class MigrationStepAttribute : Attribute
{
    /// <summary>Declares the step.</summary>
    /// <param name="version">The data version this step reaches, e.g. <c>"2.0.0"</c>.</param>
    /// <param name="description">A human-readable summary, shown in the progress UI.</param>
    public MigrationStepAttribute(string version, string description = "")
    {
        if (string.IsNullOrWhiteSpace(version))
            throw new ArgumentException("A version is required.", nameof(version));

        if (!Version.TryParse(version, out Version? parsed))
            throw new ArgumentException($"'{version}' is not a valid version.", nameof(version));

        Version = parsed;
        Description = description ?? string.Empty;
    }

    /// <summary>The data version this step reaches.</summary>
    public Version Version { get; }

    /// <summary>A human-readable summary of what the step does.</summary>
    public string Description { get; }

    /// <summary>
    /// A stable identifier for the applied-steps ledger. Defaults to the class name, which is
    /// why <b>renaming the class after shipping it changes its identity</b> - set this
    /// explicitly if you ever expect to rename or reorganise.
    /// </summary>
    public string? Id { get; set; }
}
