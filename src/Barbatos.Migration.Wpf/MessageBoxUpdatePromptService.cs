// Copyright (C) Pham The Hung and Barbatos.Migration Contributors.
// All Rights Reserved.

using System.Text;
using System.Windows;
using Barbatos.Wpf.ApplicationModel;
using Barbatos.Wpf.Dispatching;

namespace Barbatos.Migration.Wpf;

/// <summary>
/// The built-in <see cref="IUpdatePromptService"/>: a message box that explains what is about to
/// happen and, when the application allows it, offers to postpone.
/// </summary>
/// <remarks>
/// Deliberately plain. Anything that needs release notes, a "don't ask again" checkbox or the
/// application's own visual language should implement <see cref="IUpdatePromptService"/> with a
/// real window - this exists so that turning on
/// <see cref="UpdateTriggerMode.ManualInteractive"/> does not also require designing a dialog
/// before it can be tried.
/// </remarks>
public sealed class MessageBoxUpdatePromptService : IUpdatePromptService
{
    private readonly IDispatcher _dispatcher;
    private readonly IAppInfo _appInfo;

    /// <summary>Creates the prompt service.</summary>
    public MessageBoxUpdatePromptService(IDispatcher dispatcher, IAppInfo appInfo)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _appInfo = appInfo ?? throw new ArgumentNullException(nameof(appInfo));
    }

    /// <inheritdoc />
    public Task<bool> ConfirmAsync(MigrationPromptContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (cancellationToken.IsCancellationRequested)
            return Task.FromResult(false);

        if (!_dispatcher.IsDispatchRequired)
            return Task.FromResult(Ask(context));

        TaskCompletionSource<bool> completion = new();
        _dispatcher.Dispatch(() =>
        {
            try
            {
                completion.TrySetResult(Ask(context));
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        });

        return completion.Task;
    }

    private bool Ask(MigrationPromptContext context)
    {
        MessageBoxResult result = MessageBox.Show(
            BuildMessage(context),
            $"{_appInfo.Name} - update your data",
            context.CanDefer ? MessageBoxButton.YesNo : MessageBoxButton.OKCancel,
            MessageBoxImage.Information,
            MessageBoxResult.Yes);

        return result is MessageBoxResult.Yes or MessageBoxResult.OK;
    }

    private static string BuildMessage(MigrationPromptContext context)
    {
        StringBuilder message = new();

        message.Append("This version needs to update how your data is stored (")
            .Append(context.Plan.FromVersion).Append(" to ").Append(context.Plan.ToVersion).AppendLine(").")
            .AppendLine();

        if (context.Plan.Steps.Count > 0)
        {
            message.AppendLine("What will change:");
            foreach (IMigrationStep step in context.Plan.Steps)
                message.Append("  - ").AppendLine(step.Description);

            message.AppendLine();
        }

        if (context.EstimatedDataSizeBytes > 0)
        {
            message.Append("A backup of ")
                .Append(FormatSize(context.EstimatedDataSizeBytes))
                .AppendLine(" is taken first, so nothing is lost if the update is interrupted.")
                .AppendLine();
        }

        message.AppendLine(context.Model == InstallationModel.SideBySideMultiFolder
            ? "Your current version keeps its own copy of the data and is not changed."
            : "You can cancel at any time; your data is restored automatically if you do.");

        message.AppendLine();
        message.Append(context.CanDefer
            ? "Update now?"
            : "The application cannot start until this finishes. Continue?");

        return message.ToString();
    }

    private static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:0.#} {units[unit]}";
    }
}
