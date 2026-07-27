// Copyright (C) Pham The Hung and Barbatos.Migration Contributors.
// All Rights Reserved.

namespace Barbatos.Migration;

/// <summary>
/// The direction a <see cref="MigrationPlan"/> runs in.
/// </summary>
public enum MigrationDirection
{
    /// <summary>
    /// Data is older than the app: steps run in ascending version order through
    /// <see cref="IMigrationProvider.UpAsync"/>.
    /// </summary>
    Upgrade,

    /// <summary>
    /// Data is newer than the app: steps run in descending version order through
    /// <see cref="IMigrationProvider.DownAsync"/>. Only reachable under
    /// <see cref="InstallationModel.InPlaceSingleFolder"/>, and only when every step that has
    /// to be undone reports <see cref="IMigrationProvider.CanDown"/>.
    /// </summary>
    Downgrade,
}
