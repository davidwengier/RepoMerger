namespace RepoMerger;

public static class GitRunner
{
    private const string RepoMergerAttribution = "Prepared with RepoMerger, which was co-authored by Copilot.";
    private const string PythonWingetPackageId = "Python.Python.3.12";
    private static readonly PythonCommand[] PythonCommands =
    [
        new("python3", [], "python3"),
        new("python", [], "python"),
        new("py", ["-3"], "py -3"),
        new("py", [], "py"),
    ];

    public static bool IsRepository(string directory)
        => Directory.Exists(Path.Combine(directory, ".git")) || File.Exists(Path.Combine(directory, ".git"));

    public static async Task<string> RunGitAsync(string workingDirectory, params string[] arguments)
        => await RunGitAsync(workingDirectory, (IReadOnlyList<string>)arguments, environmentVariables: null).ConfigureAwait(false);

    public static async Task<string> RunGitAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string?>? environmentVariables = null)
    {
        var result = await ProcessRunner.RunProcessAsync("git", arguments, workingDirectory, environmentVariables).ConfigureAwait(false);
        ProcessRunner.EnsureCommandSucceeded(result, $"git {string.Join(' ', arguments)}");
        return result.Output;
    }

    public static async Task<string> GetShortStatusAsync(string repositoryDirectory)
        => (await RunGitAsync(repositoryDirectory, "status", "--short", "--untracked-files=no").ConfigureAwait(false)).Trim();

    public static async Task<string> GetStagedStatusAsync(string repositoryDirectory)
        => (await RunGitAsync(repositoryDirectory, "diff", "--cached", "--name-status").ConfigureAwait(false)).Trim();

    public static async Task<bool> CommitTrackedChangesAsync(string repositoryDirectory, string message, params string[] additionalParagraphs)
    {
        var status = await GetShortStatusAsync(repositoryDirectory).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(status))
            return false;

        var stagedStatus = await GetStagedStatusAsync(repositoryDirectory).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(stagedStatus))
        {
            await RunGitAsync(repositoryDirectory, "add", "--update", "--", ".").ConfigureAwait(false);
            stagedStatus = await GetStagedStatusAsync(repositoryDirectory).ConfigureAwait(false);
        }

        if (string.IsNullOrWhiteSpace(stagedStatus))
            return false;

        await CommitAsync(repositoryDirectory, message, additionalParagraphs).ConfigureAwait(false);
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

    public static async Task<string> FilterToSubdirectoryAsync(string repositoryDirectory, string path)
    {
        var normalizedPath = path.Replace('\\', '/');
        var filterRepoCommand = await EnsureFilterRepoAvailableAsync(repositoryDirectory).ConfigureAwait(false);

        await RunRequiredProcessAsync(
            filterRepoCommand.FileName,
            filterRepoCommand.WithArguments(
                "--force",
                "--refs",
                "HEAD",
                "--subdirectory-filter",
                normalizedPath),
            repositoryDirectory,
            filterRepoCommand.EnvironmentVariables).ConfigureAwait(false);

        return filterRepoCommand.DisplayName;
    }

    private static async Task<FilterRepoCommand> EnsureFilterRepoAvailableAsync(string repositoryDirectory)
    {
        var existingFilterRepoCommand = await FindFilterRepoCommandAsync(repositoryDirectory).ConfigureAwait(false);
        if (existingFilterRepoCommand is not null)
            return existingFilterRepoCommand;

        var pythonCommand = await EnsurePythonAvailableAsync(repositoryDirectory).ConfigureAwait(false);
        if (pythonCommand is not null)
        {
            Console.WriteLine($"Installing git-filter-repo via {pythonCommand.DisplayName}.");
            if (await TryInstallFilterRepoAsync(repositoryDirectory, pythonCommand).ConfigureAwait(false))
            {
                var installedFilterRepoCommand = await FindFilterRepoCommandAsync(repositoryDirectory, pythonCommand).ConfigureAwait(false);
                if (installedFilterRepoCommand is not null)
                    return installedFilterRepoCommand;
            }
        }

        throw new InvalidOperationException(
            "git filter-repo is required for source history filtering, but RepoMerger could not install it automatically. " +
            "Ensure Python 3 and git-filter-repo are available, or allow winget-based Python installation on Windows.");
    }

    private static async Task<PythonCommand?> EnsurePythonAvailableAsync(string repositoryDirectory)
    {
        var existingPythonCommand = await FindPythonCommandAsync(repositoryDirectory).ConfigureAwait(false);
        if (existingPythonCommand is not null)
            return existingPythonCommand;

        if (OperatingSystem.IsWindows())
        {
            _ = await TryInstallPythonWithWingetAsync(repositoryDirectory).ConfigureAwait(false);
            return await FindPythonCommandAsync(repositoryDirectory).ConfigureAwait(false);
        }

        return null;
    }

    private static async Task<PythonCommand?> FindPythonCommandAsync(string repositoryDirectory)
    {
        foreach (var pythonCommand in GetCandidatePythonCommands())
        {
            if (await CanRunProcessAsync(repositoryDirectory, pythonCommand.FileName, pythonCommand.WithArguments("--version")).ConfigureAwait(false))
                return pythonCommand;
        }

        return null;
    }

    private static async Task<FilterRepoCommand?> FindFilterRepoCommandAsync(
        string repositoryDirectory,
        PythonCommand? preferredPythonCommand = null)
    {
        if (await CanRunProcessAsync(repositoryDirectory, "git", ["filter-repo", "--version"]).ConfigureAwait(false))
        {
            return new FilterRepoCommand("git", ["filter-repo"], "git filter-repo");
        }

        var pythonCommands = preferredPythonCommand is null
            ? GetCandidatePythonCommands()
            : [preferredPythonCommand];

        foreach (var pythonCommand in pythonCommands)
        {
            if (await CanRunProcessAsync(
                repositoryDirectory,
                pythonCommand.FileName,
                pythonCommand.WithArguments("-m", "git_filter_repo", "--version")).ConfigureAwait(false))
            {
                return new FilterRepoCommand(
                    pythonCommand.FileName,
                    pythonCommand.WithArguments("-m", "git_filter_repo"),
                    "git filter-repo");
            }
        }

        return null;
    }

    private static async Task<bool> TryInstallPythonWithWingetAsync(string repositoryDirectory)
    {
        if (!await CanRunProcessAsync(repositoryDirectory, "winget", ["--version"]).ConfigureAwait(false))
            return false;

        Console.WriteLine($"Installing Python 3 via winget package '{PythonWingetPackageId}'.");

        try
        {
            var result = await ProcessRunner.RunProcessAsync(
                "winget",
                [
                    "install",
                    "--id",
                    PythonWingetPackageId,
                    "--exact",
                    "--source",
                    "winget",
                    "--scope",
                    "user",
                    "--silent",
                    "--accept-package-agreements",
                    "--accept-source-agreements",
                    "--disable-interactivity",
                ],
                repositoryDirectory).ConfigureAwait(false);

            return result.ExitCode == 0;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static async Task RunRequiredProcessAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string?>? environmentVariables = null)
    {
        var result = await ProcessRunner.RunProcessAsync(
            fileName,
            arguments,
            workingDirectory,
            environmentVariables).ConfigureAwait(false);
        ProcessRunner.EnsureCommandSucceeded(result, $"{fileName} {string.Join(' ', arguments)}");
    }

    private static async Task<bool> TryInstallFilterRepoAsync(string repositoryDirectory, PythonCommand pythonCommand)
    {
        var installArguments = pythonCommand.WithArguments(
            "-m", "pip", "install", "--user", "--disable-pip-version-check", "--upgrade", "git-filter-repo");

        try
        {
            var installResult = await ProcessRunner.RunProcessAsync(
                pythonCommand.FileName,
                installArguments,
                repositoryDirectory).ConfigureAwait(false);
            if (installResult.ExitCode == 0)
                return true;

            var ensurePipResult = await ProcessRunner.RunProcessAsync(
                pythonCommand.FileName,
                pythonCommand.WithArguments("-m", "ensurepip", "--user"),
                repositoryDirectory).ConfigureAwait(false);
            if (ensurePipResult.ExitCode != 0)
                return false;

            installResult = await ProcessRunner.RunProcessAsync(
                pythonCommand.FileName,
                installArguments,
                repositoryDirectory).ConfigureAwait(false);
            return installResult.ExitCode == 0;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static IEnumerable<PythonCommand> GetCandidatePythonCommands()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var pythonCommand in PythonCommands)
        {
            var key = $"{pythonCommand.FileName}|{string.Join('\0', pythonCommand.PrefixArguments)}";
            if (seen.Add(key))
                yield return pythonCommand;
        }

        foreach (var executablePath in GetInstalledPythonExecutablePaths())
        {
            if (seen.Add(executablePath))
                yield return new(executablePath, [], executablePath);
        }
    }

    private static IEnumerable<string> GetInstalledPythonExecutablePaths()
    {
        foreach (var root in GetPythonInstallRoots())
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                continue;

            foreach (var directory in Directory.EnumerateDirectories(root, "Python*"))
            {
                var pythonExecutable = Path.Combine(directory, "python.exe");
                if (File.Exists(pythonExecutable))
                    yield return pythonExecutable;
            }
        }
    }

    private static IEnumerable<string> GetPythonInstallRoots()
    {
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs",
            "Python");

        yield return Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrWhiteSpace(programFilesX86))
            yield return programFilesX86;
    }

    private static async Task<bool> CanRunProcessAsync(string repositoryDirectory, string fileName, IReadOnlyList<string> arguments)
    {
        try
        {
            var result = await ProcessRunner.RunProcessAsync(
                fileName,
                arguments,
                repositoryDirectory,
                logOutput: false).ConfigureAwait(false);
            return result.ExitCode == 0;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private sealed record PythonCommand(string FileName, string[] PrefixArguments, string DisplayName)
    {
        public string[] WithArguments(params string[] arguments)
            => [.. PrefixArguments, .. arguments];
    }

    private sealed record FilterRepoCommand(
        string FileName,
        string[] PrefixArguments,
        string DisplayName,
        IReadOnlyDictionary<string, string?>? EnvironmentVariables = null)
    {
        public string[] WithArguments(params string[] arguments)
            => [.. PrefixArguments, .. arguments];
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
    {
        var arguments = new List<string>
        {
            "checkout",
            "--no-track",
            "-B",
            branchName,
            startPoint,
        };

        _ = await RunGitAsync(repositoryDirectory, arguments).ConfigureAwait(false);
    }

    public static async Task ResetHardAsync(string repositoryDirectory, string target)
        => _ = await RunGitAsync(repositoryDirectory, "reset", "--hard", target).ConfigureAwait(false);

    public static async Task<string> GetHeadCommitAsync(string repositoryDirectory)
        => (await RunGitAsync(repositoryDirectory, "rev-parse", "HEAD").ConfigureAwait(false)).Trim();
}
