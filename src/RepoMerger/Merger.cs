namespace RepoMerger;

internal static class Merger
{
    public static async Task<int> RunAsync(Settings settings)
    {
        var toolRoot = PathHelper.GetToolRoot();
        var runName = string.IsNullOrWhiteSpace(settings.RunName)
            ? PathHelper.GetDefaultRunName(settings.SourceRepo, settings.TargetRepo, settings.TargetPath)
            : PathHelper.SanitizePathSegment(settings.RunName);
        var workRoot = PathHelper.GetAbsolutePath(toolRoot, settings.WorkRoot);
        PathHelper.EnsurePathIsOutsideRepo(toolRoot, workRoot, "--work-root");

        var workDirectory = Path.Combine(workRoot, runName);
        var targetRepoRoot = Path.Combine(workDirectory, "target");

        if (settings.PostMergeCleanupOnly)
        {
            if (!Directory.Exists(workDirectory))
            {
                throw new InvalidOperationException(
                    $"Cleanup-only mode requires an existing work directory at '{workDirectory}'. " +
                    "Run the full workflow first, or point --work-root/--run-name at an existing run.");
            }

            Console.WriteLine($"Reusing existing work directory '{workDirectory}' for post-merge cleanup only.");
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
        if (settings.PostMergeCleanupOnly)
            Console.WriteLine("Running in post-merge-cleanup-only mode.");

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
            DryRun = settings.DryRun,
        };

        var context = new StageContext(
            Settings: settings,
            ToolRoot: toolRoot,
            TargetRepoRoot: targetRepoRoot,
            RunDirectory: workDirectory,
            State: state);

        foreach (var definition in Stages.GetExecutionPlan(settings))
        {
            try
            {
                Console.WriteLine($"Starting stage '{definition.Name}': {definition.Description}");
                var summary = await definition.ExecuteAsync(context).ConfigureAwait(false);
                Console.WriteLine(summary);
                Console.WriteLine($"Completed stage '{definition.Name}'.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Stage '{definition.Name}' failed: {ex.Message}");
                return 1;
            }
        }

        Console.WriteLine("Repo-merge run completed successfully.");
        return 0;
    }
}
