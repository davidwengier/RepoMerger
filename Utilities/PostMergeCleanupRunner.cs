using System.Text.Json;
using System.Text.Json.Nodes;
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
            @"Razor should rely on Roslyn's shared repository engineering targets instead of carrying an extra Common.targets import in the merged tree.",
            RemoveCommonTargetsImportAsync),
        new(
            "copy-razor-services-props",
            "Copy Razor's Services.props into the target eng folder and rewrite Razor references to it.",
            "Copy Razor Services.props into Roslyn eng",
            "Roslyn does not carry Razor's service registrations, so the merged Razor tree needs its own imported Services.props file alongside Roslyn's existing eng\\targets services infrastructure.",
            CopyRazorServicesPropsAsync),
        new(
            "merge-msbuild-sdks",
            "Merge missing Razor msbuild-sdks entries from source global.json into the target repo global.json.",
            "Merge Razor msbuild-sdks into global.json",
            "Razor uses additional MSBuild SDKs such as Microsoft.Build.NoTargets that are not declared in Roslyn's global.json, so adding only the missing entries allows restore without disturbing Roslyn's existing toolchain versions.",
            MergeMsbuildSdksAsync),
        new(
            "rewrite-brokered-services-pkgdef",
            "Rewrite Razor's brokered services pkgdef integration to use Roslyn's existing GeneratePkgDef support.",
            "Rewrite Razor brokered services pkgdef imports",
            "Roslyn's GeneratePkgDef.targets already understands PkgDefBrokeredService items, so Razor should integrate with that shared pipeline instead of importing a repository-local target that does not exist in Roslyn.",
            RewriteBrokeredServicesPkgDefAsync),
        new(
            "rewrite-directory-build-imports",
            "Rewrite Razor Directory.Build.props/targets to import the repo-root Directory.Build files.",
            "Rewrite Razor Directory.Build imports",
            "Once Razor is nested under src\\Razor, its Directory.Build files should import the repo-root Directory.Build files so the merged tree inherits Roslyn's central build behavior.",
            RewriteDirectoryBuildImportsAsync),
        new(
            "rewrite-directory-packages-props",
            "Rewrite Razor Directory.Packages.props to import the repo-root file and remove duplicate package versions.",
            "Rewrite Razor Directory.Packages.props",
            "Razor should import Roslyn's root Directory.Packages.props and remove duplicate package version declarations so central package management stays authoritative after the merge.",
            RewriteDirectoryPackagesPropsAsync),
        new(
            "normalize-sdk-razor-package-version",
            "Replace Razor's missing Microsoft.NET.Sdk.Razor version property with the source repo's explicit lower-bound package version.",
            "Normalize Razor Microsoft.NET.Sdk.Razor version",
            "Roslyn does not define $(MicrosoftNETSdkRazorPackageVersion), so the merged Razor Directory.Packages.props should carry the source repo's explicit lower bound for Microsoft.NET.Sdk.Razor instead of restoring without a version.",
            NormalizeSdkRazorPackageVersionAsync),
        new(
            "normalize-objectpool-package-version",
            "Rewrite Razor's Microsoft.Extensions.ObjectPool package version to use the shared Microsoft.Extensions version.",
            "Normalize Razor ObjectPool package version",
            "Razor should use Roslyn's shared Microsoft.Extensions versioning instead of carrying its own ObjectPool package version entry in the merged repository.",
            NormalizeObjectPoolPackageVersionAsync),
        new(
            "remove-roslyn-diagnostics-analyzers",
            "Remove Roslyn.Diagnostics.Analyzers package references from Razor Directory.Build.props files.",
            "Remove Roslyn.Diagnostics.Analyzers refs",
            "Roslyn already manages Roslyn.Diagnostics.Analyzers centrally, so the merged Razor tree should not add duplicate local analyzer references.",
            RemoveRoslynDiagnosticsAnalyzersAsync),
        new(
            "remove-xunit-execution-package-refs",
            "Remove explicit xunit.extensibility.execution package refs from Razor test projects and defer to Roslyn's XUnit.targets.",
            "Remove Razor xunit.extensibility.execution refs",
            "Razor does not need to reference xunit.extensibility.execution explicitly because Roslyn already supplies the required xUnit test infrastructure through eng\\targets\\XUnit.targets.",
            RemoveXunitExecutionPackageReferencesAsync),
        new(
            "remove-projectsystem-sdk-package-refs",
            "Remove Razor's Microsoft.VisualStudio.ProjectSystem.SDK package usage and defer to Roslyn's existing ProjectSystem infrastructure.",
            "Remove Razor ProjectSystem.SDK refs",
            "Microsoft.VisualStudio.LanguageServices.Razor already checks in its generated ProjectSystem rule files for Core MSBuild, so the merged Roslyn tree does not need Razor's local Microsoft.VisualStudio.ProjectSystem.SDK package or its pinned 17.14.143 version.",
            RemoveProjectSystemSdkPackageReferencesAsync),
        new(
            "remove-xunit-version-overrides",
            "Remove Razor-local xUnit VersionOverride pins and defer to Roslyn's centrally managed package versions.",
            "Remove Razor xUnit version overrides",
            "Razor should use Roslyn's centrally managed xUnit package versions instead of overriding xunit.assert and xunit.analyzers locally.",
            RemoveXunitVersionOverridesAsync),
        new(
            "convert-roslyn-package-references",
            "Convert Roslyn PackageReference items into ProjectReference items.",
            "Convert Roslyn package references to project references",
            "Inside the merged Roslyn tree, Razor should reference Roslyn projects directly instead of consuming Roslyn NuGet packages that duplicate the in-repo source.",
            ConvertRoslynPackageReferencesAsync),
    ];

    public static async Task<string> RunAsync(StageContext context)
    {
        var targetRoot = context.TargetRoot;

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
            var stepSummary = await step.ExecuteAsync(context).ConfigureAwait(false);
            var committed = await GitRunner.CommitTrackedChangesAsync(
                context.TargetRepoRoot,
                step.CommitMessage,
                step.CommitRationale).ConfigureAwait(false);
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

        return $"Applied post-merge cleanup stage.{Environment.NewLine}- {string.Join($"{Environment.NewLine}- ", summaries)}";
    }

    private static async Task<string> CopyRazorServicesPropsAsync(StageContext context)
    {
        var targetRepoRoot = context.TargetRepoRoot;
        var sourceServicesPropsPath = Path.Combine(context.State.SourceCloneDirectory, "eng", "targets", "Services.props");
        if (!File.Exists(sourceServicesPropsPath))
        {
            throw new InvalidOperationException(
                $"Expected Razor Services.props at '{sourceServicesPropsPath}', but it was not found in the preserved source checkout.");
        }

        var targetServicesPropsPath = Path.Combine(targetRepoRoot, "eng", "targets", "RazorServices.props");
        Directory.CreateDirectory(Path.GetDirectoryName(targetServicesPropsPath)!);

        var sourceContent = await File.ReadAllTextAsync(sourceServicesPropsPath).ConfigureAwait(false);
        var targetContent = File.Exists(targetServicesPropsPath)
            ? await File.ReadAllTextAsync(targetServicesPropsPath).ConfigureAwait(false)
            : null;

        var copiedTargetFile = !string.Equals(sourceContent, targetContent, StringComparison.Ordinal);
        if (copiedTargetFile)
        {
            File.Copy(sourceServicesPropsPath, targetServicesPropsPath, overwrite: true);
            await GitRunner.RunGitAsync(
                targetRepoRoot,
                "add",
                "--",
                Path.GetRelativePath(targetRepoRoot, targetServicesPropsPath)).ConfigureAwait(false);
        }

        var changedFiles = await RewriteRazorServicesPropsReferencesAsync(context).ConfigureAwait(false);
        if (!copiedTargetFile && changedFiles.Count == 0)
            return "No Razor Services.props copy or reference rewrites were needed.";

        var actions = new List<string>();
        if (copiedTargetFile)
            actions.Add($@"Copied Razor eng\targets\Services.props to '{Path.GetRelativePath(targetRepoRoot, targetServicesPropsPath)}'.");
        if (changedFiles.Count > 0)
            actions.Add($"Updated Razor Services.props references in {changedFiles.Count} file(s): {string.Join(", ", changedFiles)}.");

        return string.Join(" ", actions);
    }

    private static async Task<string> MergeMsbuildSdksAsync(StageContext context)
    {
        var targetRepoRoot = context.TargetRepoRoot;
        var sourceGlobalJsonPath = Path.Combine(context.State.SourceCloneDirectory, "global.json");
        if (!File.Exists(sourceGlobalJsonPath))
            return "No Razor global.json file was found for msbuild-sdks merge.";

        var targetGlobalJsonPath = Path.Combine(targetRepoRoot, "global.json");
        if (!File.Exists(targetGlobalJsonPath))
            return "No repo-root global.json file was found for msbuild-sdks merge.";

        var sourceGlobalJson = await LoadJsonObjectAsync(sourceGlobalJsonPath).ConfigureAwait(false);
        var sourceMsbuildSdks = sourceGlobalJson["msbuild-sdks"] as JsonObject;
        if (sourceMsbuildSdks is null || sourceMsbuildSdks.Count == 0)
            return "No Razor msbuild-sdks entries were found in source global.json.";

        var targetGlobalJson = await LoadJsonObjectAsync(targetGlobalJsonPath).ConfigureAwait(false);
        if (targetGlobalJson["msbuild-sdks"] is not JsonObject targetMsbuildSdks)
        {
            targetMsbuildSdks = [];
            targetGlobalJson["msbuild-sdks"] = targetMsbuildSdks;
        }

        var addedSdkEntries = new List<string>();
        foreach (var sdkEntry in sourceMsbuildSdks)
        {
            if (targetMsbuildSdks.ContainsKey(sdkEntry.Key))
                continue;

            targetMsbuildSdks[sdkEntry.Key] = sdkEntry.Value?.DeepClone();
            addedSdkEntries.Add($"{sdkEntry.Key}={sdkEntry.Value?.ToJsonString() ?? "null"}");
        }

        if (addedSdkEntries.Count == 0)
            return "No missing Razor msbuild-sdks entries were found to merge into global.json.";

        await SaveJsonAsync(targetGlobalJson, targetGlobalJsonPath).ConfigureAwait(false);
        return
            $"Added {addedSdkEntries.Count} missing Razor msbuild-sdks entr{(addedSdkEntries.Count == 1 ? "y" : "ies")} " +
            $"to '{Path.GetRelativePath(targetRepoRoot, targetGlobalJsonPath)}': {string.Join(", ", addedSdkEntries)}.";
    }

    private static async Task<string> RemoveCommonTargetsImportAsync(StageContext context)
    {
        var targetRepoRoot = context.TargetRepoRoot;
        var targetRoot = context.TargetRoot;
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

    private static async Task<string> RewriteBrokeredServicesPkgDefAsync(StageContext context)
    {
        var targetRepoRoot = context.TargetRepoRoot;
        var targetRoot = context.TargetRoot;
        var changedFiles = new List<string>();

        foreach (var path in Directory.EnumerateFiles(targetRoot, "*.targets", SearchOption.AllDirectories))
        {
            var originalContent = await File.ReadAllTextAsync(path).ConfigureAwait(false);
            var updatedContent = BrokeredServicesBeforeTargetsPattern.Replace(
                originalContent,
                @"BeforeTargets=""GeneratePkgDef""");
            updatedContent = BrokeredServicesImportPattern.Replace(updatedContent, string.Empty);

            if (string.Equals(originalContent, updatedContent, StringComparison.Ordinal))
                continue;

            await File.WriteAllTextAsync(path, updatedContent).ConfigureAwait(false);
            changedFiles.Add(Path.GetRelativePath(targetRepoRoot, path));
        }

        return changedFiles.Count == 0
            ? "No Razor brokered services pkgdef rewrites were needed."
            : $"Updated Razor brokered services pkgdef integration in {changedFiles.Count} file(s): {string.Join(", ", changedFiles)}.";
    }

    private static async Task<string> RewriteDirectoryBuildImportsAsync(StageContext context)
    {
        var targetRepoRoot = context.TargetRepoRoot;
        var targetRoot = context.TargetRoot;
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

    private static async Task<string> RewriteDirectoryPackagesPropsAsync(StageContext context)
    {
        var targetRepoRoot = context.TargetRepoRoot;
        var targetRoot = context.TargetRoot;
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

    private static async Task<string> NormalizeObjectPoolPackageVersionAsync(StageContext context)
    {
        var targetRepoRoot = context.TargetRepoRoot;
        var targetRoot = context.TargetRoot;
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

    private static async Task<string> NormalizeSdkRazorPackageVersionAsync(StageContext context)
    {
        var targetRepoRoot = context.TargetRepoRoot;
        var targetRoot = context.TargetRoot;
        var razorPackagesPath = Path.Combine(targetRoot, "Directory.Packages.props");
        if (!File.Exists(razorPackagesPath))
            return "No Razor Directory.Packages.props file was found for Microsoft.NET.Sdk.Razor version normalization.";

        var document = await LoadXmlAsync(razorPackagesPath, preserveWhitespace: true).ConfigureAwait(false);
        var sdkRazorEntries = document
            .Descendants()
            .Where(static element => element.Name.LocalName == "PackageVersion")
            .Where(static element => IsPackageReferenceFor(element, "Microsoft.NET.Sdk.Razor"))
            .ToList();

        if (sdkRazorEntries.Count == 0)
            return "No Razor Microsoft.NET.Sdk.Razor package version entry was found.";

        var updatedCount = 0;
        foreach (var sdkRazorEntry in sdkRazorEntries)
        {
            var currentVersion = sdkRazorEntry.Attribute("Version")?.Value?.Trim();
            if (string.Equals(currentVersion, RazorSdkPackageVersion, StringComparison.Ordinal))
                continue;

            sdkRazorEntry.SetAttributeValue("Version", RazorSdkPackageVersion);
            updatedCount++;
        }

        if (updatedCount == 0)
            return "Razor Microsoft.NET.Sdk.Razor already has an explicit lower-bound package version.";

        await SaveXmlAsync(document, razorPackagesPath).ConfigureAwait(false);
        return
            $"Updated {updatedCount} Razor Microsoft.NET.Sdk.Razor package version entr{(updatedCount == 1 ? "y" : "ies")} " +
            $"in '{Path.GetRelativePath(targetRepoRoot, razorPackagesPath)}' to use {RazorSdkPackageVersion}.";
    }

    private static async Task<string> RemoveRoslynDiagnosticsAnalyzersAsync(StageContext context)
    {
        var targetRepoRoot = context.TargetRepoRoot;
        var targetRoot = context.TargetRoot;
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

    private static async Task<string> RemoveXunitExecutionPackageReferencesAsync(StageContext context)
    {
        var targetRepoRoot = context.TargetRepoRoot;
        var targetRoot = context.TargetRoot;
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

    private static async Task<string> RemoveProjectSystemSdkPackageReferencesAsync(StageContext context)
    {
        var targetRepoRoot = context.TargetRepoRoot;
        var targetRoot = context.TargetRoot;
        if (!Directory.Exists(targetRoot))
            return "No Razor target tree was found for ProjectSystem.SDK cleanup.";

        var changedFiles = new List<string>();
        var removedEntryCount = 0;

        var packagesPropsPath = Path.Combine(targetRoot, "Directory.Packages.props");
        if (File.Exists(packagesPropsPath))
        {
            var document = await LoadXmlAsync(packagesPropsPath, preserveWhitespace: true).ConfigureAwait(false);
            var packageVersions = document
                .Descendants()
                .Where(static element => element.Name.LocalName == "PackageVersion")
                .Where(static element => IsPackageReferenceFor(element, "Microsoft.VisualStudio.ProjectSystem.SDK"))
                .ToList();

            if (packageVersions.Count > 0)
            {
                removedEntryCount += packageVersions.Count;
                foreach (var packageVersion in packageVersions)
                    packageVersion.Remove();

                foreach (var itemGroup in document.Descendants().Where(static element => element.Name.LocalName == "ItemGroup").ToList())
                {
                    if (!itemGroup.Elements().Any())
                        itemGroup.Remove();
                }

                await SaveXmlAsync(document, packagesPropsPath).ConfigureAwait(false);
                changedFiles.Add(Path.GetRelativePath(targetRepoRoot, packagesPropsPath));
            }
        }

        foreach (var projectPath in Directory.EnumerateFiles(targetRoot, "*.csproj", SearchOption.AllDirectories))
        {
            var document = await LoadXmlAsync(projectPath, preserveWhitespace: true).ConfigureAwait(false);
            var packageReferences = document
                .Descendants()
                .Where(static element => element.Name.LocalName == "PackageReference")
                .Where(static element => IsPackageReferenceFor(element, "Microsoft.VisualStudio.ProjectSystem.SDK"))
                .ToList();

            if (packageReferences.Count == 0)
                continue;

            removedEntryCount += packageReferences.Count;
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

        return removedEntryCount == 0
            ? "No Razor Microsoft.VisualStudio.ProjectSystem.SDK entries were found."
            : $"Removed {removedEntryCount} Razor Microsoft.VisualStudio.ProjectSystem.SDK entr{(removedEntryCount == 1 ? "y" : "ies")} from {changedFiles.Count} file(s): {string.Join(", ", changedFiles)}.";
    }

    private static async Task<string> RemoveXunitVersionOverridesAsync(StageContext context)
    {
        var targetRepoRoot = context.TargetRepoRoot;
        var targetRoot = context.TargetRoot;
        if (!Directory.Exists(targetRoot))
            return "No Razor target tree was found for xUnit version override cleanup.";

        var changedFiles = new List<string>();
        var removedOverrideCount = 0;

        foreach (var projectPath in Directory.EnumerateFiles(targetRoot, "*.csproj", SearchOption.AllDirectories))
        {
            var document = await LoadXmlAsync(projectPath, preserveWhitespace: true).ConfigureAwait(false);
            var packageReferencesWithOverrides = document
                .Descendants()
                .Where(static element => element.Name.LocalName == "PackageReference")
                .Where(HasXunitVersionOverride)
                .ToList();

            if (packageReferencesWithOverrides.Count == 0)
                continue;

            foreach (var packageReference in packageReferencesWithOverrides)
            {
                packageReference.Attribute("VersionOverride")?.Remove();
                removedOverrideCount++;
            }

            await SaveXmlAsync(document, projectPath).ConfigureAwait(false);
            changedFiles.Add(Path.GetRelativePath(targetRepoRoot, projectPath));
        }

        return removedOverrideCount == 0
            ? "No Razor xUnit VersionOverride attributes were found."
            : $"Removed {removedOverrideCount} Razor xUnit VersionOverride attribute(s) from {changedFiles.Count} project(s): {string.Join(", ", changedFiles)}.";
    }

    private static async Task<string> ConvertRoslynPackageReferencesAsync(StageContext context)
    {
        var targetRepoRoot = context.TargetRepoRoot;
        var targetRoot = context.TargetRoot;
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

    private static async Task<List<string>> RewriteRazorServicesPropsReferencesAsync(StageContext context)
    {
        var targetRepoRoot = context.TargetRepoRoot;
        var targetRoot = context.TargetRoot;
        var changedFiles = new List<string>();

        foreach (var path in EnumerateRazorServicesReferenceFiles(targetRoot))
        {
            var originalContent = await File.ReadAllTextAsync(path).ConfigureAwait(false);
            var updatedContent = RewriteRazorServicesPropsReferenceContent(originalContent);
            if (string.Equals(originalContent, updatedContent, StringComparison.Ordinal))
                continue;

            await File.WriteAllTextAsync(path, updatedContent).ConfigureAwait(false);
            changedFiles.Add(Path.GetRelativePath(targetRepoRoot, path));
        }

        return changedFiles;
    }

    private static IEnumerable<string> EnumerateMsBuildFiles(string root)
        => Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
            .Where(static path =>
                path.EndsWith(".props", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith(".targets", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith(".vbproj", StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<string> EnumerateRazorServicesReferenceFiles(string root)
        => Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
            .Where(static path =>
                path.EndsWith(".props", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith(".targets", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith(".vbproj", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase));

    private static string RewriteRazorServicesPropsReferenceContent(string content)
    {
        var updatedContent = RepositoryEngineeringServicesPropsPattern.Replace(
            content,
            "$(RepositoryEngineeringDir)targets\\RazorServices.props");
        updatedContent = EngTargetsServicesPropsPattern.Replace(
            updatedContent,
            match => $"eng{match.Groups["separator"].Value}targets{match.Groups["separator"].Value}RazorServices.props");
        updatedContent = PathCombineServicesPropsPattern.Replace(
            updatedContent,
            "\"eng\", \"targets\", \"RazorServices.props\"");

        return updatedContent;
    }

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

    private static bool HasXunitVersionOverride(XElement element)
        => element.Attribute("VersionOverride") is not null
            && GetPackageVersionId(element) is string packageId
            && (string.Equals(packageId, "xunit", StringComparison.OrdinalIgnoreCase)
                || packageId.StartsWith("xunit.", StringComparison.OrdinalIgnoreCase));

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

    private static async Task<JsonObject> LoadJsonObjectAsync(string path)
    {
        var content = await File.ReadAllTextAsync(path).ConfigureAwait(false);
        return JsonNode.Parse(content) as JsonObject
            ?? throw new InvalidOperationException($"The file '{path}' does not contain a root JSON object.");
    }

    private static async Task SaveJsonAsync(JsonObject jsonObject, string path)
    {
        var content = jsonObject.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine;
        await File.WriteAllTextAsync(path, content).ConfigureAwait(false);
    }

    private static readonly Regex CommonTargetsImportPattern = new(
        @"^[ \t]*<Import\s+Project=""\$\(RepositoryEngineeringDir\)targets(?:\\|/)Common\.targets""\s*/>\r?\n?",
        RegexOptions.Multiline | RegexOptions.CultureInvariant);

    private static readonly Regex RepositoryEngineeringServicesPropsPattern = new(
        Regex.Escape("$(RepositoryEngineeringDir)") + @"targets(?:\\|/)Services\.props",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex EngTargetsServicesPropsPattern = new(
        @"eng(?<separator>[\\/])targets\k<separator>Services\.props",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex PathCombineServicesPropsPattern = new(
        @"""eng""\s*,\s*""targets""\s*,\s*""Services\.props""",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex BrokeredServicesBeforeTargetsPattern = new(
        @"BeforeTargets=""GenerateBrokeredServicesPkgDef""",
        RegexOptions.CultureInvariant);

    private static readonly Regex BrokeredServicesImportPattern = new(
        @"^[ \t]*<Import\s+Project=""\$\(RepositoryEngineeringDir\)targets(?:\\|/)GenerateBrokeredServicesPkgDef\.targets""\s*/>\r?\n?",
        RegexOptions.Multiline | RegexOptions.CultureInvariant);

    private static readonly Regex ProjectOpenPattern = new(
        @"<Project[^>]*>",
        RegexOptions.CultureInvariant);

    private const string RazorSdkPackageVersion = "6.0.0-alpha.1.21072.5";

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
        string CommitRationale,
        Func<StageContext, Task<string>> ExecuteAsync);

    private sealed record ProjectCandidate(string Path, string ProjectFileName, string? ExplicitPackageId);

    private sealed record DirectoryBuildImportFile(
        string FileName,
        string ReplacementImport,
        Regex RootImportPattern,
        Regex ArcadeImportPattern);
}
