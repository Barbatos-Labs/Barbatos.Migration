// Copyright (C) Pham The Hung and Barbatos.Migration Contributors.
// All Rights Reserved.

using Barbatos.Migration.FileSystem;

namespace Barbatos.Migration.Wpf.Sample.Migrations;

/// <summary>
/// A folder reorganisation. Declaring the operations rather than writing them means the
/// downgrade is derived automatically, in reverse order.
/// </summary>
[MigrationStep("1.2.0", "Move documents into a workspace folder")]
public sealed class MoveDocumentsIntoWorkspace : MigrationStepBase
{
    protected override IEnumerable<IMigrationProvider> CreateProviders()
    {
        yield return new FileSystemMigrationProvider("Reorganise the data folder", operations => operations
            .EnsureDirectory("workspace")
            .MoveDirectory("documents", "workspace/documents"));
    }
}
