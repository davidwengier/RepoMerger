using System.Diagnostics;

namespace RepoMerger;

public static class ProcessRunner
{
    public static bool IsGitRepository(string directory)
        => Directory.Exists(Path.Combine(directory, ".git")) || File.Exists(Path.Combine(directory, ".git"));

    public static async Task<string> GetPreferredRemoteNameAsync(string repositoryDirectory)
    {
        var remoteList = await RunProcessAsync("git", ["remote"], repositoryDirectory).ConfigureAwait(false);
        EnsureCommandSucceeded(remoteList, "git remote");

        var remotes = remoteList.Output
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

    public static void EnsureCommandSucceeded(ProcessResult result, string commandName)
    {
        if (result.ExitCode == 0)
            return;

        throw new InvalidOperationException(
            $"{commandName} failed with exit code {result.ExitCode}:{Environment.NewLine}{result.Output.Trim()}");
    }

    public static async Task<ProcessResult> RunProcessAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start '{fileName}'.");

        var output = await ReadProcessOutputAsync(process).ConfigureAwait(false);
        return new ProcessResult(process.ExitCode, output);
    }

    private static async Task<string> ReadProcessOutputAsync(Process process)
    {
        var output = new List<string>();

        Task PumpAsync(StreamReader reader) => Task.Run(async () =>
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                lock (output)
                    output.Add(line);

                Console.WriteLine(line);
            }
        });

        await Task.WhenAll(
            PumpAsync(process.StandardOutput),
            PumpAsync(process.StandardError),
            process.WaitForExitAsync()).ConfigureAwait(false);

        lock (output)
            return string.Join(Environment.NewLine, output);
    }
}
