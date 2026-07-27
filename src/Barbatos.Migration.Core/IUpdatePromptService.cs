// Copyright (C) Pham The Hung and Barbatos.Migration Contributors.
// All Rights Reserved.

namespace Barbatos.Migration;

/// <summary>
/// What the user is being asked to agree to under
/// <see cref="UpdateTriggerMode.ManualInteractive"/>.
/// </summary>
public sealed class MigrationPromptContext
{
    internal MigrationPromptContext(MigrationPlan plan, InstallationModel model, bool canDefer, long estimatedDataSizeBytes)
    {
        Plan = plan;
        Model = model;
        CanDefer = canDefer;
        EstimatedDataSizeBytes = estimatedDataSizeBytes;
    }

    /// <summary>Exactly what will run, in order. Show <see cref="MigrationPlan.Describe"/> to power users.</summary>
    public MigrationPlan Plan { get; }

    /// <summary>The installation model, which decides what "not now" actually costs.</summary>
    public InstallationModel Model { get; }

    /// <summary>
    /// Whether declining is a real option.
    /// </summary>
    /// <remarks>
    /// <see langword="false"/> means the application has declared - through
    /// <see cref="MigrationOptions.AllowRunningOnOlderData"/> - that it cannot run against the
    /// old data. The prompt should then explain that the choice is between migrating and
    /// closing the application, and must not offer a "Remind me later" that leads nowhere.
    /// </remarks>
    public bool CanDefer { get; }

    /// <summary>
    /// How much data will be copied before the migration starts, in bytes, so the prompt can
    /// say "this will take a few minutes" when it actually will. <c>0</c> when unknown.
    /// </summary>
    public long EstimatedDataSizeBytes { get; }

    /// <summary>
    /// Free-form release notes, filled in by the application before the prompt is shown.
    /// </summary>
    public string? ReleaseNotes { get; set; }
}

/// <summary>
/// Asks the user whether to migrate now. Only consulted when
/// <see cref="MigrationOptions.TriggerMode"/> is
/// <see cref="UpdateTriggerMode.ManualInteractive"/>.
/// </summary>
public interface IUpdatePromptService
{
    /// <summary>
    /// Returns <see langword="true"/> to migrate now, <see langword="false"/> to postpone.
    /// </summary>
    /// <remarks>
    /// Called before anything is backed up or written, so declining costs nothing. Cancelling
    /// the token counts as declining.
    /// </remarks>
    Task<bool> ConfirmAsync(MigrationPromptContext context, CancellationToken cancellationToken);
}
