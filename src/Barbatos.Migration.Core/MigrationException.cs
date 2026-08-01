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

// A failed rollback is deliberately not an exception. RunAsync returns
// MigrationOutcome.RollbackFailed with MigrationResult.RollbackError and
// MigrationResult.BackupDirectory filled in, because the one case where the application most
// needs to make a considered decision - tell the user where their data is, refuse to start
// normally - is the worst possible case to express as something a caller can forget to catch.
