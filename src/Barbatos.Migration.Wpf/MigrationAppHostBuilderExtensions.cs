// Copyright (C) Pham The Hung and Barbatos.Migration Contributors.
// All Rights Reserved.

using Barbatos.Migration.DependencyInjection;
using Barbatos.Wpf.ApplicationModel;
using Barbatos.Wpf.Hosting;
using Barbatos.Wpf.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Barbatos.Migration.Wpf;

/// <summary>
/// Adds Barbatos.Migration to a <see cref="WpfAppBuilder"/>.
/// </summary>
public static class MigrationAppHostBuilderExtensions
{
    /// <summary>
    /// Registers the migration engine, defaulting its options from the essentials services
    /// already in the host, and returns a builder for declaring the steps.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The defaults are the point of this package. <see cref="MigrationOptions.DataDirectory"/>
    /// comes from <see cref="IFileSystem.AppDataDirectory"/>, which is already the
    /// publisher/app-GUID-scoped folder the rest of Barbatos.Wpf stores things in, so the
    /// engine protects exactly the data the app actually writes.
    /// <see cref="MigrationOptions.TargetDataVersion"/> comes from
    /// <see cref="IAppInfo.Version"/>, so shipping a new build is all it takes for its steps to
    /// become due. Both can be overridden from <paramref name="configure"/> or from the
    /// <c>Barbatos:Migration</c> configuration section.
    /// </para>
    /// <para>
    /// Nothing runs yet. Call <see cref="IMigrationRunner.RunAsync"/> from your
    /// <c>OnStartup</c>, while the splash screen is up and before anything opens the data.
    /// </para>
    /// </remarks>
    public static MigrationBuilder ConfigureMigration(this WpfAppBuilder builder, Action<MigrationOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        OptionsBuilder<MigrationOptions> options = builder.Services.AddOptions<MigrationOptions>();

        // Applied first, so both the caller's delegate and configuration win over them.
        options.Configure<IFileSystem, IAppInfo, IVersionTracking>((migration, fileSystem, appInfo, versionTracking) =>
        {
            migration.DataDirectory = fileSystem.AppDataDirectory;
            migration.TargetDataVersion = appInfo.Version;
            migration.InitialDataVersion = ResolveInitialDataVersion(appInfo, versionTracking);
        });

        if (configure != null)
            options.Configure(configure);

        options.Bind(builder.Configuration.GetSection(MigrationOptions.SectionName));

        builder.Services.TryAddSingleton<IMigrationRunner, MigrationRunner>();
        builder.Services.TryAddTransient<MigrationProgressViewModel>();

        return builder.Services.AddBarbatosMigration();
    }

    /// <summary>
    /// Switches to <see cref="UpdateTriggerMode.ManualInteractive"/> and registers the built-in
    /// message-box prompt, for applications that should ask before a long migration rather than
    /// starting one under a user who opened the app to finish something.
    /// </summary>
    public static MigrationBuilder AskBeforeMigrating(this MigrationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.Configure<MigrationOptions>(options => options.TriggerMode = UpdateTriggerMode.ManualInteractive);
        builder.Services.TryAddSingleton<IUpdatePromptService, MessageBoxUpdatePromptService>();

        return builder;
    }

    /// <summary>
    /// Works out what version data with no version stamp should be treated as, using the
    /// app-version history <see cref="IVersionTracking"/> has been keeping.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Data has no stamp in exactly two situations, and telling them apart matters enormously.
    /// A genuinely fresh install has nothing to migrate, so its data should be treated as
    /// already current. An installation that has been running since before this framework was
    /// added has real data in an old shape, and replaying every step over it from
    /// <c>0.0.0.0</c> - the framework-agnostic default - would at best waste time and at worst
    /// re-run a step that is not safe to repeat.
    /// </para>
    /// <para>
    /// <see cref="IVersionTracking.VersionHistory"/> answers this exactly: it is the list of app
    /// versions that have actually run on this machine, written before any of this existed. The
    /// newest entry older than the current build is the version the data was last written by.
    /// </para>
    /// <para>
    /// Reading the <em>history</em> rather than <see cref="IVersionTracking.PreviousVersion"/>
    /// is what makes this survive a retry. By the time a migration runs, version tracking has
    /// already recorded the new build - so if the first attempt is cancelled and the user
    /// relaunches, <c>PreviousVersion</c> has become the new version and would say there is
    /// nothing to migrate. The history still contains the old one.
    /// </para>
    /// </remarks>
    private static Version ResolveInitialDataVersion(IAppInfo appInfo, IVersionTracking versionTracking)
    {
        Version current = appInfo.Version;
        Version? newestOlder = null;

        foreach (string entry in versionTracking.VersionHistory)
        {
            if (!Version.TryParse(entry, out Version? parsed) || parsed >= current)
                continue;

            if (newestOlder == null || parsed > newestOlder)
                newestOlder = parsed;
        }

        // No older build has ever run here, so there is no legacy data to bring forward.
        return newestOlder ?? current;
    }
}
