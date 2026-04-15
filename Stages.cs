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
            "prepare-workspace",
            "Create the fresh external work-area layout for the run.",
            PrepareStateAsync),
        new(
            "clone-source",
            "Clone or refresh the source repository in the external work area.",
            CloneSourceAsync),
        new(
            "clone-target",
            "Clone or refresh the target repository in the external work area.",
            CloneTargetAsync),
        new(
            "prepare-source",
            "Run the repo-specific handler against the external clone.",
            PrepareSourceAsync),
        new(
            "filter-source-history",
            "Create a separate filtered source clone so only the import path remains for merging.",
            FilterSourceHistoryAsync),
        new(
            "merge-into-target",
            "Merge the prepared source history into the target repo at the selected path.",
            MergeIntoTargetAsync),
        new(
            "post-merge-cleanup",
            "Apply targeted Razor cleanup fixes in the target repo, committing each cleanup separately.",
            PostMergeCleanupAsync),
    ];

    public static IEnumerable<StageDefinition> GetExecutionPlan(Settings settings)
    {
        if (!settings.RunsSinglePostMergeCleanup)
            return Definitions;

        return Definitions.Where(static definition =>
            definition.Name is "validate-environment" or "post-merge-cleanup");
    }

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

        if (context.Settings.PostMergeCleanupStep is not null
            && string.IsNullOrWhiteSpace(context.Settings.PostMergeCleanupStep))
        {
            throw new InvalidOperationException("--post-merge-cleanup-step must not be empty.");
        }

        if (context.Settings.RunsSinglePostMergeCleanup
            && (!Directory.Exists(context.TargetRepoRoot) || !GitRunner.IsRepository(context.TargetRepoRoot)))
        {
            throw new InvalidOperationException(
                $"--post-merge-cleanup-step requires an existing target clone at '{context.TargetRepoRoot}'. " +
                "Run the full workflow first, or point --work-root/--run-name at an existing run.");
        }

        if (context.Settings.RunsSinglePostMergeCleanup
            && !PostMergeCleanupRunner.ContainsStep(context.Settings.PostMergeCleanupStep!))
        {
            throw new InvalidOperationException(
                $"Unknown post-merge cleanup step '{context.Settings.PostMergeCleanupStep}'. " +
                $"Available steps: {string.Join(", ", PostMergeCleanupRunner.StepNames)}");
        }

        var gitVersion = await GitRunner.RunGitAsync(context.ToolRoot, "--version").ConfigureAwait(false);

        Console.WriteLine($"Resolved source repo: {context.Settings.SourceRepo}");
        Console.WriteLine($"Resolved target repo: {context.Settings.TargetRepo}");
        Console.WriteLine($"Resolved target clone directory: {context.TargetRepoRoot}");
        Console.WriteLine($"Resolved target path: {fullTargetPath}");
        Console.WriteLine($"Resolved external work root: {fullWorkRoot}");
        Console.WriteLine($"Resolved source repo key: {RepositoryPreparer.GetRepositoryKey(context.Settings.SourceRepo)}");

        return $"Validated target repo and inputs. Git: {gitVersion.Trim()}";
    }

    private static async Task<string> PrepareStateAsync(StageContext context)
    {
        Directory.CreateDirectory(context.State.WorkDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(context.State.SourceCloneDirectory)!);
        Directory.CreateDirectory(Path.GetDirectoryName(context.State.FilteredSourceCloneDirectory)!);
        Directory.CreateDirectory(Path.GetDirectoryName(context.State.TargetRepoRoot)!);

        return $"Prepared fresh work area '{context.State.WorkDirectory}'.";
    }

    private static Task<string> CloneSourceAsync(StageContext context)
        => CloneOrRefreshRepositoryAsync(
            remoteName: "source",
            repositoryDisplayName: context.Settings.SourceRepo,
            remoteUri: context.State.SourceRemoteUri,
            localBranchName: null,
            branchName: context.Settings.SourceBranch,
            cloneDirectory: context.State.SourceCloneDirectory,
            workDirectory: context.State.WorkDirectory,
            allowNoHardlinks: PathHelper.LooksLikeLocalPath(context.Settings.SourceRepo),
            isDryRun: context.Settings.DryRun,
            onHeadResolved: commit => context.State.SourceHeadCommit = commit,
            onBranchResolved: _ => { });

    private static Task<string> CloneTargetAsync(StageContext context)
        => CloneOrRefreshRepositoryAsync(
            remoteName: "target",
            repositoryDisplayName: context.Settings.TargetRepo,
            remoteUri: context.State.TargetRemoteUri,
            localBranchName: GetTargetMergeBranchName(context.State.RunName),
            branchName: null,
            cloneDirectory: context.TargetRepoRoot,
            workDirectory: context.State.WorkDirectory,
            allowNoHardlinks: false,
            isDryRun: context.Settings.DryRun,
            onHeadResolved: commit => context.State.TargetHeadCommit = commit,
            onBranchResolved: branch => context.State.TargetBranch = branch);

    private static async Task<string> PrepareSourceAsync(StageContext context)
    {
        if (context.Settings.DryRun)
        {
            return
                $"Dry run: would run the repository handler against '{context.State.SourceCloneDirectory}' " +
                "before continuing with merge preparation.";
        }

        if (!Directory.Exists(context.State.SourceCloneDirectory))
        {
            throw new InvalidOperationException(
                $"The source clone directory '{context.State.SourceCloneDirectory}' does not exist. " +
                "Run the clone-source stage first.");
        }

        if (!Directory.Exists(context.TargetRepoRoot))
        {
            throw new InvalidOperationException(
                $"The target clone directory '{context.TargetRepoRoot}' does not exist. " +
                "Run the clone-target stage first.");
        }

        if (SourceHistoryFilter.IsFilteredInPlace(context.State.SourceCloneDirectory, context.Settings.TargetPath))
        {
            context.State.SourceHeadCommit = await GitRunner.GetHeadCommitAsync(context.State.SourceCloneDirectory).ConfigureAwait(false);
            return
                $"Source repo is already filtered in place under '{context.Settings.TargetPath}'. " +
                $"Current source HEAD is '{context.State.SourceHeadCommit}'.";
        }

        var summary = await RepositoryPreparer.RunAsync(context).ConfigureAwait(false);
        context.State.SourceHeadCommit = await GitRunner.GetHeadCommitAsync(context.State.SourceCloneDirectory).ConfigureAwait(false);
        return $"{summary} Prepared source HEAD is '{context.State.SourceHeadCommit}'.";
    }

    private static async Task<string> FilterSourceHistoryAsync(StageContext context)
    {
        var targetRelativePath = PathHelper.NormalizeRelativeTargetPath(context.Settings.TargetPath, "Source history filtering");
        if (context.Settings.SkipHistoryFilter)
            return "Skipped history filtering (--skip-history-filter).";

        if (context.Settings.DryRun)
        {
            return
                $"Dry run: would create a filtered clone at '{context.State.FilteredSourceCloneDirectory}' " +
                $"from '{context.State.SourceCloneDirectory}' that retains only history for '{targetRelativePath}'.";
        }

        var summary = await SourceHistoryFilter.CreateFilteredCloneAsync(
            context.State.SourceCloneDirectory,
            context.State.FilteredSourceCloneDirectory,
            targetRelativePath).ConfigureAwait(false);

        context.State.SourceHeadCommit = await GitRunner.GetHeadCommitAsync(context.State.FilteredSourceCloneDirectory).ConfigureAwait(false);
        return $"{summary} Filtered source HEAD is '{context.State.SourceHeadCommit}'.";
    }

    private static async Task<string> MergeIntoTargetAsync(StageContext context)
    {
        var targetRelativePath = PathHelper.NormalizeRelativeTargetPath(context.Settings.TargetPath, "Target merge");
        var fullTargetPath = PathHelper.GetAbsolutePath(context.TargetRepoRoot, targetRelativePath);

        if (context.Settings.DryRun)
        {
            return
                $"Dry run: would optionally create the filtered source clone for '{targetRelativePath}' " +
                $"and merge it into '{fullTargetPath}' while preserving surviving file history.";
        }

        if (!Directory.Exists(context.State.SourceCloneDirectory) || !GitRunner.IsRepository(context.State.SourceCloneDirectory))
        {
            throw new InvalidOperationException(
                $"The source clone directory '{context.State.SourceCloneDirectory}' does not exist or is not a git repository. " +
                "Run the clone-source and prepare-source stages first.");
        }

        if (!Directory.Exists(context.TargetRepoRoot) || !GitRunner.IsRepository(context.TargetRepoRoot))
        {
            throw new InvalidOperationException(
                $"The target clone directory '{context.TargetRepoRoot}' does not exist or is not a git repository. " +
                "Run the clone-target stage first.");
        }

        var sourceSolutionPath = GetSourceSolutionPath(context.State.SourceCloneDirectory);
        var sourceSolutionContent = sourceSolutionPath is null
            ? string.Empty
            : await File.ReadAllTextAsync(sourceSolutionPath).ConfigureAwait(false);

        var filterSummary = context.Settings.SkipHistoryFilter
            ? "Skipped history filtering (--skip-history-filter)."
            : await FilterSourceHistoryAsync(context).ConfigureAwait(false);
        var sourceRootForMerge = context.Settings.SkipHistoryFilter
            ? context.State.SourceCloneDirectory
            : context.State.FilteredSourceCloneDirectory;
        var sourceHeadCommit = await GitRunner.GetHeadCommitAsync(sourceRootForMerge).ConfigureAwait(false);
        context.State.SourceHeadCommit = sourceHeadCommit;

        var mergeHeadPath = Path.Combine(context.TargetRepoRoot, ".git", "MERGE_HEAD");
        if (File.Exists(mergeHeadPath))
        {
            Console.WriteLine("Aborting an unfinished merge left in the target clone.");
            await GitRunner.RunGitAsync(context.TargetRepoRoot, "merge", "--abort").ConfigureAwait(false);
        }

        var targetStatus = await GitRunner.GetShortStatusAsync(context.TargetRepoRoot).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(targetStatus))
        {
            throw new InvalidOperationException(
                "The target clone is not clean. Merge into target expects a clean checkout.");
        }

        const string sourceRemoteName = "prepared-source";
        await GitRunner.EnsureRemoteAsync(
            context.TargetRepoRoot,
            sourceRemoteName,
            sourceRootForMerge).ConfigureAwait(false);
        await GitRunner.FetchAsync(context.TargetRepoRoot, sourceRemoteName, includeTags: false).ConfigureAwait(false);

        var targetHeadBeforeMerge = await GitRunner.GetHeadCommitAsync(context.TargetRepoRoot).ConfigureAwait(false);
        context.State.TargetHeadCommit = targetHeadBeforeMerge;

        if (await GitRunner.IsAncestorAsync(context.TargetRepoRoot, sourceHeadCommit, "HEAD").ConfigureAwait(false))
        {
            var solutionSummary = await SyncTargetSolutionAsync(context, sourceSolutionContent).ConfigureAwait(false);
            var committedSolutionUpdate = await GitRunner.CommitTrackedChangesAsync(
                context.TargetRepoRoot,
                "Add imported Razor projects to Roslyn.slnx").ConfigureAwait(false);
            var targetHeadAfterSolutionCommit = await GitRunner.GetHeadCommitAsync(context.TargetRepoRoot).ConfigureAwait(false);
            context.State.TargetHeadCommit = targetHeadAfterSolutionCommit;

            var alreadyMergedSummary =
                $"Filtered source commit '{sourceHeadCommit}' is already reachable from target HEAD '{targetHeadBeforeMerge}'.";
            if (committedSolutionUpdate)
            {
                alreadyMergedSummary +=
                    $" Committed the target solution update at '{targetHeadAfterSolutionCommit}'. {solutionSummary}";
            }
            else
            {
                alreadyMergedSummary += $" {solutionSummary}";
            }

            return alreadyMergedSummary;
        }

        Console.WriteLine($"Merging filtered source commit '{sourceHeadCommit}' into '{targetRelativePath}'.");
        await GitRunner.RunGitAsync(
            context.TargetRepoRoot,
            "merge",
            "--no-commit",
            "--allow-unrelated-histories",
            "-s",
            "ours",
            sourceHeadCommit).ConfigureAwait(false);

        try
        {
            await GitRunner.RunGitAsync(
                context.TargetRepoRoot,
                "rm",
                "-r",
                "--ignore-unmatch",
                "--",
                targetRelativePath).ConfigureAwait(false);
            await GitRunner.RunGitAsync(
                context.TargetRepoRoot,
                "checkout",
                sourceHeadCommit,
                "--",
                targetRelativePath).ConfigureAwait(false);
            var solutionSummary = await SyncTargetSolutionAsync(context, sourceSolutionContent).ConfigureAwait(false);
            await GitRunner.CommitAsync(
                context.TargetRepoRoot,
                $"Merge '{context.Settings.SourceRepo}' into '{targetRelativePath}'",
                $"Import filtered history from '{sourceHeadCommit}' into the target repo path.",
                solutionSummary).ConfigureAwait(false);
        }
        catch
        {
            if (File.Exists(mergeHeadPath))
                await GitRunner.RunGitAsync(context.TargetRepoRoot, "merge", "--abort").ConfigureAwait(false);

            throw;
        }

        var targetHeadAfterMerge = await GitRunner.GetHeadCommitAsync(context.TargetRepoRoot).ConfigureAwait(false);
        context.State.TargetHeadCommit = targetHeadAfterMerge;

        return
            $"{filterSummary} Merged filtered source commit '{sourceHeadCommit}' into '{fullTargetPath}'. " +
            $"New target HEAD: '{targetHeadAfterMerge}'.";
    }

    private static Task<string> PostMergeCleanupAsync(StageContext context)
        => PostMergeCleanupRunner.RunAsync(context);

    private static async Task<string> CloneOrRefreshRepositoryAsync(
        string remoteName,
        string repositoryDisplayName,
        string remoteUri,
        string? localBranchName,
        string? branchName,
        string cloneDirectory,
        string workDirectory,
        bool allowNoHardlinks,
        bool isDryRun,
        Action<string> onHeadResolved,
        Action<string> onBranchResolved)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(cloneDirectory)!);

        if (isDryRun)
        {
            var branchSummary = string.IsNullOrWhiteSpace(localBranchName)
                ? string.Empty
                : $" and check out local branch '{localBranchName}' without upstream tracking";
            return $"Dry run: would clone or refresh '{repositoryDisplayName}' from '{remoteUri}' into '{cloneDirectory}'{branchSummary}.";
        }

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

        if (!GitRunner.IsRepository(cloneDirectory))
            throw new InvalidOperationException($"The clone directory '{cloneDirectory}' already exists but is not a git repository.");

        var actualRemoteName = await GitRunner.GetPreferredRemoteNameAsync(cloneDirectory).ConfigureAwait(false);
        var actualRemoteUri = await GitRunner.GetRemoteUrlAsync(cloneDirectory, actualRemoteName).ConfigureAwait(false);
        if (!PathHelper.RepositoryLocationsMatch(actualRemoteUri, remoteUri))
        {
            throw new InvalidOperationException(
                $"The existing clone at '{cloneDirectory}' points at '{actualRemoteUri}', not '{remoteUri}'. " +
                "Delete the work directory or choose a different --run-name to create a fresh working copy.");
        }

        await GitRunner.FetchAsync(cloneDirectory, actualRemoteName).ConfigureAwait(false);

        var effectiveBranchName = branchName;
        if (string.IsNullOrWhiteSpace(effectiveBranchName))
            effectiveBranchName = await GitRunner.GetRemoteHeadBranchAsync(cloneDirectory, actualRemoteName).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(effectiveBranchName))
            throw new InvalidOperationException($"Could not determine which branch to check out for '{repositoryDisplayName}'.");

        var checkoutBranchName = string.IsNullOrWhiteSpace(localBranchName)
            ? effectiveBranchName
            : localBranchName;

        Console.WriteLine(
            $"Refreshing '{cloneDirectory}' from remote '{actualRemoteName}' and checking out '{checkoutBranchName}' from '{actualRemoteName}/{effectiveBranchName}'.");

        await GitRunner.CheckoutBranchAsync(
            cloneDirectory,
            checkoutBranchName,
            $"{actualRemoteName}/{effectiveBranchName}").ConfigureAwait(false);
        onBranchResolved(checkoutBranchName);

        var headCommit = await GitRunner.GetHeadCommitAsync(cloneDirectory).ConfigureAwait(false);
        onHeadResolved(headCommit);

        return $"Cloned/refreshed '{repositoryDisplayName}' into '{cloneDirectory}' on branch '{checkoutBranchName}' at commit '{headCommit}'.";
    }

    private static string GetTargetMergeBranchName(string runName)
        => $"repo-merge/{runName}";

    private static async Task<string> SyncTargetSolutionAsync(StageContext context, string sourceSolutionContent)
    {
        var targetSolutionPath = Directory.GetFiles(context.TargetRepoRoot, "*.slnx", SearchOption.TopDirectoryOnly)
            .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (targetSolutionPath is null)
            return "Skipped target solution update because no root .slnx file was found.";

        return await SlnxImporter.ImportUnderFolderAsync(sourceSolutionContent, targetSolutionPath, "Razor").ConfigureAwait(false);
    }

    private static string? GetSourceSolutionPath(string sourceRoot)
    {
        var preferredPath = Path.Combine(sourceRoot, "Razor.slnx");
        if (File.Exists(preferredPath))
            return preferredPath;

        return Directory.GetFiles(sourceRoot, "*.slnx", SearchOption.TopDirectoryOnly)
            .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }
}
