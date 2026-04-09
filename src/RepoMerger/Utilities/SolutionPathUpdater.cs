using System.Text.RegularExpressions;

namespace RepoMerger;

public static class SolutionPathUpdater
{
    private const string RepositoryEngineeringDirProperty = "$(RepositoryEngineeringDir)";

    public static async Task<int> UpdateMovedPathsAsync(string repositoryRoot, string targetRelativePath)
    {
        var updatedCount = 0;

        foreach (var filePath in Directory.GetFiles(repositoryRoot, "*.slnx", SearchOption.TopDirectoryOnly))
        {
            if (await UpdateSolutionFileAsync(repositoryRoot, targetRelativePath, filePath, pathSeparatorText: "/").ConfigureAwait(false))
                updatedCount++;
        }

        foreach (var filePath in Directory.GetFiles(repositoryRoot, "*.slnf", SearchOption.TopDirectoryOnly))
        {
            if (await UpdateSolutionFileAsync(repositoryRoot, targetRelativePath, filePath, pathSeparatorText: @"\\").ConfigureAwait(false))
                updatedCount++;
        }

        var targetRoot = Path.Combine(repositoryRoot, targetRelativePath);
        if (!Directory.Exists(targetRoot))
            return updatedCount;

        var projectFiles = Directory.GetFiles(targetRoot, "*.*proj", SearchOption.AllDirectories)
            .Concat(Directory.GetFiles(targetRoot, "*.props", SearchOption.AllDirectories))
            .Concat(Directory.GetFiles(targetRoot, "*.targets", SearchOption.AllDirectories))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var filePath in projectFiles)
        {
            if (await UpdateRepositoryEngineeringReferencesAsync(repositoryRoot, targetRelativePath, filePath).ConfigureAwait(false))
                updatedCount++;
        }

        return updatedCount;
    }

    private static async Task<bool> UpdateSolutionFileAsync(string repositoryRoot, string targetRelativePath, string filePath, string pathSeparatorText)
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
                var rewrittenPath = RewritePathIfMoved(repositoryRoot, targetRelativePath, match.Groups[2].Value, pathSeparatorText);
                return $"{match.Groups[1].Value}{rewrittenPath}{match.Groups[3].Value}";
            });

        if (string.Equals(originalText, updatedText, StringComparison.Ordinal))
            return false;

        await File.WriteAllTextAsync(filePath, updatedText).ConfigureAwait(false);
        return true;
    }

    private static string RewritePathIfMoved(string repositoryRoot, string targetRelativePath, string relativePath, string pathSeparatorText)
    {
        if (Path.IsPathRooted(relativePath))
            return relativePath;

        var pathParts = relativePath
            .Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (pathParts.Length == 0)
            return relativePath;

        var normalizedPath = Path.Combine(pathParts);
        var originalLocation = Path.Combine(repositoryRoot, normalizedPath);
        if (File.Exists(originalLocation) || Directory.Exists(originalLocation))
            return relativePath;

        var movedLocation = Path.Combine(repositoryRoot, targetRelativePath, normalizedPath);
        if (!File.Exists(movedLocation) && !Directory.Exists(movedLocation))
            return relativePath;

        var rewrittenPath = Path.Combine(targetRelativePath, normalizedPath);
        return rewrittenPath.Replace(Path.DirectorySeparatorChar.ToString(), pathSeparatorText);
    }

    private static async Task<bool> UpdateRepositoryEngineeringReferencesAsync(string repositoryRoot, string targetRelativePath, string filePath)
    {
        var originalText = await File.ReadAllTextAsync(filePath).ConfigureAwait(false);
        var relativeMovedEngPath = Path.GetRelativePath(
            Path.Combine(repositoryRoot, "eng"),
            Path.Combine(repositoryRoot, targetRelativePath, "eng"));

        var updatedText = Regex.Replace(
            originalText,
            Regex.Escape(RepositoryEngineeringDirProperty) + "(?<suffix>[^\"'<>\\r\\n;]+)",
            match => RewriteRepositoryEngineeringPathIfMoved(
                repositoryRoot,
                targetRelativePath,
                relativeMovedEngPath,
                match.Groups["suffix"].Value));

        if (string.Equals(originalText, updatedText, StringComparison.Ordinal))
            return false;

        await File.WriteAllTextAsync(filePath, updatedText).ConfigureAwait(false);
        return true;
    }

    private static string RewriteRepositoryEngineeringPathIfMoved(
        string repositoryRoot,
        string targetRelativePath,
        string relativeMovedEngPath,
        string suffix)
    {
        if (string.IsNullOrWhiteSpace(suffix))
            return RepositoryEngineeringDirProperty;

        var normalizedSuffix = suffix.TrimStart('\\', '/');
        var suffixParts = normalizedSuffix
            .Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (suffixParts.Length == 0)
            return RepositoryEngineeringDirProperty + suffix;

        var originalLocation = Path.Combine(repositoryRoot, "eng", Path.Combine(suffixParts));
        if (File.Exists(originalLocation) || Directory.Exists(originalLocation))
            return RepositoryEngineeringDirProperty + suffix;

        var movedLocation = Path.Combine(repositoryRoot, targetRelativePath, "eng", Path.Combine(suffixParts));
        if (!File.Exists(movedLocation) && !Directory.Exists(movedLocation))
            return RepositoryEngineeringDirProperty + suffix;

        var separator = suffix.Contains('/') && !suffix.Contains('\\') ? "/" : "\\";
        var normalizedRelativeMovedEngPath = relativeMovedEngPath
            .Replace(Path.DirectorySeparatorChar.ToString(), separator)
            .Replace(Path.AltDirectorySeparatorChar.ToString(), separator)
            .TrimEnd(separator[0]);
        var normalizedSuffixText = normalizedSuffix
            .Replace("\\", separator, StringComparison.Ordinal)
            .Replace("/", separator, StringComparison.Ordinal);

        return $"{RepositoryEngineeringDirProperty}{normalizedRelativeMovedEngPath}{separator}{normalizedSuffixText}";
    }
}
