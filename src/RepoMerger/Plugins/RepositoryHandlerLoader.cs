using System.Reflection;
using System.Runtime.Loader;

namespace RepoMerger;

internal static class RepositoryHandlerLoader
{
    public static async Task<IReadOnlyList<string>> TryRunAsync(StageContext context)
    {
        var repositoryKey = GetRepositoryKey(context);
        if (string.IsNullOrWhiteSpace(repositoryKey))
            return [];

        var handler = TryCreateHandler(context.ToolRoot, repositoryKey);
        if (handler is null)
            return [];

        var handlerContext = new RepositoryHandlerContext(
            SourceRepo: context.Settings.SourceRepo,
            SourceRoot: context.State.SourceCloneDirectory,
            TargetPath: context.Settings.TargetPath);

        Console.WriteLine($"Loaded '{Path.GetFileName(handler.GetType().Assembly.Location)}' for '{repositoryKey}'.");

        await handler.PrepareAsync(handlerContext).ConfigureAwait(false);
        await handler.ValidateAsync(handlerContext).ConfigureAwait(false);

        return [$"Ran {repositoryKey} handler successfully."];
    }

    private static string GetRepositoryKey(StageContext context)
    {
        if (!string.IsNullOrWhiteSpace(context.Settings.ScriptSet))
            return context.Settings.ScriptSet;

        return Path.GetFileName(context.Settings.SourceRepo.Replace('/', Path.DirectorySeparatorChar));
    }

    private static IRepositoryHandler? TryCreateHandler(string toolRoot, string repositoryKey)
    {
        var assemblyPath = FindAssemblyPath(toolRoot, repositoryKey);
        if (assemblyPath is null)
            return null;

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

    private static string? FindAssemblyPath(string toolRoot, string repositoryKey)
    {
        var assemblyBaseName = ToAssemblyBaseName(repositoryKey);
        var assemblyFileName = $"{assemblyBaseName}.dll";
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, assemblyFileName),
            Path.Combine(toolRoot, "src", assemblyBaseName, "bin", "Debug", "net10.0", assemblyFileName),
            Path.Combine(toolRoot, "src", assemblyBaseName, "bin", "Release", "net10.0", assemblyFileName),
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
                return Path.GetFullPath(candidate);
        }

        return null;
    }

    private static string ToAssemblyBaseName(string repositoryKey)
    {
        var parts = repositoryKey
            .Split(['-', '_', '.'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return string.Concat(parts.Select(static part =>
            char.ToUpperInvariant(part[0]) + part[1..].ToLowerInvariant()));
    }
}
