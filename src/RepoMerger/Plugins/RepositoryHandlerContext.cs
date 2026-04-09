namespace RepoMerger;

public sealed record RepositoryHandlerContext(
    string SourceRepo,
    string SourceRoot,
    string TargetPath);
