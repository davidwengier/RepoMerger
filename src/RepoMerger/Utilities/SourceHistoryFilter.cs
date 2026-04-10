namespace RepoMerger;

internal static class SourceHistoryFilter
{
    public static async Task<string> FilterInPlaceAsync(
        string sourceCloneDirectory,
        string targetPath)
    {
        var normalizedTargetPath = PathHelper.NormalizeRelativeTargetPath(targetPath, "Source history filtering");

        if (!Directory.Exists(sourceCloneDirectory) || !GitRunner.IsRepository(sourceCloneDirectory))
        {
            throw new InvalidOperationException(
                $"The source clone directory '{sourceCloneDirectory}' does not exist or is not a git repository.");
        }

        if (IsFilteredInPlace(sourceCloneDirectory, normalizedTargetPath))
        {
            var existingHeadCommit = await GitRunner.GetHeadCommitAsync(sourceCloneDirectory).ConfigureAwait(false);
            return $"Source history is already filtered in place under '{normalizedTargetPath}' (HEAD {existingHeadCommit}).";
        }

        await GitRunner.FilterBranchToSubdirectoryAsync(sourceCloneDirectory, normalizedTargetPath).ConfigureAwait(false);
        await NestFilteredTreeUnderTargetPathAsync(sourceCloneDirectory, normalizedTargetPath).ConfigureAwait(false);

        var filteredHeadCommit = await GitRunner.GetHeadCommitAsync(sourceCloneDirectory).ConfigureAwait(false);
        return $"Filtered source history in place under '{normalizedTargetPath}' (HEAD {filteredHeadCommit}).";
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

        Directory.CreateDirectory(Path.Combine(repositoryDirectory, targetRelativePath));

        foreach (var entry in topLevelEntries)
        {
            await GitRunner.RunGitAsync(
                repositoryDirectory,
                "mv",
                "--",
                entry,
                Path.Combine(targetRelativePath, entry)).ConfigureAwait(false);
        }

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
