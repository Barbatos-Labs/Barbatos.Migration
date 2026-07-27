// Copyright (C) Pham The Hung and Barbatos.Migration Contributors.
// All Rights Reserved.

namespace Barbatos.Migration;

/// <summary>
/// Everything a provider needs to know about the run it is taking part in.
/// </summary>
/// <remarks>
/// <para>
/// Providers must do all their reading and writing under <see cref="WorkingDirectory"/> and
/// must never touch <see cref="OriginalDirectory"/> or <see cref="BackupDirectory"/>. Under
/// <see cref="InstallationModel.SideBySideMultiFolder"/> those are three different places, and
/// hard-coding the data path instead of using <see cref="WorkingDirectory"/> is the single
/// easiest way to corrupt the version the user can still fall back to.
/// </para>
/// </remarks>
public interface IMigrationContext
{
    /// <summary>The data version the run started from.</summary>
    Version CurrentDataVersion { get; }

    /// <summary>The data version the run is heading for.</summary>
    Version TargetDataVersion { get; }

    /// <summary>Whether the run is moving forwards or backwards.</summary>
    MigrationDirection Direction { get; }

    /// <summary>The installation model in force.</summary>
    InstallationModel Model { get; }

    /// <summary>
    /// The directory providers must operate on. For <see cref="InstallationModel.InPlaceSingleFolder"/>
    /// this is the live data directory; for <see cref="InstallationModel.SideBySideMultiFolder"/>
    /// it is the freshly cloned new-version directory.
    /// </summary>
    string WorkingDirectory { get; }

    /// <summary>
    /// The directory the data came from. Same as <see cref="WorkingDirectory"/> for in-place
    /// installs; the previous version's (read-only, must not be modified) directory for
    /// side-by-side installs.
    /// </summary>
    string OriginalDirectory { get; }

    /// <summary>
    /// Where the pre-migration snapshot lives for this run, or <see langword="null"/> when the
    /// strategy does not take one (side-by-side, where the untouched original is the backup).
    /// Never write here.
    /// </summary>
    string? BackupDirectory { get; }

    /// <summary>The engine's logger, so providers log into the same stream as the engine.</summary>
    IMigrationLogger Logger { get; }

    /// <summary>
    /// Free-form state shared between the providers of a run - for example a database
    /// connection opened by one provider and reused by the next. Cleared between runs.
    /// </summary>
    IDictionary<string, object?> Items { get; }

    /// <summary>Resolves <paramref name="relativePath"/> against <see cref="WorkingDirectory"/>.</summary>
    /// <param name="relativePath">A path relative to the working directory.</param>
    /// <returns>The absolute path.</returns>
    string GetWorkingPath(string relativePath);
}
