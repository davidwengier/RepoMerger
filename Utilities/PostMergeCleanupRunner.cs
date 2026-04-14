using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace RepoMerger;

internal static class PostMergeCleanupRunner
{
    private const string RazorDiagnosticsAnalyzerReferenceCondition = "'$(BuildingInsideVisualStudio)' == 'true'";

    private static readonly CleanupStep[] Steps =
    [
        new(
            "commit-pending-solution-updates",
            "Stage pending root solution file updates for a dedicated commit before other post-merge cleanup runs.",
            "Commit merged solution updates",
            "Merged Razor solution membership updates should land in their own commit so Roslyn solution-file churn stays isolated from later cleanup steps and easier to review.",
            CommitPendingSolutionUpdatesAsync),
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
            "normalize-basic-reference-assemblies-version",
            "Remove Razor's local Basic.Reference.Assemblies version override so the merged tree uses Roslyn's shared version.",
            "Normalize Razor Basic.Reference.Assemblies version",
            "Razor's src\\Razor\\Directory.Packages.props should inherit Roslyn's shared Basic.Reference.Assemblies version instead of pinning its older local 1.7.2 value, which triggers restore failures in the merged tree.",
            NormalizeBasicReferenceAssembliesVersionAsync),
        new(
            "remove-roslyn-testing-package-overrides",
            "Rewrite Razor's local Microsoft.CodeAnalysis.Analyzer.Testing version pin to Roslyn's shared testing-version property.",
            "Normalize Razor testing package version",
            "Razor still needs a local PackageVersion entry for Microsoft.CodeAnalysis.Analyzer.Testing, but in the merged tree it should use Roslyn's shared $(MicrosoftCodeAnalysisTestingVersion) instead of pinning an older preview version that causes package downgrade errors.",
            RemoveRoslynTestingPackageOverridesAsync),
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
            "normalize-razor-vs-test-harness-refs",
            "Rewrite Razor Visual Studio integration test source-generator references to use Roslyn's analyzer-style project reference metadata.",
            "Normalize Razor VS test harness refs",
            "When Razor's Visual Studio integration tests reference Roslyn's in-repo Microsoft.VisualStudio.Extensibility.Testing.SourceGenerator project, the reference must stay an Analyzer project reference so TestService and AbstractIdeIntegrationTest sources are generated during build.",
            NormalizeRazorVisualStudioTestHarnessReferencesAsync),
        new(
            "normalize-razor-liveshare-test-session",
            "Implement the extra CollaborationSession members required by Roslyn's shared Live Share package in Razor's test stub.",
            "Normalize Razor Live Share test session",
            "Razor's TestCollaborationSession test stub should implement the ConversationId, IsSessionConnected, and SessionDisconnection members expected by Roslyn's shared Microsoft.VisualStudio.LiveShare version.",
            NormalizeRazorLiveShareTestSessionAsync),
        new(
            "normalize-razor-vs-restore-manager-refs",
            "Add Roslyn's shared NuGet.SolutionRestoreManager interop package to Razor's Visual Studio integration tests.",
            "Normalize Razor VS restore manager refs",
            "Razor's Visual Studio integration tests use IVsSolutionRestoreService APIs from NuGet.SolutionRestoreManager, so the merged tree should reference the same NuGet.SolutionRestoreManager.Interop package that Roslyn's own Visual Studio integration tests already carry.",
            NormalizeRazorVisualStudioRestoreManagerReferencesAsync),
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
            "guard-razor-diagnostics-analyzer-refs",
            "Keep Razor's local build analyzers enabled for Visual Studio developer builds, but skip them in command-line repo builds that use Roslyn's bootstrap flow.",
            "Guard Razor diagnostics analyzer refs",
            "Razor.Diagnostics.Analyzers are intended to light up diagnostics for developers working in the merged solution, so the analyzer references should remain in place for Visual Studio builds, but they need to be gated out of build.cmd's command-line bootstrap flow to avoid CS9057 compiler-version mismatch warnings.",
            GuardRazorDiagnosticsAnalyzerReferencesAsync),
        new(
            "normalize-razor-warning-cleanups",
            "Adjust Razor Live Share helpers and the code-folding integration test to compile cleanly under Roslyn's warning set.",
            "Normalize Razor warning cleanup",
            "Razor's Live Share factories and code-folding test need a few nullability-safe and definite-assignment-safe tweaks in the merged Roslyn tree so build.cmd -restore can stay clean after the merge.",
            NormalizeRazorBuildWarningsAsync),
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
            "normalize-razor-vs-workspace-refs",
            "Add explicit Roslyn workspace references for Razor's Visual Studio extension code that directly uses VisualStudioWorkspace APIs.",
            "Normalize Razor VS workspace refs",
            "Razor's syntax visualizer and related Visual Studio extension code should reference Roslyn's in-repo Microsoft.VisualStudio.LanguageServices and Workspaces projects explicitly when building inside the merged Roslyn tree.",
            NormalizeRazorVisualStudioWorkspaceReferencesAsync),
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

    private static async Task<string> CommitPendingSolutionUpdatesAsync(StageContext context)
    {
        var targetRepoRoot = context.TargetRepoRoot;
        var stagedFiles = new List<string>();

        foreach (var path in Directory.EnumerateFiles(targetRepoRoot, "*.sln*", SearchOption.TopDirectoryOnly))
        {
            var relativePath = Path.GetRelativePath(targetRepoRoot, path);
            var pendingDiff = (await GitRunner.RunGitAsync(targetRepoRoot, "diff", "--name-only", "--", relativePath).ConfigureAwait(false)).Trim();
            var pendingCachedDiff = (await GitRunner.RunGitAsync(targetRepoRoot, "diff", "--cached", "--name-only", "--", relativePath).ConfigureAwait(false)).Trim();

            if (string.IsNullOrWhiteSpace(pendingDiff) && string.IsNullOrWhiteSpace(pendingCachedDiff))
                continue;

            await GitRunner.RunGitAsync(targetRepoRoot, "add", "--", relativePath).ConfigureAwait(false);
            stagedFiles.Add(relativePath);
        }

        return stagedFiles.Count == 0
            ? "No pending root solution file changes were found."
            : $"Staged {stagedFiles.Count} root solution file change(s) for a dedicated commit: {string.Join(", ", stagedFiles)}.";
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

            await WriteTextPreservingUtf8BomAsync(path, updatedContent, templatePath: path).ConfigureAwait(false);
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
        var summaryParts = new List<string>();

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

        if (changedFiles.Count > 0)
        {
            summaryParts.Add(
                $"Removed Razor-specific PackageProjectUrl/RepositoryUrl overrides from {changedFiles.Count} file(s): {string.Join(", ", changedFiles)}.");
        }

        var settingsPropsPath = Path.Combine(targetRepoRoot, "eng", "targets", "Settings.props");
        if (File.Exists(settingsPropsPath))
        {
            var originalContent = await File.ReadAllTextAsync(settingsPropsPath).ConfigureAwait(false);
            var updatedContent = EnsureRepositoryUrlFallback(originalContent);

            if (!string.Equals(originalContent, updatedContent, StringComparison.Ordinal))
            {
                await WriteTextPreservingUtf8BomAsync(settingsPropsPath, updatedContent, templatePath: settingsPropsPath).ConfigureAwait(false);
                summaryParts.Add("Ensured Roslyn eng\\targets\\Settings.props provides a RepositoryUrl fallback from PackageProjectUrl for packable projects.");
            }
        }

        return summaryParts.Count == 0
            ? "No Razor repository metadata cleanup was needed."
            : string.Join(" ", summaryParts);
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
        var engPackagesPath = Path.Combine(targetRepoRoot, "eng", "Packages.props");
        if (File.Exists(engPackagesPath))
            rootPackageIds.UnionWith(await CollectPackageVersionIdsAsync(engPackagesPath).ConfigureAwait(false));

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

    private static async Task<string> NormalizeBasicReferenceAssembliesVersionAsync(StageContext context)
    {
        var targetRepoRoot = context.TargetRepoRoot;
        var targetRoot = context.TargetRoot;
        var razorPackagesPath = Path.Combine(targetRoot, "Directory.Packages.props");
        if (!File.Exists(razorPackagesPath))
            return "No Razor Directory.Packages.props file was found for Basic.Reference.Assemblies version normalization.";

        var originalContent = await File.ReadAllTextAsync(razorPackagesPath).ConfigureAwait(false);
        var updatedContent = BasicReferenceAssembliesVersionPattern.Replace(originalContent, string.Empty, 1);

        if (string.Equals(originalContent, updatedContent, StringComparison.Ordinal))
            return "No Razor Basic.Reference.Assemblies version override was found.";

        await WriteTextPreservingUtf8BomAsync(razorPackagesPath, updatedContent, templatePath: razorPackagesPath).ConfigureAwait(false);
        return
            $"Updated '{Path.GetRelativePath(targetRepoRoot, razorPackagesPath)}' to inherit Roslyn's shared Basic.Reference.Assemblies version.";
    }

    private static async Task<string> RemoveRoslynTestingPackageOverridesAsync(StageContext context)
    {
        var targetRepoRoot = context.TargetRepoRoot;
        var targetRoot = context.TargetRoot;
        var razorPackagesPath = Path.Combine(targetRoot, "Directory.Packages.props");
        if (!File.Exists(razorPackagesPath))
            return "No Razor Directory.Packages.props file was found for Roslyn testing package normalization.";

        var originalContent = await File.ReadAllTextAsync(razorPackagesPath).ConfigureAwait(false);
        var updatedContent = AnalyzerTestingPackageVersionPattern.IsMatch(originalContent)
            ? AnalyzerTestingPackageVersionPattern.Replace(
                originalContent,
                "${prefix}$(MicrosoftCodeAnalysisTestingVersion)${suffix}",
                1)
            : AnalyzerTestingInsertionAnchorPattern.Replace(
                originalContent,
                "$0" + Environment.NewLine + @"    <PackageVersion Include=""Microsoft.CodeAnalysis.Analyzer.Testing"" Version=""$(MicrosoftCodeAnalysisTestingVersion)"" />",
                1);

        if (string.Equals(originalContent, updatedContent, StringComparison.Ordinal))
            return "No Razor Microsoft.CodeAnalysis.Analyzer.Testing version override was found.";

        await WriteTextPreservingUtf8BomAsync(razorPackagesPath, updatedContent, templatePath: razorPackagesPath).ConfigureAwait(false);
        return
            $"Updated '{Path.GetRelativePath(targetRepoRoot, razorPackagesPath)}' to use Roslyn's shared Microsoft.CodeAnalysis.Analyzer.Testing version.";
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

        foreach (var integrationTestProjectPath in Directory.EnumerateFiles(targetRoot, "*.csproj", SearchOption.AllDirectories))
        {
            var projectName = Path.GetFileNameWithoutExtension(integrationTestProjectPath);
            if (!projectName.EndsWith(".IntegrationTests", StringComparison.OrdinalIgnoreCase))
                continue;

            var originalContent = await File.ReadAllTextAsync(integrationTestProjectPath).ConfigureAwait(false);
            var updatedContent = SetBooleanPropertyValue(originalContent, "IsTestProject", true);
            updatedContent = SetBooleanPropertyValue(updatedContent, "IsUnitTestProject", false);
            updatedContent = SetBooleanPropertyValue(updatedContent, "IsIntegrationTestProject", true);

            if (!string.Equals(originalContent, updatedContent, StringComparison.Ordinal))
            {
                await WriteTextPreservingUtf8BomAsync(integrationTestProjectPath, updatedContent, templatePath: integrationTestProjectPath).ConfigureAwait(false);
                changedFiles.Add(Path.GetRelativePath(targetRepoRoot, integrationTestProjectPath));
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

    private static async Task<string> NormalizeRazorVisualStudioTestHarnessReferencesAsync(StageContext context)
    {
        var targetRepoRoot = context.TargetRepoRoot;
        var targetRoot = context.TargetRoot;
        var changedFiles = new List<string>();

        foreach (var projectPath in Directory.EnumerateFiles(targetRoot, "*.csproj", SearchOption.AllDirectories))
        {
            var document = await LoadXmlAsync(projectPath, preserveWhitespace: true).ConfigureAwait(false);
            var sourceGeneratorReferences = document
                .Descendants()
                .Where(static element => element.Name.LocalName == "ProjectReference")
                .Where(element =>
                    element.Attribute("Include")?.Value.Contains(
                        "Microsoft.VisualStudio.Extensibility.Testing.SourceGenerator.csproj",
                        StringComparison.OrdinalIgnoreCase) ?? false)
                .ToList();

            var changed = false;
            foreach (var sourceGeneratorReference in sourceGeneratorReferences)
            {
                changed |= EnsureProjectReferenceAttributeValue(sourceGeneratorReference, "OutputItemType", "Analyzer");
                changed |= EnsureProjectReferenceAttributeValue(sourceGeneratorReference, "ReferenceOutputAssembly", "false");
                changed |= EnsureProjectReferenceAttributeValue(sourceGeneratorReference, "SetTargetFramework", "TargetFramework=netstandard2.0");
                changed |= EnsureProjectReferenceAttributeValue(sourceGeneratorReference, "SkipGetTargetFrameworkProperties", "true");
            }

            if (!changed)
                continue;

            await SaveXmlAsync(document, projectPath).ConfigureAwait(false);
            changedFiles.Add(Path.GetRelativePath(targetRepoRoot, projectPath));
        }

        return changedFiles.Count == 0
            ? "No Razor Visual Studio test harness project reference cleanup was needed."
            : $"Normalized Razor Visual Studio test harness project references in {changedFiles.Count} file(s): {string.Join(", ", changedFiles)}.";
    }

    private static async Task<string> NormalizeRazorLiveShareTestSessionAsync(StageContext context)
    {
        var targetRepoRoot = context.TargetRepoRoot;
        var testSessionPath = Path.Combine(
            context.TargetRoot,
            "src",
            "Razor",
            "test",
            "Microsoft.VisualStudio.LanguageServices.Razor.Test",
            "LiveShare",
            "TestCollaborationSession.cs");

        if (!File.Exists(testSessionPath))
            return "No Razor Live Share test-session stub was found for compatibility cleanup.";

        var originalContent = await File.ReadAllTextAsync(testSessionPath).ConfigureAwait(false);
        var updatedContent = originalContent.Replace(
            "    public override string SessionId => throw new NotImplementedException();",
            "    public override string SessionId => \"test-session\";",
            StringComparison.Ordinal);

        if (!updatedContent.Contains("public override string ConversationId =>", StringComparison.Ordinal))
        {
            updatedContent = updatedContent.Replace(
                "    public override PeerAccess Access => throw new NotImplementedException();",
                "    public override PeerAccess Access => throw new NotImplementedException();" + Environment.NewLine +
                "    public override string ConversationId => SessionId;" + Environment.NewLine +
                "    public override bool IsSessionConnected => true;" + Environment.NewLine +
                "    public override Task SessionDisconnection => Task.CompletedTask;",
                StringComparison.Ordinal);
        }

        if (string.Equals(originalContent, updatedContent, StringComparison.Ordinal))
            return "No Razor Live Share test-session compatibility changes were needed.";

        await WriteTextPreservingUtf8BomAsync(testSessionPath, updatedContent, templatePath: testSessionPath).ConfigureAwait(false);
        return $"Updated '{Path.GetRelativePath(targetRepoRoot, testSessionPath)}' to match Roslyn's current CollaborationSession API surface.";
    }

    private static async Task<string> NormalizeRazorVisualStudioRestoreManagerReferencesAsync(StageContext context)
    {
        var targetRepoRoot = context.TargetRepoRoot;
        var projectPath = Path.Combine(
            context.TargetRoot,
            "src",
            "Razor",
            "test",
            "Microsoft.VisualStudio.Razor.IntegrationTests",
            "Microsoft.VisualStudio.Razor.IntegrationTests.csproj");

        if (!File.Exists(projectPath))
            return "No Razor Visual Studio integration test project was found for restore-manager reference cleanup.";

        var originalContent = await File.ReadAllTextAsync(projectPath).ConfigureAwait(false);
        var updatedContent = EnsurePackageReferenceAfterPackageReference(
            originalContent,
            "NuGet.VisualStudio",
            "NuGet.SolutionRestoreManager.Interop");

        if (string.Equals(originalContent, updatedContent, StringComparison.Ordinal))
            return "No Razor Visual Studio restore-manager package reference cleanup was needed.";

        await WriteTextPreservingUtf8BomAsync(projectPath, updatedContent, templatePath: projectPath).ConfigureAwait(false);
        return $"Added Roslyn's shared NuGet.SolutionRestoreManager.Interop reference to '{Path.GetRelativePath(targetRepoRoot, projectPath)}'.";
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

    private static async Task<string> GuardRazorDiagnosticsAnalyzerReferencesAsync(StageContext context)
    {
        var targetRepoRoot = context.TargetRepoRoot;
        var targetRoot = context.TargetRoot;
        var sourceRazorRoot = Path.Combine(context.State.SourceCloneDirectory, "src", "Razor");
        var changedFiles = new List<string>();
        var guardedReferenceCount = 0;

        foreach (var propsPath in Directory.EnumerateFiles(targetRoot, "Directory.Build.props", SearchOption.AllDirectories))
        {
            var originalContent = await File.ReadAllTextAsync(propsPath).ConfigureAwait(false);
            var updatedContent = originalContent.Replace(
                @" Condition=""'$(BootstrapBuildPath)' == '' or '$(BuildingInsideVisualStudio)' == 'true'""",
                $@" Condition=""{RazorDiagnosticsAnalyzerReferenceCondition}""",
                StringComparison.Ordinal);
            updatedContent = RazorDiagnosticsAnalyzerProjectReferencePattern.Replace(
                updatedContent,
                match =>
                {
                    if (match.Value.Contains("BootstrapBuildPath", StringComparison.OrdinalIgnoreCase))
                        return match.Value.Replace(
                            @" Condition=""'$(BootstrapBuildPath)' == '' or '$(BuildingInsideVisualStudio)' == 'true'""",
                            $@" Condition=""{RazorDiagnosticsAnalyzerReferenceCondition}""",
                            StringComparison.Ordinal);

                    guardedReferenceCount++;
                    return match.Groups["prefix"].Value +
                        $@" Condition=""{RazorDiagnosticsAnalyzerReferenceCondition}""" +
                        match.Groups["suffix"].Value;
                });

            if (!RazorDiagnosticsAnalyzerProjectReferencePattern.IsMatch(updatedContent))
            {
                var relativePath = Path.GetRelativePath(targetRoot, propsPath);
                var sourcePath = Path.Combine(sourceRazorRoot, relativePath);
                if (File.Exists(sourcePath))
                {
                    var sourceContent = await File.ReadAllTextAsync(sourcePath).ConfigureAwait(false);
                    var sourceMatch = RazorDiagnosticsAnalyzerProjectReferencePattern.Match(sourceContent);
                    if (sourceMatch.Success)
                    {
                        var guardedReference = sourceMatch.Value.Contains("BootstrapBuildPath", StringComparison.OrdinalIgnoreCase)
                            ? sourceMatch.Value.Replace(
                                @" Condition=""'$(BootstrapBuildPath)' == '' or '$(BuildingInsideVisualStudio)' == 'true'""",
                                $@" Condition=""{RazorDiagnosticsAnalyzerReferenceCondition}""",
                                StringComparison.Ordinal)
                            : sourceMatch.Groups["prefix"].Value +
                                $@" Condition=""{RazorDiagnosticsAnalyzerReferenceCondition}""" +
                                sourceMatch.Groups["suffix"].Value;

                        var restoredContent = EnsureRawItemAfterPackageReference(
                            updatedContent,
                            "Microsoft.CodeAnalysis.Analyzers",
                            guardedReference);
                        if (!string.Equals(updatedContent, restoredContent, StringComparison.Ordinal))
                        {
                            updatedContent = restoredContent;
                            guardedReferenceCount++;
                        }
                    }
                }
            }

            if (string.Equals(originalContent, updatedContent, StringComparison.Ordinal))
                continue;

            await WriteTextPreservingUtf8BomAsync(propsPath, updatedContent, templatePath: propsPath).ConfigureAwait(false);
            changedFiles.Add(Path.GetRelativePath(targetRepoRoot, propsPath));
        }

        return guardedReferenceCount == 0
            ? "No Razor.Diagnostics.Analyzers Visual Studio guard changes were needed in Razor Directory.Build.props files."
            : $"Guarded {guardedReferenceCount} Razor.Diagnostics.Analyzers build-analyzer reference(s) in {changedFiles.Count} file(s) so they stay active in Visual Studio without breaking build.cmd: {string.Join(", ", changedFiles)}.";
    }

    private static async Task<string> NormalizeRazorBuildWarningsAsync(StageContext context)
    {
        var targetRepoRoot = context.TargetRepoRoot;
        var targetRoot = context.TargetRoot;
        var changedFiles = new List<string>();

        var candidateFiles = new[]
        {
            Path.Combine(targetRoot, "src", "Razor", "src", "Microsoft.VisualStudio.LanguageServices.Razor", "LiveShare", "RemoteHierarchyServiceFactory.cs"),
            Path.Combine(targetRoot, "src", "Razor", "src", "Microsoft.VisualStudio.LanguageServices.Razor", "LiveShare", "Host", "ProjectHierarchyProxyFactory.cs"),
            Path.Combine(targetRoot, "src", "Razor", "src", "Microsoft.VisualStudio.LanguageServices.Razor", "LiveShare", "Guest", "RazorGuestInitializationService.cs"),
            Path.Combine(targetRoot, "src", "Razor", "src", "Microsoft.VisualStudio.LanguageServices.Razor", "LiveShare", "Guest", "ProxyAccessor.cs"),
            Path.Combine(targetRoot, "src", "Razor", "src", "Microsoft.VisualStudio.LanguageServices.Razor", "LiveShare", "Guest", "GuestProjectPathProvider.cs"),
            Path.Combine(targetRoot, "src", "Razor", "src", "Microsoft.VisualStudio.LanguageServices.Razor", "ProjectCapabilityResolver.cs"),
            Path.Combine(targetRoot, "src", "Razor", "test", "Microsoft.VisualStudio.LanguageServices.Razor.Test", "LiveShare", "Guest", "RazorGuestInitializationServiceTest.cs"),
            Path.Combine(targetRoot, "src", "Razor", "test", "Microsoft.VisualStudio.Razor.IntegrationTests", "CodeFoldingTests.cs"),
        };

        foreach (var path in candidateFiles)
        {
            if (!File.Exists(path))
                continue;

            var originalContent = await File.ReadAllTextAsync(path).ConfigureAwait(false);
            var updatedContent = NormalizeRazorBuildWarnings(path, originalContent);

            if (string.Equals(originalContent, updatedContent, StringComparison.Ordinal))
                continue;

            await WriteTextPreservingUtf8BomAsync(path, updatedContent, templatePath: path).ConfigureAwait(false);
            changedFiles.Add(Path.GetRelativePath(targetRepoRoot, path));
        }

        return changedFiles.Count == 0
            ? "No Razor warning cleanup changes were needed."
            : $"Normalized Razor warning cleanup in {changedFiles.Count} file(s): {string.Join(", ", changedFiles)}.";
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

            var updatedContent = NormalizeVisualStudioGetGlobalServiceCalls(originalContent);

            if (string.Equals(originalContent, updatedContent, StringComparison.Ordinal))
                continue;

            await WriteTextPreservingUtf8BomAsync(path, updatedContent, templatePath: path).ConfigureAwait(false);
            changedFiles.Add(Path.GetRelativePath(targetRepoRoot, path));
        }

        return changedFiles.Count == 0
            ? "No Razor Visual Studio service lookup rewrites were needed."
            : $"Normalized Razor Visual Studio service lookups in {changedFiles.Count} file(s): {string.Join(", ", changedFiles)}.";
    }

    private static async Task<string> NormalizeRazorVisualStudioWorkspaceReferencesAsync(StageContext context)
    {
        var targetRepoRoot = context.TargetRepoRoot;
        var targetRoot = context.TargetRoot;
        var changedFiles = new List<string>();

        var projectPath = Path.Combine(targetRoot, "src", "Razor", "src", "Microsoft.VisualStudio.RazorExtension", "Microsoft.VisualStudio.RazorExtension.csproj");
        if (!File.Exists(projectPath))
            return "No Razor Visual Studio extension project was found for workspace reference cleanup.";

        var originalContent = await File.ReadAllTextAsync(projectPath).ConfigureAwait(false);
        var updatedContent = originalContent;
        var anchorBlock =
            "    <ProjectReference Include=\"..\\Microsoft.VisualStudio.LanguageServices.Razor\\Microsoft.VisualStudio.LanguageServices.Razor.csproj\">" + Environment.NewLine +
            "      <NgenPriority>2</NgenPriority>" + Environment.NewLine +
            "    </ProjectReference>" + Environment.NewLine;
        var workspaceReferenceBlock =
            "    <ProjectReference Include=\"..\\..\\..\\..\\..\\VisualStudio\\Core\\Def\\Microsoft.VisualStudio.LanguageServices.csproj\" />" + Environment.NewLine +
            "    <ProjectReference Include=\"..\\..\\..\\..\\..\\Workspaces\\Core\\Portable\\Microsoft.CodeAnalysis.Workspaces.csproj\" />" + Environment.NewLine;

        if (!updatedContent.Contains("..\\..\\..\\..\\..\\VisualStudio\\Core\\Def\\Microsoft.VisualStudio.LanguageServices.csproj", StringComparison.Ordinal))
        {
            updatedContent = updatedContent.Replace(anchorBlock, anchorBlock + workspaceReferenceBlock, StringComparison.Ordinal);

            if (string.Equals(originalContent, updatedContent, StringComparison.Ordinal))
            {
                var compilerReferenceAnchor = "    <ProjectReference Include=\"..\\..\\..\\Compiler\\Microsoft.CodeAnalysis.Razor.Compiler\\src\\Microsoft.CodeAnalysis.Razor.Compiler.csproj\">";
                updatedContent = updatedContent.Replace(
                    compilerReferenceAnchor,
                    workspaceReferenceBlock + compilerReferenceAnchor,
                    StringComparison.Ordinal);
            }
        }

        if (string.Equals(originalContent, updatedContent, StringComparison.Ordinal))
            return "No Razor Visual Studio workspace reference rewrites were needed.";

        await WriteTextPreservingUtf8BomAsync(projectPath, updatedContent, templatePath: projectPath).ConfigureAwait(false);
        changedFiles.Add(Path.GetRelativePath(targetRepoRoot, projectPath));
        return $"Added explicit Roslyn workspace references for Razor's Visual Studio extension project: {string.Join(", ", changedFiles)}.";
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

            ApplySpecialProjectReferenceMetadata(packageId, projectReference);

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

    private static string EnsureProjectReferenceAfterProjectReference(string content, string anchorProjectReferencePath, string newProjectReferencePath)
    {
        content = NormalizeAdjacentMsBuildItemFormatting(content);

        var canonicalProjectReferenceLine = $"    <ProjectReference Include=\"{newProjectReferencePath}\" />{Environment.NewLine}";
        var existingProjectReferencePattern = new Regex(
            $@"^[ \t]*<ProjectReference Include=""{Regex.Escape(newProjectReferencePath)}""(?:\s+[^>]*)?.*$",
            RegexOptions.Multiline | RegexOptions.CultureInvariant);

        if (existingProjectReferencePattern.IsMatch(content))
            return content;

        var blockAnchorPattern = new Regex(
            $@"(?<anchor>^[ \t]*<ProjectReference Include=""{Regex.Escape(anchorProjectReferencePath)}""(?:\s+[^>]*)?>.*?^[ \t]*</ProjectReference>\r?\n)",
            RegexOptions.Multiline | RegexOptions.Singleline | RegexOptions.CultureInvariant);

        if (blockAnchorPattern.IsMatch(content))
        {
            return blockAnchorPattern.Replace(
                content,
                match => $"{match.Groups["anchor"].Value}{canonicalProjectReferenceLine}",
                1);
        }

        var selfClosingAnchorPattern = new Regex(
            $@"(?<anchor>^[ \t]*<ProjectReference Include=""{Regex.Escape(anchorProjectReferencePath)}""\s*/>[ \t]*\r?\n)",
            RegexOptions.Multiline | RegexOptions.CultureInvariant);

        return selfClosingAnchorPattern.Replace(
            content,
            match => $"{match.Groups["anchor"].Value}{canonicalProjectReferenceLine}",
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

    private static string EnsureRawItemAfterPackageReference(string content, string packageId, string itemLine)
    {
        content = NormalizeAdjacentMsBuildItemFormatting(content);
        var normalizedItemLine = itemLine.TrimEnd('\r', '\n');
        if (content.Contains(normalizedItemLine, StringComparison.Ordinal))
            return content;

        var pattern = new Regex(
            $@"^(?<indent>[ \t]*)<PackageReference Include=""{Regex.Escape(packageId)}""(?:\s+[^>]*)?/>\r?\n",
            RegexOptions.Multiline | RegexOptions.CultureInvariant);

        return pattern.Replace(
            content,
            match => $"{match.Value}{normalizedItemLine}{Environment.NewLine}",
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

    private static string NormalizeRazorBuildWarnings(string path, string content)
    {
        content = content.Replace(
            "public Task<ICollaborationService> CreateServiceAsync(",
            "public Task<ICollaborationService?> CreateServiceAsync(",
            StringComparison.Ordinal);
        content = content.Replace(
            "return Task.FromResult<ICollaborationService>(",
            "return Task.FromResult<ICollaborationService?>(",
            StringComparison.Ordinal);

        if (path.EndsWith("ProxyAccessor.cs", StringComparison.OrdinalIgnoreCase))
        {
            content = content.Replace(
                "        Assumes.NotNull(_liveShareSessionAccessor.Session);" + Environment.NewLine +
                Environment.NewLine +
                "        return _jtf.Run(" + Environment.NewLine +
                "            () => _liveShareSessionAccessor.Session.GetRemoteServiceAsync<TProxy>(typeof(TProxy).Name, CancellationToken.None));",
                "        var session = _liveShareSessionAccessor.Session;" + Environment.NewLine +
                "        Assumes.NotNull(session);" + Environment.NewLine +
                Environment.NewLine +
                "        var proxy = _jtf.Run(" + Environment.NewLine +
                "            () => session.GetRemoteServiceAsync<TProxy>(typeof(TProxy).Name, CancellationToken.None));" + Environment.NewLine +
                "        return proxy ?? throw new global::System.InvalidOperationException($\"Unable to resolve Live Share proxy for {typeof(TProxy).Name}.\");",
                StringComparison.Ordinal);
        }

        if (path.EndsWith("GuestProjectPathProvider.cs", StringComparison.OrdinalIgnoreCase))
        {
            content = content.Replace(
                "        Assumes.NotNull(_liveShareSessionAccessor.Session);" + Environment.NewLine +
                Environment.NewLine +
                "        // The path we're given is from the guest so following other patterns we always ask the host information in its own form (aka convert on guest instead of on host)." + Environment.NewLine +
                "        var ownerPath = _liveShareSessionAccessor.Session.ConvertLocalPathToSharedUri(textDocument.FilePath);",
                "        var session = _liveShareSessionAccessor.Session;" + Environment.NewLine +
                "        Assumes.NotNull(session);" + Environment.NewLine +
                Environment.NewLine +
                "        if (string.IsNullOrEmpty(textDocument.FilePath))" + Environment.NewLine +
                "        {" + Environment.NewLine +
                "            return null;" + Environment.NewLine +
                "        }" + Environment.NewLine +
                Environment.NewLine +
                "        // The path we're given is from the guest so following other patterns we always ask the host information in its own form (aka convert on guest instead of on host)." + Environment.NewLine +
                "        var ownerPath = session.ConvertLocalPathToSharedUri(textDocument.FilePath);" + Environment.NewLine +
                "        if (ownerPath is null)" + Environment.NewLine +
                "        {" + Environment.NewLine +
                "            return null;" + Environment.NewLine +
                "        }",
                StringComparison.Ordinal);

            content = content.Replace(
                "        Assumes.NotNull(_liveShareSessionAccessor.Session);" + Environment.NewLine +
                Environment.NewLine +
                "        return _liveShareSessionAccessor.Session.ConvertSharedUriToLocalPath(hostProjectPath);",
                "        var session = _liveShareSessionAccessor.Session;" + Environment.NewLine +
                "        Assumes.NotNull(session);" + Environment.NewLine +
                Environment.NewLine +
                "        return session.ConvertSharedUriToLocalPath(hostProjectPath) ?? hostProjectPath.LocalPath;",
                StringComparison.Ordinal);
        }

        if (path.EndsWith("ProjectCapabilityResolver.cs", StringComparison.OrdinalIgnoreCase))
        {
            content = content.Replace(
                "            var remoteHierarchyService = await session" + Environment.NewLine +
                "                .GetRemoteServiceAsync<IRemoteHierarchyService>(nameof(IRemoteHierarchyService), cancellationToken)" + Environment.NewLine +
                "                .ConfigureAwait(false);" + Environment.NewLine +
                Environment.NewLine +
                "            var documentFilePathUri = session.ConvertLocalPathToSharedUri(documentFilePath);" + Environment.NewLine +
                Environment.NewLine +
                "            var isMatch = await remoteHierarchyService" + Environment.NewLine +
                "                .HasCapabilityAsync(documentFilePathUri, capability, cancellationToken)" + Environment.NewLine +
                "                .ConfigureAwait(false);",
                "            var remoteHierarchyService = await session" + Environment.NewLine +
                "                .GetRemoteServiceAsync<IRemoteHierarchyService>(nameof(IRemoteHierarchyService), cancellationToken)" + Environment.NewLine +
                "                .ConfigureAwait(false);" + Environment.NewLine +
                "            if (remoteHierarchyService is null)" + Environment.NewLine +
                "            {" + Environment.NewLine +
                "                _logger.LogWarning(\"Live Share remote hierarchy service was unavailable during capability resolution.\");" + Environment.NewLine +
                "                return new(IsInProject: false, HasCapability: false);" + Environment.NewLine +
                "            }" + Environment.NewLine +
                Environment.NewLine +
                "            var documentFilePathUri = session.ConvertLocalPathToSharedUri(documentFilePath);" + Environment.NewLine +
                "            if (documentFilePathUri is null)" + Environment.NewLine +
                "            {" + Environment.NewLine +
                "                _logger.LogWarning($\"Live Share could not convert document path to shared URI: {documentFilePath}\");" + Environment.NewLine +
                "                return new(IsInProject: false, HasCapability: false);" + Environment.NewLine +
                "            }" + Environment.NewLine +
                Environment.NewLine +
                "            var isMatch = await remoteHierarchyService" + Environment.NewLine +
                "                .HasCapabilityAsync(documentFilePathUri, capability, cancellationToken)" + Environment.NewLine +
                "                .ConfigureAwait(false);",
                StringComparison.Ordinal);
        }

        if (path.EndsWith("CodeFoldingTests.cs", StringComparison.OrdinalIgnoreCase))
        {
            content = content.Replace(
                "        ImmutableArray<CollapsibleBlock> missingLines;",
                "        ImmutableArray<CollapsibleBlock> missingLines = [];",
                StringComparison.Ordinal);
        }

        if (path.EndsWith("RazorGuestInitializationServiceTest.cs", StringComparison.OrdinalIgnoreCase))
        {
            content = content.Replace(
                "        IDisposable? sessionService = null;" + Environment.NewLine,
                string.Empty,
                StringComparison.Ordinal);
            content = content.Replace(
                "        sessionService = (IDisposable)await service.CreateServiceAsync(session.Object, DisposalToken);",
                "        var sessionService = Assert.IsAssignableFrom<IDisposable>(await service.CreateServiceAsync(session.Object, DisposalToken));",
                StringComparison.Ordinal);
            content = content.Replace(
                "        sessionService = (IDisposable)await service.CreateServiceAsync(session.Object, cts.Token);",
                "        var sessionService = Assert.IsAssignableFrom<IDisposable>(await service.CreateServiceAsync(session.Object, cts.Token));",
                StringComparison.Ordinal);
            content = content.Replace(
                "        // Act" + Environment.NewLine +
                "        sessionService.Dispose();",
                "        // Act" + Environment.NewLine +
                "        Assert.NotNull(sessionService);" + Environment.NewLine +
                "        sessionService.Dispose();",
                StringComparison.Ordinal);
        }

        return content;
    }

    private static string NormalizeStrictMoqCalls(string content)
    {
        if (content.Contains("public static class StrictMock", StringComparison.Ordinal))
        {
            content = StrictMockNoPredicateImplementationPattern.Replace(
                content,
                "        => new Mock<T>(MockBehavior.Strict).Object;");
            content = StrictMockPredicateImplementationPattern.Replace(
                content,
                "        => new MockRepository(MockBehavior.Strict).OneOf<T>(predicate);");
        }

        content = BrokenQualifiedStrictMockPattern.Replace(content, "StrictMock.Of<");
        content = QualifiedStrictMockPattern.Replace(content, "StrictMock.Of<");

        content = StrictMockOfWithPredicatePattern.Replace(
            content,
            "StrictMock.Of<${type}>(${predicate})");
        content = StrictMockOfPattern.Replace(
            content,
            "StrictMock.Of<${type}>()");
        content = StrictMoqOfWithPredicatePattern.Replace(
            content,
            "StrictMock.Of<${type}>(${predicate})");
        content = StrictMoqOfPattern.Replace(
            content,
            "StrictMock.Of<${type}>()");
        content = ResidualStrictBehaviorWithPredicatePattern.Replace(
            content,
            "StrictMock.Of<${type}>(${predicate})");
        content = ResidualStrictBehaviorOnlyPattern.Replace(
            content,
            "StrictMock.Of<${type}>()");

        if (content.Contains("public static class StrictMock", StringComparison.Ordinal))
        {
            content = StrictMockNoPredicateImplementationPattern.Replace(
                content,
                "        => new Mock<T>(MockBehavior.Strict).Object;");
            content = StrictMockPredicateImplementationPattern.Replace(
                content,
                "        => new MockRepository(MockBehavior.Strict).OneOf<T>(predicate);");
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
        content = ResidualActiveConfigurationGroupBlockPattern.Replace(content, Environment.NewLine);
        content = TestActiveConfigurationGroupInitializationPattern.Replace(content, string.Empty);
        content = TestActiveConfigurationGroupPropertyPattern.Replace(content, string.Empty);
        content = TestActiveConfigurationGroupInterfaceImplementationPattern.Replace(content, string.Empty);
        content = TestActiveConfigurationGroupClassPattern.Replace(content, string.Empty);

        return content;
    }

    private static string NormalizeVisualStudioGetGlobalServiceCalls(string content)
    {
        const string replacementToken = "__REPO_MERGER_GET_GLOBAL_SERVICE__";

        content = WeirdShellQualifiedGetGlobalServicePattern.Replace(content, replacementToken);
        content = ShellQualifiedGetGlobalServicePattern.Replace(content, replacementToken);
        content = DoubleQualifiedGetGlobalServicePattern.Replace(content, replacementToken);
        content = BareGetGlobalServicePattern.Replace(content, replacementToken);

        return content.Replace(
            replacementToken,
            "Microsoft.VisualStudio.Shell.Package.GetGlobalService(",
            StringComparison.Ordinal);
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

    private static string EnsureRepositoryUrlFallback(string content)
    {
        if (content.Contains("<RepositoryUrl", StringComparison.OrdinalIgnoreCase))
            return content;

        return PackageProjectUrlElementPattern.Replace(
            content,
            match => $"{match.Value}{Environment.NewLine}{match.Groups["indent"].Value}<RepositoryUrl Condition=\"'$(RepositoryUrl)' == ''\">$(PackageProjectUrl)</RepositoryUrl>",
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
        => localName is "Condition" or "PrivateAssets" or "ReferenceOutputAssembly" or "OutputItemType" or "Aliases" or "SetTargetFramework" or "SkipGetTargetFrameworkProperties";

    private static bool ShouldKeepProjectReferenceElement(string localName)
        => localName is "Condition" or "PrivateAssets" or "ReferenceOutputAssembly" or "OutputItemType" or "Aliases" or "SetTargetFramework" or "SkipGetTargetFrameworkProperties";

    private static void ApplySpecialProjectReferenceMetadata(string packageId, XElement projectReference)
    {
        if (!string.Equals(packageId, "Microsoft.VisualStudio.Extensibility.Testing.SourceGenerator", StringComparison.OrdinalIgnoreCase))
            return;

        projectReference.SetAttributeValue("OutputItemType", "Analyzer");
        projectReference.SetAttributeValue("ReferenceOutputAssembly", "false");
        projectReference.SetAttributeValue("SetTargetFramework", "TargetFramework=netstandard2.0");
        projectReference.SetAttributeValue("SkipGetTargetFrameworkProperties", "true");
    }

    private static bool EnsureProjectReferenceAttributeValue(XElement projectReference, string attributeName, string expectedValue)
    {
        var attribute = projectReference.Attribute(attributeName);
        if (string.Equals(attribute?.Value, expectedValue, StringComparison.Ordinal))
            return false;

        projectReference.SetAttributeValue(attributeName, expectedValue);
        return true;
    }

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
        var lineEnding = File.Exists(templatePath)
            ? GetPreferredLineEnding(templatePath)
            : Environment.NewLine;
        content = NormalizeLineEndings(content, lineEnding);
        await File.WriteAllTextAsync(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: emitBom)).ConfigureAwait(false);
    }

    private static string GetPreferredLineEnding(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
        var content = reader.ReadToEnd();

        var newLineIndex = content.IndexOf('\n');
        if (newLineIndex < 0)
            return Environment.NewLine;

        return newLineIndex > 0 && content[newLineIndex - 1] == '\r'
            ? "\r\n"
            : "\n";
    }

    private static string NormalizeLineEndings(string content, string lineEnding)
        => content
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\r", "\n", StringComparison.Ordinal)
            .Replace("\n", lineEnding, StringComparison.Ordinal);

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
        @"^[ \t]*<Import\s+Project=""(?:\$\(RepositoryEngineeringDir\)targets|eng(?:\\|/)targets)(?:\\|/)Common\.targets""\s*/>\r?\n?",
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

    private static readonly Regex PackageProjectUrlElementPattern = new(
        @"^(?<indent>[ \t]*)<PackageProjectUrl>\s*[^<]+\s*</PackageProjectUrl>\s*$",
        RegexOptions.Multiline | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex BasicReferenceAssembliesVersionPattern = new(
        @"^[ \t]*<_BasicReferenceAssembliesVersion>.*?</_BasicReferenceAssembliesVersion>\r?\n",
        RegexOptions.Multiline | RegexOptions.CultureInvariant);

    private static readonly Regex AnalyzerTestingPackageVersionPattern = new(
        @"(?<prefix><PackageVersion Include=""Microsoft\.CodeAnalysis\.Analyzer\.Testing""\s+Version="")[^""]+(?<suffix>""(?:\s+[^>]*)?/>)",
        RegexOptions.CultureInvariant);

    private static readonly Regex AnalyzerTestingInsertionAnchorPattern = new(
        @"^[ \t]*<PackageVersion Include=""Microsoft\.AspNetCore\.App\.Runtime\.\$\(NetCoreSDKRuntimeIdentifier\)""[^>]*/>\s*$",
        RegexOptions.Multiline | RegexOptions.CultureInvariant);

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
        @"(?<![\w:.])Mock\.Of<(?<type>[^>]+)>\(\s*MockBehavior\.Strict\s*\)",
        RegexOptions.CultureInvariant);

    private static readonly Regex StrictMoqOfWithPredicatePattern = new(
        @"(?<![\w:.])Mock\.Of<(?<type>[^>]+)>\((?<predicate>[\s\S]*?),\s*MockBehavior\.Strict\s*\)",
        RegexOptions.CultureInvariant);

    private static readonly Regex StrictMockOfPattern = new(
        @"(?<![\w:.])StrictMock\.Of<(?<type>[^>]+)>\(\s*MockBehavior\.Strict\s*\)",
        RegexOptions.CultureInvariant);

    private static readonly Regex StrictMockOfWithPredicatePattern = new(
        @"(?<![\w:.])StrictMock\.Of<(?<type>[^>]+)>\((?<predicate>[\s\S]*?),\s*MockBehavior\.Strict\s*\)",
        RegexOptions.CultureInvariant);

    private static readonly Regex QualifiedStrictMockPattern = new(
        @"global::Microsoft\.AspNetCore\.Razor\.Test\.Common\.StrictMock\.Of<",
        RegexOptions.CultureInvariant);

    private static readonly Regex BrokenQualifiedStrictMockPattern = new(
        @"global::Microsoft\.AspNetCore\.Razor\.Test\.Common\.Strictglobal::Microsoft\.AspNetCore\.Razor\.Test\.Common\.StrictMock\.Of<",
        RegexOptions.CultureInvariant);

    private static readonly Regex StrictMockNoPredicateImplementationPattern = new(
        @"^[ \t]*=>\s*(?:Mock\.Of<T>\(MockBehavior\.Strict\)|StrictMock\.Of<T>\(MockBehavior\.Strict\))\s*;\s*$",
        RegexOptions.Multiline | RegexOptions.CultureInvariant);

    private static readonly Regex StrictMockPredicateImplementationPattern = new(
        @"^[ \t]*=>\s*(?:Mock\.Of<T>\(predicate,\s*MockBehavior\.Strict\)|StrictMock\.Of<T>\(predicate\))\s*;\s*$",
        RegexOptions.Multiline | RegexOptions.CultureInvariant);

    private static readonly Regex ResidualStrictBehaviorOnlyPattern = new(
        @"(?<![\w:.])(?:(?:global::Microsoft\.AspNetCore\.Razor\.Test\.Common\.)?StrictMock|Mock)\.Of<(?<type>[^>]+)>\(\s*MockBehavior\.Strict\s*\)",
        RegexOptions.CultureInvariant | RegexOptions.Singleline);

    private static readonly Regex ResidualStrictBehaviorWithPredicatePattern = new(
        @"(?<![\w:.])(?:(?:global::Microsoft\.AspNetCore\.Razor\.Test\.Common\.)?StrictMock|Mock)\.Of<(?<type>[^>]+)>\((?<predicate>[\s\S]*?),\s*MockBehavior\.Strict\s*\)",
        RegexOptions.CultureInvariant | RegexOptions.Singleline);

    private static readonly Regex RazorDiagnosticsAnalyzerProjectReferencePattern = new(
        @"(?<prefix>^[ \t]*<ProjectReference Include=""[^""]*Razor\.Diagnostics\.Analyzers\.csproj""(?:(?!\sCondition=)[^>])*)(?<suffix>\s*/>)",
        RegexOptions.Multiline | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

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

    private static readonly Regex ResidualActiveConfigurationGroupBlockPattern = new(
        @"\r?\n[ \t]*public BufferBlock<IProjectVersionedValue<ConfigurationSubscriptionSources>> SourceBlock \{ get; \}\r?\n(?:[ \t]*\r?\n)?[ \t]*ConfigurationSubscriptionSources IActiveConfigurationGroupSubscriptionService\.Current \{ get; \}\r?\n(?:[ \t]*\r?\n)?[ \t]*IReceivableSourceBlock<IProjectVersionedValue<ConfigurationSubscriptionSources>> IProjectValueDataSource<ConfigurationSubscriptionSources>\.SourceBlock => SourceBlock;\r?\n(?:[ \t]*\r?\n)?[ \t]*ISourceBlock<IProjectVersionedValue<object>> IProjectValueDataSource\.SourceBlock => SourceBlock;\r?\n(?:[ \t]*\r?\n)?[ \t]*NamedIdentity IProjectValueDataSource\.DataSourceKey \{ get; \}\r?\n(?:[ \t]*\r?\n)?[ \t]*IComparable IProjectValueDataSource\.DataSourceVersion \{ get; \}\r?\n(?:[ \t]*\r?\n)?[ \t]*IDisposable IJoinableProjectValueDataSource\.Join\(\) => null;\r?\n[ \t]*\}\r?\n",
        RegexOptions.Multiline | RegexOptions.CultureInvariant);

    private static readonly Regex DoubleQualifiedGetGlobalServicePattern = new(
        @"Microsoft\.VisualStudio\.Shell\.Microsoft\.VisualStudio\.Shell\.Package\.GetGlobalService\(",
        RegexOptions.CultureInvariant);

    private static readonly Regex WeirdShellQualifiedGetGlobalServicePattern = new(
        @"Shell\.Microsoft\.VisualStudio\.Shell\.Package\.GetGlobalService\(",
        RegexOptions.CultureInvariant);

    private static readonly Regex ShellQualifiedGetGlobalServicePattern = new(
        @"(?<![\w.])Shell\.Package\.GetGlobalService\(",
        RegexOptions.CultureInvariant);

    private static readonly Regex BareGetGlobalServicePattern = new(
        @"(?<![\w.])Package\.GetGlobalService\(",
        RegexOptions.CultureInvariant);

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
            "  <PropertyGroup Condition=\"'$(IsUnitTestProject)' == '' OR '$(IsIntegrationTestProject)' == ''\">",
            "    <IsUnitTestProject>false</IsUnitTestProject>",
            "    <IsUnitTestProject Condition=\"$(MSBuildProjectName.EndsWith('.Test'))\">true</IsUnitTestProject>",
            "    <IsIntegrationTestProject>false</IsIntegrationTestProject>",
            "    <IsIntegrationTestProject Condition=\"$(MSBuildProjectName.EndsWith('.IntegrationTests'))\">true</IsIntegrationTestProject>",
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
