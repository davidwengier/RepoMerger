namespace RepoMerger;

internal readonly record struct StageContext(
    MergeSettings Settings,
    string ToolRoot,
    string TargetRepoRoot,
    string RunDirectory,
    string StatePath,
    MergeRunState State);
