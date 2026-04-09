#!/usr/bin/env dotnet

using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;

try
{
    if (args.Length != 1 || string.IsNullOrWhiteSpace(args[0]))
        throw new InvalidOperationException("Usage: dotnet run --file prepare.cs -- <source-root>");

    var sourceRoot = Path.GetFullPath(args[0]);
    if (!Directory.Exists(sourceRoot))
        throw new InvalidOperationException($"Source root '{sourceRoot}' does not exist.");

    var gitDirectory = Path.Combine(sourceRoot, ".git");
    if (!Directory.Exists(gitDirectory) && !File.Exists(gitDirectory))
        throw new InvalidOperationException($"'{sourceRoot}' is not a git repository.");

    var sourceDirectory = Path.Combine(sourceRoot, "src");
    var targetRoot = Path.Combine(sourceDirectory, "Razor");
    var srcTreeAlreadyNested = IsSourceTreeAlreadyNested(sourceRoot, targetRoot);

    Console.WriteLine($"Preparing Razor source repo at '{sourceRoot}'.");

    var status = await RunGitAsync(sourceRoot, "status", "--short", "--untracked-files=no").ConfigureAwait(false);
    if (!string.IsNullOrWhiteSpace(status))
        throw new InvalidOperationException("The source clone is not clean. Preparation expects a clean checkout.");

    var topLevelEntries = Directory.GetFileSystemEntries(sourceRoot)
        .Select(static path => Path.GetFileName(path))
        .Where(static name => !string.IsNullOrEmpty(name) && !string.Equals(name, ".git", StringComparison.OrdinalIgnoreCase))
        .Select(static name => name!)
        .ToArray();
    var entriesToMove = topLevelEntries
        .Where(static name => !ShouldStayAtRoot(name))
        .ToArray();

    const string temporarySourceDirectoryName = "__repo_merge_original_src";
    if (topLevelEntries.Contains(temporarySourceDirectoryName, StringComparer.OrdinalIgnoreCase))
        throw new InvalidOperationException($"The temporary folder '{temporarySourceDirectoryName}' already exists.");

    if (!srcTreeAlreadyNested && entriesToMove.Contains("src", StringComparer.OrdinalIgnoreCase))
        await RunGitAsync(sourceRoot, "mv", "--", "src", temporarySourceDirectoryName).ConfigureAwait(false);

    Directory.CreateDirectory(targetRoot);

    foreach (var entry in entriesToMove)
    {
        if (string.Equals(entry, "src", StringComparison.OrdinalIgnoreCase)
            || string.Equals(entry, "eng", StringComparison.OrdinalIgnoreCase))
        {
            continue;
        }

        await RunGitAsync(sourceRoot, "mv", "--", entry, Path.Combine("src", "Razor", entry)).ConfigureAwait(false);
    }

    if (!srcTreeAlreadyNested && Directory.Exists(Path.Combine(sourceRoot, temporarySourceDirectoryName)))
    {
        await RunGitAsync(
            sourceRoot,
            "mv",
            "--",
            temporarySourceDirectoryName,
            Path.Combine("src", "Razor", "src")).ConfigureAwait(false);
    }

    var engMoveCount = await MoveEngContentsAsync(sourceRoot).ConfigureAwait(false);
    var updatedSolutionFiles = await UpdateSolutionFilesAsync(sourceRoot).ConfigureAwait(false);
    var rootMoveCount = entriesToMove.Count(static entry =>
        !string.Equals(entry, "src", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(entry, "eng", StringComparison.OrdinalIgnoreCase));

    if (srcTreeAlreadyNested && rootMoveCount == 0 && engMoveCount == 0 && updatedSolutionFiles == 0)
    {
        Console.WriteLine($"Razor repo is already prepared under '{targetRoot}'.");
        return 0;
    }

    Console.WriteLine($@"Moved {rootMoveCount} root entr{(rootMoveCount == 1 ? "y" : "ies")} and {engMoveCount} eng entr{(engMoveCount == 1 ? "y" : "ies")} under 'src\Razor'.");
    if (updatedSolutionFiles > 0)
        Console.WriteLine($"Updated {updatedSolutionFiles} solution file(s).");

    return 0;
}
catch (Exception ex)
{
    Console.WriteLine($"ERROR: {ex.Message}");
    return 1;
}

static bool IsSourceTreeAlreadyNested(string sourceRoot, string targetRoot)
{
    var sourceDirectory = Path.Combine(sourceRoot, "src");
    if (!Directory.Exists(sourceDirectory) || !Directory.Exists(targetRoot))
        return false;

    if (!Directory.Exists(Path.Combine(targetRoot, "src")))
        return false;

    var rootSrcEntries = Directory.GetFileSystemEntries(sourceDirectory)
        .Select(static path => Path.GetFileName(path))
        .Where(static name => !string.IsNullOrEmpty(name))
        .Select(static name => name!)
        .ToArray();

    return rootSrcEntries.Length == 1
        && string.Equals(rootSrcEntries[0], "Razor", StringComparison.OrdinalIgnoreCase);
}

static bool ShouldStayAtRoot(string name)
{
    if (name.StartsWith(".git", StringComparison.OrdinalIgnoreCase))
        return true;

    if (name is ".azuredevops" or ".config" or ".devcontainer" or ".dotnet" or ".github" or ".tools" or ".vs" or ".vscode")
        return true;

    if (name is "artifacts" or "eng")
        return true;

    if (name is ".editorconfig" or ".globalconfig" or ".vsconfig" or "global.json" or "NuGet.config")
        return true;

    if (name.StartsWith("build.", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("restore.", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("clean.", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("activate.", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("start", StringComparison.OrdinalIgnoreCase))
    {
        return true;
    }

    if (name.EndsWith(".yml", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".dic", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".slnf", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
    {
        return true;
    }

    return false;
}

static async Task<int> MoveEngContentsAsync(string sourceRoot)
{
    var engDirectory = Path.Combine(sourceRoot, "eng");
    if (!Directory.Exists(engDirectory))
        return 0;

    var entriesToMove = Directory.GetFileSystemEntries(engDirectory)
        .Select(static path => Path.GetFileName(path))
        .Where(static name => !string.IsNullOrEmpty(name) && !ShouldStayInRootEng(name!))
        .Select(static name => name!)
        .ToArray();

    if (entriesToMove.Length == 0)
        return 0;

    Directory.CreateDirectory(Path.Combine(sourceRoot, "src", "Razor", "eng"));

    foreach (var entry in entriesToMove)
        await RunGitAsync(sourceRoot, "mv", "--", Path.Combine("eng", entry), Path.Combine("src", "Razor", "eng", entry)).ConfigureAwait(false);

    return entriesToMove.Length;
}

static bool ShouldStayInRootEng(string name)
    => string.Equals(name, "common", StringComparison.OrdinalIgnoreCase);

static async Task<int> UpdateSolutionFilesAsync(string sourceRoot)
{
    var updatedCount = 0;

    foreach (var filePath in Directory.GetFiles(sourceRoot, "*.slnx", SearchOption.TopDirectoryOnly))
    {
        if (await UpdateSolutionFileAsync(sourceRoot, filePath, pathSeparatorText: "/").ConfigureAwait(false))
            updatedCount++;
    }

    foreach (var filePath in Directory.GetFiles(sourceRoot, "*.slnf", SearchOption.TopDirectoryOnly))
    {
        if (await UpdateSolutionFileAsync(sourceRoot, filePath, pathSeparatorText: @"\\").ConfigureAwait(false))
            updatedCount++;
    }

    return updatedCount;
}

static async Task<bool> UpdateSolutionFileAsync(string sourceRoot, string filePath, string pathSeparatorText)
{
    var originalText = await File.ReadAllTextAsync(filePath).ConfigureAwait(false);
    var pathPattern = Path.GetExtension(filePath).Equals(".slnx", StringComparison.OrdinalIgnoreCase)
        ? """(Path=")([^"]+)(")"""
        : """(")([^"\r\n]+\.(?:csproj|vbproj|fsproj|shproj))(")""";

    var updatedText = Regex.Replace(
        originalText,
        pathPattern,
        match =>
        {
            var rewrittenPath = RewritePathIfMoved(sourceRoot, match.Groups[2].Value, pathSeparatorText);
            return $"{match.Groups[1].Value}{rewrittenPath}{match.Groups[3].Value}";
        });

    if (string.Equals(originalText, updatedText, StringComparison.Ordinal))
        return false;

    await File.WriteAllTextAsync(filePath, updatedText).ConfigureAwait(false);
    return true;
}

static string RewritePathIfMoved(string sourceRoot, string relativePath, string pathSeparatorText)
{
    if (Path.IsPathRooted(relativePath))
        return relativePath;

    var pathParts = relativePath
        .Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    if (pathParts.Length == 0)
        return relativePath;

    var normalizedPath = Path.Combine(pathParts);
    var originalLocation = Path.Combine(sourceRoot, normalizedPath);
    if (File.Exists(originalLocation) || Directory.Exists(originalLocation))
        return relativePath;

    var movedLocation = Path.Combine(sourceRoot, "src", "Razor", normalizedPath);
    if (!File.Exists(movedLocation) && !Directory.Exists(movedLocation))
        return relativePath;

    var rewrittenPath = Path.Combine("src", "Razor", normalizedPath);
    return rewrittenPath.Replace(Path.DirectorySeparatorChar.ToString(), pathSeparatorText);
}

static async Task<string> RunGitAsync(string workingDirectory, params string[] arguments)
{
    var startInfo = new ProcessStartInfo
    {
        FileName = "git",
        WorkingDirectory = workingDirectory,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true,
    };

    foreach (var argument in arguments)
        startInfo.ArgumentList.Add(argument);

    using var process = Process.Start(startInfo)
        ?? throw new InvalidOperationException("Failed to start 'git'.");

    var output = await ReadProcessOutputAsync(process).ConfigureAwait(false);
    if (process.ExitCode != 0)
        throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed:{Environment.NewLine}{output.Trim()}");

    return output;
}

static async Task<string> ReadProcessOutputAsync(Process process)
{
    var output = new List<string>();

    Task PumpAsync(StreamReader reader) => Task.Run(async () =>
    {
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            output.Add(line);
            Console.WriteLine(line);
        }
    });

    await Task.WhenAll(
        PumpAsync(process.StandardOutput),
        PumpAsync(process.StandardError),
        process.WaitForExitAsync()).ConfigureAwait(false);

    return string.Join(Environment.NewLine, output);
}
