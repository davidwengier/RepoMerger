#!/usr/bin/env dotnet

using System.Diagnostics;

try
{
    if (args.Length != 1 || string.IsNullOrWhiteSpace(args[0]))
        throw new InvalidOperationException("Usage: dotnet run --file validate.cs -- <source-root>");

    var sourceRoot = Path.GetFullPath(args[0]);
    if (!Directory.Exists(sourceRoot))
        throw new InvalidOperationException($"Source root '{sourceRoot}' does not exist.");

    var buildScript = Path.Combine(sourceRoot, "build.cmd");
    if (!File.Exists(buildScript))
        throw new InvalidOperationException($"Expected '{buildScript}' to exist.");

    Console.WriteLine($"Validating Razor source repo at '{sourceRoot}'.");

    await RunBuildAsync(sourceRoot).ConfigureAwait(false);

    Console.WriteLine("Razor source repo validation completed successfully.");
    return 0;
}
catch (Exception ex)
{
    Console.WriteLine($"ERROR: {ex.Message}");
    return 1;
}

static async Task RunBuildAsync(string workingDirectory)
{
    var startInfo = new ProcessStartInfo
    {
        FileName = "cmd.exe",
        WorkingDirectory = workingDirectory,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true,
    };

    startInfo.ArgumentList.Add("/c");
    startInfo.ArgumentList.Add("build.cmd");
    startInfo.ArgumentList.Add("-restore");

    Console.WriteLine("> build.cmd -restore");

    using var process = Process.Start(startInfo)
        ?? throw new InvalidOperationException("Failed to start 'build.cmd'.");

    await ReadProcessOutputAsync(process).ConfigureAwait(false);

    if (process.ExitCode != 0)
        throw new InvalidOperationException($"build.cmd -restore failed with exit code {process.ExitCode}.");
}

static async Task ReadProcessOutputAsync(Process process)
{
    Task PumpAsync(StreamReader reader) => Task.Run(async () =>
    {
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            Console.WriteLine(line);
    });

    await Task.WhenAll(
        PumpAsync(process.StandardOutput),
        PumpAsync(process.StandardError),
        process.WaitForExitAsync()).ConfigureAwait(false);
}
