using System.Diagnostics;
using ImageArchiver.Core;

namespace ImageArchiver.Cli;

/// <summary>A one-line progress bar, with file lines scrolling above it.</summary>
internal sealed class ProgressBar(bool printEveryFile)
{
    private const int BarWidth = 30;
    private const int RedrawMilliseconds = 50;

    private readonly bool _interactive = !Console.IsOutputRedirected;
    private readonly Stopwatch _sinceRedraw = Stopwatch.StartNew();
    private bool _drawn;

    public void Update(ArchiveProgress progress)
    {
        if (progress.Last is { } file)
            PrintFile(file);

        if (!_interactive)
            return;
        if (progress.Scanning)
            Draw("Scanning source directory...");
        else if (progress.Processed == progress.Total || _sinceRedraw.ElapsedMilliseconds >= RedrawMilliseconds)
            Draw(BarText(progress));
    }

    public void Update(SourceProgress progress)
    {
        if (progress.Detail is { } detail)
        {
            if (progress.IsFailure)
            {
                Clear();
                Console.Error.WriteLine(detail);
            }
            else if (printEveryFile)
            {
                Clear();
                Console.WriteLine(detail);
            }
        }

        if (!_interactive)
            return;
        if (progress.Scanning)
            Draw("Scanning source directory...");
        else if (progress.Processed == progress.Total || _sinceRedraw.ElapsedMilliseconds >= RedrawMilliseconds)
            Draw(BarText(progress.Processed, progress.Total, progress.Summary));
    }

    public void Clear()
    {
        if (!_drawn)
            return;
        Console.Write($"\r{new string(' ', LineWidth)}\r");
        _drawn = false;
    }

    private void PrintFile(FileRecord file)
    {
        if (file.Status == FileStatus.Failed)
        {
            Clear();
            Console.Error.WriteLine($"error     {file.SourcePath}: {file.Note}");
            return;
        }
        if (!printEveryFile)
            return;

        var line = $"{Catalog.StatusText(file.Status),-9} {file.SourcePath} -> {file.TargetPath}";
        if (file.Status == FileStatus.Duplicate)
            line += $" (archived from {file.ExistingSourcePath})";
        if (!string.IsNullOrEmpty(file.Note))
            line += $" [{file.Note}]";
        Clear();
        Console.WriteLine(line);
    }

    private static string BarText(ArchiveProgress p) => BarText(
        p.Processed, p.Total,
        $"copied {p.Stats.Copied + p.Stats.Renamed}, skipped {p.Stats.Skipped}, dup {p.Stats.Duplicate}, failed {p.Stats.Failed}");

    private static string BarText(int processed, int total, string summary)
    {
        var fraction = total == 0 ? 1.0 : (double)processed / total;
        var filled = (int)(fraction * BarWidth);
        return $"[{new string('█', filled)}{new string('░', BarWidth - filled)}] {fraction,4:P0} {processed}/{total}  {summary}";
    }

    private void Draw(string text)
    {
        var width = LineWidth;
        Console.Write('\r' + (text.Length > width ? text[..width] : text.PadRight(width)));
        _drawn = true;
        _sinceRedraw.Restart();
    }

    /// <summary>One less than the window, as filling the last column would wrap the line.</summary>
    private static int LineWidth => Math.Max(Console.WindowWidth - 1, 20);
}
