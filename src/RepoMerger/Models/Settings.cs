namespace RepoMerger;

internal readonly record struct Settings(
    string SourceRepo,
    string SourceBranch,
    string TargetRepo,
    string TargetPath,
    string WorkRoot,
    string? RunName,
    bool SkipHistoryFilter,
    bool DryRun);
