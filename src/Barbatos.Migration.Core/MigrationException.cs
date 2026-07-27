// Copyright (C) Pham The Hung and Barbatos.Migration Contributors.
// All Rights Reserved.

namespace Barbatos.Migration;

/// <summary>
/// Thrown when a migration cannot run at all - as opposed to a step failing mid-run, which is
/// reported through <see cref="MigrationResult.Error"/> after the data has been rolled back.
/// </summary>
public class MigrationException : Exception
{
    /// <summary>Creates the exception with a message.</summary>
    public MigrationException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and an inner exception.</summary>
    public MigrationException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Thrown when the registered steps cannot form a valid plan - duplicate target versions, a
/// downgrade across a step that has no <see cref="IMigrationProvider.DownAsync"/>, or a gap the
/// engine is not allowed to skip.
/// </summary>
public sealed class MigrationPlanException : MigrationException
{
    /// <summary>Creates the exception with a message.</summary>
    public MigrationPlanException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and an inner exception.</summary>
    public MigrationPlanException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Thrown when another process (another instance of the app, or its updater) is already
/// migrating the same data directory.
/// </summary>
public sealed class MigrationLockException : MigrationException
{
    /// <summary>Creates the exception with a message.</summary>
    public MigrationLockException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Thrown when a rollback fails. The data directory may be in an inconsistent state, and
/// <see cref="BackupDirectory"/> - which the engine deliberately leaves on disk in this case -
/// holds the last known-good copy.
/// </summary>
public sealed class MigrationRollbackException : MigrationException
{
    /// <summary>Creates the exception with a message, the surviving backup and the causes.</summary>
    public MigrationRollbackException(string message, string? backupDirectory, Exception? originalError, Exception? rollbackError)
        : base(message, rollbackError ?? originalError)
    {
        BackupDirectory = backupDirectory;
        OriginalError = originalError;
        RollbackError = rollbackError;
    }

    /// <summary>The snapshot that was not restored, and which is now the user's only intact copy.</summary>
    public string? BackupDirectory { get; }

    /// <summary>The failure that triggered the rollback, if the rollback was not caused by a cancellation.</summary>
    public Exception? OriginalError { get; }

    /// <summary>The failure that broke the rollback itself.</summary>
    public Exception? RollbackError { get; }
}
