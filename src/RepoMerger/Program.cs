namespace RepoMerger;

using System.CommandLine;

internal static class Program
{
    private const string DefaultSourceRepo = "dotnet/razor";
    private const string DefaultSourceBranch = "main";
    private const string DefaultTargetRepo = "dotnet/roslyn";
    private const string DefaultTargetPath = @"src\Razor";
    private const string DefaultWorkRoot = @"..\repo-merge-work";
    private const string DefaultStateRoot = DefaultWorkRoot;

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

        var stateRootOption = new Option<string>("--state-root")
        {
            Description = "Where run state is persisted.",
            DefaultValueFactory = _ => DefaultStateRoot
        };

        var workRootOption = new Option<string>("--work-root")
        {
            Description = "Where the cloned working repos live.",
            DefaultValueFactory = _ => DefaultWorkRoot
        };

        var runNameOption = new Option<string?>("--run-name")
        {
            Description = "Stable run name for resume/rerun."
        };

        var stageOption = new Option<string?>("--stage")
        {
            Description = "Run a single named stage."
        };

        var startAtOption = new Option<string?>("--start-at")
        {
            Description = "Start execution at a specific stage."
        };

        var stopAfterOption = new Option<string?>("--stop-after")
        {
            Description = "Stop after a specific stage."
        };

        var listStagesOption = new Option<bool>("--list-stages")
        {
            Description = "List the available stage names and exit."
        };

        var dryRunOption = new Option<bool>("--dry-run")
        {
            Description = "Skip side-effecting stage work."
        };

        var skipHistoryFilterOption = new Option<bool>("--skip-history-filter")
        {
            Description = "Skip the post-prepare history filtering step and merge the full prepared source history."
        };

        var resumeOption = new Option<bool>("--resume")
        {
            Description = "Resume a previous run from state.json."
        };

        var rerunOption = new Option<bool>("--rerun")
        {
            Description = "Re-execute completed stages."
        };

        var resetOption = new Option<bool>("--reset")
        {
            Description = "Delete previous run state and start fresh."
        };

        var rootCommand = new RootCommand("Repeatable, resumable merge orchestration for bringing a source repo into a target repo.")
        {
            sourceRepoOption,
            sourceBranchOption,
            targetRepoOption,
            targetPathOption,
            stateRootOption,
            workRootOption,
            runNameOption,
            stageOption,
            startAtOption,
            stopAfterOption,
            listStagesOption,
            skipHistoryFilterOption,
            dryRunOption,
            resumeOption,
            rerunOption,
            resetOption,
        };

        rootCommand.SetAction(async (parseResult, cancellationToken) =>
        {
            var sourceRepo = parseResult.GetValue(sourceRepoOption)!;
            var sourceBranch = parseResult.GetValue(sourceBranchOption)!;
            var targetRepo = parseResult.GetValue(targetRepoOption)!;
            var targetPath = parseResult.GetValue(targetPathOption)!;
            var stateRoot = parseResult.GetValue(stateRootOption)!;
            var workRoot = parseResult.GetValue(workRootOption)!;
            var runName = parseResult.GetValue(runNameOption);
            var stage = parseResult.GetValue(stageOption);
            var startAt = parseResult.GetValue(startAtOption);
            var stopAfter = parseResult.GetValue(stopAfterOption);
            var listStages = parseResult.GetValue(listStagesOption);
            var skipHistoryFilter = parseResult.GetValue(skipHistoryFilterOption);
            var dryRun = parseResult.GetValue(dryRunOption);
            var resume = parseResult.GetValue(resumeOption);
            var rerun = parseResult.GetValue(rerunOption);
            var reset = parseResult.GetValue(resetOption);

            return await InvokeRunAsync(sourceRepo, sourceBranch, targetRepo, targetPath, stateRoot,
                workRoot, runName, stage, startAt, stopAfter, listStages, skipHistoryFilter, dryRun, resume, rerun, reset,
                cancellationToken);
        });

        return await rootCommand.Parse(args).InvokeAsync();
    }

    private static async Task<int> InvokeRunAsync(string sourceRepo, string sourceBranch, string targetRepo,
        string targetPath, string stateRoot, string workRoot, string? runName, string? stage, string? startAt,
        string? stopAfter, bool listStages, bool skipHistoryFilter, bool dryRun, bool resume, bool rerun, bool reset,
        CancellationToken cancellationToken)
    {
        if (listStages)
        {
            PrintStages();
            return 0;
        }

        var settings = new Settings(
            SourceRepo: sourceRepo,
            SourceBranch: sourceBranch,
            TargetRepo: targetRepo,
            TargetPath: targetPath,
            StateRoot: stateRoot,
            WorkRoot: workRoot,
            RunName: runName,
            Stage: stage,
            StartAt: startAt,
            StopAfter: stopAfter,
            ListStages: listStages,
            SkipHistoryFilter: skipHistoryFilter,
            DryRun: dryRun,
            Resume: resume,
            Rerun: rerun,
            Reset: reset);

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

    private static void PrintStages()
    {
        Console.WriteLine("Available repo-merge stages:");
        foreach (var stage in Stages.Definitions)
            Console.WriteLine($"  {stage.Name,-20} {stage.Description}");
    }
}
