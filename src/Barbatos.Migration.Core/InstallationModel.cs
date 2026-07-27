// Copyright (C) Pham The Hung and Barbatos.Migration Contributors.
// All Rights Reserved.

namespace Barbatos.Migration;

/// <summary>
/// How the host application lays its versions out on disk, which decides how the engine
/// protects user data while a migration runs.
/// </summary>
public enum InstallationModel
{
    /// <summary>
    /// Every version of the app shares one data folder (<c>AppData/MyApp/Data</c>). Upgrading
    /// rewrites that folder in place, so the engine takes a full snapshot beforehand and
    /// restores it if anything goes wrong. Downgrade is possible when every step involved
    /// implements <see cref="IMigrationProvider.DownAsync"/>.
    /// </summary>
    InPlaceSingleFolder,

    /// <summary>
    /// Each version owns its data folder (<c>AppData/MyApp/2.0.0</c>). Upgrading clones the
    /// previous version's folder and migrates the clone, so the old folder is never touched and
    /// "rolling back" is just launching the old build again. Forward-only by nature: a
    /// downgrade means running the older version, not transforming data backwards.
    /// </summary>
    SideBySideMultiFolder,
}
