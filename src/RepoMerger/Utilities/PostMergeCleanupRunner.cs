using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace RepoMerger;

internal static class PostMergeCleanupRunner
{
    private static readonly CleanupStep[] Steps =
    [
        new(
            "remove-common-targets-import",
            @"Remove Razor imports of $(RepositoryEngineeringDir)targets\Common.targets.",
            "Remove Razor Common.targets import",
            RemoveCommonTargetsImportAsync),
        new(
            "rewrite-directory-build-imports",
            "Rewrite Razor Directory.Build.props/targets to import the repo-root Directory.Build files.",
            "Rewrite Razor Directory.Build imports",
            RewriteDirectoryBuildImportsAsync),
        new(
            "rewrite-directory-packages-props",
            "Rewrite Razor Directory.Packages.props to import the repo-root file and remove duplicate package versions.",
            "Rewrite Razor Directory.Packages.props",
            RewriteDirectoryPackagesPropsAsync),
        new(
            "normalize-objectpool-package-version",
            "Rewrite Razor's Microsoft.Extensions.ObjectPool package version to use the shared Microsoft.Extensions version.",
            "Normalize Razor ObjectPool package version",
            NormalizeObjectPoolPackageVersionAsync),
        new(
            "remove-roslyn-diagnostics-analyzers",
            "Remove Roslyn.Diagnostics.Analyzers package references from Razor Directory.Build.props files.",
            "Remove Roslyn.Diagnostics.Analyzers refs",
            RemoveRoslynDiagnosticsAnalyzersAsync),
        new(
            "remove-xunit-execution-package-refs",
            "Remove explicit xunit.extensibility.execution package refs from Razor test projects and defer to Roslyn's XUnit.targets.",
            "Remove Razor xunit.extensibility.execution refs",
            RemoveXunitExecutionPackageReferencesAsync),
        new(
            "convert-roslyn-package-references",
            "Convert Roslyn PackageReference items into ProjectReference items.",
            "Convert Roslyn package references to project references",
            ConvertRoslynPackageReferencesAsync),
    ];

    public static async Task<string> RunAsync(StageContext context)
    {
        var targetRelativePath = PathHelper.NormalizeRelativeTargetPath(context.Settings.TargetPath, "Post-merge cleanup");
        var targetRoot = PathHelper.GetAbsolutePath(context.TargetRepoRoot, targetRelativePath);

        if (context.Settings.DryRun)
        {
            return
                $"Dry run: would apply {Steps.Length} post-merge cleanup step(s) under '{targetRoot}', " +
                "committing each cleanup separately.";
        }

        if (!Directory.Exists(targetRoot))
        {
            throw new InvalidOperationException(
                $"The merged target path '{targetRoot}' does not exist. Run the merge-into-target stage first.");
        }

        var summaries = new List<string>();
        foreach (var step in Steps)
        {
            var stepSummary = await step.ExecuteAsync(context.TargetRepoRoot, targetRoot).ConfigureAwait(false);
            var committed = await GitRunner.CommitTrackedChangesAsync(context.TargetRepoRoot, step.CommitMessage).ConfigureAwait(false);
            if (committed)
            {
                context.State.TargetHeadCommit = await GitRunner.GetHeadCommitAsync(context.TargetRepoRoot).ConfigureAwait(false);
                summaries.Add($"{stepSummary} Committed as '{context.State.TargetHeadCommit}'.");
            }
            else
            {
                summaries.Add($"{stepSummary} No commit was needed.");
            }
        }

        return $"Applied post-merge cleanup stage. {string.Join(" ", summaries)}";
    }

    private static async Task<string> RemoveCommonTargetsImportAsync(string targetRepoRoot, string targetRoot)
    {
        var changedFiles = new List<string>();

        foreach (var path in EnumerateMsBuildFiles(targetRoot))
        {
            var originalContent = await File.ReadAllTextAsync(path).ConfigureAwait(false);
            var updatedContent = CommonTargetsImportPattern.Replace(originalContent, string.Empty);
            if (string.Equals(originalContent, updatedContent, StringComparison.Ordinal))
                continue;

            await File.WriteAllTextAsync(path, updatedContent).ConfigureAwait(false);
            changedFiles.Add(Path.GetRelativePath(targetRepoRoot, path));
        }

        return changedFiles.Count == 0
            ? @"No Razor imports of $(RepositoryEngineeringDir)targets\Common.targets were found."
            : $@"Removed Razor imports of $(RepositoryEngineeringDir)targets\Common.targets from {changedFiles.Count} file(s): {string.Join(", ", changedFiles)}.";
    }

    private static async Task<string> RewriteDirectoryBuildImportsAsync(string targetRepoRoot, string targetRoot)
    {
        var changedFiles = new List<string>();

        foreach (var file in DirectoryBuildImportFiles)
        {
            var path = Path.Combine(targetRoot, file.FileName);
            if (!File.Exists(path))
                continue;

            var originalContent = await File.ReadAllTextAsync(path).ConfigureAwait(false);
            var updatedContent = RewriteDirectoryBuildImportContent(originalContent, file);
            if (string.Equals(originalContent, updatedContent, StringComparison.Ordinal))
                continue;

            await File.WriteAllTextAsync(path, updatedContent).ConfigureAwait(false);
            changedFiles.Add(Path.GetRelativePath(targetRepoRoot, path));
        }

        return changedFiles.Count == 0
            ? "No Razor Directory.Build import rewrites were needed."
            : $"Rewrote Razor Directory.Build imports in {changedFiles.Count} file(s): {string.Join(", ", changedFiles)}.";
    }

    private static async Task<string> RewriteDirectoryPackagesPropsAsync(string targetRepoRoot, string targetRoot)
    {
        var razorPackagesPath = Path.Combine(targetRoot, "Directory.Packages.props");
        if (!File.Exists(razorPackagesPath))
            return "No Razor Directory.Packages.props file was found.";

        var rootPackagesPath = Path.Combine(targetRepoRoot, "Directory.Packages.props");
        if (!File.Exists(rootPackagesPath))
            return "No repo-root Directory.Packages.props file was found to import.";

        var rootPackageIds = await CollectPackageVersionIdsAsync(rootPackagesPath).ConfigureAwait(false);

        var document = await LoadXmlAsync(razorPackagesPath).ConfigureAwait(false);
        var project = document.Root
            ?? throw new InvalidOperationException($"The file '{razorPackagesPath}' does not contain a root <Project> element.");

        var existingImports = project
            .Elements()
            .Where(IsRootDirectoryPackagesImport)
            .ToList();
        var importNeedsRewrite = existingImports.Count != 1
            || !ReferenceEquals(project.Elements().FirstOrDefault(), existingImports[0]);

        if (importNeedsRewrite)
        {
            foreach (var existingImport in existingImports)
                existingImport.Remove();

            project.AddFirst(CreateRootDirectoryImport(project.Name.Namespace, "Directory.Packages.props"));
        }

        var duplicatePackageVersions = project
            .Descendants()
            .Where(static element => element.Name.LocalName == "PackageVersion")
            .Where(element =>
            {
                var packageId = GetPackageVersionId(element);
                return !string.IsNullOrWhiteSpace(packageId) && rootPackageIds.Contains(packageId);
            })
            .ToList();

        foreach (var duplicatePackageVersion in duplicatePackageVersions)
            duplicatePackageVersion.Remove();

        foreach (var itemGroup in project.Elements().Where(static element => element.Name.LocalName == "ItemGroup").ToList())
        {
            if (!itemGroup.Elements().Any())
                itemGroup.Remove();
        }

        if (!importNeedsRewrite && duplicatePackageVersions.Count == 0)
            return "No Razor Directory.Packages.props rewrite was needed.";

        await SaveXmlAsync(document, razorPackagesPath).ConfigureAwait(false);
        return
            $"Updated '{Path.GetRelativePath(targetRepoRoot, razorPackagesPath)}' to import the repo-root Directory.Packages.props " +
            $"and removed {duplicatePackageVersions.Count} duplicate PackageVersion item(s).";
    }

    private static async Task<string> NormalizeObjectPoolPackageVersionAsync(string targetRepoRoot, string targetRoot)
    {
        var razorPackagesPath = Path.Combine(targetRoot, "Directory.Packages.props");
        if (!File.Exists(razorPackagesPath))
            return "No Razor Directory.Packages.props file was found for ObjectPool version normalization.";

        var document = await LoadXmlAsync(razorPackagesPath, preserveWhitespace: true).ConfigureAwait(false);
        var objectPoolEntries = document
            .Descendants()
            .Where(static element => element.Name.LocalName == "PackageVersion")
            .Where(static element => IsPackageReferenceFor(element, "Microsoft.Extensions.ObjectPool"))
            .ToList();

        if (objectPoolEntries.Count == 0)
            return "No Razor Microsoft.Extensions.ObjectPool package version entry was found.";

        var updatedCount = 0;
        foreach (var objectPoolEntry in objectPoolEntries)
        {
            var currentVersion = objectPoolEntry.Attribute("Version")?.Value?.Trim();
            if (string.Equals(currentVersion, "$(_MicrosoftExtensionsPackageVersion)", StringComparison.Ordinal))
                continue;

            objectPoolEntry.SetAttributeValue("Version", "$(_MicrosoftExtensionsPackageVersion)");
            updatedCount++;
        }

        if (updatedCount == 0)
            return "Razor Microsoft.Extensions.ObjectPool already uses the shared Microsoft.Extensions version.";

        await SaveXmlAsync(document, razorPackagesPath).ConfigureAwait(false);
        return
            $"Updated {updatedCount} Razor Microsoft.Extensions.ObjectPool package version entr{(updatedCount == 1 ? "y" : "ies")} " +
            $"in '{Path.GetRelativePath(targetRepoRoot, razorPackagesPath)}' to use $(_MicrosoftExtensionsPackageVersion).";
    }

    private static async Task<string> RemoveRoslynDiagnosticsAnalyzersAsync(string targetRepoRoot, string targetRoot)
    {
        var changedFiles = new List<string>();
        var removedReferenceCount = 0;

        foreach (var propsPath in Directory.EnumerateFiles(targetRoot, "Directory.Build.props", SearchOption.AllDirectories))
        {
            var document = await LoadXmlAsync(propsPath, preserveWhitespace: true).ConfigureAwait(false);
            var packageReferences = document
                .Descendants()
                .Where(static element => element.Name.LocalName == "PackageReference")
                .Where(IsRoslynDiagnosticsAnalyzersReference)
                .ToList();

            if (packageReferences.Count == 0)
                continue;

            removedReferenceCount += packageReferences.Count;
            foreach (var packageReference in packageReferences)
                packageReference.Remove();

            foreach (var itemGroup in document.Descendants().Where(static element => element.Name.LocalName == "ItemGroup").ToList())
            {
                if (!itemGroup.Elements().Any())
                    itemGroup.Remove();
            }

            await SaveXmlAsync(document, propsPath).ConfigureAwait(false);
            changedFiles.Add(Path.GetRelativePath(targetRepoRoot, propsPath));
        }

        return removedReferenceCount == 0
            ? "No Roslyn.Diagnostics.Analyzers references were found in Razor Directory.Build.props files."
            : $"Removed {removedReferenceCount} Roslyn.Diagnostics.Analyzers reference(s) from {changedFiles.Count} file(s): {string.Join(", ", changedFiles)}.";
    }

    private static async Task<string> RemoveXunitExecutionPackageReferencesAsync(string targetRepoRoot, string targetRoot)
    {
        var changedFiles = new List<string>();
        var removedReferenceCount = 0;

        foreach (var projectPath in Directory.EnumerateFiles(targetRoot, "*.csproj", SearchOption.AllDirectories))
        {
            var document = await LoadXmlAsync(projectPath, preserveWhitespace: true).ConfigureAwait(false);
            var packageReferences = document
                .Descendants()
                .Where(static element => element.Name.LocalName == "PackageReference")
                .Where(static element => IsPackageReferenceFor(element, "xunit.extensibility.execution"))
                .ToList();

            if (packageReferences.Count == 0)
                continue;

            removedReferenceCount += packageReferences.Count;
            foreach (var packageReference in packageReferences)
                packageReference.Remove();

            foreach (var itemGroup in document.Descendants().Where(static element => element.Name.LocalName == "ItemGroup").ToList())
            {
                if (!itemGroup.Elements().Any())
                    itemGroup.Remove();
            }

            await SaveXmlAsync(document, projectPath).ConfigureAwait(false);
            changedFiles.Add(Path.GetRelativePath(targetRepoRoot, projectPath));
        }

        return removedReferenceCount == 0
            ? "No explicit xunit.extensibility.execution package references were found in Razor projects."
            : $"Removed {removedReferenceCount} explicit xunit.extensibility.execution reference(s) from {changedFiles.Count} Razor project(s): {string.Join(", ", changedFiles)}.";
    }

    private static async Task<string> ConvertRoslynPackageReferencesAsync(string targetRepoRoot, string targetRoot)
    {
        var roslynProjectMap = await BuildRoslynProjectMapAsync(targetRepoRoot, targetRoot).ConfigureAwait(false);
        if (roslynProjectMap.Count == 0)
            return "No Roslyn package references were found to convert.";

        var changedFiles = new List<string>();
        var convertedReferenceCount = 0;

        foreach (var projectPath in Directory.EnumerateFiles(targetRoot, "*.csproj", SearchOption.AllDirectories))
        {
            var convertedInProject = await ConvertRoslynPackageReferencesInProjectAsync(projectPath, roslynProjectMap).ConfigureAwait(false);
            if (convertedInProject == 0)
                continue;

            convertedReferenceCount += convertedInProject;
            changedFiles.Add(Path.GetRelativePath(targetRepoRoot, projectPath));
        }

        return convertedReferenceCount == 0
            ? "No Roslyn package references were found to convert."
            : $"Converted {convertedReferenceCount} Roslyn package reference(s) to project references in {changedFiles.Count} file(s): {string.Join(", ", changedFiles)}.";
    }

    private static IEnumerable<string> EnumerateMsBuildFiles(string root)
        => Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
            .Where(static path =>
                path.EndsWith(".props", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith(".targets", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith(".vbproj", StringComparison.OrdinalIgnoreCase));

    private static async Task<Dictionary<string, string>> BuildRoslynProjectMapAsync(string targetRepoRoot, string targetRoot)
    {
        var referencedPackageIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var projectPath in Directory.EnumerateFiles(targetRoot, "*.csproj", SearchOption.AllDirectories))
        {
            var document = await LoadXmlAsync(projectPath).ConfigureAwait(false);
            foreach (var packageReference in document.Descendants().Where(static element => element.Name.LocalName == "PackageReference"))
            {
                var packageId = packageReference.Attribute("Include")?.Value?.Trim();
                if (LooksLikeRoslynPackageReference(packageId))
                    referencedPackageIds.Add(packageId!);
            }
        }

        if (referencedPackageIds.Count == 0)
            return [];

        var candidates = referencedPackageIds.ToDictionary(
            static packageId => packageId,
            static _ => new List<ProjectCandidate>(),
            StringComparer.OrdinalIgnoreCase);

        foreach (var projectPath in Directory.EnumerateFiles(Path.Combine(targetRepoRoot, "src"), "*.csproj", SearchOption.AllDirectories))
        {
            var projectFileName = Path.GetFileNameWithoutExtension(projectPath);
            foreach (var packageId in referencedPackageIds)
            {
                if (string.Equals(projectFileName, packageId, StringComparison.OrdinalIgnoreCase))
                    candidates[packageId].Add(new ProjectCandidate(projectPath, projectFileName, ExplicitPackageId: null));
            }

            var document = await LoadXmlAsync(projectPath).ConfigureAwait(false);
            foreach (var explicitPackageId in document.Descendants().Where(static element => element.Name.LocalName == "PackageId").Select(static element => element.Value.Trim()))
            {
                if (candidates.TryGetValue(explicitPackageId, out var matches))
                    matches.Add(new ProjectCandidate(projectPath, projectFileName, explicitPackageId));
            }
        }

        return candidates
            .Where(static entry => entry.Value.Count > 0)
            .ToDictionary(
                static entry => entry.Key,
                static entry => ChooseBestCandidate(entry.Key, entry.Value).Path,
                StringComparer.OrdinalIgnoreCase);
    }

    private static async Task<int> ConvertRoslynPackageReferencesInProjectAsync(
        string projectPath,
        IReadOnlyDictionary<string, string> roslynProjectMap)
    {
        var document = await LoadXmlAsync(projectPath, preserveWhitespace: true).ConfigureAwait(false);
        var projectDirectory = Path.GetDirectoryName(projectPath)!;
        var existingProjectReferences = document
            .Descendants()
            .Where(static element => element.Name.LocalName == "ProjectReference")
            .Select(static element => element.Attribute("Include")?.Value)
            .Where(static include => !string.IsNullOrWhiteSpace(include))
            .Select(include => Path.GetFullPath(Path.Combine(projectDirectory, include!)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var packageReferences = document
            .Descendants()
            .Where(static element => element.Name.LocalName == "PackageReference")
            .ToList();

        var convertedCount = 0;
        foreach (var packageReference in packageReferences)
        {
            var packageId = packageReference.Attribute("Include")?.Value?.Trim();
            if (string.IsNullOrWhiteSpace(packageId) || !roslynProjectMap.TryGetValue(packageId, out var referencedProjectPath))
                continue;

            var normalizedProjectPath = Path.GetFullPath(referencedProjectPath);
            if (existingProjectReferences.Contains(normalizedProjectPath))
            {
                packageReference.Remove();
                convertedCount++;
                continue;
            }

            var projectReference = new XElement(packageReference.Name.Namespace + "ProjectReference");
            projectReference.SetAttributeValue("Include", Path.GetRelativePath(projectDirectory, normalizedProjectPath));

            foreach (var attribute in packageReference.Attributes())
            {
                if (ShouldKeepProjectReferenceAttribute(attribute.Name.LocalName))
                    projectReference.SetAttributeValue(attribute.Name, attribute.Value);
            }

            foreach (var child in packageReference.Elements())
            {
                if (ShouldKeepProjectReferenceElement(child.Name.LocalName))
                    projectReference.Add(new XElement(child));
            }

            packageReference.ReplaceWith(projectReference);
            existingProjectReferences.Add(normalizedProjectPath);
            convertedCount++;
        }

        if (convertedCount > 0)
            await SaveXmlAsync(document, projectPath).ConfigureAwait(false);

        return convertedCount;
    }

    private static bool LooksLikeRoslynPackageReference(string? packageId)
        => !string.IsNullOrWhiteSpace(packageId)
            && (string.Equals(packageId, "Microsoft.CodeAnalysis", StringComparison.OrdinalIgnoreCase)
                || packageId.StartsWith("Microsoft.CodeAnalysis.", StringComparison.OrdinalIgnoreCase)
                || string.Equals(packageId, "Microsoft.VisualStudio.Extensibility.Testing.Xunit", StringComparison.OrdinalIgnoreCase)
                || string.Equals(packageId, "Microsoft.VisualStudio.Extensibility.Testing.SourceGenerator", StringComparison.OrdinalIgnoreCase)
                || string.Equals(packageId, "Microsoft.VisualStudio.LanguageServices", StringComparison.OrdinalIgnoreCase)
                || string.Equals(packageId, "Microsoft.VisualStudio.LanguageServices.CSharp.Symbols", StringComparison.OrdinalIgnoreCase)
                || string.Equals(packageId, "Microsoft.VisualStudio.LanguageServices.Implementation.Symbols", StringComparison.OrdinalIgnoreCase));

    private static string? GetPackageVersionId(XElement element)
        => element.Attribute("Include")?.Value?.Trim()
            ?? element.Attribute("Update")?.Value?.Trim();

    private static bool IsPackageReferenceFor(XElement element, string packageId)
        => string.Equals(element.Attribute("Include")?.Value?.Trim(), packageId, StringComparison.OrdinalIgnoreCase)
            || string.Equals(element.Attribute("Update")?.Value?.Trim(), packageId, StringComparison.OrdinalIgnoreCase);

    private static bool IsRoslynDiagnosticsAnalyzersReference(XElement element)
        => IsPackageReferenceFor(element, "Roslyn.Diagnostics.Analyzers")
            || IsPackageReferenceFor(element, "Roslyn.Diagnostics.Anaylzers");

    private static bool IsRootDirectoryPackagesImport(XElement element)
        => element.Name.LocalName == "Import"
            && (element.Attribute("Project")?.Value?.Contains("GetPathOfFileAbove('Directory.Packages.props'", StringComparison.Ordinal) ?? false);

    private static XElement CreateRootDirectoryImport(XNamespace xmlNamespace, string fileName)
        => new(
            xmlNamespace + "Import",
            new XAttribute("Project", $@"$([MSBuild]::GetPathOfFileAbove('{fileName}', '$(MSBuildThisFileDirectory)../'))"));

    private static string RewriteDirectoryBuildImportContent(string content, DirectoryBuildImportFile file)
    {
        var hasDesiredImport = file.RootImportPattern.IsMatch(content);
        if (hasDesiredImport)
            return file.ArcadeImportPattern.Replace(content, string.Empty, 1);

        if (file.ArcadeImportPattern.IsMatch(content))
            return file.ArcadeImportPattern.Replace(content, file.ReplacementImport + Environment.NewLine, 1);

        return ProjectOpenPattern.IsMatch(content)
            ? ProjectOpenPattern.Replace(content, match => $"{match.Value}{Environment.NewLine}{file.ReplacementImport}", 1)
            : content;
    }

    private static ProjectCandidate ChooseBestCandidate(string packageId, IEnumerable<ProjectCandidate> candidates)
        => candidates
            .OrderByDescending(candidate => ScoreCandidate(packageId, candidate))
            .ThenBy(candidate => candidate.Path, StringComparer.OrdinalIgnoreCase)
            .First();

    private static int ScoreCandidate(string packageId, ProjectCandidate candidate)
    {
        var score = 0;

        if (string.Equals(candidate.ProjectFileName, packageId, StringComparison.OrdinalIgnoreCase))
            score += 100;
        if (string.Equals(candidate.ExplicitPackageId, packageId, StringComparison.OrdinalIgnoreCase))
            score += 80;
        if (!candidate.Path.Contains($"{Path.DirectorySeparatorChar}NuGet{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            score += 20;
        if (candidate.ProjectFileName.EndsWith(".Package", StringComparison.OrdinalIgnoreCase))
            score -= 50;
        if (candidate.ProjectFileName.Contains("UnitTest", StringComparison.OrdinalIgnoreCase)
            || candidate.ProjectFileName.EndsWith(".Test", StringComparison.OrdinalIgnoreCase)
            || candidate.ProjectFileName.EndsWith(".Tests", StringComparison.OrdinalIgnoreCase))
        {
            score -= 100;
        }

        return score;
    }

    private static bool ShouldKeepProjectReferenceAttribute(string localName)
        => localName is "Condition" or "PrivateAssets" or "ReferenceOutputAssembly" or "OutputItemType" or "Aliases";

    private static bool ShouldKeepProjectReferenceElement(string localName)
        => localName is "Condition" or "PrivateAssets" or "ReferenceOutputAssembly" or "OutputItemType" or "Aliases" or "SetTargetFramework";

    private static async Task<HashSet<string>> CollectPackageVersionIdsAsync(string projectFilePath)
    {
        var collectedPackageIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await CollectPackageVersionIdsAsync(projectFilePath, collectedPackageIds, new HashSet<string>(StringComparer.OrdinalIgnoreCase)).ConfigureAwait(false);
        return collectedPackageIds;
    }

    private static async Task CollectPackageVersionIdsAsync(
        string projectFilePath,
        HashSet<string> collectedPackageIds,
        HashSet<string> visitedFiles)
    {
        var normalizedPath = Path.GetFullPath(projectFilePath);
        if (!File.Exists(normalizedPath) || !visitedFiles.Add(normalizedPath))
            return;

        var document = await LoadXmlAsync(normalizedPath).ConfigureAwait(false);

        foreach (var packageId in document
                     .Descendants()
                     .Where(static element => element.Name.LocalName == "PackageVersion")
                     .Select(GetPackageVersionId)
                     .Where(static packageId => !string.IsNullOrWhiteSpace(packageId)))
        {
            collectedPackageIds.Add(packageId!);
        }

        foreach (var importPath in document
                     .Descendants()
                     .Where(static element => element.Name.LocalName == "Import")
                     .Select(element => ResolveImportedProjectPath(normalizedPath, element.Attribute("Project")?.Value))
                     .Where(static path => !string.IsNullOrWhiteSpace(path)))
        {
            await CollectPackageVersionIdsAsync(importPath!, collectedPackageIds, visitedFiles).ConfigureAwait(false);
        }
    }

    private static string? ResolveImportedProjectPath(string importerPath, string? projectValue)
    {
        if (string.IsNullOrWhiteSpace(projectValue))
            return null;

        var importerDirectory = Path.GetDirectoryName(importerPath)!;
        var resolvedPath = TryResolveSimpleImportedProjectPath(importerDirectory, projectValue)
            ?? TryResolveGetPathOfFileAboveImport(importerDirectory, projectValue);

        return resolvedPath is not null && File.Exists(resolvedPath)
            ? resolvedPath
            : null;
    }

    private static string? TryResolveSimpleImportedProjectPath(string importerDirectory, string projectValue)
        => projectValue.Contains('$', StringComparison.Ordinal)
            ? null
            : Path.GetFullPath(Path.Combine(importerDirectory, projectValue));

    private static string? TryResolveGetPathOfFileAboveImport(string importerDirectory, string projectValue)
    {
        var match = GetPathOfFileAbovePattern.Match(projectValue);
        if (!match.Success)
            return null;

        var fileName = match.Groups["fileName"].Value;
        var searchStart = match.Groups["relativeStart"].Success
            ? Path.GetFullPath(Path.Combine(importerDirectory, match.Groups["relativeStart"].Value))
            : importerDirectory;

        for (var current = new DirectoryInfo(searchStart); current is not null; current = current.Parent)
        {
            var candidatePath = Path.Combine(current.FullName, fileName);
            if (File.Exists(candidatePath))
                return candidatePath;
        }

        return null;
    }

    private static async Task<XDocument> LoadXmlAsync(string path, bool preserveWhitespace = false)
    {
        await using var stream = File.OpenRead(path);
        return await XDocument.LoadAsync(
            stream,
            preserveWhitespace ? LoadOptions.PreserveWhitespace : LoadOptions.None,
            CancellationToken.None).ConfigureAwait(false);
    }

    private static async Task SaveXmlAsync(XDocument document, string path)
    {
        await using var stream = File.Create(path);
        await document.SaveAsync(stream, SaveOptions.None, CancellationToken.None).ConfigureAwait(false);
    }

    private static readonly Regex CommonTargetsImportPattern = new(
        @"^[ \t]*<Import\s+Project=""\$\(RepositoryEngineeringDir\)targets(?:\\|/)Common\.targets""\s*/>\r?\n?",
        RegexOptions.Multiline | RegexOptions.CultureInvariant);

    private static readonly Regex ProjectOpenPattern = new(
        @"<Project[^>]*>",
        RegexOptions.CultureInvariant);

    private static readonly Regex GetPathOfFileAbovePattern = new(
        @"GetPathOfFileAbove\('(?<fileName>[^']+)'(?:,\s*'\$\(MSBuildThisFileDirectory\)(?<relativeStart>[^']*)')?\)",
        RegexOptions.CultureInvariant);

    private static readonly DirectoryBuildImportFile[] DirectoryBuildImportFiles =
    [
        new(
            "Directory.Build.props",
            "  <Import Project=\"$([MSBuild]::GetPathOfFileAbove('Directory.Build.props', '$(MSBuildThisFileDirectory)../'))\" />",
            new Regex(@"GetPathOfFileAbove\('Directory\.Build\.props'", RegexOptions.CultureInvariant),
            new Regex(@"^[ \t]*(?:<!--\s*)?<Import\s+Project=""Sdk\.props""\s+Sdk=""Microsoft\.DotNet\.Arcade\.Sdk""\s*/>(?:\s*-->)?\r?\n?", RegexOptions.Multiline | RegexOptions.CultureInvariant)),
        new(
            "Directory.Build.targets",
            "  <Import Project=\"$([MSBuild]::GetPathOfFileAbove('Directory.Build.targets', '$(MSBuildThisFileDirectory)../'))\" />",
            new Regex(@"GetPathOfFileAbove\('Directory\.Build\.targets'", RegexOptions.CultureInvariant),
            new Regex(@"^[ \t]*(?:<!--\s*)?<Import\s+Project=""Sdk\.targets""\s+Sdk=""Microsoft\.DotNet\.Arcade\.Sdk""\s*/>(?:\s*-->)?\r?\n?", RegexOptions.Multiline | RegexOptions.CultureInvariant)),
    ];

    private sealed record CleanupStep(
        string Name,
        string Description,
        string CommitMessage,
        Func<string, string, Task<string>> ExecuteAsync);

    private sealed record ProjectCandidate(string Path, string ProjectFileName, string? ExplicitPackageId);

    private sealed record DirectoryBuildImportFile(
        string FileName,
        string ReplacementImport,
        Regex RootImportPattern,
        Regex ArcadeImportPattern);
}
