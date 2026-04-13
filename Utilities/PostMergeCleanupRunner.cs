using System.Text;
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
            "disable-empty-razorextension-dependencies-pkgdef",
            "Disable pkgdef generation for Razor's dependencies-only VSIX project when it produces no pkgdef entries.",
            "Disable empty Razor dependencies pkgdef generation",
            "Microsoft.VisualStudio.RazorExtension.Dependencies is a deployment helper project and does not contribute any PkgDef items in the merged Roslyn build, so leaving GeneratePkgDefFile enabled only trips Roslyn's shared GeneratePkgDef validation.",
            DisableEmptyRazorDependenciesPkgDefAsync),
        new(
            "rewrite-directory-build-imports",
            "Rewrite Razor Directory.Build.props/targets to import the repo-root Directory.Build files.",
            "Rewrite Razor Directory.Build imports",
            "Once Razor is nested under src\\Razor, its Directory.Build files should import the repo-root Directory.Build files so the merged tree inherits Roslyn's central build behavior.",
            RewriteDirectoryBuildImportsAsync),
        new(
            "overlay-razor-globalconfigs",
            "Copy Razor globalconfigs directly into src\\Razor and keep only Razor-specific analyzer settings.",
            "Overlay Razor globalconfigs",
            "After the merge, Razor should import local copies of its globalconfigs from src\\Razor so Razor-specific analyzer settings are preserved, while trimming entries already supplied by Roslyn avoids duplicate key warnings.",
            OverlayRazorGlobalConfigsAsync),
        new(
            "remove-razor-repository-metadata-overrides",
            "Remove Razor-local PackageProjectUrl and RepositoryUrl overrides so Roslyn's repo metadata stays authoritative.",
            "Remove Razor repository metadata overrides",
            "Inside the merged Roslyn tree, Razor packages and projects should inherit Roslyn's repository metadata instead of overriding it back to https://github.com/dotnet/razor, which can break shared packaging validation.",
            RemoveRazorRepositoryMetadataOverridesAsync),
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
            "normalize-razor-benchmarkdotnet-apis",
            "Rewrite Razor microbenchmark runners to avoid BenchmarkDotNet APIs newer than Roslyn's shared package version.",
            "Normalize Razor BenchmarkDotNet runner APIs",
            "Razor's microbenchmark runner programs should compile against Roslyn's centrally managed BenchmarkDotNet package instead of depending on newer API surface that Roslyn does not carry.",
            NormalizeRazorBenchmarkDotNetApisAsync),
        new(
            "normalize-razor-unit-test-detection",
            "Keep real Razor test projects on Roslyn's xUnit infrastructure and normalize their output naming for Roslyn's test runner.",
            "Align Razor test infrastructure with Roslyn",
            "Razor test projects in the merged Roslyn tree should get Roslyn's required test utility references and produce *.UnitTests.dll outputs so they match Roslyn's test-runner conventions, while the microbenchmark generator helper should not be treated as a Roslyn UnitTests assembly.",
            NormalizeRazorUnitTestDetectionAsync),
        new(
            "normalize-razor-moq-apis",
            "Rewrite Razor test Moq usage to stay compatible with Roslyn's shared Moq package version.",
            "Normalize Razor Moq APIs",
            "Razor test helpers and test code in the merged Roslyn tree should use Moq APIs that exist in Roslyn's shared version, instead of relying on newer Mock.Of(..., MockBehavior.Strict) overloads that Roslyn does not carry.",
            NormalizeRazorMoqApisAsync),
        new(
            "remove-roslyn-diagnostics-analyzers",
            "Remove Roslyn.Diagnostics.Analyzers package references from Razor Directory.Build.props files.",
            "Remove Roslyn.Diagnostics.Analyzers refs",
            "Roslyn already manages Roslyn.Diagnostics.Analyzers centrally, so the merged Razor tree should not add duplicate local analyzer references.",
            RemoveRoslynDiagnosticsAnalyzersAsync),
        new(
            "remove-xunit-execution-package-refs",
            "Remove redundant xunit.extensibility.execution refs from Razor unit tests while preserving helper projects that still use xUnit discovery APIs.",
            "Normalize Razor xunit.extensibility.execution refs",
            "Roslyn's XUnit.targets already adds xunit.extensibility.execution for true unit test projects, but helper libraries like Microsoft.AspNetCore.Razor.Test.Common still need an explicit reference because they are not marked as IsUnitTestProject.",
            RemoveXunitExecutionPackageReferencesAsync),
        new(
            "remove-projectsystem-sdk-package-refs",
            "Replace Razor's Microsoft.VisualStudio.ProjectSystem.SDK usage with Roslyn's shared Microsoft.VisualStudio.ProjectSystem package.",
            "Normalize Razor ProjectSystem package refs",
            "Microsoft.VisualStudio.LanguageServices.Razor still uses Microsoft.VisualStudio.ProjectSystem APIs, but in the merged Roslyn tree it should consume Roslyn's shared Microsoft.VisualStudio.ProjectSystem package instead of keeping Razor's separate ProjectSystem.SDK reference.",
            RemoveProjectSystemSdkPackageReferencesAsync),
        new(
            "normalize-razor-projectsystem-apis",
            "Remove Razor's unused dependency on newer CPS subscription service APIs that Roslyn's shared ProjectSystem package does not provide.",
            "Normalize Razor ProjectSystem APIs",
            "The merged Roslyn tree already carries an older Microsoft.VisualStudio.ProjectSystem package, so Razor should drop its unused IActiveConfigurationGroupSubscriptionService plumbing instead of depending on newer CPS API surface that Roslyn does not expose.",
            NormalizeRazorProjectSystemApisAsync),
        new(
            "normalize-razor-vs-service-lookups",
            "Rewrite Razor Visual Studio service lookups to use Roslyn's existing Shell.Package helper pattern.",
            "Normalize Razor VS service lookups",
            "Inside the merged Roslyn tree, Razor should use the same Shell.Package.GetGlobalService pattern as Roslyn's existing Visual Studio code instead of relying on an ambiguous Package.GetGlobalService reference.",
            NormalizeRazorVisualStudioServiceLookupsAsync),
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

    private static async Task<string> DisableEmptyRazorDependenciesPkgDefAsync(StageContext context)
    {
        var targetRepoRoot = context.TargetRepoRoot;
        var projectPath = Directory
            .EnumerateFiles(targetRepoRoot, "Microsoft.VisualStudio.RazorExtension.Dependencies.csproj", SearchOption.AllDirectories)
            .FirstOrDefault();

        if (string.IsNullOrWhiteSpace(projectPath))
            return "No RazorExtension.Dependencies project was found for pkgdef cleanup.";

        var originalContent = await File.ReadAllTextAsync(projectPath).ConfigureAwait(false);
        var updatedContent = GeneratePkgDefFileTruePattern.Replace(
            originalContent,
            "<GeneratePkgDefFile>false</GeneratePkgDefFile>",
            1);

        if (string.Equals(originalContent, updatedContent, StringComparison.Ordinal))
            return "RazorExtension.Dependencies pkgdef generation was already disabled or did not need cleanup.";

        await WriteTextPreservingUtf8BomAsync(projectPath, updatedContent, templatePath: projectPath).ConfigureAwait(false);
        return $"Disabled empty pkgdef generation in '{Path.GetRelativePath(targetRepoRoot, projectPath)}'.";
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

    private static async Task<string> OverlayRazorGlobalConfigsAsync(StageContext context)
    {
        var targetRepoRoot = context.TargetRepoRoot;
        var targetRoot = context.TargetRoot;
        var sourceGlobalConfigsRoot = Path.Combine(context.State.SourceCloneDirectory, "eng", "config", "globalconfigs");
        var directoryBuildTargetsPath = Path.Combine(targetRoot, "Directory.Build.targets");
        if (!Directory.Exists(sourceGlobalConfigsRoot))
            return "No Razor globalconfigs directory was found for overlay cleanup.";

        var targetGlobalConfigsRoot = targetRoot;
        var inheritedRoslynKeys = await CollectGlobalConfigKeysAsync(
            Directory.EnumerateFiles(
                Path.Combine(targetRepoRoot, "eng", "config", "globalconfigs"),
                "*.globalconfig",
                SearchOption.TopDirectoryOnly)).ConfigureAwait(false);
        var retainedOverlayKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var updatedOverlayFiles = new List<string>();
        foreach (var fileName in RazorGlobalConfigFileNames)
        {
            var sourcePath = Path.Combine(sourceGlobalConfigsRoot, fileName);
            if (!File.Exists(sourcePath))
                continue;

            var overlayContent = await BuildRazorGlobalConfigOverlayContentAsync(
                sourcePath,
                inheritedRoslynKeys,
                retainedOverlayKeys).ConfigureAwait(false);
            var targetPath = Path.Combine(targetGlobalConfigsRoot, fileName);
            var existingContent = File.Exists(targetPath)
                ? await File.ReadAllTextAsync(targetPath).ConfigureAwait(false)
                : null;

            if (string.Equals(existingContent, overlayContent, StringComparison.Ordinal))
                continue;

            await WriteTextPreservingUtf8BomAsync(targetPath, overlayContent, templatePath: sourcePath).ConfigureAwait(false);
            await GitRunner.RunGitAsync(
                targetRepoRoot,
                "add",
                "--",
                Path.GetRelativePath(targetRepoRoot, targetPath)).ConfigureAwait(false);
            updatedOverlayFiles.Add(Path.GetRelativePath(targetRepoRoot, targetPath));
        }

        var removedLegacyOverlayFiles = RemoveLegacyRazorGlobalConfigOverlayFiles(targetRoot);

        var importsUpdated = false;
        if (File.Exists(directoryBuildTargetsPath))
        {
            var originalContent = await File.ReadAllTextAsync(directoryBuildTargetsPath).ConfigureAwait(false);
            var updatedContent = RewriteRazorGlobalConfigImportContent(originalContent);
            if (!string.Equals(originalContent, updatedContent, StringComparison.Ordinal))
            {
                await WriteTextPreservingUtf8BomAsync(directoryBuildTargetsPath, updatedContent, templatePath: directoryBuildTargetsPath).ConfigureAwait(false);
                importsUpdated = true;
            }
        }

        if (updatedOverlayFiles.Count == 0 && removedLegacyOverlayFiles.Count == 0 && !importsUpdated)
            return "No Razor globalconfig overlay changes were needed.";

        var summaryParts = new List<string>();
        if (updatedOverlayFiles.Count > 0)
        {
            summaryParts.Add(
                $"Copied or updated {updatedOverlayFiles.Count} Razor-local globalconfig file(s): {string.Join(", ", updatedOverlayFiles)}.");
        }

        if (importsUpdated)
        {
            summaryParts.Add(
                $"Updated '{Path.GetRelativePath(targetRepoRoot, directoryBuildTargetsPath)}' to import the Razor-local globalconfig overlay.");
        }

        if (removedLegacyOverlayFiles.Count > 0)
        {
            summaryParts.Add(
                $"Removed {removedLegacyOverlayFiles.Count} legacy Razor overlay file(s): {string.Join(", ", removedLegacyOverlayFiles)}.");
        }

        return string.Join(" ", summaryParts);
    }

    private static async Task<string> RemoveRazorRepositoryMetadataOverridesAsync(StageContext context)
    {
        var targetRepoRoot = context.TargetRepoRoot;
        var targetRoot = context.TargetRoot;
        var changedFiles = new List<string>();

        var candidateFiles = new[]
        {
            Path.Combine(targetRoot, "Directory.Build.props"),
            Path.Combine(targetRoot, "src", "Compiler", "Directory.Build.props"),
        };

        foreach (var path in candidateFiles)
        {
            if (!File.Exists(path))
                continue;

            var originalContent = await File.ReadAllTextAsync(path).ConfigureAwait(false);
            var updatedContent = RazorPackageProjectUrlPattern.Replace(originalContent, string.Empty);
            updatedContent = RazorPackageProjectUrlCommentPattern.Replace(updatedContent, string.Empty);
            updatedContent = RazorRepositoryUrlPattern.Replace(updatedContent, string.Empty);

            if (string.Equals(originalContent, updatedContent, StringComparison.Ordinal))
                continue;

            await WriteTextPreservingUtf8BomAsync(path, updatedContent, templatePath: path).ConfigureAwait(false);
            changedFiles.Add(Path.GetRelativePath(targetRepoRoot, path));
        }

        return changedFiles.Count == 0
            ? "No Razor repository metadata overrides were found."
            : $"Removed Razor-specific PackageProjectUrl/RepositoryUrl overrides from {changedFiles.Count} file(s): {string.Join(", ", changedFiles)}.";
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

    private static async Task<string> NormalizeRazorBenchmarkDotNetApisAsync(StageContext context)
    {
        var targetRepoRoot = context.TargetRepoRoot;
        var targetRoot = context.TargetRoot;
        var changedFiles = new List<string>();

        var candidateFiles = new[]
        {
            Path.Combine(targetRoot, "src", "Compiler", "perf", "Microbenchmarks", "Program.cs"),
            Path.Combine(targetRoot, "src", "Razor", "benchmarks", "Microsoft.AspNetCore.Razor.Microbenchmarks", "Program.cs"),
        };

        foreach (var path in candidateFiles)
        {
            if (!File.Exists(path))
                continue;

            var originalContent = await File.ReadAllTextAsync(path).ConfigureAwait(false);
            var updatedContent = BenchmarkDotNetBuildTimeoutPattern.Replace(originalContent, string.Empty);
            updatedContent = BenchmarkDotNetNetCoreApp80JobPattern.Replace(
                updatedContent,
                "${indent}.AddJob(Job.Default.DontEnforcePowerPlan()) // use the current runtime with Roslyn's shared BenchmarkDotNet defaults" + Environment.NewLine);
            updatedContent = BenchmarkDotNetDisassemblySyntaxPattern.Replace(updatedContent, string.Empty);

            if (string.Equals(originalContent, updatedContent, StringComparison.Ordinal))
                continue;

            await WriteTextPreservingUtf8BomAsync(path, updatedContent, templatePath: path).ConfigureAwait(false);
            changedFiles.Add(Path.GetRelativePath(targetRepoRoot, path));
        }

        return changedFiles.Count == 0
            ? "No Razor BenchmarkDotNet compatibility rewrites were needed."
            : $"Rewrote Razor BenchmarkDotNet runner configuration in {changedFiles.Count} file(s) to stay compatible with Roslyn's shared package version: {string.Join(", ", changedFiles)}.";
    }

    private static async Task<string> NormalizeRazorUnitTestDetectionAsync(StageContext context)
    {
        var targetRepoRoot = context.TargetRepoRoot;
        var targetRoot = context.TargetRoot;
        var changedFiles = new List<string>();

        var directoryBuildPropsPath = Path.Combine(targetRoot, "Directory.Build.props");
        if (File.Exists(directoryBuildPropsPath))
        {
            var originalContent = await File.ReadAllTextAsync(directoryBuildPropsPath).ConfigureAwait(false);
            var updatedContent = RazorUnitTestPropertyGroupPattern.Replace(originalContent, RazorUnitTestPropertyGroupBlock, 1);

            if (!string.Equals(originalContent, updatedContent, StringComparison.Ordinal))
            {
                await WriteTextPreservingUtf8BomAsync(directoryBuildPropsPath, updatedContent, templatePath: directoryBuildPropsPath).ConfigureAwait(false);
                changedFiles.Add(Path.GetRelativePath(targetRepoRoot, directoryBuildPropsPath));
            }
        }

        var analyzerTestProjectPath = Path.Combine(
            targetRoot,
            "src",
            "Analyzers",
            "Razor.Diagnostics.Analyzers.Test",
            "Razor.Diagnostics.Analyzers.Test.csproj");
        if (File.Exists(analyzerTestProjectPath))
        {
            var originalContent = await File.ReadAllTextAsync(analyzerTestProjectPath).ConfigureAwait(false);
            var updatedContent = EnsureProjectReferenceAfterPackageReference(
                originalContent,
                "Microsoft.CodeAnalysis.CSharp.Analyzer.Testing",
                @"..\..\..\..\Compilers\Test\Core\Microsoft.CodeAnalysis.Test.Utilities.csproj");

            if (!string.Equals(originalContent, updatedContent, StringComparison.Ordinal))
            {
                await WriteTextPreservingUtf8BomAsync(analyzerTestProjectPath, updatedContent, templatePath: analyzerTestProjectPath).ConfigureAwait(false);
                changedFiles.Add(Path.GetRelativePath(targetRepoRoot, analyzerTestProjectPath));
            }
        }

        var microbenchmarkGeneratorProjectPath = Path.Combine(
            targetRoot,
            "src",
            "Compiler",
            "perf",
            "Microsoft.AspNetCore.Razor.Microbenchmarks.Generator",
            "Microsoft.AspNetCore.Razor.Microbenchmarks.Generator.csproj");
        if (File.Exists(microbenchmarkGeneratorProjectPath))
        {
            var originalContent = await File.ReadAllTextAsync(microbenchmarkGeneratorProjectPath).ConfigureAwait(false);
            var updatedContent = EnsureProjectReferenceAfterPackageReference(
                originalContent,
                "System.Security.Cryptography.Xml",
                @"..\..\..\..\..\Compilers\Test\Core\Microsoft.CodeAnalysis.Test.Utilities.csproj");
            updatedContent = SetBooleanPropertyValue(updatedContent, "IsTestProject", true);
            updatedContent = SetBooleanPropertyValue(updatedContent, "IsUnitTestProject", false);

            if (!string.Equals(originalContent, updatedContent, StringComparison.Ordinal))
            {
                await WriteTextPreservingUtf8BomAsync(microbenchmarkGeneratorProjectPath, updatedContent, templatePath: microbenchmarkGeneratorProjectPath).ConfigureAwait(false);
                changedFiles.Add(Path.GetRelativePath(targetRepoRoot, microbenchmarkGeneratorProjectPath));
            }
        }

        return changedFiles.Count == 0
            ? "No Razor unit test detection cleanup was needed."
            : $"Normalized Razor unit test detection in {changedFiles.Count} file(s): {string.Join(", ", changedFiles)}.";
    }

    private static async Task<string> NormalizeRazorMoqApisAsync(StageContext context)
    {
        var targetRepoRoot = context.TargetRepoRoot;
        var targetRoot = context.TargetRoot;
        var changedFiles = new List<string>();

        foreach (var path in Directory.EnumerateFiles(targetRoot, "*.cs", SearchOption.AllDirectories))
        {
            var originalContent = await File.ReadAllTextAsync(path).ConfigureAwait(false);
            var updatedContent = NormalizeStrictMoqCalls(originalContent);

            if (string.Equals(originalContent, updatedContent, StringComparison.Ordinal))
                continue;

            await WriteTextPreservingUtf8BomAsync(path, updatedContent, templatePath: path).ConfigureAwait(false);
            changedFiles.Add(Path.GetRelativePath(targetRepoRoot, path));
        }

        return changedFiles.Count == 0
            ? "No Razor Moq compatibility rewrites were needed."
            : $"Normalized Razor Moq API usage in {changedFiles.Count} file(s) to stay compatible with Roslyn's shared package version: {string.Join(", ", changedFiles)}.";
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
        var sourceRazorRoot = Path.Combine(context.State.SourceCloneDirectory, "src", "Razor");
        var changedFiles = new List<string>();
        var removedReferenceCount = 0;
        var restoredReferenceCount = 0;
        var sourceProjectsWithExplicitReference = Directory.Exists(sourceRazorRoot)
            ? await GetSourceProjectsWithExplicitPackageReferenceAsync(sourceRazorRoot, "xunit.extensibility.execution").ConfigureAwait(false)
            : [];

        foreach (var projectPath in Directory.EnumerateFiles(targetRoot, "*.csproj", SearchOption.AllDirectories))
        {
            var document = await LoadXmlAsync(projectPath, preserveWhitespace: true).ConfigureAwait(false);
            var packageReferences = document
                .Descendants()
                .Where(static element => element.Name.LocalName == "PackageReference")
                .Where(static element => IsPackageReferenceFor(element, "xunit.extensibility.execution"))
                .ToList();

            var relativeProjectPath = NormalizeRelativePath(Path.GetRelativePath(targetRoot, projectPath));
            var shouldKeepExplicitReference =
                sourceProjectsWithExplicitReference.Contains(relativeProjectPath)
                && !IsRoslynUnitTestProject(projectPath, document);

            if (shouldKeepExplicitReference)
            {
                var originalContent = await File.ReadAllTextAsync(projectPath).ConfigureAwait(false);
                var updatedContent = EnsurePackageReferenceAfterPackageReference(
                    originalContent,
                    "xunit.analyzers",
                    "xunit.extensibility.execution");

                if (!string.Equals(originalContent, updatedContent, StringComparison.Ordinal))
                {
                    if (packageReferences.Count == 0)
                        restoredReferenceCount++;

                    await WriteTextPreservingUtf8BomAsync(projectPath, updatedContent, templatePath: projectPath).ConfigureAwait(false);
                    changedFiles.Add(Path.GetRelativePath(targetRepoRoot, projectPath));
                }

                continue;
            }

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

        return removedReferenceCount == 0 && restoredReferenceCount == 0 && changedFiles.Count == 0
            ? "No xunit.extensibility.execution cleanup changes were needed in Razor projects."
            : $"Normalized xunit.extensibility.execution references in {changedFiles.Count} Razor project(s): removed {removedReferenceCount} redundant reference(s) and restored {restoredReferenceCount} required reference(s): {string.Join(", ", changedFiles)}.";
    }

    private static async Task<string> RemoveProjectSystemSdkPackageReferencesAsync(StageContext context)
    {
        var targetRepoRoot = context.TargetRepoRoot;
        var targetRoot = context.TargetRoot;
        var sourceRazorRoot = Path.Combine(context.State.SourceCloneDirectory, "src", "Razor");
        if (!Directory.Exists(targetRoot))
            return "No Razor target tree was found for ProjectSystem.SDK cleanup.";

        var changedFiles = new List<string>();
        var normalizedEntryCount = 0;
        var sourceProjectsWithSdkReference = Directory.Exists(sourceRazorRoot)
            ? await GetSourceProjectsWithExplicitPackageReferenceAsync(sourceRazorRoot, "Microsoft.VisualStudio.ProjectSystem.SDK").ConfigureAwait(false)
            : [];

        var packagesPropsPath = Path.Combine(targetRoot, "Directory.Packages.props");
        if (File.Exists(packagesPropsPath))
        {
            var originalContent = await File.ReadAllTextAsync(packagesPropsPath).ConfigureAwait(false);
            var updatedContent = RemovePackageVersionEntries(originalContent, "Microsoft.VisualStudio.ProjectSystem.SDK");
            if (!string.Equals(originalContent, updatedContent, StringComparison.Ordinal))
            {
                normalizedEntryCount++;
                await WriteTextPreservingUtf8BomAsync(packagesPropsPath, updatedContent, templatePath: packagesPropsPath).ConfigureAwait(false);
                changedFiles.Add(Path.GetRelativePath(targetRepoRoot, packagesPropsPath));
            }
        }

        foreach (var projectPath in Directory.EnumerateFiles(targetRoot, "*.csproj", SearchOption.AllDirectories))
        {
            var originalContent = await File.ReadAllTextAsync(projectPath).ConfigureAwait(false);
            var updatedContent = RemovePackageReferenceEntries(originalContent, "Microsoft.VisualStudio.ProjectSystem.SDK");

            var relativeProjectPath = NormalizeRelativePath(Path.GetRelativePath(targetRoot, projectPath));
            if (sourceProjectsWithSdkReference.Contains(relativeProjectPath))
            {
                updatedContent = EnsurePackageReferenceAfterPackageReference(
                    updatedContent,
                    "Microsoft.VisualStudio.Editor",
                    "Microsoft.VisualStudio.ProjectSystem");
            }

            if (string.Equals(originalContent, updatedContent, StringComparison.Ordinal))
                continue;

            normalizedEntryCount++;
            await WriteTextPreservingUtf8BomAsync(projectPath, updatedContent, templatePath: projectPath).ConfigureAwait(false);
            changedFiles.Add(Path.GetRelativePath(targetRepoRoot, projectPath));
        }

        return normalizedEntryCount == 0
            ? "No Razor ProjectSystem package cleanup changes were needed."
            : $"Normalized Razor ProjectSystem package references in {changedFiles.Count} file(s): {string.Join(", ", changedFiles)}.";
    }

    private static async Task<string> NormalizeRazorProjectSystemApisAsync(StageContext context)
    {
        var targetRepoRoot = context.TargetRepoRoot;
        var targetRoot = context.TargetRoot;
        var changedFiles = new List<string>();

        var candidateFiles = new[]
        {
            Path.Combine(targetRoot, "src", "Razor", "src", "Microsoft.VisualStudio.LanguageServices.Razor", "ProjectSystem", "IUnconfiguredProjectCommonServices.cs"),
            Path.Combine(targetRoot, "src", "Razor", "src", "Microsoft.VisualStudio.LanguageServices.Razor", "ProjectSystem", "UnconfiguredProjectCommonServices.cs"),
            Path.Combine(targetRoot, "src", "Razor", "test", "Microsoft.VisualStudio.LanguageServices.Razor.Test", "ProjectSystem", "TestProjectSystemServices.cs"),
        };

        foreach (var path in candidateFiles)
        {
            if (!File.Exists(path))
                continue;

            var originalContent = await File.ReadAllTextAsync(path).ConfigureAwait(false);
            var updatedContent = RemoveUnusedProjectSystemSubscriptionService(originalContent);

            if (string.Equals(originalContent, updatedContent, StringComparison.Ordinal))
                continue;

            await WriteTextPreservingUtf8BomAsync(path, updatedContent, templatePath: path).ConfigureAwait(false);
            changedFiles.Add(Path.GetRelativePath(targetRepoRoot, path));
        }

        return changedFiles.Count == 0
            ? "No Razor ProjectSystem API compatibility rewrites were needed."
            : $"Removed Razor's unused newer CPS subscription service dependency from {changedFiles.Count} file(s): {string.Join(", ", changedFiles)}.";
    }

    private static async Task<string> NormalizeRazorVisualStudioServiceLookupsAsync(StageContext context)
    {
        var targetRepoRoot = context.TargetRepoRoot;
        var targetRoot = context.TargetRoot;
        var changedFiles = new List<string>();

        foreach (var path in Directory.EnumerateFiles(targetRoot, "*.cs", SearchOption.AllDirectories))
        {
            var originalContent = await File.ReadAllTextAsync(path).ConfigureAwait(false);
            if (!originalContent.Contains("Package.GetGlobalService(", StringComparison.Ordinal))
                continue;

            var updatedContent = originalContent.Replace(
                "Package.GetGlobalService(",
                "Shell.Package.GetGlobalService(",
                StringComparison.Ordinal);

            if (string.Equals(originalContent, updatedContent, StringComparison.Ordinal))
                continue;

            await WriteTextPreservingUtf8BomAsync(path, updatedContent, templatePath: path).ConfigureAwait(false);
            changedFiles.Add(Path.GetRelativePath(targetRepoRoot, path));
        }

        return changedFiles.Count == 0
            ? "No Razor Visual Studio service lookup rewrites were needed."
            : $"Normalized Razor Visual Studio service lookups in {changedFiles.Count} file(s): {string.Join(", ", changedFiles)}.";
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

    private static bool IsRoslynUnitTestProject(string projectPath, XDocument document)
    {
        var explicitIsUnitTestValues = document
            .Descendants()
            .Where(static element => element.Name.LocalName == "IsUnitTestProject")
            .Select(static element => element.Value.Trim())
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .ToList();

        if (explicitIsUnitTestValues.Any(static value => string.Equals(value, "false", StringComparison.OrdinalIgnoreCase)))
            return false;

        if (explicitIsUnitTestValues.Any(static value => string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)))
            return true;

        var projectName = Path.GetFileNameWithoutExtension(projectPath);
        return projectName.EndsWith(".Test", StringComparison.OrdinalIgnoreCase)
            || projectName.EndsWith(".Tests", StringComparison.OrdinalIgnoreCase)
            || projectName.EndsWith(".UnitTests", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<HashSet<string>> GetSourceProjectsWithExplicitPackageReferenceAsync(string sourceRoot, string packageId)
    {
        var projects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var projectPath in Directory.EnumerateFiles(sourceRoot, "*.csproj", SearchOption.AllDirectories))
        {
            var document = await LoadXmlAsync(projectPath).ConfigureAwait(false);
            if (document.Descendants().Any(element => element.Name.LocalName == "PackageReference" && IsPackageReferenceFor(element, packageId)))
                projects.Add(NormalizeRelativePath(Path.GetRelativePath(sourceRoot, projectPath)));
        }

        return projects;
    }

    private static string NormalizeRelativePath(string path)
        => path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);

    private static XElement CreateRootDirectoryImport(XNamespace xmlNamespace, string fileName)
        => new(
            xmlNamespace + "Import",
            new XAttribute("Project", $@"$([MSBuild]::GetPathOfFileAbove('{fileName}', '$(MSBuildThisFileDirectory)../'))"));

    private static async Task<string> BuildRazorGlobalConfigOverlayContentAsync(
        string sourcePath,
        ISet<string> inheritedRoslynKeys,
        ISet<string> retainedOverlayKeys)
    {
        var sourceLines = await File.ReadAllLinesAsync(sourcePath).ConfigureAwait(false);
        var retainedBlocks = new List<string>();
        var pendingComments = new List<string>();

        foreach (var rawLine in sourceLines)
        {
            var line = rawLine.TrimEnd();
            if (string.IsNullOrWhiteSpace(line))
                continue;

            if (TryParseGlobalConfigSetting(line, out var key, out _))
            {
                if (!inheritedRoslynKeys.Contains(key) && retainedOverlayKeys.Add(key))
                {
                    var blockLines = pendingComments
                        .Where(static comment => !string.IsNullOrWhiteSpace(comment))
                        .ToList();
                    blockLines.Add(line.TrimStart('\uFEFF'));
                    retainedBlocks.Add(string.Join(Environment.NewLine, blockLines));
                }

                pendingComments.Clear();
                continue;
            }

            if (line.TrimStart().StartsWith("#", StringComparison.Ordinal))
            {
                pendingComments.Add(line.TrimStart('\uFEFF'));
                continue;
            }

            pendingComments.Clear();
        }

        var builder = new StringBuilder();
        builder.AppendLine("is_global = true");

        if (retainedBlocks.Count > 0)
        {
            builder.AppendLine();
            builder.Append(string.Join($"{Environment.NewLine}{Environment.NewLine}", retainedBlocks));
            builder.AppendLine();
        }

        return builder.ToString();
    }

    private static async Task<HashSet<string>> CollectGlobalConfigKeysAsync(IEnumerable<string> paths)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in paths)
        {
            if (!File.Exists(path))
                continue;

            foreach (var line in await File.ReadAllLinesAsync(path).ConfigureAwait(false))
            {
                if (TryParseGlobalConfigSetting(line, out var key, out _))
                    keys.Add(key);
            }
        }

        return keys;
    }

    private static bool TryParseGlobalConfigSetting(string line, out string key, out string value)
    {
        var match = GlobalConfigSettingPattern.Match(line);
        if (!match.Success)
        {
            key = string.Empty;
            value = string.Empty;
            return false;
        }

        key = match.Groups["key"].Value.Trim().TrimStart('\uFEFF');
        value = match.Groups["value"].Value.Trim();
        return !string.Equals(key, "is_global", StringComparison.OrdinalIgnoreCase);
    }

    private static List<string> RemoveLegacyRazorGlobalConfigOverlayFiles(string targetRoot)
    {
        var removedFiles = new List<string>();
        var legacyRoot = Path.Combine(targetRoot, "eng", "config", "globalconfigs");
        if (!Directory.Exists(legacyRoot))
            return removedFiles;

        foreach (var fileName in RazorGlobalConfigFileNames)
        {
            var legacyPath = Path.Combine(legacyRoot, fileName);
            if (!File.Exists(legacyPath))
                continue;

            File.Delete(legacyPath);
            removedFiles.Add(Path.GetRelativePath(targetRoot, legacyPath));
        }

        DeleteDirectoryIfEmpty(legacyRoot);
        DeleteDirectoryIfEmpty(Path.Combine(targetRoot, "eng", "config"));
        DeleteDirectoryIfEmpty(Path.Combine(targetRoot, "eng"));
        return removedFiles;
    }

    private static void DeleteDirectoryIfEmpty(string path)
    {
        if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
            Directory.Delete(path);
    }

    private static string EnsureProjectReferenceAfterPackageReference(string content, string packageId, string projectReferencePath)
    {
        content = NormalizeAdjacentMsBuildItemFormatting(content);

        var canonicalProjectReferenceLine = $"    <ProjectReference Include=\"{projectReferencePath}\" />{Environment.NewLine}";
        var existingProjectReferencePattern = new Regex(
            $@"^[ \t]*<ProjectReference Include=""{Regex.Escape(projectReferencePath)}""\s*/>[ \t]*\r?\n?",
            RegexOptions.Multiline | RegexOptions.CultureInvariant);

        if (existingProjectReferencePattern.IsMatch(content))
            return existingProjectReferencePattern.Replace(content, canonicalProjectReferenceLine, 1);

        var pattern = new Regex(
            $@"^(?<indent>[ \t]*)<PackageReference Include=""{Regex.Escape(packageId)}""(?:\s+[^>]*)?/>\r?\n",
            RegexOptions.Multiline | RegexOptions.CultureInvariant);

        return pattern.Replace(
            content,
            match => $"{match.Value}{canonicalProjectReferenceLine}",
            1);
    }

    private static string EnsurePackageReferenceAfterPackageReference(string content, string packageId, string requiredPackageId)
    {
        content = NormalizeAdjacentMsBuildItemFormatting(content);

        var existingPackageReferencePattern = new Regex(
            $@"^[ \t]*<PackageReference Include=""{Regex.Escape(requiredPackageId)}""(?:\s+[^>]*)?/>\s*$",
            RegexOptions.Multiline | RegexOptions.CultureInvariant);
        if (existingPackageReferencePattern.IsMatch(content))
            return content;

        var pattern = new Regex(
            $@"^(?<indent>[ \t]*)<PackageReference Include=""{Regex.Escape(packageId)}""(?:\s+[^>]*)?/>\r?\n",
            RegexOptions.Multiline | RegexOptions.CultureInvariant);

        return pattern.Replace(
            content,
            match => $"{match.Value}{match.Groups["indent"].Value}<PackageReference Include=\"{requiredPackageId}\" />{Environment.NewLine}",
            1);
    }

    private static string RemovePackageReferenceEntries(string content, string packageId)
    {
        var pattern = new Regex(
            $@"^[ \t]*<PackageReference Include=""{Regex.Escape(packageId)}""(?:\s+[^>]*)?/>\r?\n?",
            RegexOptions.Multiline | RegexOptions.CultureInvariant);
        return pattern.Replace(content, string.Empty);
    }

    private static string RemovePackageVersionEntries(string content, string packageId)
    {
        var pattern = new Regex(
            $@"^[ \t]*<PackageVersion Include=""{Regex.Escape(packageId)}""(?:\s+[^>]*)?/>\r?\n?",
            RegexOptions.Multiline | RegexOptions.CultureInvariant);
        return pattern.Replace(content, string.Empty);
    }

    private static string NormalizeStrictMoqCalls(string content)
    {
        content = StrictMoqOfWithPredicatePattern.Replace(
            content,
            "global::Microsoft.AspNetCore.Razor.Test.Common.StrictMock.Of<${type}>(${predicate})");
        content = StrictMoqOfPattern.Replace(
            content,
            "global::Microsoft.AspNetCore.Razor.Test.Common.StrictMock.Of<${type}>()");

        if (content.Contains("public static class StrictMock", StringComparison.Ordinal))
        {
            content = StrictMockNoPredicateImplementationPattern.Replace(
                content,
                "        => new Mock<T>(MockBehavior.Strict).Object;");
            content = StrictMockPredicateImplementationPattern.Replace(
                content,
                "        => Mock.Of(predicate);");
        }

        return content;
    }

    private static string RemoveUnusedProjectSystemSubscriptionService(string content)
    {
        content = content.Replace(
            "    IActiveConfigurationGroupSubscriptionService ActiveConfigurationGroupSubscriptionService { get; }" + Environment.NewLine,
            string.Empty,
            StringComparison.Ordinal);

        content = content.Replace(
            "        IProjectFaultHandlerService faultHandlerService," + Environment.NewLine +
            "        IActiveConfigurationGroupSubscriptionService activeConfigurationGroupSubscriptionService)" + Environment.NewLine,
            "        IProjectFaultHandlerService faultHandlerService)" + Environment.NewLine,
            StringComparison.Ordinal);

        content = ActiveConfigurationGroupNullCheckPattern.Replace(content, string.Empty);
        content = ActiveConfigurationGroupAssignmentPattern.Replace(content, string.Empty);
        content = ActiveConfigurationGroupPropertyPattern.Replace(content, string.Empty);
        content = TestActiveConfigurationGroupInitializationPattern.Replace(content, string.Empty);
        content = TestActiveConfigurationGroupPropertyPattern.Replace(content, string.Empty);
        content = TestActiveConfigurationGroupInterfaceImplementationPattern.Replace(content, string.Empty);
        content = TestActiveConfigurationGroupClassPattern.Replace(content, string.Empty);

        return content;
    }

    private static string NormalizeAdjacentMsBuildItemFormatting(string content)
    {
        string previous;
        do
        {
            previous = content;
            content = AdjacentMsBuildItemPattern.Replace(
                content,
                match => $"{match.Groups["indent"].Value}{match.Groups["first"].Value}{Environment.NewLine}{match.Groups["indent"].Value}{match.Groups["second"].Value}");
        }
        while (!string.Equals(previous, content, StringComparison.Ordinal));

        return content;
    }

    private static string SetBooleanPropertyValue(string content, string propertyName, bool value)
    {
        var pattern = new Regex(
            $@"^(?<indent>[ \t]*)<{Regex.Escape(propertyName)}>\s*(?:true|false)\s*</{Regex.Escape(propertyName)}>\s*$",
            RegexOptions.Multiline | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        return pattern.Replace(
            content,
            $"    <{propertyName}>{value.ToString().ToLowerInvariant()}</{propertyName}>",
            1);
    }

    private static string RewriteRazorGlobalConfigImportContent(string content)
        => GlobalAnalyzerConfigItemGroupPattern.IsMatch(content)
            ? GlobalAnalyzerConfigItemGroupPattern.Replace(content, $"$1{RazorGlobalConfigItemGroupBlock}", 1)
            : content;

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

    private static async Task WriteTextPreservingUtf8BomAsync(string path, string content, string templatePath)
    {
        var emitBom = File.Exists(templatePath) && HasUtf8Bom(templatePath);
        await File.WriteAllTextAsync(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: emitBom)).ConfigureAwait(false);
    }

    private static bool HasUtf8Bom(string path)
    {
        using var stream = File.OpenRead(path);
        Span<byte> prefix = stackalloc byte[3];
        var bytesRead = stream.Read(prefix);
        return bytesRead >= 3
            && prefix[0] == 0xEF
            && prefix[1] == 0xBB
            && prefix[2] == 0xBF;
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

    private static readonly Regex GlobalAnalyzerConfigItemGroupPattern = new(
        @"(^[ \t]*<!-- Global Analyzer Config -->\r?\n)[ \t]*<ItemGroup>\r?\n.*?^[ \t]*</ItemGroup>\r?\n",
        RegexOptions.Multiline | RegexOptions.Singleline | RegexOptions.CultureInvariant);

    private static readonly Regex GlobalConfigSettingPattern = new(
        @"^\s*(?<key>[^#][^=]*?)\s*=\s*(?<value>.*?)\s*$",
        RegexOptions.CultureInvariant);

    private static readonly Regex RazorPackageProjectUrlPattern = new(
        @"(?:^[ \t]*<!--\s*When building in the VMR, we still want the package project url to point to this repo\s*-->\r?\n)?^[ \t]*<PackageProjectUrl>\s*https://github\.com/dotnet/razor\s*</PackageProjectUrl>\r?\n?",
        RegexOptions.Multiline | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex RazorPackageProjectUrlCommentPattern = new(
        @"^[ \t]*<!--\s*When building in the VMR, we still want the package project url to point to this repo\s*-->\r?\n(?:^[ \t]*\r?\n)?",
        RegexOptions.Multiline | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex RazorRepositoryUrlPattern = new(
        @"^[ \t]*<RepositoryUrl>\s*https://github\.com/dotnet/razor\s*</RepositoryUrl>\r?\n?",
        RegexOptions.Multiline | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex GeneratePkgDefFileTruePattern = new(
        @"<GeneratePkgDefFile>\s*true\s*</GeneratePkgDefFile>",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex BenchmarkDotNetBuildTimeoutPattern = new(
        @"^[ \t]*\.WithBuildTimeout\(TimeSpan\.FromMinutes\(15\)\)\s*(?://.*)?\r?\n",
        RegexOptions.Multiline | RegexOptions.CultureInvariant);

    private static readonly Regex BenchmarkDotNetNetCoreApp80JobPattern = new(
        @"^(?<indent>[ \t]*)\.AddJob\(GetJob\(CsProjCoreToolchain\.NetCoreApp80\)\)\s*(?://.*)?\r?\n",
        RegexOptions.Multiline | RegexOptions.CultureInvariant);

    private static readonly Regex BenchmarkDotNetDisassemblySyntaxPattern = new(
        @"^[ \t]*syntax:\s*DisassemblySyntax\.Masm,\s*(?://.*)?\r?\n",
        RegexOptions.Multiline | RegexOptions.CultureInvariant);

    private static readonly Regex RazorUnitTestPropertyGroupPattern = new(
        @"(?:^[ \t]*<!--\r?\n[ \t]*We don't follow Arcade conventions for project naming\.\r?\n[ \t]*-->\r?\n)?^[ \t]*<PropertyGroup Condition=""'\$\(IsUnitTestProject\)' == ''"">\r?\n.*?^[ \t]*</PropertyGroup>\r?\n",
        RegexOptions.Multiline | RegexOptions.Singleline | RegexOptions.CultureInvariant);

    private static readonly Regex ProjectOpenPattern = new(
        @"<Project[^>]*>",
        RegexOptions.CultureInvariant);

    private static readonly Regex AdjacentMsBuildItemPattern = new(
        @"(?m)^(?<indent>[ \t]*)(?<first><(?:PackageReference|ProjectReference)\b[^>]*/>)[ \t]*(?<second><(?:PackageReference|ProjectReference)\b[^>]*/>)",
        RegexOptions.CultureInvariant);

    private static readonly Regex StrictMoqOfPattern = new(
        @"Mock\.Of<(?<type>.+?)>\(\s*MockBehavior\.Strict\s*\)",
        RegexOptions.CultureInvariant);

    private static readonly Regex StrictMoqOfWithPredicatePattern = new(
        @"Mock\.Of<(?<type>.+?)>\((?<predicate>.+?),\s*MockBehavior\.Strict\s*\)",
        RegexOptions.CultureInvariant);

    private static readonly Regex StrictMockNoPredicateImplementationPattern = new(
        @"^[ \t]*=>\s*Mock\.Of<T>\(MockBehavior\.Strict\);\s*$",
        RegexOptions.Multiline | RegexOptions.CultureInvariant);

    private static readonly Regex StrictMockPredicateImplementationPattern = new(
        @"^[ \t]*=>\s*Mock\.Of<T>\(predicate,\s*MockBehavior\.Strict\);\s*$",
        RegexOptions.Multiline | RegexOptions.CultureInvariant);

    private static readonly Regex ActiveConfigurationGroupNullCheckPattern = new(
        @"\r?\n[ \t]*if \(activeConfigurationGroupSubscriptionService is null\)\r?\n[ \t]*\{\r?\n[ \t]*throw new ArgumentNullException\(nameof\(activeConfigurationGroupSubscriptionService\)\);\r?\n[ \t]*\}\r?\n",
        RegexOptions.CultureInvariant);

    private static readonly Regex ActiveConfigurationGroupAssignmentPattern = new(
        @"^[ \t]*ActiveConfigurationGroupSubscriptionService = activeConfigurationGroupSubscriptionService;\r?\n",
        RegexOptions.Multiline | RegexOptions.CultureInvariant);

    private static readonly Regex ActiveConfigurationGroupPropertyPattern = new(
        @"^[ \t]*public IActiveConfigurationGroupSubscriptionService ActiveConfigurationGroupSubscriptionService \{ get; \}\r?\n",
        RegexOptions.Multiline | RegexOptions.CultureInvariant);

    private static readonly Regex TestActiveConfigurationGroupInitializationPattern = new(
        @"^[ \t]*ActiveConfigurationGroupSubscriptionService = new TestActiveConfigurationGroupSubscriptionService\(\);\r?\n",
        RegexOptions.Multiline | RegexOptions.CultureInvariant);

    private static readonly Regex TestActiveConfigurationGroupPropertyPattern = new(
        @"^[ \t]*public TestActiveConfigurationGroupSubscriptionService ActiveConfigurationGroupSubscriptionService \{ get; \}\r?\n",
        RegexOptions.Multiline | RegexOptions.CultureInvariant);

    private static readonly Regex TestActiveConfigurationGroupInterfaceImplementationPattern = new(
        @"^[ \t]*IActiveConfigurationGroupSubscriptionService IUnconfiguredProjectCommonServices\.ActiveConfigurationGroupSubscriptionService => ActiveConfigurationGroupSubscriptionService;\r?\n",
        RegexOptions.Multiline | RegexOptions.CultureInvariant);

    private static readonly Regex TestActiveConfigurationGroupClassPattern = new(
        @"\r?\n[ \t]*public class TestActiveConfigurationGroupSubscriptionService : IActiveConfigurationGroupSubscriptionService\r?\n[ \t]*\{.*?^[ \t]*\}\r?\n",
        RegexOptions.Singleline | RegexOptions.Multiline | RegexOptions.CultureInvariant);

    private const string RazorSdkPackageVersion = "6.0.0-alpha.1.21072.5";

    private static readonly string[] RazorGlobalConfigFileNames =
    [
        "Common.globalconfig",
        "Shipping.globalconfig",
        "NonShipping.globalconfig",
        "ApiShim.globalconfig",
    ];

    private static readonly string RazorUnitTestPropertyGroupBlock = string.Join(
        Environment.NewLine,
        [
            "  <!--",
            "    We don't follow Arcade conventions for project naming.",
            "  -->",
            "  <PropertyGroup Condition=\"'$(IsUnitTestProject)' == ''\">",
            "    <IsUnitTestProject>false</IsUnitTestProject>",
            "    <IsUnitTestProject Condition=\"$(MSBuildProjectName.EndsWith('.Test'))\">true</IsUnitTestProject>",
            "    <TargetFileName Condition=\"'$(IsUnitTestProject)' == 'true' AND !$(TargetFileName.EndsWith('.UnitTests.dll'))\">$([System.String]::Copy('$(MSBuildProjectName)').Replace('.Test', '.UnitTests')).dll</TargetFileName>",
            "  </PropertyGroup>",
        ]) + Environment.NewLine;

    private static readonly string RazorGlobalConfigItemGroupBlock = string.Join(
        Environment.NewLine,
        [
            "  <ItemGroup>",
            "    <!-- Always include Common.globalconfig -->",
            @"    <EditorConfigFiles Include=""$(MSBuildThisFileDirectory)Common.globalconfig"" />",
            "    <!-- Include Shipping.globalconfig for shipping projects -->",
            @"    <EditorConfigFiles Condition=""'$(IsShipping)' == 'true'"" Include=""$(MSBuildThisFileDirectory)Shipping.globalconfig"" />",
            "    <!-- Include NonShipping.globalconfig for non-shipping projects, except for API shims -->",
            @"    <EditorConfigFiles Condition=""'$(IsShipping)' != 'true' AND '$(IsApiShim)' != 'true'"" Include=""$(MSBuildThisFileDirectory)NonShipping.globalconfig"" />",
            "    <!-- Include ApiShim.globalconfig for API shim projects -->",
            @"    <EditorConfigFiles Condition=""'$(IsApiShim)' == 'true'"" Include=""$(MSBuildThisFileDirectory)ApiShim.globalconfig"" />",
            "  </ItemGroup>",
        ]) + Environment.NewLine;

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
