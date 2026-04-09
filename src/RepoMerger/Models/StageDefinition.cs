namespace RepoMerger;

internal readonly record struct StageDefinition(string Name, string Description, Func<StageContext, Task<string>> ExecuteAsync);
