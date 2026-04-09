namespace RepoMerger;

internal readonly record struct ExecutionPlan(int StartIndex, int StopIndex, string StartStageName, string StopStageName);
