// Copyright (C) Pham The Hung and Barbatos.Migration Contributors.
// All Rights Reserved.

using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace Barbatos.Migration.Wpf;

/// <summary>
/// A ready-made view model for a migration splash screen: bind a progress bar to
/// <see cref="Percentage"/>, a label to <see cref="Status"/>, and a button to
/// <see cref="CancelCommand"/>.
/// </summary>
/// <remarks>
/// It is itself an <see cref="IProgress{T}"/>, so it can be handed straight to
/// <see cref="IMigrationRunner.RunAsync"/>. The runner has already marshalled reports onto the
/// UI thread by the time they arrive here.
/// </remarks>
/// <example>
/// <code>
/// protected override async void OnStartup(StartupEventArgs e)
/// {
///     base.OnStartup(e);
///
///     MigrationProgressViewModel progress = Services.GetRequiredService&lt;MigrationProgressViewModel&gt;();
///     SplashViewModel.Migration = progress;   // bound by the splash screen
///
///     MigrationResult result = await Services.GetRequiredService&lt;IMigrationRunner&gt;()
///         .RunAsync(progress, progress.CancellationToken);
///
///     await CloseSplashScreenAsync();
///
///     if (!result.CanContinue)
///     {
///         MessageBox.Show(result.Outcome == MigrationOutcome.RollbackFailed
///             ? $"Your data could not be restored automatically. A copy is at {result.BackupDirectory}."
///             : "The update did not finish, so the application cannot start.");
///         Shutdown(1);
///         return;
///     }
///
///     new MainWindow().Show();
/// }
/// </code>
/// </example>
public sealed class MigrationProgressViewModel : IProgress<MigrationProgress>, INotifyPropertyChanged, IDisposable
{
    private readonly CancellationTokenSource _cancellation = new();
    private readonly RelayCommand _cancelCommand;

    private double _percentage;
    private bool _isIndeterminate;
    private string _status = string.Empty;
    private string _stepDescription = string.Empty;
    private MigrationPhase _phase = MigrationPhase.Planning;
    private bool _isRunning = true;

    /// <summary>Creates the view model.</summary>
    public MigrationProgressViewModel()
    {
        _cancelCommand = new RelayCommand(Cancel, () => CanCancel);
    }

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Overall progress, 0 to 100. Never moves backwards.</summary>
    public double Percentage
    {
        get => _percentage;
        private set => Set(ref _percentage, value);
    }

    /// <summary>Whether the progress bar should be a marquee rather than a filled bar.</summary>
    public bool IsIndeterminate
    {
        get => _isIndeterminate;
        private set => Set(ref _isIndeterminate, value);
    }

    /// <summary>The current message, ready to show to a user.</summary>
    public string Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    /// <summary>The description of the step being applied, for a secondary line.</summary>
    public string StepDescription
    {
        get => _stepDescription;
        private set => Set(ref _stepDescription, value);
    }

    /// <summary>The phase the engine is in.</summary>
    public MigrationPhase Phase
    {
        get => _phase;
        private set
        {
            if (Set(ref _phase, value))
            {
                OnPropertyChanged(nameof(CanCancel));
                _cancelCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>Whether the migration is still going.</summary>
    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (Set(ref _isRunning, value))
            {
                OnPropertyChanged(nameof(CanCancel));
                _cancelCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>
    /// Whether cancelling is still possible.
    /// </summary>
    /// <remarks>
    /// Goes false once the engine reaches <see cref="MigrationPhase.Committing"/>. Leaving a
    /// Cancel button enabled past that point offers the user something the engine will not do,
    /// and a button that ignores a click is worse than one that is greyed out.
    /// </remarks>
    public bool CanCancel =>
        IsRunning
        && !_cancellation.IsCancellationRequested
        && Phase is not (MigrationPhase.Committing or MigrationPhase.RollingBack or MigrationPhase.Recovering or MigrationPhase.Completed);

    /// <summary>Cancels the migration; the engine then restores the data.</summary>
    public ICommand CancelCommand => _cancelCommand;

    /// <summary>Pass this to <see cref="IMigrationRunner.RunAsync"/>.</summary>
    public CancellationToken CancellationToken => _cancellation.Token;

    /// <inheritdoc />
    public void Report(MigrationProgress value)
    {
        Phase = value.Phase;
        Percentage = value.Percentage;
        IsIndeterminate = value.IsIndeterminate;
        StepDescription = value.StepDescription;

        Status = value.Detail.Length > 0
            ? value.Detail
            : value.StepDescription.Length > 0 ? value.StepDescription : Status;

        if (value.Phase == MigrationPhase.Completed)
            IsRunning = false;
    }

    /// <summary>Requests cancellation.</summary>
    public void Cancel()
    {
        if (_cancellation.IsCancellationRequested)
            return;

        Status = "Cancelling...";
        _cancellation.Cancel();

        OnPropertyChanged(nameof(CanCancel));
        _cancelCommand.RaiseCanExecuteChanged();
    }

    /// <inheritdoc />
    public void Dispose() => _cancellation.Dispose();

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

    private sealed class RelayCommand : ICommand
    {
        private readonly Action _execute;
        private readonly Func<bool> _canExecute;

        public RelayCommand(Action execute, Func<bool> canExecute)
        {
            _execute = execute;
            _canExecute = canExecute;
        }

        public event EventHandler? CanExecuteChanged;

        public bool CanExecute(object? parameter) => _canExecute();

        public void Execute(object? parameter) => _execute();

        public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
