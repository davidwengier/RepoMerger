namespace RepoMerger;

readonly record struct MergeStageDefinition(string Name, string Description, Func<StageContext, Task<string>> ExecuteAsync);
