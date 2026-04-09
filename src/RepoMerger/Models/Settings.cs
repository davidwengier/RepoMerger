namespace RepoMerger;

internal readonly record struct Settings(
    string SourceRepo,
    string SourceBranch,
    string TargetRepo,
    string TargetPath,
    string StateRoot,
    string WorkRoot,
    string? RunName,
    string? Stage,
    string? StartAt,
    string? StopAfter,
    bool ListStages,
    bool DryRun,
    bool Resume,
    bool Rerun,
    bool Reset);
