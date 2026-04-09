using System.Text;

namespace RepoMerger;

internal static class PathHelper
{
    public static string GetToolRoot()
    {
        foreach (var startPath in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var directory = Path.GetFullPath(startPath);
            while (!string.IsNullOrEmpty(directory))
            {
                if (File.Exists(Path.Combine(directory, "RepoMerger.slnx"))
                    || Directory.Exists(Path.Combine(directory, ".git")))
                {
                    return directory;
                }

                directory = Path.GetDirectoryName(directory) ?? string.Empty;
            }
        }

        throw new InvalidOperationException("Could not locate the RepoMerger repository root.");
    }

    public static string GetDefaultRunName(string sourceRepo, string targetRepo, string targetPath)
        => SanitizePathSegment($"{sourceRepo}-to-{targetRepo}-{targetPath}");

    public static string GetScriptSetName(MergeSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.ScriptSet))
            return SanitizePathSegment(settings.ScriptSet);

        var repoName = Path.GetFileName(settings.SourceRepo.Replace('/', Path.DirectorySeparatorChar));
        return SanitizePathSegment(repoName);
    }

    public static string SanitizePathSegment(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(char.ToLowerInvariant(ch));
            }
            else if (ch is '-' or '_' or '.')
            {
                builder.Append(ch);
            }
            else
            {
                builder.Append('-');
            }
        }

        return builder.ToString().Trim('-');
    }

    public static string GetAbsolutePath(string rootPath, string path)
        => Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(rootPath, path));

    public static void EnsurePathIsOutsideRepo(string repoRoot, string candidatePath, string optionName)
    {
        if (IsPathWithinRoot(repoRoot, candidatePath))
        {
            throw new InvalidOperationException(
                $"{optionName} resolved to '{candidatePath}', which is inside '{repoRoot}'. " +
                "Choose a path outside that checkout so folder structure and repo-local configuration cannot interfere.");
        }
    }

    public static bool IsPathWithinRoot(string rootPath, string candidatePath)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath)) + Path.DirectorySeparatorChar;
        var normalizedCandidate = Path.GetFullPath(candidatePath);
        return normalizedCandidate.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase)
            || string.Equals(
                Path.TrimEndingDirectorySeparator(normalizedCandidate),
                Path.TrimEndingDirectorySeparator(rootPath),
                StringComparison.OrdinalIgnoreCase);
    }

    public static bool LooksLikeLocalPath(string value)
        => value.Contains('\\')
            || value.Contains(':')
            || value.StartsWith(".", StringComparison.Ordinal);

    public static string ResolveRepositoryUri(string repository, string toolRoot)
        => LooksLikeLocalPath(repository)
            ? GetAbsolutePath(toolRoot, repository)
            : $"https://github.com/{repository}.git";

    public static bool RepositoryLocationsMatch(string left, string right)
        => string.Equals(NormalizeRepositoryLocation(left), NormalizeRepositoryLocation(right), StringComparison.OrdinalIgnoreCase);

    private static string NormalizeRepositoryLocation(string value)
    {
        value = value.Trim().Replace('\\', '/');

        if (value.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            value = value[..^4];

        return value.TrimEnd('/');
    }
}
