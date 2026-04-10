namespace RepoMerger;

public interface IRepositoryHandler
{
    string Key { get; }

    Task PrepareAsync(RepositoryHandlerContext context);
}
