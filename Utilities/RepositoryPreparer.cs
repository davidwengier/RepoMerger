namespace RepoMerger;

internal static class RepositoryPreparer
{
    public static async Task<string> RunAsync(StageContext context)
    {
        var repositoryKey = GetRepositoryKey(context.Settings.SourceRepo);
        if (string.IsNullOrWhiteSpace(repositoryKey))
        {
            throw new InvalidOperationException(
                $"Could not determine a source repo key from '{context.Settings.SourceRepo}'.");
        }

        Console.WriteLine($"Using built-in source preparer '{repositoryKey}'.");

        return repositoryKey switch
        {
            "razor" => await PrepareRazorAsync(context.State.SourceCloneDirectory, context.Settings.TargetPath).ConfigureAwait(false),
            _ => throw new InvalidOperationException($"No built-in source preparer is available for '{repositoryKey}'."),
        };
    }

    internal static string GetRepositoryKey(string sourceRepo)
    {
        var normalizedRepo = sourceRepo.Trim()
            .Replace('/', Path.DirectorySeparatorChar)
            .TrimEnd(Path.DirectorySeparatorChar);
        var repoName = Path.GetFileName(normalizedRepo);

        if (repoName.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            repoName = repoName[..^4];

        return string.IsNullOrWhiteSpace(repoName)
            ? string.Empty
            : PathHelper.SanitizePathSegment(repoName);
    }

    private static async Task<string> PrepareRazorAsync(string sourceRoot, string targetPath)
    {
        if (!Directory.Exists(sourceRoot))
            throw new InvalidOperationException($"Source root '{sourceRoot}' does not exist.");

        if (!GitRunner.IsRepository(sourceRoot))
            throw new InvalidOperationException($"'{sourceRoot}' is not a git repository.");

        var targetRelativePath = PathHelper.NormalizeRelativeTargetPath(targetPath, "Razor preparation");
        var targetRoot = Path.Combine(sourceRoot, targetRelativePath);
        var srcTreeAlreadyNested = IsSourceTreeAlreadyNested(sourceRoot, targetRoot);

        Console.WriteLine($"Preparing Razor source repo at '{sourceRoot}'.");

        var status = await GitRunner.GetShortStatusAsync(sourceRoot).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(status))
            throw new InvalidOperationException("The source clone is not clean. Preparation expects a clean checkout.");

        var topLevelEntries = Directory.GetFileSystemEntries(sourceRoot)
            .Select(static path => Path.GetFileName(path))
            .Where(static name => !string.IsNullOrEmpty(name) && !string.Equals(name, ".git", StringComparison.OrdinalIgnoreCase))
            .Select(static name => name!)
            .ToArray();
        var entriesToMove = topLevelEntries
            .Where(static name => !ShouldStayAtRoot(name))
            .ToArray();

        const string temporarySourceDirectoryName = "__repo_merge_original_src";
        if (topLevelEntries.Contains(temporarySourceDirectoryName, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException($"The temporary folder '{temporarySourceDirectoryName}' already exists.");

        if (!srcTreeAlreadyNested && entriesToMove.Contains("src", StringComparer.OrdinalIgnoreCase))
            await GitRunner.RunGitAsync(sourceRoot, "mv", "--", "src", temporarySourceDirectoryName).ConfigureAwait(false);

        Directory.CreateDirectory(targetRoot);

        foreach (var entry in entriesToMove)
        {
            if (string.Equals(entry, "src", StringComparison.OrdinalIgnoreCase))
                continue;

            await GitRunner.RunGitAsync(sourceRoot, "mv", "--", entry, Path.Combine(targetRelativePath, entry)).ConfigureAwait(false);
        }

        if (!srcTreeAlreadyNested && Directory.Exists(Path.Combine(sourceRoot, temporarySourceDirectoryName)))
        {
            await GitRunner.RunGitAsync(
                sourceRoot,
                "mv",
                "--",
                temporarySourceDirectoryName,
                Path.Combine(targetRelativePath, "src")).ConfigureAwait(false);
        }

        var updatedPathFiles = await SolutionPathUpdater.UpdateMovedPathsAsync(sourceRoot, targetRelativePath).ConfigureAwait(false);
        var rootMoveCount = entriesToMove.Count(static entry =>
            !string.Equals(entry, "src", StringComparison.OrdinalIgnoreCase));

        if (rootMoveCount > 0 || updatedPathFiles > 0)
        {
            await CommitPreparationStepAsync(
                sourceRoot,
                $"Move Razor repo contents under '{targetRelativePath}'").ConfigureAwait(false);
        }

        var rewrittenRepoRootFiles = await SolutionPathUpdater.RewriteRepoRootReferencesAsync(sourceRoot, targetRelativePath).ConfigureAwait(false);
        if (rewrittenRepoRootFiles > 0)
        {
            await CommitPreparationStepAsync(
                sourceRoot,
                $"Rewrite $(RepoRoot) references for '{targetRelativePath}' nesting").ConfigureAwait(false);
        }

        var updatedFileCount = updatedPathFiles + rewrittenRepoRootFiles;

        if (srcTreeAlreadyNested && rootMoveCount == 0 && updatedFileCount == 0)
        {
            Console.WriteLine($"Razor repo is already prepared under '{targetRoot}'.");
            return "Ran built-in Razor source preparation successfully.";
        }

        Console.WriteLine($"Moved {rootMoveCount} root entr{(rootMoveCount == 1 ? "y" : "ies")} under '{targetRelativePath}'.");
        if (updatedFileCount > 0)
            Console.WriteLine($"Updated {updatedFileCount} solution/build file(s).");

        return "Ran built-in Razor source preparation successfully.";
    }

    private static bool IsSourceTreeAlreadyNested(string sourceRoot, string targetRoot)
    {
        var sourceDirectory = Path.Combine(sourceRoot, "src");
        if (!Directory.Exists(sourceDirectory) || !Directory.Exists(targetRoot))
            return false;

        if (!Directory.Exists(Path.Combine(targetRoot, "src")))
            return false;

        var relativeTargetPath = Path.GetRelativePath(sourceDirectory, targetRoot);
        var expectedTopLevelEntry = relativeTargetPath
            .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();

        if (string.IsNullOrWhiteSpace(expectedTopLevelEntry))
            return false;

        var rootSrcEntries = Directory.GetFileSystemEntries(sourceDirectory)
            .Select(static path => Path.GetFileName(path))
            .Where(static name => !string.IsNullOrEmpty(name))
            .Select(static name => name!)
            .ToArray();

        return rootSrcEntries.Length == 1
            && string.Equals(rootSrcEntries[0], expectedTopLevelEntry, StringComparison.OrdinalIgnoreCase);
    }

    private static bool ShouldStayAtRoot(string name)
    {
        if (name.StartsWith(".git", StringComparison.OrdinalIgnoreCase))
            return true;

        if (name is ".azuredevops" or ".config" or ".devcontainer" or ".dotnet" or ".github" or ".tools" or ".vs" or ".vscode")
            return true;

        if (name is "artifacts" or "eng")
            return true;

        if (name is ".editorconfig" or ".globalconfig" or ".vsconfig" or "global.json" or "NuGet.config")
            return true;

        if (string.Equals(name, "NOTICE.txt", StringComparison.OrdinalIgnoreCase))
            return false;

        if (name.StartsWith("build.", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("restore.", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("clean.", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("activate.", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("start", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (name.EndsWith(".yml", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".dic", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".slnf", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private static async Task CommitPreparationStepAsync(string sourceRoot, string message)
    {
        if (await GitRunner.CommitTrackedChangesAsync(sourceRoot, message).ConfigureAwait(false))
            Console.WriteLine($"Committed source repo changes: {message}");
    }
}
