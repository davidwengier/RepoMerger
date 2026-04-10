namespace RepoMerger;

using System.CommandLine;

internal static class Program
{
    private const string DefaultSourceRepo = "dotnet/razor";
    private const string DefaultSourceBranch = "main";
    private const string DefaultTargetRepo = "dotnet/roslyn";
    private const string DefaultTargetPath = @"src\Razor";
    private const string DefaultWorkRoot = @"..\repo-merge-work";

    public static async Task<int> Main(string[] args)
    {
        var sourceRepoOption = new Option<string>("--source-repo")
        {
            Description = "Source repository or local path.",
            DefaultValueFactory = _ => DefaultSourceRepo
        };

        var sourceBranchOption = new Option<string>("--source-branch")
        {
            Description = "Source branch to use.",
            DefaultValueFactory = _ => DefaultSourceBranch
        };

        var targetRepoOption = new Option<string>("--target-repo")
        {
            Description = "Target repository in owner/repo format.",
            DefaultValueFactory = _ => DefaultTargetRepo
        };

        var targetPathOption = new Option<string>("--target-path")
        {
            Description = "Destination path inside the target repo.",
            DefaultValueFactory = _ => DefaultTargetPath
        };

        var workRootOption = new Option<string>("--work-root")
        {
            Description = "Where the cloned working repos live.",
            DefaultValueFactory = _ => DefaultWorkRoot
        };

        var runNameOption = new Option<string?>("--run-name")
        {
            Description = "Stable run name for the external work directory."
        };

        var dryRunOption = new Option<bool>("--dry-run")
        {
            Description = "Skip side-effecting stage work."
        };

        var skipHistoryFilterOption = new Option<bool>("--skip-history-filter")
        {
            Description = "Skip the post-prepare history filtering step and merge the full prepared source history."
        };

        var rootCommand = new RootCommand("Run a fresh source-to-target merge in an external work area.")
        {
            sourceRepoOption,
            sourceBranchOption,
            targetRepoOption,
            targetPathOption,
            workRootOption,
            runNameOption,
            skipHistoryFilterOption,
            dryRunOption,
        };

        rootCommand.SetAction(async (parseResult, cancellationToken) =>
        {
            var sourceRepo = parseResult.GetValue(sourceRepoOption)!;
            var sourceBranch = parseResult.GetValue(sourceBranchOption)!;
            var targetRepo = parseResult.GetValue(targetRepoOption)!;
            var targetPath = parseResult.GetValue(targetPathOption)!;
            var workRoot = parseResult.GetValue(workRootOption)!;
            var runName = parseResult.GetValue(runNameOption);
            var skipHistoryFilter = parseResult.GetValue(skipHistoryFilterOption);
            var dryRun = parseResult.GetValue(dryRunOption);

            return await InvokeRunAsync(sourceRepo, sourceBranch, targetRepo, targetPath,
                workRoot, runName, skipHistoryFilter, dryRun,
                cancellationToken);
        });

        return await rootCommand.Parse(args).InvokeAsync();
    }

    private static async Task<int> InvokeRunAsync(string sourceRepo, string sourceBranch, string targetRepo,
        string targetPath, string workRoot, string? runName, bool skipHistoryFilter, bool dryRun,
        CancellationToken cancellationToken)
    {
        var settings = new Settings(
            SourceRepo: sourceRepo,
            SourceBranch: sourceBranch,
            TargetRepo: targetRepo,
            TargetPath: targetPath,
            WorkRoot: workRoot,
            RunName: runName,
            SkipHistoryFilter: skipHistoryFilter,
            DryRun: dryRun);

        try
        {
            await Merger.RunAsync(settings).ConfigureAwait(false);
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ERROR: {ex.Message}");
            return 1;
        }
    }
}
