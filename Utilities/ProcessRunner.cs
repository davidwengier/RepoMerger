using System.Diagnostics;

namespace RepoMerger;

public static class ProcessRunner
{
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
        string workingDirectory,
        IReadOnlyDictionary<string, string?>? environmentVariables = null,
        bool logOutput = true)
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

        if (environmentVariables is not null)
        {
            foreach (var (name, value) in environmentVariables)
            {
                if (value is null)
                    startInfo.Environment.Remove(name);
                else
                    startInfo.Environment[name] = value;
            }
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start '{fileName}'.");

        var output = await ReadProcessOutputAsync(process, logOutput).ConfigureAwait(false);
        return new ProcessResult(process.ExitCode, output);
    }

    private static async Task<string> ReadProcessOutputAsync(Process process, bool logOutput)
    {
        var output = new List<string>();

        Task PumpAsync(StreamReader reader) => Task.Run(async () =>
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                lock (output)
                    output.Add(line);

                if (logOutput)
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
