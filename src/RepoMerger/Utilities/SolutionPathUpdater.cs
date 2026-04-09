using System.Text.RegularExpressions;

namespace RepoMerger;

public static class SolutionPathUpdater
{
    private const string RepositoryEngineeringDirProperty = "$(RepositoryEngineeringDir)";
    private const string MSBuildThisFileDirectoryProperty = "$(MSBuildThisFileDirectory)";
    private const string RepoRootProperty = "$(RepoRoot)";

    private static readonly Regex MsBuildThisFileDirectoryEngPathPattern = new(
        Regex.Escape(MSBuildThisFileDirectoryProperty) + """eng(?<suffix>(?:[\\/][^"'<>;\r\n]*)?)(?=(?:["'<>\s;]|$))""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex RepoRootEngPathPattern = new(
        Regex.Escape(RepoRootProperty) + """eng(?<suffix>(?:[\\/][^"'<>;\r\n]*)?)(?=(?:["'<>\s;]|$))""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex DirectEngPathPattern = new(
        """(?<prefix>^|["'=;>\s])eng(?<suffix>(?:[\\/][^"'<>;\r\n]*)?)(?=(?:["'<>\s;]|$))""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Multiline);

    private static readonly Regex RepoRootReferencePattern = new(
        Regex.Escape(RepoRootProperty) + """(?<suffix>[^"'<>;\r\n]*)""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static async Task<int> NormalizeRepositoryEngineeringReferencesAsync(string repositoryRoot)
    {
        var updatedCount = 0;
        foreach (var filePath in EnumerateProjectAndBuildFiles(repositoryRoot))
        {
            if (await NormalizeRepositoryEngineeringReferencesInFileAsync(filePath).ConfigureAwait(false))
                updatedCount++;
        }

        return updatedCount;
    }

    public static async Task<int> RewriteRepoRootReferencesAsync(string repositoryRoot, string targetRelativePath)
    {
        var updatedCount = 0;
        foreach (var filePath in EnumerateProjectAndBuildFiles(repositoryRoot))
        {
            if (await RewriteRepoRootReferencesInFileAsync(repositoryRoot, filePath, targetRelativePath).ConfigureAwait(false))
                updatedCount++;
        }

        return updatedCount;
    }

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

        return updatedCount;
    }

    private static IEnumerable<string> EnumerateProjectAndBuildFiles(string repositoryRoot)
        => Directory.GetFiles(repositoryRoot, "*.*proj", SearchOption.AllDirectories)
            .Concat(Directory.GetFiles(repositoryRoot, "*.props", SearchOption.AllDirectories))
            .Concat(Directory.GetFiles(repositoryRoot, "*.targets", SearchOption.AllDirectories))
            .Distinct(StringComparer.OrdinalIgnoreCase);

    private static async Task<bool> NormalizeRepositoryEngineeringReferencesInFileAsync(string filePath)
    {
        var originalText = await File.ReadAllTextAsync(filePath).ConfigureAwait(false);
        var updatedText = MsBuildThisFileDirectoryEngPathPattern.Replace(
            originalText,
            match => RewriteRepositoryEngineeringReference(match.Groups["suffix"].Value));
        updatedText = RepoRootEngPathPattern.Replace(
            updatedText,
            match => RewriteRepositoryEngineeringReference(match.Groups["suffix"].Value));
        updatedText = DirectEngPathPattern.Replace(
            updatedText,
            match => $"{match.Groups["prefix"].Value}{RewriteRepositoryEngineeringReference(match.Groups["suffix"].Value)}");

        if (string.Equals(originalText, updatedText, StringComparison.Ordinal))
            return false;

        await File.WriteAllTextAsync(filePath, updatedText).ConfigureAwait(false);
        return true;
    }

    private static async Task<bool> RewriteRepoRootReferencesInFileAsync(string repositoryRoot, string filePath, string targetRelativePath)
    {
        var originalText = await File.ReadAllTextAsync(filePath).ConfigureAwait(false);
        var updatedText = RepoRootReferencePattern.Replace(
            originalText,
            match => RewriteRepoRootReference(repositoryRoot, targetRelativePath, match.Groups["suffix"].Value));

        if (string.Equals(originalText, updatedText, StringComparison.Ordinal))
            return false;

        await File.WriteAllTextAsync(filePath, updatedText).ConfigureAwait(false);
        return true;
    }

    private static string RewriteRepositoryEngineeringReference(string suffix)
    {
        var normalizedSuffix = suffix.TrimStart('\\', '/');
        return string.IsNullOrEmpty(normalizedSuffix)
            ? RepositoryEngineeringDirProperty
            : RepositoryEngineeringDirProperty + normalizedSuffix;
    }

    private static string RewriteRepoRootReference(string repositoryRoot, string targetRelativePath, string suffix)
    {
        if (string.IsNullOrEmpty(suffix))
            return RepoRootProperty;

        var normalizedSuffix = suffix.TrimStart('\\', '/');
        if (normalizedSuffix.Length == 0)
            return RepoRootProperty + suffix;

        if (StartsWithPathSegment(normalizedSuffix, "eng"))
        {
            var engSuffix = normalizedSuffix.Length == "eng".Length
                ? string.Empty
                : normalizedSuffix["eng".Length..];
            return RewriteRepositoryEngineeringReference(engSuffix);
        }

        var normalizedRelativePath = NormalizeRelativePath(normalizedSuffix, Path.DirectorySeparatorChar);
        var originalLocation = Path.Combine(repositoryRoot, normalizedRelativePath);
        if (File.Exists(originalLocation) || Directory.Exists(originalLocation))
            return RepoRootProperty + suffix;

        var movedLocation = Path.Combine(repositoryRoot, targetRelativePath, normalizedRelativePath);
        if (!File.Exists(movedLocation) && !Directory.Exists(movedLocation))
            return RepoRootProperty + suffix;

        var separator = suffix.Contains('/') && !suffix.Contains('\\') ? '/' : '\\';
        var rewrittenPath = Path.Combine(targetRelativePath, normalizedRelativePath)
            .Replace(Path.DirectorySeparatorChar, separator)
            .Replace(Path.AltDirectorySeparatorChar, separator);

        return RepoRootProperty + rewrittenPath;
    }

    private static string NormalizeRelativePath(string relativePath, char preferredSeparator)
        => string.Join(
            preferredSeparator,
            relativePath.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static bool StartsWithPathSegment(string value, string segment)
        => value.Length >= segment.Length
            && value.StartsWith(segment, StringComparison.OrdinalIgnoreCase)
            && (value.Length == segment.Length || value[segment.Length] is '\\' or '/');

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
}
