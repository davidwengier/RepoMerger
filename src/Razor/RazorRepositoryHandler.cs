namespace RepoMerger;

public sealed class RazorRepositoryHandler : IRepositoryHandler
{
    public string Key => "razor";

    public async Task PrepareAsync(RepositoryHandlerContext context)
    {
        if (!Directory.Exists(context.SourceRoot))
            throw new InvalidOperationException($"Source root '{context.SourceRoot}' does not exist.");

        if (!GitRunner.IsRepository(context.SourceRoot))
            throw new InvalidOperationException($"'{context.SourceRoot}' is not a git repository.");

        var targetRelativePath = PathHelper.NormalizeRelativeTargetPath(context.TargetPath, "Razor preparation");
        var targetRoot = Path.Combine(context.SourceRoot, targetRelativePath);
        var srcTreeAlreadyNested = IsSourceTreeAlreadyNested(context.SourceRoot, targetRoot);

        Console.WriteLine($"Preparing Razor source repo at '{context.SourceRoot}'.");

        var status = await GitRunner.GetShortStatusAsync(context.SourceRoot).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(status))
            throw new InvalidOperationException("The source clone is not clean. Preparation expects a clean checkout.");

        var normalizedEngFiles = await SolutionPathUpdater.NormalizeRepositoryEngineeringReferencesAsync(context.SourceRoot).ConfigureAwait(false);
        if (normalizedEngFiles > 0)
        {
            await CommitPreparationStepAsync(
                context.SourceRoot,
                "Normalize eng path references to $(RepositoryEngineeringDir)").ConfigureAwait(false);
        }

        var topLevelEntries = Directory.GetFileSystemEntries(context.SourceRoot)
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
            await GitRunner.RunGitAsync(context.SourceRoot, "mv", "--", "src", temporarySourceDirectoryName).ConfigureAwait(false);

        Directory.CreateDirectory(targetRoot);

        foreach (var entry in entriesToMove)
        {
            if (string.Equals(entry, "src", StringComparison.OrdinalIgnoreCase))
                continue;

            await GitRunner.RunGitAsync(context.SourceRoot, "mv", "--", entry, Path.Combine(targetRelativePath, entry)).ConfigureAwait(false);
        }

        if (!srcTreeAlreadyNested && Directory.Exists(Path.Combine(context.SourceRoot, temporarySourceDirectoryName)))
        {
            await GitRunner.RunGitAsync(
                context.SourceRoot,
                "mv",
                "--",
                temporarySourceDirectoryName,
                Path.Combine(targetRelativePath, "src")).ConfigureAwait(false);
        }

        var updatedPathFiles = await SolutionPathUpdater.UpdateMovedPathsAsync(context.SourceRoot, targetRelativePath).ConfigureAwait(false);
        var rootMoveCount = entriesToMove.Count(static entry =>
            !string.Equals(entry, "src", StringComparison.OrdinalIgnoreCase));

        if (rootMoveCount > 0 || updatedPathFiles > 0)
        {
            await CommitPreparationStepAsync(
                context.SourceRoot,
                $"Move Razor repo contents under '{targetRelativePath}'").ConfigureAwait(false);
        }

        var rewrittenRepoRootFiles = await SolutionPathUpdater.RewriteRepoRootReferencesAsync(context.SourceRoot, targetRelativePath).ConfigureAwait(false);
        if (rewrittenRepoRootFiles > 0)
        {
            await CommitPreparationStepAsync(
                context.SourceRoot,
                $"Rewrite $(RepoRoot) references for '{targetRelativePath}' nesting").ConfigureAwait(false);
        }

        var updatedFileCount = normalizedEngFiles + updatedPathFiles + rewrittenRepoRootFiles;

        if (srcTreeAlreadyNested && rootMoveCount == 0 && updatedFileCount == 0)
        {
            Console.WriteLine($"Razor repo is already prepared under '{targetRoot}'.");
            return;
        }

        Console.WriteLine($"Moved {rootMoveCount} root entr{(rootMoveCount == 1 ? "y" : "ies")} under '{targetRelativePath}'.");
        if (updatedFileCount > 0)
            Console.WriteLine($"Updated {updatedFileCount} solution/build file(s).");
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
