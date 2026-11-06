using System.CommandLine;
using System.Text;
using ImageArchiver.Cli;
using ImageArchiver.Core;

const int ExitOk = 0, ExitFailed = 1, ExitInvalidArguments = 2, ExitCancelled = 130;

var source = new Argument<string>("source") { Description = "Directory to scan recursively for .jpg, .jpeg, .heic and .heif files" };
var target = new Argument<string?>("target")
{
    Description = "Root directory of the archive, created if it doesn't exist. Not used with --dry-run or --update-source-dates",
    Arity = ArgumentArity.ZeroOrOne,
};
var catalog = new Option<CatalogFormat>("--catalog")
{
    Description = "Catalog written to the target root: Csv (catalog.csv) or Sqlite (catalog.db)",
    DefaultValueFactory = _ => CatalogFormat.Csv,
};
var keep = new Option<bool>("--keep-duplicates")
{
    Description = "Store the same content again when its modified date differs, as archivejpg does. By default identical content is stored once",
};
var dryRun = new Option<bool>("--dry-run")
{
    Description = "Only report the duplicates in the source. Nothing is copied or changed",
};
var updateDates = new Option<bool>("--update-source-dates")
{
    Description = "Only set each source file's created and modified dates to its smallest detected date. The previous dates are not kept",
};
var verbose = new Option<bool>("--verbose", "-v") { Description = "Print a line for every file" };

var root = new RootCommand("Archive JPEG and HEIC photos into yyyy-MM folders by the smallest date detected for each photo.")
{
    source, target, catalog, keep, dryRun, updateDates, verbose,
};
root.SetAction(parsed =>
{
    var isDryRun = parsed.GetValue(dryRun);
    var isUpdate = parsed.GetValue(updateDates);
    var targetPath = parsed.GetValue(target);
    var sourcePath = parsed.GetValue(source)!;
    var isVerbose = parsed.GetValue(verbose);

    var problem =
        isDryRun && isUpdate ? "--dry-run and --update-source-dates cannot be combined."
        : (isDryRun || isUpdate) && targetPath is not null ? "--dry-run and --update-source-dates only work on the source, so they take no target."
        : (isDryRun || isUpdate) && (parsed.GetValue(keep) || parsed.GetResult(catalog) is { Implicit: false }) ? "--keep-duplicates and --catalog only apply when archiving."
        : !isDryRun && !isUpdate && targetPath is null ? "A target directory is required to archive."
        : null;
    if (problem is not null)
    {
        Console.Error.WriteLine(problem);
        return ExitInvalidArguments;
    }

    if (isDryRun)
        return FindDuplicates(sourcePath, isVerbose);
    if (isUpdate)
        return UpdateDates(sourcePath, isVerbose);
    return Archive(
        new ArchiveOptions(sourcePath, targetPath!)
        {
            CatalogFormat = parsed.GetValue(catalog),
            Duplicates = parsed.GetValue(keep) ? DuplicateHandling.Keep : DuplicateHandling.Suppress,
        },
        isVerbose);
});

var parseResult = root.Parse(args);
if (parseResult.Errors.Count > 0)
{
    foreach (var error in parseResult.Errors)
        Console.Error.WriteLine(error.Message);
    Console.Error.WriteLine("Use --help for usage.");
    return ExitInvalidArguments;
}
return parseResult.Invoke();

static int Archive(ArchiveOptions options, bool verbose)
{
    var bar = new ProgressBar(printEveryFile: verbose || Console.IsOutputRedirected);
    ArchiveResult result;
    using var cancellation = StartCancellation();
    try
    {
        result = Archiver.Run(options, new DirectProgress<ArchiveProgress>(bar.Update), cancellation.Token);
    }
    catch (ArchiveException ex)
    {
        return Fail(bar, ex);
    }
    bar.Clear();

    foreach (var warning in result.Warnings)
        Console.Error.WriteLine($"warning: {warning}");

    var stats = result.Stats;
    Console.WriteLine(
        $"{(result.Cancelled ? "Cancelled" : "Done")}: {stats.Copied} copied, {stats.Renamed} copied with new name, " +
        $"{stats.Skipped} skipped (already archived), {stats.Duplicate} duplicates, {stats.Failed} failed");
    Console.WriteLine($"Catalog: {result.CatalogPath}");

    return ExitCode(result.Cancelled, stats.Failed);
}

static int FindDuplicates(string source, bool verbose)
{
    var bar = new ProgressBar(printEveryFile: verbose);
    DuplicateReport report;
    using var cancellation = StartCancellation();
    try
    {
        report = SourceTools.FindDuplicates(source, new DirectProgress<SourceProgress>(bar.Update), cancellation.Token);
    }
    catch (ArchiveException ex)
    {
        return Fail(bar, ex);
    }
    bar.Clear();

    foreach (var group in report.Groups)
    {
        Console.WriteLine($"keep      {group.Files[0]}");
        foreach (var file in group.Files.Skip(1))
            Console.WriteLine($"duplicate {file}");
        Console.WriteLine();
    }

    Console.WriteLine(
        $"{(report.Cancelled ? "Cancelled dry run" : "Dry run")}: {report.FilesScanned} files scanned, " +
        $"{report.DuplicateCount} duplicates in {report.Groups.Count} groups, {report.Failures.Count} failed. Nothing was changed.");

    return ExitCode(report.Cancelled, report.Failures.Count);
}

static int UpdateDates(string source, bool verbose)
{
    // Every change is listed when output goes to a file, which is then the record of the old dates.
    var bar = new ProgressBar(printEveryFile: verbose || Console.IsOutputRedirected);
    DateUpdateResult result;
    using var cancellation = StartCancellation();
    try
    {
        result = SourceTools.UpdateSourceDates(source, new DirectProgress<SourceProgress>(bar.Update), cancellation.Token);
    }
    catch (ArchiveException ex)
    {
        return Fail(bar, ex);
    }
    bar.Clear();

    Console.WriteLine(
        $"{(result.Cancelled ? "Cancelled" : "Done")}: {result.Updated} updated, {result.Unchanged} unchanged, {result.Failures.Count} failed");

    return ExitCode(result.Cancelled, result.Failures.Count);
}

static CancellationTokenSource StartCancellation()
{
    Console.OutputEncoding = Encoding.UTF8;
    var cancellation = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true; // Finish the current file, so that the record stays accurate.
        cancellation.Cancel();
    };
    return cancellation;
}

static int Fail(ProgressBar bar, ArchiveException ex)
{
    bar.Clear();
    Console.Error.WriteLine($"error: {ex.Message}");
    return ExitFailed;
}

static int ExitCode(bool cancelled, int failed) =>
    cancelled ? ExitCancelled : failed > 0 ? ExitFailed : ExitOk;
