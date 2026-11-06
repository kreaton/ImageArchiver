using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using ImageArchiver.Core;
using Microsoft.Win32;

namespace ImageArchiver.Wpf;

public partial class MainWindow : Window
{
    /// <summary>Oldest lines are dropped beyond this; the catalog has the full record.</summary>
    private const int MaxLogLines = 5000;

    /// <summary>What the window shows about a run, whichever kind it is.</summary>
    private sealed record RunState(bool Scanning, int Processed, int Total, string Status);

    /// <summary>What a finished run leaves in the status line and log.</summary>
    private sealed record RunOutcome(string Status, IEnumerable<string> Lines, bool ShowOpenTarget = false);

    private readonly ObservableCollection<string> _log = [];
    private readonly ConcurrentQueue<string> _pendingLines = new();
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private volatile RunState? _latest;
    private CancellationTokenSource? _cancellation;
    private bool _closeWhenDone;

    public MainWindow()
    {
        InitializeComponent();
        LogList.ItemsSource = _log;
        _refresh.Tick += (_, _) => ShowProgress();
    }

    private void OnBrowseSource(object sender, RoutedEventArgs e) => Browse(SourceBox, "Choose the source directory");

    private void OnBrowseTarget(object sender, RoutedEventArgs e) => Browse(TargetBox, "Choose the target directory");

    private void Browse(System.Windows.Controls.TextBox box, string title)
    {
        var dialog = new OpenFolderDialog { Title = title };
        if (Directory.Exists(box.Text))
            dialog.InitialDirectory = box.Text;
        if (dialog.ShowDialog(this) == true)
            box.Text = dialog.FolderName;
    }

    private void OnPathChanged(object sender, RoutedEventArgs e) => UpdateButtons();

    private void UpdateButtons()
    {
        var idle = _cancellation is null;
        var hasSource = !string.IsNullOrWhiteSpace(SourceBox.Text);
        StartButton.IsEnabled = idle && hasSource && !string.IsNullOrWhiteSpace(TargetBox.Text);
        DuplicatesButton.IsEnabled = DatesButton.IsEnabled = idle && hasSource;
        CancelButton.IsEnabled = !idle;
        SourceBox.IsEnabled = TargetBox.IsEnabled = CatalogBox.IsEnabled = SuppressBox.IsEnabled = idle;
    }

    private async void OnStart(object sender, RoutedEventArgs e)
    {
        var options = new ArchiveOptions(SourceBox.Text.Trim(), TargetBox.Text.Trim())
        {
            CatalogFormat = CatalogBox.SelectedIndex == 1 ? CatalogFormat.Sqlite : CatalogFormat.Csv,
            Duplicates = SuppressBox.IsChecked == true ? DuplicateHandling.Suppress : DuplicateHandling.Keep,
        };
        await RunAsync(token =>
        {
            var result = Archiver.Run(options, new DirectProgress<ArchiveProgress>(OnArchiveProgress), token);
            return new RunOutcome(
                $"{(result.Cancelled ? "Cancelled" : "Done")}: {Summary(result.Stats)}. Catalog: {result.CatalogPath}",
                result.Warnings.Select(w => $"warning   {w}"),
                ShowOpenTarget: true);
        });
    }

    private async void OnFindDuplicates(object sender, RoutedEventArgs e)
    {
        var source = SourceBox.Text.Trim();
        await RunAsync(token =>
        {
            // The lines come from the report, in groups, rather than one by one as they are found.
            var report = SourceTools.FindDuplicates(source, new DirectProgress<SourceProgress>(p => OnSourceProgress(p, showDetail: false)), token);
            var lines = report.Groups.SelectMany(g => g.Files.Select((file, i) => $"{(i == 0 ? "keep" : "duplicate"),-9} {file}").Append(""));
            lines = lines.Concat(report.Failures.Select(f => $"error     {f.Path}: {f.Reason}"));
            return new RunOutcome(
                $"{(report.Cancelled ? "Cancelled dry run" : "Dry run")}: {report.FilesScanned} files scanned, " +
                $"{report.DuplicateCount} duplicates in {report.Groups.Count} groups, {report.Failures.Count} failed. Nothing was changed.",
                lines);
        });
    }

    private async void OnUpdateDates(object sender, RoutedEventArgs e)
    {
        var source = SourceBox.Text.Trim();
        var answer = MessageBox.Show(
            this,
            $"This sets the created and modified dates of every JPEG in\n\n{source}\n\nto its smallest detected date. The file contents aren't touched, but the previous dates are not kept.\n\nUpdate the source files?",
            "Update source dates",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes)
            return;

        await RunAsync(token =>
        {
            var result = SourceTools.UpdateSourceDates(source, new DirectProgress<SourceProgress>(p => OnSourceProgress(p, showDetail: true)), token);
            return new RunOutcome(
                $"{(result.Cancelled ? "Cancelled" : "Done")}: {result.Updated} updated, {result.Unchanged} unchanged, {result.Failures.Count} failed.",
                []);
        });
    }

    private async Task RunAsync(Func<CancellationToken, RunOutcome> run)
    {
        _log.Clear();
        _latest = null;
        Bar.Value = 0;
        _cancellation = new CancellationTokenSource();
        OpenButton.Visibility = Visibility.Collapsed;
        UpdateButtons();
        StatusText.Text = "Starting...";
        _refresh.Start();

        var token = _cancellation.Token;
        try
        {
            var outcome = await Task.Run(() => run(token));
            ShowProgress();
            Bar.IsIndeterminate = false;
            foreach (var line in outcome.Lines)
                _log.Add(line);
            if (_log.Count > 0)
                LogList.ScrollIntoView(_log[^1]);
            StatusText.Text = outcome.Status;
            if (outcome.ShowOpenTarget)
                OpenButton.Visibility = Visibility.Visible;
        }
        catch (ArchiveException ex)
        {
            ShowProgress();
            Bar.IsIndeterminate = false;
            StatusText.Text = ex.Message;
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _refresh.Stop();
            _cancellation.Dispose();
            _cancellation = null;
            UpdateButtons();
        }

        if (_closeWhenDone)
            Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => RequestCancel();

    private void RequestCancel()
    {
        _cancellation?.Cancel();
        CancelButton.IsEnabled = false;
        StatusText.Text = "Cancelling after the current file...";
    }

    private void OnOpenTarget(object sender, RoutedEventArgs e)
    {
        var path = TargetBox.Text.Trim();
        if (Directory.Exists(path))
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    /// <summary>Lets a running job finish its current file, then closes.</summary>
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_cancellation is null)
            return;
        e.Cancel = true;
        _closeWhenDone = true;
        RequestCancel();
    }

    /// <summary>
    /// The progress callbacks run on the worker thread. The UI picks this up on a timer, because
    /// a run that is mostly skipping files can report far faster than it could usefully redraw.
    /// </summary>
    private void OnArchiveProgress(ArchiveProgress progress)
    {
        _latest = new RunState(progress.Scanning, progress.Processed, progress.Total, Summary(progress.Stats));
        if (progress.Last is { } file)
            _pendingLines.Enqueue(LogLine(file));
    }

    private void OnSourceProgress(SourceProgress progress, bool showDetail)
    {
        _latest = new RunState(progress.Scanning, progress.Processed, progress.Total, progress.Summary);
        if (showDetail && progress.Detail is { } detail)
            _pendingLines.Enqueue(detail);
    }

    private void ShowProgress()
    {
        var added = false;
        while (_pendingLines.TryDequeue(out var line))
        {
            _log.Add(line);
            added = true;
        }
        while (_log.Count > MaxLogLines)
            _log.RemoveAt(0);
        if (added)
            LogList.ScrollIntoView(_log[^1]);

        if (_latest is not { } state)
            return;
        Bar.IsIndeterminate = state.Scanning;
        Bar.Maximum = Math.Max(state.Total, 1);
        Bar.Value = state.Processed;

        if (_cancellation?.IsCancellationRequested == true)
            return;
        StatusText.Text = state.Scanning
            ? "Scanning source directory..."
            : $"{state.Processed} of {state.Total}  |  {state.Status}";
    }

    private static string Summary(ArchiveStats s) =>
        $"{s.Copied} copied, {s.Renamed} with new name, {s.Skipped} skipped, {s.Duplicate} duplicates, {s.Failed} failed";

    private static string LogLine(FileRecord file)
    {
        var line = $"{Catalog.StatusText(file.Status),-9} {file.SourcePath}";
        if (!string.IsNullOrEmpty(file.TargetPath))
            line += $" -> {file.TargetPath}";
        if (file.Status == FileStatus.Duplicate)
            line += $" (archived from {file.ExistingSourcePath})";
        if (!string.IsNullOrEmpty(file.Note))
            line += $" [{file.Note}]";
        return line;
    }
}
