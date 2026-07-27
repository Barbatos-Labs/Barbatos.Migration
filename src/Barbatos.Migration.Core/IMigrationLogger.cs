// Copyright (C) Pham The Hung and Barbatos.Migration Contributors.
// All Rights Reserved.

namespace Barbatos.Migration;

/// <summary>
/// The engine's logging sink. Deliberately a one-method interface of its own rather than
/// <c>Microsoft.Extensions.Logging.ILogger</c>, so that <c>Barbatos.Migration.Core</c> stays
/// dependency-free and usable from Unity. <c>Barbatos.Migration.DependencyInjection</c> ships an
/// adapter that forwards to <c>ILogger</c>.
/// </summary>
/// <remarks>
/// A migration is the one moment where an application rewrites data it can never regenerate,
/// and the log is often the only evidence available when a user reports data loss. The engine
/// logs every phase transition, every step, and every rollback decision.
/// </remarks>
public interface IMigrationLogger
{
    /// <summary>Writes one entry.</summary>
    /// <param name="level">How severe the entry is.</param>
    /// <param name="message">The message.</param>
    /// <param name="exception">The associated exception, if any.</param>
    void Log(MigrationLogLevel level, string message, Exception? exception = null);
}

/// <summary>Severity levels for <see cref="IMigrationLogger"/>.</summary>
public enum MigrationLogLevel
{
    /// <summary>Fine-grained detail, e.g. individual files being copied.</summary>
    Debug,

    /// <summary>Normal progress: phases, steps, versions.</summary>
    Information,

    /// <summary>Something recoverable, e.g. an old backup that could not be pruned.</summary>
    Warning,

    /// <summary>A migration failed and was rolled back.</summary>
    Error,

    /// <summary>A rollback failed. User data may be inconsistent.</summary>
    Critical,
}

/// <summary>An <see cref="IMigrationLogger"/> that discards everything.</summary>
public sealed class NullMigrationLogger : IMigrationLogger
{
    /// <summary>The shared instance.</summary>
    public static readonly NullMigrationLogger Instance = new();

    private NullMigrationLogger()
    {
    }

    /// <inheritdoc />
    public void Log(MigrationLogLevel level, string message, Exception? exception = null)
    {
    }
}

/// <summary>
/// An <see cref="IMigrationLogger"/> that forwards to a delegate - handy for Unity
/// (<c>UnityEngine.Debug.Log</c>) and for tests.
/// </summary>
public sealed class DelegateMigrationLogger : IMigrationLogger
{
    private readonly Action<MigrationLogLevel, string, Exception?> _write;

    /// <summary>Creates the logger over <paramref name="write"/>.</summary>
    public DelegateMigrationLogger(Action<MigrationLogLevel, string, Exception?> write)
    {
        _write = write ?? throw new ArgumentNullException(nameof(write));
    }

    /// <inheritdoc />
    public void Log(MigrationLogLevel level, string message, Exception? exception = null) =>
        _write(level, message, exception);
}
