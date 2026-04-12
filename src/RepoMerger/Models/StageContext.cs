namespace RepoMerger;

internal readonly record struct StageContext(
    Settings Settings,
    string ToolRoot,
    string TargetRepoRoot,
    string RunDirectory,
    RunState State)
{
    public string TargetRoot
        => PathHelper.GetAbsolutePath(
            TargetRepoRoot,
            PathHelper.NormalizeRelativeTargetPath(Settings.TargetPath, "Stage execution"));
}
