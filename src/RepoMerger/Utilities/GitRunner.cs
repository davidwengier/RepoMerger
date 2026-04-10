namespace RepoMerger;

public static class GitRunner
{
    private const string RepoMergerAttribution = "Prepared with RepoMerger, which was co-authored by Copilot.";

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

    public static async Task<bool> CommitTrackedChangesAsync(string repositoryDirectory, string message)
    {
        var status = await GetShortStatusAsync(repositoryDirectory).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(status))
            return false;

        await RunGitAsync(repositoryDirectory, "add", "--update", "--", ".").ConfigureAwait(false);
        await CommitAsync(repositoryDirectory, message).ConfigureAwait(false);
        return true;
    }

    public static async Task CommitAsync(string repositoryDirectory, string message, params string[] additionalParagraphs)
    {
        var arguments = new List<string>
        {
            "commit",
            "-m",
            message,
        };

        foreach (var paragraph in additionalParagraphs.Where(static paragraph => !string.IsNullOrWhiteSpace(paragraph)))
        {
            arguments.Add("-m");
            arguments.Add(paragraph);
        }

        arguments.Add("-m");
        arguments.Add(RepoMergerAttribution);

        await RunGitAsync(repositoryDirectory, arguments).ConfigureAwait(false);
    }

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

    public static async Task EnsureRemoteAsync(string repositoryDirectory, string remoteName, string remoteUri)
    {
        var remoteList = await RunGitAsync(repositoryDirectory, "remote").ConfigureAwait(false);
        var remotes = remoteList
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (remotes.Contains(remoteName))
        {
            var existingRemoteUri = await GetRemoteUrlAsync(repositoryDirectory, remoteName).ConfigureAwait(false);
            if (!PathHelper.RepositoryLocationsMatch(existingRemoteUri, remoteUri))
            {
                await RunGitAsync(repositoryDirectory, "remote", "set-url", remoteName, remoteUri).ConfigureAwait(false);
            }

            return;
        }

        await RunGitAsync(repositoryDirectory, "remote", "add", remoteName, remoteUri).ConfigureAwait(false);
    }

    public static async Task FetchAsync(string repositoryDirectory, string remoteName, bool includeTags = true)
    {
        var arguments = new List<string> { "fetch", remoteName, "--prune" };
        arguments.Add(includeTags ? "--tags" : "--no-tags");
        _ = await RunGitAsync(repositoryDirectory, arguments).ConfigureAwait(false);
    }

    public static async Task<bool> IsAncestorAsync(string repositoryDirectory, string ancestorCommit, string descendantCommit)
    {
        var result = await ProcessRunner.RunProcessAsync(
            "git",
            ["merge-base", "--is-ancestor", ancestorCommit, descendantCommit],
            repositoryDirectory).ConfigureAwait(false);

        return result.ExitCode switch
        {
            0 => true,
            1 => false,
            _ => throw new InvalidOperationException(
                $"git merge-base --is-ancestor {ancestorCommit} {descendantCommit} failed with exit code {result.ExitCode}:{Environment.NewLine}{result.Output.Trim()}")
        };
    }

    public static async Task<bool> PathExistsAsync(string repositoryDirectory, string revision, string path)
    {
        var normalizedPath = path.Replace('\\', '/');
        var result = await ProcessRunner.RunProcessAsync(
            "git",
            ["cat-file", "-e", $"{revision}:{normalizedPath}"],
            repositoryDirectory).ConfigureAwait(false);

        return result.ExitCode == 0;
    }

    public static async Task FilterBranchToSubdirectoryAsync(string repositoryDirectory, string path)
    {
        var normalizedPath = path.Replace('\\', '/');
        await RunGitAsync(
            repositoryDirectory,
            "filter-branch",
            "--force",
            "--prune-empty",
            "--subdirectory-filter",
            normalizedPath,
            "--",
            "HEAD").ConfigureAwait(false);
    }

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

    public static async Task CheckoutBranchAsync(string repositoryDirectory, string branchName, string startPoint)
        => _ = await RunGitAsync(
            repositoryDirectory,
            "checkout",
            "-B",
            branchName,
            startPoint).ConfigureAwait(false);

    public static async Task ResetHardAsync(string repositoryDirectory, string target)
        => _ = await RunGitAsync(repositoryDirectory, "reset", "--hard", target).ConfigureAwait(false);

    public static async Task<string> GetHeadCommitAsync(string repositoryDirectory)
        => (await RunGitAsync(repositoryDirectory, "rev-parse", "HEAD").ConfigureAwait(false)).Trim();
}
