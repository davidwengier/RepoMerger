namespace RepoMerger;

internal readonly record struct StageContext(
    Settings Settings,
    string ToolRoot,
    string TargetRepoRoot,
    string RunDirectory,
    RunState State);
