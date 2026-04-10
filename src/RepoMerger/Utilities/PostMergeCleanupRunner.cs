using System.Text.RegularExpressions;

namespace RepoMerger;

internal static class PostMergeCleanupRunner
{
    private static readonly CleanupStep[] Steps =
    [
        new(
            "remove-common-targets-import",
            @"Remove Razor imports of $(RepositoryEngineeringDir)targets\Common.targets.",
            "Remove Razor Common.targets import",
            RemoveCommonTargetsImportAsync),
    ];

    public static async Task<string> RunAsync(StageContext context)
    {
        var targetRelativePath = PathHelper.NormalizeRelativeTargetPath(context.Settings.TargetPath, "Post-merge cleanup");
        var targetRoot = PathHelper.GetAbsolutePath(context.TargetRepoRoot, targetRelativePath);

        if (context.Settings.DryRun)
        {
            return
                $"Dry run: would apply {Steps.Length} post-merge cleanup step(s) under '{targetRoot}', " +
                "committing each cleanup separately.";
        }

        if (!Directory.Exists(targetRoot))
        {
            throw new InvalidOperationException(
                $"The merged target path '{targetRoot}' does not exist. Run the merge-into-target stage first.");
        }

        Directory.CreateDirectory(context.State.ImportPreviewDirectory);

        var summaries = new List<string>();
        foreach (var step in Steps)
        {
            var stepSummary = await step.ExecuteAsync(context.TargetRepoRoot, targetRoot).ConfigureAwait(false);
            var committed = await GitRunner.CommitTrackedChangesAsync(context.TargetRepoRoot, step.CommitMessage).ConfigureAwait(false);
            if (committed)
            {
                context.State.TargetHeadCommit = await GitRunner.GetHeadCommitAsync(context.TargetRepoRoot).ConfigureAwait(false);
                summaries.Add($"{stepSummary} Committed as '{context.State.TargetHeadCommit}'.");
            }
            else
            {
                summaries.Add($"{stepSummary} No commit was needed.");
            }
        }

        var summaryPath = Path.Combine(context.State.ImportPreviewDirectory, "cleanup-summary.txt");
        await File.WriteAllLinesAsync(summaryPath, summaries).ConfigureAwait(false);

        return $"Applied post-merge cleanup stage. Review summary: '{summaryPath}'. {string.Join(" ", summaries)}";
    }

    private static async Task<string> RemoveCommonTargetsImportAsync(string targetRepoRoot, string targetRoot)
    {
        var changedFiles = new List<string>();

        foreach (var path in EnumerateMsBuildFiles(targetRoot))
        {
            var originalContent = await File.ReadAllTextAsync(path).ConfigureAwait(false);
            var updatedContent = CommonTargetsImportPattern.Replace(originalContent, string.Empty);
            if (string.Equals(originalContent, updatedContent, StringComparison.Ordinal))
                continue;

            await File.WriteAllTextAsync(path, updatedContent).ConfigureAwait(false);
            changedFiles.Add(Path.GetRelativePath(targetRepoRoot, path));
        }

        return changedFiles.Count == 0
            ? @"No Razor imports of $(RepositoryEngineeringDir)targets\Common.targets were found."
            : $@"Removed Razor imports of $(RepositoryEngineeringDir)targets\Common.targets from {changedFiles.Count} file(s): {string.Join(", ", changedFiles)}.";
    }

    private static IEnumerable<string> EnumerateMsBuildFiles(string root)
        => Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
            .Where(static path =>
                path.EndsWith(".props", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith(".targets", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith(".vbproj", StringComparison.OrdinalIgnoreCase));

    private static readonly Regex CommonTargetsImportPattern = new(
        @"^[ \t]*<Import\s+Project=""\$\(RepositoryEngineeringDir\)targets(?:\\|/)Common\.targets""\s*/>\r?\n?",
        RegexOptions.Multiline | RegexOptions.CultureInvariant);

    private sealed record CleanupStep(
        string Name,
        string Description,
        string CommitMessage,
        Func<string, string, Task<string>> ExecuteAsync);
}
