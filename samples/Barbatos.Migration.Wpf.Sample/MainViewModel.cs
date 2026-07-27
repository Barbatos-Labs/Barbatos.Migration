// Copyright (C) Pham The Hung and Barbatos.Migration Contributors.
// All Rights Reserved.

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Data.Common;
using System.Runtime.CompilerServices;
using System.Text;
using Barbatos.Wpf.Dispatching;

namespace Barbatos.Migration.Wpf.Sample;

/// <summary>
/// The playground behind the main window: a sandbox data folder the sample can seed, upgrade,
/// downgrade, break on purpose and cancel, so every outcome the engine can produce is one click
/// away.
/// </summary>
/// <remarks>
/// It builds its engines with <see cref="MigrationEngineBuilder"/> rather than resolving the one
/// in the container, because each button needs a different target version - and that shows the
/// second way the framework is used: no DI container at all, which is how it is meant to be
/// consumed from Unity or a console tool.
/// </remarks>
public sealed class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly IDispatcher _dispatcher;
    private CancellationTokenSource? _cancellation;

    private string _status = "Press “Reset to 1.0.0” to create a sandbox data folder.";
    private double _percentage;
    private bool _isIndeterminate;
    private bool _isRunning;
    private bool _useSideBySide;
    private string _dataVersion = "(no data yet)";

    public MainViewModel(IDispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        SandboxRoot = Path.Combine(Path.GetTempPath(), "Barbatos.Migration.Sample");
        Refresh();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Where the playground keeps its files, shown in the window so it can be opened.</summary>
    public string SandboxRoot { get; }

    /// <summary>Everything the engine logged, newest last.</summary>
    public ObservableCollection<string> Log { get; } = [];

    /// <summary>The current contents of the sandbox data folder.</summary>
    public ObservableCollection<string> Files { get; } = [];

    public string Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    public double Percentage
    {
        get => _percentage;
        private set => Set(ref _percentage, value);
    }

    public bool IsIndeterminate
    {
        get => _isIndeterminate;
        private set => Set(ref _isIndeterminate, value);
    }

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (Set(ref _isRunning, value))
                OnPropertyChanged(nameof(IsIdle));
        }
    }

    public bool IsIdle => !IsRunning;

    /// <summary>The version stamped on the sandbox data, read back from disk.</summary>
    public string DataVersion
    {
        get => _dataVersion;
        private set => Set(ref _dataVersion, value);
    }

    /// <summary>Switches the playground between the two installation models.</summary>
    public bool UseSideBySide
    {
        get => _useSideBySide;
        set
        {
            if (Set(ref _useSideBySide, value))
                Refresh();
        }
    }

    private string InPlaceDirectory => Path.Combine(SandboxRoot, "in-place");

    private string VersionRootDirectory => Path.Combine(SandboxRoot, "side-by-side");

    /// <summary>Wipes the sandbox and writes a data folder in its original 1.0.0 shape.</summary>
    public void Reset()
    {
        Log.Clear();

        TryDelete(InPlaceDirectory);
        TryDelete(VersionRootDirectory);
        TryDelete(Path.Combine(SandboxRoot, ".migration"));

        SampleData.SeedVersion1(UseSideBySide
            ? Path.Combine(VersionRootDirectory, "1.0.0")
            : InPlaceDirectory);

        Percentage = 0;
        Status = "Sandbox reset to 1.0.0.";
        Append("Seeded a 1.0.0 data folder: settings.json, plugins.ini, licences.csv, documents/, app.db with 3 users.");
        Refresh();
    }

    /// <summary>Migrates the sandbox up to 2.0.0.</summary>
    public Task UpgradeAsync(bool failMidway) =>
        RunAsync(SampleData.CurrentVersion, failMidway, "Upgrading to 2.0.0...");

    /// <summary>
    /// Migrates the sandbox back down to 1.0.0. Only possible under the in-place model, and only
    /// because every step in this sample implements a downgrade.
    /// </summary>
    public Task DowngradeAsync() =>
        RunAsync(SampleData.InitialVersion, failMidway: false, "Downgrading to 1.0.0...");

    /// <summary>Requests cancellation; the engine then restores the data.</summary>
    public void Cancel()
    {
        Status = "Cancelling...";
        _cancellation?.Cancel();
    }

    private async Task RunAsync(Version target, bool failMidway, string startingStatus)
    {
        if (IsRunning)
            return;

        IsRunning = true;
        Percentage = 0;
        Status = startingStatus;

        _cancellation = new CancellationTokenSource();

        try
        {
            MigrationEngineBuilder builder = new MigrationEngineBuilder()
                .TargetVersion(target)
                .StartingFromVersion(SampleData.InitialVersion)
                .LogTo((level, message, exception) => Append(exception == null
                    ? $"[{level}] {message}"
                    : $"[{level}] {message} -- {exception.GetType().Name}: {exception.Message}"))
                .Configure(options =>
                {
                    options.AllowDowngrade = true;
                    options.BackupRetentionCount = 1;
                    options.SkipFreeSpaceCheck = true;
                });

            if (UseSideBySide)
                builder.UseSideBySideModel(VersionRootDirectory);
            else
                builder.UseInPlaceModel().UseDataDirectory(InPlaceDirectory);

            // The same steps the startup path uses - one [Migration] class per file under
            // Migrations/, found by scanning rather than listed here.
            SampleData.SimulateFailure = failMidway;
            builder.AddStepsFromAssembly(typeof(SampleData).Assembly);

            MigrationEngine engine = builder.Build();

            Append(engine.CreatePlan().Describe());

            Progress<MigrationProgress> progress = new(report =>
            {
                Percentage = report.Percentage;
                IsIndeterminate = report.IsIndeterminate;
                Status = $"[{report.Phase}] {report.Detail}";
            });

            // Task.Run, because the engine's directory copying is synchronous I/O and would
            // otherwise freeze this window for the whole migration.
            MigrationResult result = await Task.Run(
                () => engine.RunAsync(progress, _cancellation.Token),
                _cancellation.Token);

            Status = Describe(result);
            Append($"==> {result}");

            if (result.BackupDirectory != null)
                Append($"    A backup was kept at {result.BackupDirectory}");
        }
        catch (Exception ex)
        {
            Status = $"Unexpected failure: {ex.Message}";
            Append($"[Critical] {ex}");
        }
        finally
        {
            SampleData.SimulateFailure = false;
            _cancellation?.Dispose();
            _cancellation = null;
            IsIndeterminate = false;
            IsRunning = false;
            Refresh();
        }
    }

    private static string Describe(MigrationResult result) => result.Outcome switch
    {
        MigrationOutcome.Succeeded => $"Done. Data is now at {result.CurrentVersion}.",
        MigrationOutcome.UpToDate => $"Nothing to do - data is already at {result.CurrentVersion}.",
        MigrationOutcome.Canceled => $"Cancelled. Data was restored to {result.CurrentVersion}.",
        MigrationOutcome.Failed => $"Failed, and the data was restored to {result.CurrentVersion}. {result.Error?.Message}",
        MigrationOutcome.RollbackFailed => "The rollback itself failed - the data may be inconsistent.",
        MigrationOutcome.Blocked => $"Blocked: {result.Error?.Message}",
        MigrationOutcome.Deferred => "The user postponed the migration.",
        _ => result.ToString(),
    };

    private void Refresh()
    {
        string directory = UseSideBySide ? NewestVersionDirectory() : InPlaceDirectory;

        Files.Clear();

        if (!Directory.Exists(directory))
        {
            DataVersion = "(no data yet)";
            Files.Add("(the sandbox is empty - press “Reset to 1.0.0”)");
            return;
        }

        DataVersion = new FileDataVersionStore(directory).Read()?.ToString() ?? "(not stamped)";

        foreach (string path in Directory
            .EnumerateFileSystemEntries(directory, "*", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            string relative = path.Substring(directory.Length).TrimStart(Path.DirectorySeparatorChar);
            Files.Add(Directory.Exists(path) ? relative + Path.DirectorySeparatorChar : relative);
        }

        if (UseSideBySide)
            Files.Add($"--- reading version folder: {Path.GetFileName(directory)} ---");

        AppendDatabaseSummary(directory);

        // The INI file is the one worth watching: the comments and their alignment are still
        // there after the migration renamed a section and two keys underneath them.
        AppendTextPreview(directory, SampleData.LegacySettingsFileName);
        AppendTextPreview(directory, SampleData.SettingsFileName);
        AppendTextPreview(directory, SampleData.LicencesFileName);
    }

    private void AppendTextPreview(string directory, string fileName)
    {
        string path = Path.Combine(directory, fileName);
        if (!File.Exists(path))
            return;

        Files.Add($"--- {fileName} ---");

        foreach (string line in File.ReadAllLines(path))
            Files.Add("    " + line);
    }

    private string NewestVersionDirectory()
    {
        if (!Directory.Exists(VersionRootDirectory))
            return Path.Combine(VersionRootDirectory, "1.0.0");

        return Directory
            .EnumerateDirectories(VersionRootDirectory)
            .Where(path => Version.TryParse(Path.GetFileName(path), out _))
            .OrderByDescending(path => Version.Parse(Path.GetFileName(path)))
            .FirstOrDefault() ?? Path.Combine(VersionRootDirectory, "1.0.0");
    }

    private void AppendDatabaseSummary(string directory)
    {
        string database = Path.Combine(directory, SampleData.DatabaseFileName);
        if (!File.Exists(database))
            return;

        try
        {
            using DbConnection connection = SampleData.OpenConnection(database);
            connection.Open();

            using DbCommand columns = connection.CreateCommand();
            columns.CommandText = "SELECT name FROM pragma_table_info('Users');";

            StringBuilder schema = new("Users(");
            using (DbDataReader reader = columns.ExecuteReader())
            {
                bool first = true;
                while (reader.Read())
                {
                    if (!first)
                        schema.Append(", ");

                    schema.Append(reader.GetString(0));
                    first = false;
                }
            }

            schema.Append(')');
            Files.Add("--- " + schema + " ---");

            using DbCommand rows = connection.CreateCommand();
            rows.CommandText = "SELECT * FROM Users ORDER BY Id;";
            using DbDataReader rowReader = rows.ExecuteReader();
            while (rowReader.Read())
            {
                StringBuilder line = new("    ");
                for (int i = 0; i < rowReader.FieldCount; i++)
                {
                    if (i > 0)
                        line.Append(" | ");

                    line.Append(rowReader.IsDBNull(i) ? "NULL" : rowReader.GetValue(i)?.ToString());
                }

                Files.Add(line.ToString());
            }
        }
        catch (DbException ex)
        {
            Files.Add($"--- could not read the database: {ex.Message} ---");
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _cancellation?.Dispose();
        _cancellation = null;
    }

    private static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private void Append(string message)
    {
        if (_dispatcher.IsDispatchRequired)
            _dispatcher.Dispatch(() => Log.Add(message));
        else
            Log.Add(message);
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged(string? propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
