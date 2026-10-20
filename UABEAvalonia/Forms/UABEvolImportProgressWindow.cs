using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using System;
using System.Threading;

namespace UABEAvalonia;

/// <summary>
/// Modal, responsive busy indicator for UABEvol RAW/Dump imports. Closing or
/// cancelling requests cancellation; no asset is committed after cancellation.
/// </summary>
public sealed class UABEvolImportProgressWindow : Window
{
    private readonly TextBlock _status;
    private readonly Button _cancelButton;
    private bool _finished;

    public CancellationTokenSource Cancellation { get; } = new();

    public UABEvolImportProgressWindow()
    {
        Title = "UABEvol import";
        Width = 460;
        Height = 155;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _status = new TextBlock { Text = "Preparing asset...", TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        _cancelButton = new Button
        {
            Content = "Cancel import",
            HorizontalAlignment = HorizontalAlignment.Right,
            MinWidth = 110
        };
        _cancelButton.Click += (_, _) => RequestCancel();
        Closing += (_, e) =>
        {
            if (!_finished)
            {
                RequestCancel();
                e.Cancel = true;
            }
        };
        Content = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 12,
            Children =
            {
                _status,
                new ProgressBar { IsIndeterminate = true, Height = 14 },
                _cancelButton
            }
        };
    }

    public void SetStatus(string status)
    {
        if (!_finished && !Cancellation.IsCancellationRequested)
            _status.Text = status;
    }

    public void RequestCancel()
    {
        if (_finished || Cancellation.IsCancellationRequested)
            return;
        Cancellation.Cancel();
        _status.Text = "Cancelling import; waiting for current parse to stop...";
        _cancelButton.IsEnabled = false;
    }

    public void Finish()
    {
        _finished = true;
        Close();
    }
}
