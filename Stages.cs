using System.Text;
using System.Text.Json;

namespace RepoMerger;

internal static class Stages
{
    public static MergeStageDefinition[] Definitions { get; } =
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
            "Run the repo-specific prepare.cs and validate.cs scripts against the external clone.",
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

        var gitVersion = await ProcessRunner.RunProcessAsync("git", ["--version"], context.ToolRoot).ConfigureAwait(false);
        if (gitVersion.ExitCode != 0)
            throw new InvalidOperationException("`git --version` failed.");

        Console.WriteLine($"Resolved source repo: {context.Settings.SourceRepo}");
        Console.WriteLine($"Resolved target repo: {context.Settings.TargetRepo}");
        Console.WriteLine($"Resolved target clone directory: {context.TargetRepoRoot}");
        Console.WriteLine($"Resolved target path: {fullTargetPath}");
        Console.WriteLine($"Resolved external work root: {fullWorkRoot}");
        Console.WriteLine($"Using script directory: {context.State.ScriptDirectory}");

        return $"Validated target repo and inputs. Git: {gitVersion.Output.Trim()}";
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
            context.State.ScriptRoot,
            context.State.ScriptSet,
            context.State.ScriptDirectory,
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
        var scriptDirectory = context.State.ScriptDirectory;
        if (!Directory.Exists(scriptDirectory))
        {
            throw new InvalidOperationException(
                $"The script directory '{scriptDirectory}' does not exist. " +
                "Add repo-specific scripts like prepare.cs and validate.cs or override it with --script-set/--script-root.");
        }

        if (!Directory.Exists(context.State.SourceCloneDirectory))
        {
            throw new InvalidOperationException(
                $"The source clone directory '{context.State.SourceCloneDirectory}' does not exist. " +
                "Run the clone-source stage first.");
        }

        var summaries = new List<string>();
        summaries.AddRange(await RunRepoScriptIfPresentAsync(context, "prepare.cs").ConfigureAwait(false));
        summaries.AddRange(await RunRepoScriptIfPresentAsync(context, "validate.cs").ConfigureAwait(false));

        if (summaries.Count == 0)
        {
            throw new InvalidOperationException(
                $"No prepare.cs or validate.cs script was found in '{scriptDirectory}'. " +
                "Add at least one repo-specific script for this stage.");
        }

        return string.Join(" ", summaries);
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
        summary.AppendLine($"Script directory : {context.State.ScriptDirectory}");
        summary.AppendLine($"Source HEAD      : {context.State.SourceHeadCommit}");
        summary.AppendLine($"Target HEAD      : {context.State.TargetHeadCommit}");
        summary.AppendLine($"Dry run          : {context.Settings.DryRun}");
        summary.AppendLine($"State file       : {context.StatePath}");
        summary.AppendLine();
        summary.AppendLine("Available follow-up commands:");
        summary.AppendLine($@"  dotnet run --project . -- --run-name {context.State.RunName} --resume");
        summary.AppendLine($@"  dotnet run --project . -- --run-name {context.State.RunName} --stage clone-source --rerun");
        summary.AppendLine($@"  dotnet run --project . -- --run-name {context.State.RunName} --stage prepare-source --rerun");
        summary.AppendLine();
        summary.AppendLine("Current status: the external clone and repo-specific prepare/validate stages are implemented.");

        var summaryPath = Path.Combine(context.RunDirectory, "summary.txt");
        await File.WriteAllTextAsync(summaryPath, summary.ToString()).ConfigureAwait(false);

        return $"Wrote run summary to '{summaryPath}'.";
    }

    private static async Task<IReadOnlyList<string>> RunRepoScriptIfPresentAsync(StageContext context, string scriptFileName)
    {
        var scriptPath = Path.Combine(context.State.ScriptDirectory, scriptFileName);
        if (!File.Exists(scriptPath))
            return [];

        Console.WriteLine($"Running repo-specific script '{scriptPath}' against '{context.State.SourceCloneDirectory}'.");
        var result = await ProcessRunner.RunProcessAsync(
            "dotnet",
            ["run", "--file", scriptPath, "--", context.State.SourceCloneDirectory],
            context.State.SourceCloneDirectory).ConfigureAwait(false);
        ProcessRunner.EnsureCommandSucceeded(result, $"dotnet run --file {scriptPath}");

        return [$"Ran {scriptFileName} successfully."];
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
            var cloneArguments = new List<string> { "clone", "--origin", remoteName };

            if (!string.IsNullOrWhiteSpace(branchName))
            {
                cloneArguments.Add("--branch");
                cloneArguments.Add(branchName);
            }

            if (allowNoHardlinks)
                cloneArguments.Add("--no-hardlinks");

            cloneArguments.Add(remoteUri);
            cloneArguments.Add(cloneDirectory);

            Console.WriteLine($"Cloning '{remoteUri}' into '{cloneDirectory}'.");
            var cloneResult = await ProcessRunner.RunProcessAsync("git", cloneArguments, workDirectory).ConfigureAwait(false);
            ProcessRunner.EnsureCommandSucceeded(cloneResult, "git clone");
        }
        else
        {
            if (!ProcessRunner.IsGitRepository(cloneDirectory))
                throw new InvalidOperationException($"The clone directory '{cloneDirectory}' already exists but is not a git repository.");

            var actualRemoteName = await ProcessRunner.GetPreferredRemoteNameAsync(cloneDirectory).ConfigureAwait(false);
            var remoteUrlResult = await ProcessRunner.RunProcessAsync("git", ["remote", "get-url", actualRemoteName], cloneDirectory).ConfigureAwait(false);
            ProcessRunner.EnsureCommandSucceeded(remoteUrlResult, "git remote get-url");
            var actualRemoteUri = remoteUrlResult.Output.Trim();
            if (!PathHelper.RepositoryLocationsMatch(actualRemoteUri, remoteUri))
            {
                throw new InvalidOperationException(
                    $"The existing clone at '{cloneDirectory}' points at '{actualRemoteUri}', not '{remoteUri}'. " +
                    "Use --reset or a different --run-name to create a fresh working copy.");
            }

            Console.WriteLine($"Refreshing existing clone in '{cloneDirectory}' from remote '{actualRemoteName}'.");

            var fetchResult = await ProcessRunner.RunProcessAsync("git", ["fetch", actualRemoteName, "--prune", "--tags"], cloneDirectory).ConfigureAwait(false);
            ProcessRunner.EnsureCommandSucceeded(fetchResult, "git fetch");

            var effectiveBranchName = branchName;
            if (string.IsNullOrWhiteSpace(effectiveBranchName))
            {
                var remoteHead = await ProcessRunner.RunProcessAsync(
                    "git",
                    ["symbolic-ref", "--short", $"refs/remotes/{actualRemoteName}/HEAD"],
                    cloneDirectory).ConfigureAwait(false);
                ProcessRunner.EnsureCommandSucceeded(remoteHead, "git symbolic-ref");
                effectiveBranchName = remoteHead.Output.Trim().Split('/', 2, StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
            }

            if (string.IsNullOrWhiteSpace(effectiveBranchName))
                throw new InvalidOperationException($"Could not determine which branch to check out for '{repositoryDisplayName}'.");

            var checkoutResult = await ProcessRunner.RunProcessAsync(
                "git",
                ["checkout", "-B", effectiveBranchName, $"{actualRemoteName}/{effectiveBranchName}"],
                cloneDirectory).ConfigureAwait(false);
            ProcessRunner.EnsureCommandSucceeded(checkoutResult, "git checkout");
        }

        var headCommitResult = await ProcessRunner.RunProcessAsync("git", ["rev-parse", "HEAD"], cloneDirectory).ConfigureAwait(false);
        ProcessRunner.EnsureCommandSucceeded(headCommitResult, "git rev-parse");
        var headCommit = headCommitResult.Output.Trim();
        onHeadResolved(headCommit);

        return $"Cloned/refreshed '{repositoryDisplayName}' into '{cloneDirectory}' at commit '{headCommit}'.";
    }
}
