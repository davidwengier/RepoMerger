namespace RepoMerger;

internal readonly record struct MergeSettings(
    string SourceRepo,
    string SourceBranch,
    string TargetRepo,
    string TargetPath,
    string StateRoot,
    string WorkRoot,
    string ScriptRoot,
    string? ScriptSet,
    string? RunName,
    string? Stage,
    string? StartAt,
    string? StopAfter,
    bool ListStages,
    bool DryRun,
    bool Resume,
    bool Rerun,
    bool Reset,
    bool ShowHelp);
