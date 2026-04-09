namespace RepoMerger;

public static class GitRunner
{
    public static bool IsRepository(string directory)
        => Directory.Exists(Path.Combine(directory, ".git")) || File.Exists(Path.Combine(directory, ".git"));

    public static async Task<string> RunGitAsync(string workingDirectory, params string[] arguments)
        => await RunGitAsync(workingDirectory, (IReadOnlyList<string>)arguments).ConfigureAwait(false);

    public static async Task<string> RunGitAsync(string workingDirectory, IReadOnlyList<string> arguments)
    {
        var result = await ProcessRunner.RunProcessAsync("git", arguments, workingDirectory).ConfigureAwait(false);
        ProcessRunner.EnsureCommandSucceeded(result, $"git {string.Join(' ', arguments)}");
        return result.Output;
    }

    public static async Task<string> GetShortStatusAsync(string repositoryDirectory)
        => (await RunGitAsync(repositoryDirectory, "status", "--short", "--untracked-files=no").ConfigureAwait(false)).Trim();

    public static async Task<string> GetPreferredRemoteNameAsync(string repositoryDirectory)
    {
        var remoteList = await RunGitAsync(repositoryDirectory, "remote").ConfigureAwait(false);
        var remotes = remoteList
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (remotes.Contains("source"))
            return "source";

        if (remotes.Contains("target"))
            return "target";

        if (remotes.Contains("origin"))
            return "origin";

        if (remotes.Count == 1)
            return remotes.Single();

        throw new InvalidOperationException(
            $"The repository at '{repositoryDirectory}' does not have a recognizable remote to refresh.");
    }

    public static async Task CloneAsync(
        string workingDirectory,
        string remoteName,
        string remoteUri,
        string cloneDirectory,
        string? branchName = null,
        bool noHardlinks = false)
    {
        var arguments = new List<string> { "clone", "--origin", remoteName };

        if (!string.IsNullOrWhiteSpace(branchName))
        {
            arguments.Add("--branch");
            arguments.Add(branchName);
        }

        if (noHardlinks)
            arguments.Add("--no-hardlinks");

        arguments.Add(remoteUri);
        arguments.Add(cloneDirectory);

        await RunGitAsync(workingDirectory, arguments).ConfigureAwait(false);
    }

    public static async Task<string> GetRemoteUrlAsync(string repositoryDirectory, string remoteName)
        => (await RunGitAsync(repositoryDirectory, "remote", "get-url", remoteName).ConfigureAwait(false)).Trim();

    public static async Task FetchAsync(string repositoryDirectory, string remoteName)
        => _ = await RunGitAsync(repositoryDirectory, "fetch", remoteName, "--prune", "--tags").ConfigureAwait(false);

    public static async Task<string> GetRemoteHeadBranchAsync(string repositoryDirectory, string remoteName)
    {
        var remoteHead = await RunGitAsync(
            repositoryDirectory,
            "symbolic-ref",
            "--short",
            $"refs/remotes/{remoteName}/HEAD").ConfigureAwait(false);

        return remoteHead.Trim().Split('/', 2, StringSplitOptions.RemoveEmptyEntries).LastOrDefault()
            ?? string.Empty;
    }

    public static async Task CheckoutTrackingBranchAsync(string repositoryDirectory, string remoteName, string branchName)
        => _ = await RunGitAsync(
            repositoryDirectory,
            "checkout",
            "-B",
            branchName,
            $"{remoteName}/{branchName}").ConfigureAwait(false);

    public static async Task<string> GetHeadCommitAsync(string repositoryDirectory)
        => (await RunGitAsync(repositoryDirectory, "rev-parse", "HEAD").ConfigureAwait(false)).Trim();
}
