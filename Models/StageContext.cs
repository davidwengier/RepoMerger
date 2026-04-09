namespace RepoMerger;

readonly record struct StageContext(
    MergeSettings Settings,
    string ToolRoot,
    string TargetRepoRoot,
    string RunDirectory,
    string StatePath,
    MergeRunState State);
