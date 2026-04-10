using System.Reflection;
using System.Runtime.Loader;

namespace RepoMerger;

internal static class RepositoryHandlerLoader
{
    public static async Task<string> RunAsync(StageContext context)
    {
        var repositoryKey = GetRepositoryKey(context.Settings.SourceRepo);
        if (string.IsNullOrWhiteSpace(repositoryKey))
        {
            throw new InvalidOperationException(
                $"Could not determine a repository handler key from source repo '{context.Settings.SourceRepo}'.");
        }

        var handler = CreateHandler(context.ToolRoot, repositoryKey, out var assemblyPath);

        var handlerContext = new RepositoryHandlerContext(
            SourceRepo: context.Settings.SourceRepo,
            SourceRoot: context.State.SourceCloneDirectory,
            TargetPath: context.Settings.TargetPath);

        Console.WriteLine($"Loaded '{Path.GetFileName(assemblyPath)}' for '{repositoryKey}'.");

        await handler.PrepareAsync(handlerContext).ConfigureAwait(false);

        return $"Ran {repositoryKey} handler successfully.";
    }

    internal static string GetRepositoryKey(string sourceRepo)
    {
        var normalizedRepo = sourceRepo.Trim()
            .Replace('/', Path.DirectorySeparatorChar)
            .TrimEnd(Path.DirectorySeparatorChar);
        var repoName = Path.GetFileName(normalizedRepo);

        if (repoName.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            repoName = repoName[..^4];

        return string.IsNullOrWhiteSpace(repoName)
            ? string.Empty
            : PathHelper.SanitizePathSegment(repoName);
    }

    private static IRepositoryHandler CreateHandler(string toolRoot, string repositoryKey, out string assemblyPath)
    {
        assemblyPath = FindAssemblyPath(toolRoot, repositoryKey)
            ?? throw CreateMissingHandlerException(toolRoot, repositoryKey);

        var assemblyName = Path.GetFileNameWithoutExtension(assemblyPath);
        var assembly = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(assembly => !string.IsNullOrWhiteSpace(assembly.Location)
                && string.Equals(Path.GetFileNameWithoutExtension(assembly.Location), assemblyName, StringComparison.OrdinalIgnoreCase));

        assembly ??= AssemblyLoadContext.Default.LoadFromAssemblyPath(assemblyPath);

        foreach (var type in assembly.GetTypes())
        {
            if (type.IsAbstract || type.IsInterface || !typeof(IRepositoryHandler).IsAssignableFrom(type))
                continue;

            if (Activator.CreateInstance(type) is IRepositoryHandler handler
                && string.Equals(handler.Key, repositoryKey, StringComparison.OrdinalIgnoreCase))
            {
                return handler;
            }
        }

        throw new InvalidOperationException(
            $"Loaded '{assemblyName}.dll' from '{assemblyPath}', but it did not contain an IRepositoryHandler for '{repositoryKey}'.");
    }

    private static Exception CreateMissingHandlerException(string toolRoot, string repositoryKey)
    {
        var assemblyBaseName = ToAssemblyBaseName(repositoryKey);
        var searchedPaths = string.Join(
            Environment.NewLine,
            GetAssemblyCandidates(toolRoot, assemblyBaseName).Select(static path => $"  - {path}"));

        return new InvalidOperationException(
            $"No compiled repository handler was found for '{repositoryKey}'. " +
            $"Build the matching plugin project so '{assemblyBaseName}.dll' is available. Searched:{Environment.NewLine}{searchedPaths}");
    }

    private static string? FindAssemblyPath(string toolRoot, string repositoryKey)
    {
        var assemblyBaseName = ToAssemblyBaseName(repositoryKey);
        foreach (var candidate in GetAssemblyCandidates(toolRoot, assemblyBaseName))
        {
            if (File.Exists(candidate))
                return Path.GetFullPath(candidate);
        }

        return null;
    }

    private static string[] GetAssemblyCandidates(string toolRoot, string assemblyBaseName)
    {
        var assemblyFileName = $"{assemblyBaseName}.dll";
        return
        [
            Path.Combine(AppContext.BaseDirectory, assemblyFileName),
            Path.Combine(toolRoot, "src", assemblyBaseName, "bin", "Debug", "net10.0", assemblyFileName),
            Path.Combine(toolRoot, "src", assemblyBaseName, "bin", "Release", "net10.0", assemblyFileName),
        ];
    }

    private static string ToAssemblyBaseName(string repositoryKey)
    {
        var parts = repositoryKey
            .Split(['-', '_', '.'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return string.Concat(parts.Select(static part =>
            char.ToUpperInvariant(part[0]) + part[1..].ToLowerInvariant()));
    }
}
