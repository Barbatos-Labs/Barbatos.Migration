// Copyright (C) Pham The Hung and Barbatos.Migration Contributors.
// All Rights Reserved.

using System.Windows;
using Barbatos.Migration.Wpf;
using Microsoft.Extensions.DependencyInjection;

namespace Barbatos.Migration.Wpf.Sample;

/// <summary>
/// The startup path: show a splash screen, migrate the application's own data folder behind it,
/// and only then open the main window.
/// </summary>
public partial class App : WpfApplication
{
    /// <inheritdoc />
    protected override WpfApp CreateWpfApp() => WpfProgram.CreateWpfApp();

    /// <inheritdoc />
    protected override SplashScreenOptions GetSplashScreenOptions() => new()
    {
        AppName = "Barbatos.Migration Sample",
        Tagline = "Preparing your data...",
        MinimumDisplayDuration = TimeSpan.FromSeconds(1.5),
    };

    /// <inheritdoc />
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        MigrationProgressViewModel progress = Services.GetRequiredService<MigrationProgressViewModel>();

        // Off the UI thread, with progress marshalled back onto it. In a real application the
        // splash screen binds to `progress` - Percentage, Status and CancelCommand are all
        // ready for it.
        MigrationResult result = await Services.GetRequiredService<IMigrationRunner>()
            .RunAsync(progress, progress.CancellationToken);

        await CloseSplashScreenAsync();

        if (!result.CanContinue)
        {
            MessageBox.Show(
                result.Outcome == MigrationOutcome.RollbackFailed
                    ? $"Your data could not be restored automatically.\n\nA copy is at:\n{result.BackupDirectory}"
                    : $"The update did not finish ({result.Outcome}), so the application cannot start.\n\n{result.Error?.Message}",
                "Barbatos.Migration Sample",
                MessageBoxButton.OK,
                MessageBoxImage.Error);

            Shutdown(1);
            return;
        }

        MainWindow window = Services.GetRequiredService<MainWindow>();
        window.DataContext = Services.GetRequiredService<MainViewModel>();
        window.Show();
    }
}
