// Copyright (C) Pham The Hung and Barbatos.Migration Contributors.
// All Rights Reserved.

using AwesomeAssertions;
using Barbatos.Wpf.Dispatching;
using Microsoft.Extensions.Options;
using Xunit;

namespace Barbatos.Migration.Wpf.UnitTests;

/// <summary>
/// <see cref="MigrationRunner"/> adds two things to calling the engine directly: it gets the
/// work off the UI thread, and it marshals progress back onto it without flooding the queue.
/// </summary>
public sealed class MigrationRunnerTests : IDisposable
{
    private readonly string _root;

    public MigrationRunnerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "barbatos-migration-wpf-tests", Guid.NewGuid().ToString("N"));
        DataDirectory = Path.Combine(_root, "Data");
        Directory.CreateDirectory(DataDirectory);
        new FileDataVersionStore(DataDirectory).Write(new Version(1, 0, 0), []);
    }

    private string DataDirectory { get; }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private MigrationEngine CreateEngine(params IMigrationStep[] steps) =>
        new(new MigrationOptions
        {
            DataDirectory = DataDirectory,
            BackupRootDirectory = Path.Combine(_root, ".migration"),
            TargetDataVersion = new Version(2, 0, 0),
            InitialDataVersion = new Version(1, 0, 0),
            SkipFreeSpaceCheck = true,
        },
        steps);

    private MigrationRunner CreateRunner(FakeDispatcher dispatcher, params IMigrationStep[] steps) =>
        new(CreateEngine(steps), dispatcher, Options.Create(new MigrationOptions { DataDirectory = DataDirectory }));

    [Fact]
    public async Task The_caller_keeps_running_while_the_engine_blocks_on_synchronous_work()
    {
        // Thread identity would be the obvious assertion and the wrong one: the test itself runs
        // on a pool thread, so Task.Run may legitimately pick the very same thread back up. The
        // guarantee that actually matters is that the caller is not blocked - which is what
        // keeps a splash screen animating while a data directory is copied.
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        FakeDispatcher dispatcher = new(dispatchRequired: true);
        MigrationRunner runner = CreateRunner(
            dispatcher,
            new MigrationStep(new Version(2, 0, 0), "Blocks synchronously", new DelegateMigrationProvider(
                "blocking",
                (_, _, _) =>
                {
                    started.SetResult();

                    // Blocking, exactly like DirectoryOperations.Copy does.
                    release.Task.GetAwaiter().GetResult();
                    return Task.CompletedTask;
                })));

        Task<MigrationResult> run = runner.RunAsync();

        // If the runner had not moved the work off this thread, RunAsync would still be inside
        // the provider and this await would never complete.
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        run.IsCompleted.Should().BeFalse("the caller has control back while the engine is still working");

        release.SetResult();

        MigrationResult result = await run;
        result.Outcome.Should().Be(MigrationOutcome.Succeeded);
    }

    [Fact]
    public async Task Progress_is_marshalled_through_the_dispatcher()
    {
        FakeDispatcher dispatcher = new(dispatchRequired: true);
        List<MigrationProgress> reports = [];

        MigrationRunner runner = CreateRunner(
            dispatcher,
            new MigrationStep(new Version(2, 0, 0), "Reports", new DelegateMigrationProvider(
                "p",
                (_, progress, _) =>
                {
                    progress?.Report(new MigrationProgress(50, "halfway"));
                    return Task.CompletedTask;
                })));

        await runner.RunAsync(new CollectingProgress(reports));

        dispatcher.DispatchCount.Should().BeGreaterThan(0, "the UI thread is where a bound property must be set");
        reports.Should().NotBeEmpty();
        reports[^1].Phase.Should().Be(MigrationPhase.Completed);
        reports[^1].Percentage.Should().Be(100);
    }

    [Fact]
    public async Task Reports_are_delivered_directly_when_no_dispatch_is_needed()
    {
        FakeDispatcher dispatcher = new(dispatchRequired: false);
        List<MigrationProgress> reports = [];

        await CreateRunner(
            dispatcher,
            new MigrationStep(new Version(2, 0, 0), "Reports", new DelegateMigrationProvider(
                "p",
                (_, progress, _) =>
                {
                    progress?.Report(new MigrationProgress(50, "halfway"));
                    return Task.CompletedTask;
                }))).RunAsync(new CollectingProgress(reports));

        dispatcher.DispatchCount.Should().Be(0, "already on the UI thread");
        reports.Should().NotBeEmpty();
    }

    [Fact]
    public async Task A_chatty_provider_does_not_flood_the_dispatcher()
    {
        FakeDispatcher dispatcher = new(dispatchRequired: true);
        List<MigrationProgress> reports = [];

        await CreateRunner(
            dispatcher,
            new MigrationStep(new Version(2, 0, 0), "Very chatty", new DelegateMigrationProvider(
                "p",
                (_, progress, _) =>
                {
                    // A provider rewriting a hundred thousand rows reports like this.
                    for (int i = 0; i < 5000; i++)
                        progress?.Report(new MigrationProgress(i / 100.0, $"row {i}"));

                    return Task.CompletedTask;
                }))).RunAsync(new CollectingProgress(reports));

        reports.Count.Should().BeLessThan(500,
            "forwarding every report would make the window less responsive the harder the provider tries");

        reports[^1].Percentage.Should().Be(100, "the terminal report always gets through");
    }

    [Fact]
    public async Task Cancelling_through_the_runner_restores_the_data()
    {
        File.WriteAllText(Path.Combine(DataDirectory, "keep.txt"), "original");

        using CancellationTokenSource cancellation = new();
        FakeDispatcher dispatcher = new(dispatchRequired: true);

        MigrationRunner runner = CreateRunner(
            dispatcher,
            new MigrationStep(new Version(2, 0, 0), "Cancels itself", new DelegateMigrationProvider(
                "p",
                (context, _, token) =>
                {
                    File.WriteAllText(context.GetWorkingPath("keep.txt"), "PARTIAL");
                    cancellation.Cancel();
                    token.ThrowIfCancellationRequested();
                    return Task.CompletedTask;
                })));

        MigrationResult result = await runner.RunAsync(cancellationToken: cancellation.Token);

        result.Outcome.Should().Be(MigrationOutcome.Canceled);
        File.ReadAllText(Path.Combine(DataDirectory, "keep.txt")).Should().Be("original");
    }

    [Fact]
    public void CreatePlan_answers_without_running_anything()
    {
        FakeDispatcher dispatcher = new(dispatchRequired: true);

        MigrationRunner runner = CreateRunner(
            dispatcher,
            new MigrationStep(new Version(2, 0, 0), "Planned", new DelegateMigrationProvider("p", (_, _, _) => Task.CompletedTask)));

        MigrationPlan plan = runner.CreatePlan();

        plan.Steps.Should().ContainSingle();
        plan.Describe().Should().Contain("Planned");
    }

    [Fact]
    public void The_runner_rejects_missing_dependencies()
    {
        ((Action)(() => new MigrationRunner(null!, new FakeDispatcher(true), Options.Create(new MigrationOptions()))))
            .Should().Throw<ArgumentNullException>();

        ((Action)(() => new MigrationRunner(CreateEngine(), null!, Options.Create(new MigrationOptions()))))
            .Should().Throw<ArgumentNullException>();

        ((Action)(() => new MigrationRunner(CreateEngine(), new FakeDispatcher(true), null!)))
            .Should().Throw<ArgumentNullException>();
    }

    private sealed class CollectingProgress(List<MigrationProgress> reports) : IProgress<MigrationProgress>
    {
        private readonly Lock _gate = new();

        public void Report(MigrationProgress value)
        {
            lock (_gate)
            {
                reports.Add(value);
            }
        }
    }

    /// <summary>
    /// Stands in for WPF's dispatcher: runs the action immediately but records that it was asked
    /// to, which is what the throttling assertions look at.
    /// </summary>
    private sealed class FakeDispatcher(bool dispatchRequired) : IDispatcher
    {
        private readonly Lock _gate = new();
        private int _dispatchCount;

        public bool IsDispatchRequired => dispatchRequired;

        public int DispatchCount
        {
            get
            {
                lock (_gate)
                {
                    return _dispatchCount;
                }
            }
        }

        public bool Dispatch(Action action)
        {
            lock (_gate)
            {
                _dispatchCount++;
            }

            action();
            return true;
        }

        public bool DispatchDelayed(TimeSpan delay, Action action) => Dispatch(action);

        public IDispatcherTimer CreateTimer() => throw new NotSupportedException();
    }
}
