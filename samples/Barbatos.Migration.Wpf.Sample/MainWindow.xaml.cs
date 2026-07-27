// Copyright (C) Pham The Hung and Barbatos.Migration Contributors.
// All Rights Reserved.

using System.Collections.Specialized;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace Barbatos.Migration.Wpf.Sample;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private MainViewModel Model => (MainViewModel)DataContext;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Keep the newest log line in view without the user having to chase it.
        if (Model.Log is INotifyCollectionChanged log)
        {
            log.CollectionChanged += (_, args) =>
            {
                if (args.NewItems is { Count: > 0 })
                    LogList.ScrollIntoView(args.NewItems[args.NewItems.Count - 1]);
            };
        }
    }

    private void OnReset(object sender, RoutedEventArgs e) => Model.Reset();

    private async void OnUpgrade(object sender, RoutedEventArgs e) => await Model.UpgradeAsync(failMidway: false);

    private async void OnUpgradeAndFail(object sender, RoutedEventArgs e) => await Model.UpgradeAsync(failMidway: true);

    private async void OnDowngrade(object sender, RoutedEventArgs e) => await Model.DowngradeAsync();

    private void OnCancel(object sender, RoutedEventArgs e) => Model.Cancel();

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(Model.SandboxRoot);
        Process.Start(new ProcessStartInfo(Model.SandboxRoot) { UseShellExecute = true });
    }
}

/// <summary>Inverts a boolean, so one property can drive two mutually exclusive radio buttons.</summary>
public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is bool flag ? !flag : value;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is bool flag ? !flag : value;
}
