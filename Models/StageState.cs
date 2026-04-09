namespace RepoMerger;

sealed class StageState
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public StageStatus Status { get; set; } = StageStatus.Pending;
    public int AttemptCount { get; set; }
    public DateTimeOffset? StartedUtc { get; set; }
    public DateTimeOffset? CompletedUtc { get; set; }
    public string? LastMessage { get; set; }
}
