namespace RepoMerger;

internal sealed class RunState
{
    public int SchemaVersion { get; set; }
    public string WorkflowVersion { get; set; } = string.Empty;
    public string RunName { get; set; } = string.Empty;
    public string SourceRepo { get; set; } = string.Empty;
    public string SourceBranch { get; set; } = string.Empty;
    public string TargetRepo { get; set; } = string.Empty;
    public string TargetRepoRoot { get; set; } = string.Empty;
    public string TargetPath { get; set; } = string.Empty;
    public string StateRoot { get; set; } = string.Empty;
    public string WorkRoot { get; set; } = string.Empty;
    public string RunDirectory { get; set; } = string.Empty;
    public string WorkDirectory { get; set; } = string.Empty;
    public string SourceRemoteUri { get; set; } = string.Empty;
    public string TargetRemoteUri { get; set; } = string.Empty;
    public string SourceCloneDirectory { get; set; } = string.Empty;
    public string ImportPreviewDirectory { get; set; } = string.Empty;
    public string SourceHeadCommit { get; set; } = string.Empty;
    public string TargetHeadCommit { get; set; } = string.Empty;
    public bool DryRun { get; set; }
    public string CurrentStage { get; set; } = string.Empty;
    public string LastCompletedStage { get; set; } = string.Empty;
    public string SelectedStartStage { get; set; } = string.Empty;
    public string SelectedStopStage { get; set; } = string.Empty;
    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset UpdatedUtc { get; set; }
    public List<StageState> Stages { get; set; } = [];
}
