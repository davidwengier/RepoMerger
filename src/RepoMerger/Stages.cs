using System.Text;
using System.Text.Json;

namespace RepoMerger;

internal static class Stages
{
    public static StageDefinition[] Definitions { get; } =
    [
        new(
            "validate-environment",
            "Validate the target repo, input arguments, and required tooling.",
            ValidateEnvironmentAsync),
        new(
            "prepare-state",
            "Create the persisted state and external work-area layout for the run.",
            PrepareStateAsync),
        new(
            "clone-source",
            "Clone or refresh the source and target repositories in the external work area.",
            CloneSourceAsync),
        new(
            "prepare-source",
            "Run the repo-specific handler against the external clone.",
            PrepareSourceAsync),
        new(
            "merge-into-target",
            "Placeholder stage for importing the prepared repo into the target repo.",
            MergeIntoTargetAsync),
        new(
            "finalize-scaffold",
            "Write the current run summary and next-step commands.",
            FinalizeScaffoldAsync),
    ];

    private static async Task<string> ValidateEnvironmentAsync(StageContext context)
    {
        if (string.IsNullOrWhiteSpace(context.Settings.SourceRepo))
            throw new InvalidOperationException("--source-repo must not be empty.");

        if (string.IsNullOrWhiteSpace(context.Settings.TargetPath))
            throw new InvalidOperationException("--target-path must not be empty.");

        if (string.IsNullOrWhiteSpace(context.Settings.TargetRepo))
            throw new InvalidOperationException("--target-repo must not be empty.");

        var fullTargetPath = PathHelper.GetAbsolutePath(context.TargetRepoRoot, context.Settings.TargetPath);
        var fullWorkRoot = PathHelper.GetAbsolutePath(context.ToolRoot, context.Settings.WorkRoot);
        if (!PathHelper.IsPathWithinRoot(context.TargetRepoRoot, fullTargetPath))
            throw new InvalidOperationException($"The target path '{context.Settings.TargetPath}' escapes the target repo root.");

        if (Path.IsPathRooted(context.Settings.TargetPath))
            throw new InvalidOperationException("--target-path must be relative to the target repo root.");

        PathHelper.EnsurePathIsOutsideRepo(context.ToolRoot, fullWorkRoot, "--work-root");

        if (PathHelper.LooksLikeLocalPath(context.Settings.SourceRepo))
        {
            var fullSourcePath = PathHelper.GetAbsolutePath(context.ToolRoot, context.Settings.SourceRepo);
            if (!Directory.Exists(fullSourcePath))
                throw new InvalidOperationException($"The local source repo path '{fullSourcePath}' does not exist.");
        }
        else if (context.Settings.SourceRepo.Count(static c => c == '/') != 1)
        {
            throw new InvalidOperationException("--source-repo must be in owner/repo format or a valid local path.");
        }

        if (PathHelper.LooksLikeLocalPath(context.Settings.TargetRepo) || context.Settings.TargetRepo.Count(static c => c == '/') != 1)
            throw new InvalidOperationException("--target-repo must be in owner/repo format.");

        var gitVersion = await GitRunner.RunGitAsync(context.ToolRoot, "--version").ConfigureAwait(false);

        Console.WriteLine($"Resolved source repo: {context.Settings.SourceRepo}");
        Console.WriteLine($"Resolved target repo: {context.Settings.TargetRepo}");
        Console.WriteLine($"Resolved target clone directory: {context.TargetRepoRoot}");
        Console.WriteLine($"Resolved target path: {fullTargetPath}");
        Console.WriteLine($"Resolved external work root: {fullWorkRoot}");
        Console.WriteLine($"Resolved repository handler key: {RepositoryHandlerLoader.GetRepositoryKey(context.Settings.SourceRepo)}");

        return $"Validated target repo and inputs. Git: {gitVersion.Trim()}";
    }

    private static async Task<string> PrepareStateAsync(StageContext context)
    {
        Directory.CreateDirectory(Path.Combine(context.RunDirectory, "sentinels"));
        Directory.CreateDirectory(context.State.WorkDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(context.State.SourceCloneDirectory)!);
        Directory.CreateDirectory(Path.GetDirectoryName(context.State.TargetRepoRoot)!);

        var manifest = new
        {
            workflowVersion = Constants.WorkflowVersion,
            stateSchemaVersion = Constants.StateSchemaVersion,
            context.Settings.SourceRepo,
            context.Settings.SourceBranch,
            context.Settings.TargetRepo,
            context.Settings.TargetPath,
            context.State.WorkRoot,
            context.State.WorkDirectory,
            context.State.SourceCloneDirectory,
            context.State.TargetRepoRoot,
            context.Settings.DryRun,
            selectedStages = new
            {
                start = context.State.SelectedStartStage,
                stop = context.State.SelectedStopStage,
            },
        };

        var manifestPath = Path.Combine(context.RunDirectory, "inputs.json");
        await File.WriteAllTextAsync(
            manifestPath,
            JsonSerializer.Serialize(manifest, RunStateStore.JsonOptions)).ConfigureAwait(false);

        return $"Prepared persisted state under '{context.RunDirectory}' and external work area '{context.State.WorkDirectory}'.";
    }

    private static async Task<string> CloneSourceAsync(StageContext context)
    {
        var summaries = new List<string>
        {
            await CloneOrRefreshRepositoryAsync(
                remoteName: "source",
                repositoryDisplayName: context.Settings.SourceRepo,
                remoteUri: context.State.SourceRemoteUri,
                branchName: context.Settings.SourceBranch,
                cloneDirectory: context.State.SourceCloneDirectory,
                workDirectory: context.State.WorkDirectory,
                allowNoHardlinks: PathHelper.LooksLikeLocalPath(context.Settings.SourceRepo),
                isDryRun: context.Settings.DryRun,
                onHeadResolved: commit => context.State.SourceHeadCommit = commit).ConfigureAwait(false),
            await CloneOrRefreshRepositoryAsync(
                remoteName: "target",
                repositoryDisplayName: context.Settings.TargetRepo,
                remoteUri: context.State.TargetRemoteUri,
                branchName: null,
                cloneDirectory: context.TargetRepoRoot,
                workDirectory: context.State.WorkDirectory,
                allowNoHardlinks: false,
                isDryRun: context.Settings.DryRun,
                onHeadResolved: commit => context.State.TargetHeadCommit = commit).ConfigureAwait(false),
        };

        return string.Join(" ", summaries);
    }

    private static async Task<string> PrepareSourceAsync(StageContext context)
    {
        if (!Directory.Exists(context.State.SourceCloneDirectory))
        {
            throw new InvalidOperationException(
                $"The source clone directory '{context.State.SourceCloneDirectory}' does not exist. " +
                "Run the clone-source stage first.");
        }

        return await RepositoryHandlerLoader.RunAsync(context).ConfigureAwait(false);
    }

    private static Task<string> MergeIntoTargetAsync(StageContext context)
    {
        var importDirectory = context.State.ImportPreviewDirectory;
        Directory.CreateDirectory(importDirectory);

        var fullTargetPath = PathHelper.GetAbsolutePath(context.TargetRepoRoot, context.Settings.TargetPath);
        return Task.FromResult(
            $"Placeholder only. A future milestone will import the prepared repo into '{fullTargetPath}' under target repo '{context.TargetRepoRoot}'.");
    }

    private static async Task<string> FinalizeScaffoldAsync(StageContext context)
    {
        var summary = new StringBuilder();
        summary.AppendLine("RepoMerger run summary");
        summary.AppendLine("=====================");
        summary.AppendLine($"Workflow version : {Constants.WorkflowVersion}");
        summary.AppendLine($"Run name         : {context.State.RunName}");
        summary.AppendLine($"Source repo      : {context.Settings.SourceRepo}");
        summary.AppendLine($"Source branch    : {context.Settings.SourceBranch}");
        summary.AppendLine($"Target repo      : {context.Settings.TargetRepo}");
        summary.AppendLine($"Target path      : {context.Settings.TargetPath}");
        summary.AppendLine($"Work root        : {context.State.WorkRoot}");
        summary.AppendLine($"Source clone dir : {context.State.SourceCloneDirectory}");
        summary.AppendLine($"Target clone dir : {context.TargetRepoRoot}");
        summary.AppendLine($"Handler key      : {RepositoryHandlerLoader.GetRepositoryKey(context.Settings.SourceRepo)}");
        summary.AppendLine($"Source HEAD      : {context.State.SourceHeadCommit}");
        summary.AppendLine($"Target HEAD      : {context.State.TargetHeadCommit}");
        summary.AppendLine($"Dry run          : {context.Settings.DryRun}");
        summary.AppendLine($"State file       : {context.StatePath}");
        summary.AppendLine();
        summary.AppendLine("Available follow-up commands:");
        summary.AppendLine($@"  dotnet run --project src\RepoMerger\RepoMerger.csproj -- --run-name {context.State.RunName} --resume");
        summary.AppendLine($@"  dotnet run --project src\RepoMerger\RepoMerger.csproj -- --run-name {context.State.RunName} --stage clone-source --rerun");
        summary.AppendLine($@"  dotnet run --project src\RepoMerger\RepoMerger.csproj -- --run-name {context.State.RunName} --stage prepare-source --rerun");
        summary.AppendLine();
        summary.AppendLine("Current status: the external clone and repo-specific prepare/validate stages are implemented.");

        var summaryPath = Path.Combine(context.RunDirectory, "summary.txt");
        await File.WriteAllTextAsync(summaryPath, summary.ToString()).ConfigureAwait(false);

        return $"Wrote run summary to '{summaryPath}'.";
    }

    private static async Task<string> CloneOrRefreshRepositoryAsync(
        string remoteName,
        string repositoryDisplayName,
        string remoteUri,
        string? branchName,
        string cloneDirectory,
        string workDirectory,
        bool allowNoHardlinks,
        bool isDryRun,
        Action<string> onHeadResolved)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(cloneDirectory)!);

        if (isDryRun)
            return $"Dry run: would clone or refresh '{repositoryDisplayName}' from '{remoteUri}' into '{cloneDirectory}'.";

        if (!Directory.Exists(cloneDirectory))
        {
            Console.WriteLine($"Cloning '{remoteUri}' into '{cloneDirectory}'.");
            await GitRunner.CloneAsync(
                workingDirectory: workDirectory,
                remoteName: remoteName,
                remoteUri: remoteUri,
                cloneDirectory: cloneDirectory,
                branchName: branchName,
                noHardlinks: allowNoHardlinks).ConfigureAwait(false);
        }
        else
        {
            if (!GitRunner.IsRepository(cloneDirectory))
                throw new InvalidOperationException($"The clone directory '{cloneDirectory}' already exists but is not a git repository.");

            var actualRemoteName = await GitRunner.GetPreferredRemoteNameAsync(cloneDirectory).ConfigureAwait(false);
            var actualRemoteUri = await GitRunner.GetRemoteUrlAsync(cloneDirectory, actualRemoteName).ConfigureAwait(false);
            if (!PathHelper.RepositoryLocationsMatch(actualRemoteUri, remoteUri))
            {
                throw new InvalidOperationException(
                    $"The existing clone at '{cloneDirectory}' points at '{actualRemoteUri}', not '{remoteUri}'. " +
                    "Use --reset or a different --run-name to create a fresh working copy.");
            }

            Console.WriteLine($"Refreshing existing clone in '{cloneDirectory}' from remote '{actualRemoteName}'.");

            await GitRunner.FetchAsync(cloneDirectory, actualRemoteName).ConfigureAwait(false);

            var effectiveBranchName = branchName;
            if (string.IsNullOrWhiteSpace(effectiveBranchName))
            {
                effectiveBranchName = await GitRunner.GetRemoteHeadBranchAsync(cloneDirectory, actualRemoteName).ConfigureAwait(false);
            }

            if (string.IsNullOrWhiteSpace(effectiveBranchName))
                throw new InvalidOperationException($"Could not determine which branch to check out for '{repositoryDisplayName}'.");

            await GitRunner.CheckoutTrackingBranchAsync(cloneDirectory, actualRemoteName, effectiveBranchName).ConfigureAwait(false);
        }

        var headCommit = await GitRunner.GetHeadCommitAsync(cloneDirectory).ConfigureAwait(false);
        onHeadResolved(headCommit);

        return $"Cloned/refreshed '{repositoryDisplayName}' into '{cloneDirectory}' at commit '{headCommit}'.";
    }
}
