namespace RepoMerger;

internal static class SourceHistoryFilter
{
    public static async Task<string> CreateFilteredCloneAsync(
        string sourceCloneDirectory,
        string filteredCloneDirectory,
        string targetPath)
    {
        var normalizedTargetPath = PathHelper.NormalizeRelativeTargetPath(targetPath, "Source history filtering");

        if (!Directory.Exists(sourceCloneDirectory) || !GitRunner.IsRepository(sourceCloneDirectory))
        {
            throw new InvalidOperationException(
                $"The source clone directory '{sourceCloneDirectory}' does not exist or is not a git repository.");
        }

        var fullSourceCloneDirectory = Path.GetFullPath(sourceCloneDirectory);
        var fullFilteredCloneDirectory = Path.GetFullPath(filteredCloneDirectory);
        if (string.Equals(fullSourceCloneDirectory, fullFilteredCloneDirectory, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The filtered source clone directory must be different from the prepared source clone.");

        if (Directory.Exists(filteredCloneDirectory) && IsFilteredInPlace(filteredCloneDirectory, normalizedTargetPath))
        {
            var existingHeadCommit = await GitRunner.GetHeadCommitAsync(filteredCloneDirectory).ConfigureAwait(false);
            return
                $"Filtered source clone at '{filteredCloneDirectory}' already contains only '{normalizedTargetPath}' " +
                $"(HEAD {existingHeadCommit}).";
        }

        if (Directory.Exists(filteredCloneDirectory))
            Directory.Delete(filteredCloneDirectory, recursive: true);

        var filteredCloneParentDirectory = Path.GetDirectoryName(filteredCloneDirectory);
        if (string.IsNullOrWhiteSpace(filteredCloneParentDirectory))
            throw new InvalidOperationException($"Could not determine the parent directory for '{filteredCloneDirectory}'.");

        Directory.CreateDirectory(filteredCloneParentDirectory);
        await GitRunner.CloneAsync(
            workingDirectory: filteredCloneParentDirectory,
            remoteName: "source",
            remoteUri: sourceCloneDirectory,
            cloneDirectory: filteredCloneDirectory,
            noHardlinks: true).ConfigureAwait(false);

        var filterTool = await GitRunner.FilterToSubdirectoryAsync(filteredCloneDirectory, normalizedTargetPath).ConfigureAwait(false);
        await NestFilteredTreeUnderTargetPathAsync(filteredCloneDirectory, normalizedTargetPath).ConfigureAwait(false);

        var filteredHeadCommit = await GitRunner.GetHeadCommitAsync(filteredCloneDirectory).ConfigureAwait(false);
        return
            $"Created filtered source clone at '{filteredCloneDirectory}' under '{normalizedTargetPath}' " +
            $"with {filterTool} (HEAD {filteredHeadCommit}).";
    }

    private static async Task NestFilteredTreeUnderTargetPathAsync(string repositoryDirectory, string targetRelativePath)
    {
        var topLevelEntries = Directory.GetFileSystemEntries(repositoryDirectory)
            .Select(static path => Path.GetFileName(path))
            .Where(static name => !string.IsNullOrEmpty(name) && !string.Equals(name, ".git", StringComparison.OrdinalIgnoreCase))
            .Select(static name => name!)
            .ToArray();

        if (topLevelEntries.Length == 0)
            return;

        const string temporaryRootDirectoryName = "__repo_merge_filtered_root";
        if (topLevelEntries.Contains(temporaryRootDirectoryName, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException($"The temporary folder '{temporaryRootDirectoryName}' already exists.");

        Directory.CreateDirectory(Path.Combine(repositoryDirectory, temporaryRootDirectoryName));

        foreach (var entry in topLevelEntries)
        {
            await GitRunner.RunGitAsync(
                repositoryDirectory,
                "mv",
                "--",
                entry,
                Path.Combine(temporaryRootDirectoryName, entry)).ConfigureAwait(false);
        }

        var targetParentPath = Path.GetDirectoryName(targetRelativePath);
        if (!string.IsNullOrWhiteSpace(targetParentPath))
            Directory.CreateDirectory(Path.Combine(repositoryDirectory, targetParentPath));

        await GitRunner.RunGitAsync(
            repositoryDirectory,
            "mv",
            "--",
            temporaryRootDirectoryName,
            targetRelativePath).ConfigureAwait(false);

        await GitRunner.CommitAsync(
            repositoryDirectory,
            $"Re-root filtered history under '{targetRelativePath}'").ConfigureAwait(false);
    }

    public static bool IsFilteredInPlace(string repositoryDirectory, string targetRelativePath)
    {
        var normalizedTargetPath = targetRelativePath
            .Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (normalizedTargetPath.Length == 0)
            return false;

        if (!Directory.Exists(Path.Combine(repositoryDirectory, targetRelativePath)))
            return false;

        var topLevelEntries = Directory.GetFileSystemEntries(repositoryDirectory)
            .Select(static path => Path.GetFileName(path))
            .Where(static name => !string.IsNullOrEmpty(name) && !string.Equals(name, ".git", StringComparison.OrdinalIgnoreCase))
            .Select(static name => name!)
            .ToArray();

        return topLevelEntries.Length == 1
            && string.Equals(topLevelEntries[0], normalizedTargetPath[0], StringComparison.OrdinalIgnoreCase);
    }
}
