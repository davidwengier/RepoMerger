using System.Text.RegularExpressions;

namespace RepoMerger;

public sealed class RazorRepositoryHandler : IRepositoryHandler
{
    public string Key => "razor";

    public async Task PrepareAsync(RepositoryHandlerContext context)
    {
        if (!Directory.Exists(context.SourceRoot))
            throw new InvalidOperationException($"Source root '{context.SourceRoot}' does not exist.");

        if (!ProcessRunner.IsGitRepository(context.SourceRoot))
            throw new InvalidOperationException($"'{context.SourceRoot}' is not a git repository.");

        var targetRelativePath = NormalizeTargetPath(context.TargetPath);
        var targetRoot = Path.Combine(context.SourceRoot, targetRelativePath);
        var srcTreeAlreadyNested = IsSourceTreeAlreadyNested(context.SourceRoot, targetRoot);

        Console.WriteLine($"Preparing Razor source repo at '{context.SourceRoot}'.");

        var status = await RunGitAsync(context.SourceRoot, "status", "--short", "--untracked-files=no").ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(status))
            throw new InvalidOperationException("The source clone is not clean. Preparation expects a clean checkout.");

        var topLevelEntries = Directory.GetFileSystemEntries(context.SourceRoot)
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
            await RunGitAsync(context.SourceRoot, "mv", "--", "src", temporarySourceDirectoryName).ConfigureAwait(false);

        Directory.CreateDirectory(targetRoot);

        foreach (var entry in entriesToMove)
        {
            if (string.Equals(entry, "src", StringComparison.OrdinalIgnoreCase)
                || string.Equals(entry, "eng", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            await RunGitAsync(context.SourceRoot, "mv", "--", entry, Path.Combine(targetRelativePath, entry)).ConfigureAwait(false);
        }

        if (!srcTreeAlreadyNested && Directory.Exists(Path.Combine(context.SourceRoot, temporarySourceDirectoryName)))
        {
            await RunGitAsync(
                context.SourceRoot,
                "mv",
                "--",
                temporarySourceDirectoryName,
                Path.Combine(targetRelativePath, "src")).ConfigureAwait(false);
        }

        var engMoveCount = await MoveEngContentsAsync(context.SourceRoot, targetRelativePath).ConfigureAwait(false);
        var updatedSolutionFiles = await UpdateSolutionFilesAsync(context.SourceRoot, targetRelativePath).ConfigureAwait(false);
        var rootMoveCount = entriesToMove.Count(static entry =>
            !string.Equals(entry, "src", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(entry, "eng", StringComparison.OrdinalIgnoreCase));

        if (srcTreeAlreadyNested && rootMoveCount == 0 && engMoveCount == 0 && updatedSolutionFiles == 0)
        {
            Console.WriteLine($"Razor repo is already prepared under '{targetRoot}'.");
            return;
        }

        Console.WriteLine($@"Moved {rootMoveCount} root entr{(rootMoveCount == 1 ? "y" : "ies")} and {engMoveCount} eng entr{(engMoveCount == 1 ? "y" : "ies")} under '{targetRelativePath}'.");
        if (updatedSolutionFiles > 0)
            Console.WriteLine($"Updated {updatedSolutionFiles} solution file(s).");
    }

    public async Task ValidateAsync(RepositoryHandlerContext context)
    {
        if (!Directory.Exists(context.SourceRoot))
            throw new InvalidOperationException($"Source root '{context.SourceRoot}' does not exist.");

        var buildScript = Path.Combine(context.SourceRoot, "build.cmd");
        if (!File.Exists(buildScript))
            throw new InvalidOperationException($"Expected '{buildScript}' to exist.");

        Console.WriteLine($"Validating Razor source repo at '{context.SourceRoot}'.");
        Console.WriteLine("> build.cmd -restore");

        var result = await ProcessRunner.RunProcessAsync("cmd.exe", ["/c", "build.cmd", "-restore"], context.SourceRoot).ConfigureAwait(false);
        ProcessRunner.EnsureCommandSucceeded(result, "build.cmd -restore");

        Console.WriteLine("Razor source repo validation completed successfully.");
    }

    private static string NormalizeTargetPath(string targetPath)
    {
        if (string.IsNullOrWhiteSpace(targetPath))
            throw new InvalidOperationException("Razor preparation requires a non-empty target path.");

        var normalizedTargetPath = targetPath.Replace('/', Path.DirectorySeparatorChar).Trim();
        if (Path.IsPathRooted(normalizedTargetPath))
            throw new InvalidOperationException("Razor preparation requires a relative target path.");

        return normalizedTargetPath;
    }

    private static bool IsSourceTreeAlreadyNested(string sourceRoot, string targetRoot)
    {
        var sourceDirectory = Path.Combine(sourceRoot, "src");
        if (!Directory.Exists(sourceDirectory) || !Directory.Exists(targetRoot))
            return false;

        if (!Directory.Exists(Path.Combine(targetRoot, "src")))
            return false;

        var relativeTargetPath = Path.GetRelativePath(sourceDirectory, targetRoot);
        var expectedTopLevelEntry = relativeTargetPath
            .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();

        if (string.IsNullOrWhiteSpace(expectedTopLevelEntry))
            return false;

        var rootSrcEntries = Directory.GetFileSystemEntries(sourceDirectory)
            .Select(static path => Path.GetFileName(path))
            .Where(static name => !string.IsNullOrEmpty(name))
            .Select(static name => name!)
            .ToArray();

        return rootSrcEntries.Length == 1
            && string.Equals(rootSrcEntries[0], expectedTopLevelEntry, StringComparison.OrdinalIgnoreCase);
    }

    private static bool ShouldStayAtRoot(string name)
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

    private static async Task<int> MoveEngContentsAsync(string sourceRoot, string targetRelativePath)
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

        Directory.CreateDirectory(Path.Combine(sourceRoot, targetRelativePath, "eng"));

        foreach (var entry in entriesToMove)
            await RunGitAsync(sourceRoot, "mv", "--", Path.Combine("eng", entry), Path.Combine(targetRelativePath, "eng", entry)).ConfigureAwait(false);

        return entriesToMove.Length;
    }

    private static bool ShouldStayInRootEng(string name)
        => string.Equals(name, "common", StringComparison.OrdinalIgnoreCase);

    private static async Task<int> UpdateSolutionFilesAsync(string sourceRoot, string targetRelativePath)
    {
        var updatedCount = 0;

        foreach (var filePath in Directory.GetFiles(sourceRoot, "*.slnx", SearchOption.TopDirectoryOnly))
        {
            if (await UpdateSolutionFileAsync(sourceRoot, targetRelativePath, filePath, pathSeparatorText: "/").ConfigureAwait(false))
                updatedCount++;
        }

        foreach (var filePath in Directory.GetFiles(sourceRoot, "*.slnf", SearchOption.TopDirectoryOnly))
        {
            if (await UpdateSolutionFileAsync(sourceRoot, targetRelativePath, filePath, pathSeparatorText: @"\\").ConfigureAwait(false))
                updatedCount++;
        }

        return updatedCount;
    }

    private static async Task<bool> UpdateSolutionFileAsync(string sourceRoot, string targetRelativePath, string filePath, string pathSeparatorText)
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
                var rewrittenPath = RewritePathIfMoved(sourceRoot, targetRelativePath, match.Groups[2].Value, pathSeparatorText);
                return $"{match.Groups[1].Value}{rewrittenPath}{match.Groups[3].Value}";
            });

        if (string.Equals(originalText, updatedText, StringComparison.Ordinal))
            return false;

        await File.WriteAllTextAsync(filePath, updatedText).ConfigureAwait(false);
        return true;
    }

    private static string RewritePathIfMoved(string sourceRoot, string targetRelativePath, string relativePath, string pathSeparatorText)
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

        var movedLocation = Path.Combine(sourceRoot, targetRelativePath, normalizedPath);
        if (!File.Exists(movedLocation) && !Directory.Exists(movedLocation))
            return relativePath;

        var rewrittenPath = Path.Combine(targetRelativePath, normalizedPath);
        return rewrittenPath.Replace(Path.DirectorySeparatorChar.ToString(), pathSeparatorText);
    }

    private static async Task<string> RunGitAsync(string workingDirectory, params string[] arguments)
    {
        var result = await ProcessRunner.RunProcessAsync("git", arguments, workingDirectory).ConfigureAwait(false);
        ProcessRunner.EnsureCommandSucceeded(result, $"git {string.Join(' ', arguments)}");
        return result.Output;
    }
}
