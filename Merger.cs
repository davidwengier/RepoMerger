using System.Diagnostics;
using System.Text;

namespace RepoMerger;

internal static class Merger
{
    public static async Task<int> RunAsync(Settings settings)
    {
        var runStopwatch = Stopwatch.StartNew();
        var toolRoot = PathHelper.GetToolRoot();
        var runName = string.IsNullOrWhiteSpace(settings.RunName)
            ? PathHelper.GetDefaultRunName(settings.SourceRepo, settings.TargetRepo, settings.TargetPath)
            : PathHelper.SanitizePathSegment(settings.RunName);
        var workRoot = PathHelper.GetAbsolutePath(toolRoot, settings.WorkRoot);
        PathHelper.EnsurePathIsOutsideRepo(toolRoot, workRoot, "--work-root");

        var workDirectory = Path.Combine(workRoot, runName);
        var targetRepoRoot = Path.Combine(workDirectory, "target");

        if (settings.RunsSinglePostMergeCleanup)
        {
            if (!Directory.Exists(workDirectory))
            {
                throw new InvalidOperationException(
                    $"Single-step cleanup mode requires an existing work directory at '{workDirectory}'. " +
                    "Run the full workflow first, or point --work-root/--run-name at an existing run.");
            }

            Console.WriteLine(
                $"Reusing existing work directory '{workDirectory}' for post-merge cleanup step '{settings.PostMergeCleanupStep}'.");
        }
        else if (Directory.Exists(workDirectory))
        {
            Console.WriteLine($"Deleting existing work directory '{workDirectory}' for a fresh run.");
            Directory.Delete(workDirectory, recursive: true);
        }

        Directory.CreateDirectory(workDirectory);

        Console.WriteLine($"Starting repo-merge run '{runName}' (workflow version {Constants.WorkflowVersion}).");
        if (settings.DryRun)
            Console.WriteLine("Running in dry-run mode.");
        if (settings.RunsSinglePostMergeCleanup)
            Console.WriteLine($"Running in single post-merge cleanup mode for '{settings.PostMergeCleanupStep}'.");

        var state = new RunState
        {
            WorkflowVersion = Constants.WorkflowVersion,
            RunName = runName,
            SourceRepo = settings.SourceRepo,
            SourceBranch = settings.SourceBranch,
            TargetRepo = settings.TargetRepo,
            TargetRepoRoot = targetRepoRoot,
            TargetPath = settings.TargetPath,
            WorkRoot = workRoot,
            RunDirectory = workDirectory,
            WorkDirectory = workDirectory,
            SourceRemoteUri = PathHelper.ResolveRepositoryUri(settings.SourceRepo, toolRoot),
            TargetRemoteUri = PathHelper.ResolveRepositoryUri(settings.TargetRepo, toolRoot),
            SourceCloneDirectory = Path.Combine(workDirectory, "source"),
            FilteredSourceCloneDirectory = Path.Combine(workDirectory, "filtered-source"),
            DryRun = settings.DryRun,
        };

        var context = new StageContext(
            Settings: settings,
            ToolRoot: toolRoot,
            TargetRepoRoot: targetRepoRoot,
            RunDirectory: workDirectory,
            State: state);

        var exitCode = 0;
        foreach (var definition in Stages.GetExecutionPlan(settings))
        {
            var stageStopwatch = Stopwatch.StartNew();
            try
            {
                Console.WriteLine($"Starting stage '{definition.Name}': {definition.Description}");
                var summary = await definition.ExecuteAsync(context).ConfigureAwait(false);
                stageStopwatch.Stop();
                context.State.StageResults.Add(new StageExecutionResult(
                    definition.Name,
                    definition.Description,
                    stageStopwatch.Elapsed,
                    Succeeded: true,
                    summary));
                Console.WriteLine(summary);
                Console.WriteLine($"Completed stage '{definition.Name}'.");
            }
            catch (Exception ex)
            {
                stageStopwatch.Stop();
                context.State.StageResults.Add(new StageExecutionResult(
                    definition.Name,
                    definition.Description,
                    stageStopwatch.Elapsed,
                    Succeeded: false,
                    ex.Message));
                Console.WriteLine($"Stage '{definition.Name}' failed: {ex.Message}");
                exitCode = 1;
                break;
            }
        }

        if (exitCode == 0)
            Console.WriteLine("Repo-merge run completed successfully.");

        runStopwatch.Stop();
        PrintRunSummary(context.State, runStopwatch.Elapsed, exitCode == 0);

        return exitCode;
    }

    private static void PrintRunSummary(RunState state, TimeSpan totalDuration, bool succeeded)
    {
        var builder = new StringBuilder()
            .AppendLine()
            .AppendLine("Run summary")
            .AppendLine("===========")
            .AppendLine($"Overall result: {(succeeded ? "success" : "failed")}")
            .AppendLine("Stages:");

        foreach (var stage in state.StageResults)
            builder.AppendLine($"- {stage.Name} [{(stage.Succeeded ? "ok" : "failed")}] ({FormatDuration(stage.Duration)})");

        if (state.CleanupResults.Count > 0)
        {
            builder.AppendLine("Cleanup steps:");
            foreach (var step in state.CleanupResults)
                builder.AppendLine($"- {step.DisplayName} [{step.Status}] ({FormatDuration(step.Duration)})");
        }

        builder.AppendLine($"Total runtime: {FormatDuration(totalDuration)}");
        Console.Write(builder.ToString());
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalHours >= 1)
            return duration.ToString(@"h\:mm\:ss\.ff");

        if (duration.TotalMinutes >= 1)
            return duration.ToString(@"m\:ss\.ff");

        return duration.ToString(@"s\.ff\s");
    }
}
