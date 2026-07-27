// Copyright (C) Pham The Hung and Barbatos.Migration Contributors.
// All Rights Reserved.

using Barbatos.Migration.Wpf;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Barbatos.Migration.Wpf.Sample;

/// <summary>
/// Composes the application host. The migration engine is registered here, alongside everything
/// else, and run from <see cref="App.OnStartup"/> while the splash screen is up.
/// </summary>
public static class WpfProgram
{
    public static WpfApp CreateWpfApp()
    {
        WpfAppBuilder builder = WpfApp.CreateBuilder();

        builder.Configuration.AddJsonFile("appsettings.json", optional: true, reloadOnChange: false);
        builder.Logging.AddDebug().SetMinimumLevel(LogLevel.Debug);

        // Everything the engine needs is defaulted from the host: the data directory from
        // IFileSystem, the target version from AppInfo.Version (2.0.0, set in the .csproj), and
        // the log from ILogger. The Barbatos:Migration section of appsettings.json overrides
        // the rest.
        //
        // AddStepsFromAssembly finds every [Migration] class in this project - one per file
        // under Migrations/ - so adding a step means adding a file and nothing else.
        builder.ConfigureMigration()
            .AddStepsFromAssembly();

        builder.Services.AddSingleton<MainViewModel>();
        builder.Services.AddTransient<MainWindow>();

        return builder.Build();
    }
}
