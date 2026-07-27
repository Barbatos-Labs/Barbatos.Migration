// Copyright (C) Pham The Hung and Barbatos.Migration Contributors.
// All Rights Reserved.

using System.ComponentModel;
using AwesomeAssertions;
using Xunit;

namespace Barbatos.Migration.Wpf.UnitTests;

/// <summary>
/// The view model a splash screen binds to. Its job is small but its details are what the user
/// sees, so they are worth pinning down.
/// </summary>
public class MigrationProgressViewModelTests
{
    private static List<string> TrackChanges(INotifyPropertyChanged model)
    {
        List<string> changed = [];
        model.PropertyChanged += (_, args) => changed.Add(args.PropertyName ?? string.Empty);
        return changed;
    }

    [Fact]
    public void A_report_flows_into_the_bindable_properties()
    {
        using MigrationProgressViewModel model = new();
        List<string> changed = TrackChanges(model);

        model.Report(new MigrationProgress(MigrationPhase.Migrating, 42, "Rewriting rows"));

        model.Percentage.Should().Be(42);
        model.Status.Should().Be("Rewriting rows");
        model.Phase.Should().Be(MigrationPhase.Migrating);
        model.IsRunning.Should().BeTrue();

        changed.Should().Contain([nameof(model.Percentage), nameof(model.Status), nameof(model.Phase)]);
    }

    [Fact]
    public void An_indeterminate_report_switches_the_bar_to_a_marquee()
    {
        using MigrationProgressViewModel model = new();

        model.Report(new MigrationProgress(0, "Applying schema migrations", isIndeterminate: true));

        model.IsIndeterminate.Should().BeTrue();
    }

    [Fact]
    public void Cancelling_is_offered_while_the_run_is_still_interruptible()
    {
        using MigrationProgressViewModel model = new();

        model.CanCancel.Should().BeTrue();
        model.CancelCommand.CanExecute(null).Should().BeTrue();

        model.Report(new MigrationProgress(MigrationPhase.Preparing, 5, "Backing up your data..."));
        model.CanCancel.Should().BeTrue("the snapshot is the longest phase and the one most worth interrupting");

        model.Report(new MigrationProgress(MigrationPhase.Migrating, 50, "Running"));
        model.CanCancel.Should().BeTrue();
    }

    [Theory]
    [InlineData(MigrationPhase.Committing)]
    [InlineData(MigrationPhase.RollingBack)]
    [InlineData(MigrationPhase.Recovering)]
    [InlineData(MigrationPhase.Completed)]
    public void Cancelling_stops_being_offered_once_the_engine_will_not_honour_it(MigrationPhase phase)
    {
        using MigrationProgressViewModel model = new();

        model.Report(new MigrationProgress(phase, 98, "Finalising"));

        model.CanCancel.Should().BeFalse("a Cancel button that ignores clicks is worse than one that is greyed out");
        model.CancelCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public void Reaching_the_commit_phase_raises_CanExecuteChanged_so_the_button_actually_greys_out()
    {
        using MigrationProgressViewModel model = new();

        int raised = 0;
        model.CancelCommand.CanExecuteChanged += (_, _) => raised++;

        model.Report(new MigrationProgress(MigrationPhase.Committing, 98, "Finalising"));

        raised.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Cancel_signals_the_token_the_runner_was_given()
    {
        using MigrationProgressViewModel model = new();

        model.CancellationToken.IsCancellationRequested.Should().BeFalse();

        model.CancelCommand.Execute(null);

        model.CancellationToken.IsCancellationRequested.Should().BeTrue();
        model.Status.Should().Be("Cancelling...");
        model.CanCancel.Should().BeFalse("asking twice does nothing");
    }

    [Fact]
    public void Cancelling_twice_is_harmless()
    {
        using MigrationProgressViewModel model = new();
        model.Cancel();

        Action act = model.Cancel;

        act.Should().NotThrow();
    }

    [Fact]
    public void The_completed_phase_ends_the_run()
    {
        using MigrationProgressViewModel model = new();

        model.Report(new MigrationProgress(MigrationPhase.Completed, 100, "Your data is up to date."));

        model.IsRunning.Should().BeFalse();
        model.IsIdleForBinding().Should().BeTrue();
        model.Percentage.Should().Be(100);
    }

    [Fact]
    public void The_view_model_is_itself_the_progress_reporter()
    {
        using MigrationProgressViewModel model = new();

        model.Should().BeAssignableTo<IProgress<MigrationProgress>>();
    }
}

internal static class ViewModelAssertionExtensions
{
    /// <summary>Mirrors what a XAML trigger on <c>IsRunning</c> would evaluate.</summary>
    public static bool IsIdleForBinding(this MigrationProgressViewModel model) => !model.IsRunning;
}
