using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace RepoMerger;

internal static class PostMergeCleanupRunner
{
    // Append new cleanup steps to the end so single-step validation runs them in the same
    // environment they would see during a full clean run.
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
            "overlay-razor-editorconfig",
            "Copy Razor's repo-root .editorconfig and spelling exclusions file into src\\Razor.",
            "Overlay Razor .editorconfig",
            "Razor source files in the merged Roslyn tree should keep Razor's repo-local editorconfig and spelling exclusions under src\\Razor, otherwise Roslyn's root editorconfig takes over and triggers unwanted file-header and style diagnostics.",
            OverlayRazorRootEditorConfigAsync),
        new(
            "merge-razor-publish-data",
            "Merge missing Razor package publish entries into Roslyn's PublishData.json without overriding Roslyn's existing branch routing.",
            "Merge Razor PublishData packages",
            "Roslyn's eng\\config\\PublishData.json should stay authoritative for feeds and branch routing, but the merged tree still needs Razor's package publish entries so build.cmd -pack/publish continues to include Razor's insertion packages.",
            MergeRazorPublishDataAsync),
        new(
            "overlay-razor-banned-symbols",
            "Copy Razor-specific banned symbol files into src\\Razor and rewrite Razor project references to their local paths.",
            "Overlay Razor banned symbol files",
            "Roslyn already imports its shared banned-symbol configuration, but Razor carries extra API restrictions and MEF-specific symbol lists that should live under src\\Razor with project references rewritten to those local files.",
            OverlayRazorBannedSymbolsAsync),
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
            "remove-roslyn-diagnostics-analyzers",
            "Remove Roslyn.Diagnostics.Analyzers package references from Razor Directory.Build.props files.",
            "Remove Roslyn.Diagnostics.Analyzers refs",
            "Roslyn already manages Roslyn.Diagnostics.Analyzers centrally, so the merged Razor tree should not add duplicate local analyzer references.",
            RemoveRoslynDiagnosticsAnalyzersAsync),
        new(
            "guard-razor-diagnostics-analyzer-refs",
            "Comment out Razor's local diagnostics-analyzer project references and leave a TODO to restore them once the merged Roslyn build can consume them cleanly again.",
            "Comment Razor diagnostics analyzer refs",
            "The merged Roslyn tree should not actively reference Razor.Diagnostics.Analyzers.csproj yet, but keeping the intended ProjectReference in a TODO comment makes it clear how to restore the analyzer once the compatibility issues are resolved.",
            GuardRazorDiagnosticsAnalyzerReferencesAsync),
        new(
            "convert-roslyn-package-references",
            "Convert Roslyn PackageReference items into ProjectReference items.",
            "Convert Roslyn package references to project references",
            "Inside the merged Roslyn tree, Razor should reference Roslyn projects directly instead of consuming Roslyn NuGet packages that duplicate the in-repo source.",
            ConvertRoslynPackageReferencesAsync),
        new(
            "remove-xunit-version-overrides",
            "Remove Razor-local xUnit VersionOverride pins and defer to Roslyn's centrally managed package versions.",
            "Remove Razor xUnit version overrides",
            "Razor should use Roslyn's centrally managed xUnit package versions instead of overriding xunit.assert and xunit.analyzers locally.",
            RemoveXunitVersionOverridesAsync),
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
            "align-razor-directory-packages-versions",
            "Align Razor's local Directory.Packages.props version entries with Roslyn's shared package versions.",
            "Align Razor Directory.Packages.props versions",
            "Razor's local Microsoft.NET.Sdk.Razor, Microsoft.Extensions.ObjectPool, Basic.Reference.Assemblies, and Microsoft.CodeAnalysis.Analyzer.Testing version entries should align with Roslyn's shared package versioning so the merged tree uses one coherent set of package pins.",
            AlignRazorDirectoryPackagesVersionsAsync),
        new(
            "normalize-razor-benchmarkdotnet-apis",
            "Rewrite Razor microbenchmark runners to avoid BenchmarkDotNet APIs newer than Roslyn's shared package version.",
            "Normalize Razor BenchmarkDotNet runner APIs",
            "Razor's microbenchmark runner programs should compile against Roslyn's centrally managed BenchmarkDotNet package instead of depending on newer API surface that Roslyn does not carry.",
            NormalizeRazorBenchmarkDotNetApisAsync),
        new(
            "disable-razor-nonshipping-public-api-analyzers",
            "Disable Roslyn public API analyzers for Razor test helpers, shims, and benchmark executables that are not shipped APIs.",
            "Disable Razor non-shipping public API analyzers",
            "Razor's test-only helpers, shims, and benchmark executables are not shipped public API surface in the merged Roslyn tree, so they should opt out of PublicApiAnalyzers instead of failing RS0016 during repo builds.",
            DisableRazorNonShippingPublicApiAnalyzersAsync),
        new(
            "suppress-razor-specializedtasks-vsthrd200",
            "Suppress VSTHRD200 on Razor's shared SpecializedTasks helpers that intentionally expose Task wrappers without Async suffixes.",
            "Suppress Razor SpecializedTasks VSTHRD200",
            "Razor's shared SpecializedTasks helper mirrors Roslyn's cached Task-wrapper APIs, so the merged tree should suppress VSTHRD200 on those members instead of renaming the established utility surface.",
            SuppressRazorSpecializedTasksVSTHRD200Async),
        new(
            "fix-razor-enum-gethashcode-ban",
            "Rewrite Razor's DocumentationId hash-code calculation to cast the enum to an integral type instead of calling Enum.GetHashCode().",
            "Fix Razor enum hash-code ban",
            "Roslyn bans Enum.GetHashCode because it can box on .NET Framework, so the merged Razor tree should cast DocumentationId to int in DocumentationDescriptor.SimpleDescriptor.ComputeHashCode instead.",
            FixRazorEnumGetHashCodeBanAsync),
        new(
            "make-razorsyntaxgenerator-program-static",
            "Make RazorSyntaxGenerator.Program static to satisfy Roslyn's style analyzers.",
            "Make RazorSyntaxGenerator.Program static",
            "RazorSyntaxGenerator.Program only exposes static entry-point helpers, so the merged Roslyn tree should mark it static to match Roslyn's analyzer expectations.",
            MakeRazorSyntaxGeneratorProgramStaticAsync),
        new(
            "normalize-razor-unit-test-detection",
            "Rename Razor unit test projects to Roslyn's UnitTests convention and keep their references aligned.",
            "Align Razor test infrastructure with Roslyn",
            "Inside the merged Roslyn tree, Razor unit test projects should be renamed to Roslyn's *.UnitTests convention so CLI and Visual Studio discovery work without TargetFileName or AssemblyName hacks, while the microbenchmark generator helper should not be treated as a Roslyn unit test assembly.",
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
            "normalize-razor-warning-cleanups",
            "Adjust Razor Live Share helpers and the code-folding integration test to compile cleanly under Roslyn's warning set.",
            "Normalize Razor warning cleanup",
            "Razor's Live Share factories and code-folding test need a few nullability-safe and definite-assignment-safe tweaks in the merged Roslyn tree so build.cmd -restore can stay clean after the merge.",
            NormalizeRazorBuildWarningsAsync),
        // Temporarily disabled so a fresh rerun can show the unsuppressed warning behavior for review.
        /*
        new(
            "normalize-razor-warning-baseline",
            "Apply a Razor-local analyzer baseline so Roslyn's broader repo-wide style rules do not flood the merged build with non-functional warnings.",
            "Normalize Razor warning baseline",
            "Razor brings its own analyzer and style expectations, but the merged Roslyn tree enables additional repo-wide rules that surface hundreds of non-functional warnings. The post-merge cleanup should localize those severities under src\\Razor so build validation stays focused on real merge regressions.",
            NormalizeRazorWarningBaselineAsync),
        */
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
            "fix-razor-modifier-ordering",
            "Normalize Razor modifier ordering to satisfy Roslyn's IDE0036 analyzer.",
            "Fix Razor modifier ordering",
            "Razor's StringExtensions helper and ContainedLanguage project still carry a few legacy modifier-order spellings such as `public unsafe static`, `public async override`, and `readonly static`, but the merged Roslyn tree enforces Roslyn's preferred modifier order for those declarations.",
            FixRazorModifierOrderingAsync),
        new(
            "suppress-razorpackage-vssdk003",
            "Suppress VSSDK003 on RazorPackage's legacy syntax visualizer tool-window registration.",
            "Suppress RazorPackage VSSDK003",
            "Razor's Visual Studio extension still registers its syntax visualizer with a synchronous ProvideToolWindow attribute, and the merged Roslyn tree surfaces VSSDK003 for that legacy pattern. The post-merge cleanup should suppress that one warning at the attribute site instead of broadening the suppression scope.",
            SuppressRazorPackageVSSDK003Async),
        new(
            "suppress-nestedfile-threadhelper-rs0030",
            "Suppress RS0030 on NestedFileCommandHandler's legacy ThreadHelper.JoinableTaskFactory usage.",
            "Suppress NestedFile ThreadHelper RS0030",
            "Razor's NestedFileCommandHandler still uses ThreadHelper.JoinableTaskFactory in two legacy Visual Studio extension call sites, and the merged Roslyn tree bans that API in favor of IThreadingContext.JoinableTaskFactory. The post-merge cleanup should suppress those two warning sites locally instead of broadening the suppression scope.",
            SuppressNestedFileThreadHelperRS0030Async),
        new(
            "normalize-razor-parsetext-sourcetext",
            "Rewrite Razor's string-based CSharpSyntaxTree.ParseText calls to use SourceText.",
            "Normalize Razor ParseText SourceText usage",
            "Razor's banned-symbols policy disallows the string-based CSharpSyntaxTree.ParseText overload, so the merged Roslyn tree should wrap Razor's test and generator inputs in SourceText instead of relying on the banned API.",
            NormalizeRazorParseTextSourceTextAsync),
        new(
            "rewrite-sdk-razor-package-paths",
            @"Rewrite Razor Microsoft.NET.Sdk.Razor asset paths from $(PkgMicrosoft_NET_Sdk_Razor)\build\netstandard2.0 to $(PkgMicrosoft_NET_Sdk_Razor)\targets.",
            "Rewrite Razor SDK package asset paths",
            @"The merged Roslyn tree consumes Microsoft.NET.Sdk.Razor assets from $(PkgMicrosoft_NET_Sdk_Razor)\targets, so Razor projects and VSIX content should stop referencing the old build\netstandard2.0 layout.",
            RewriteSdkRazorPackagePathsAsync),
        new(
            "normalize-razor-moq-compatibility",
            "Normalize Razor's Moq usage and Moq-related banned-symbol state for Roslyn's shared test stack.",
            "Normalize Razor Moq compatibility",
            "Razor test code in the merged Roslyn tree should use only the Moq APIs Roslyn's shared package version exposes, express strict Mock.Of behavior explicitly, and defer duplicate constructor bans to Roslyn's shared banned-symbol list.",
            NormalizeRazorMoqCompatibilityAsync),
        new(
            "normalize-razor-xunit-analyzers",
            "Normalize Razor test patterns that Roslyn's shared xUnit analyzers flag.",
            "Normalize Razor xUnit analyzers",
            "Razor tests in the merged Roslyn tree should use explicit TheoryData types and xUnit's direct predicate overloads instead of older TheoryData and filtered Assert.Single/Assert.Empty patterns that Roslyn's shared xUnit analyzers flag.",
            NormalizeRazorXunitAnalyzersAsync),
        new(
            "rewrite-razor-pack-content-paths",
            "Rewrite Razor pack content includes to consume Roslyn artifact outputs instead of relying on local OutDir and PublishDir copies.",
            "Rewrite Razor pack content paths",
            @"Roslyn's build layout keeps Razor project outputs under artifacts\bin instead of copying them into the local pack project's OutDir or PublishDir, so Razor pack projects should reference those artifact paths directly when packaging compiler and workspace binaries.",
            RewriteRazorPackContentPathsAsync),
        new(
            "move-razor-shipping-symbol-packages",
            "Move Razor shipping .symbols.nupkg files out of the Shipping package directory before Roslyn's release repack runs.",
            "Move Razor shipping symbols packages",
            @"Roslyn's release-packaging pass repacks every *.nupkg in the Shipping package directory after build.cmd -pack, so Razor's legacy .symbols.nupkg files should be moved aside after pack instead of colliding with the release repack step.",
            MoveRazorShippingSymbolPackagesAsync),
        new(
            "restore-razor-versioning-props",
            "Restore Razor's local package and VSIX versioning props in the merged root Directory.Build.props.",
            "Restore Razor versioning props",
            @"Standalone Razor defines both its package version line and its VSIX tooling version line in eng\Versions.props, so the merged Razor subtree should restore those property groups locally instead of inheriting Roslyn's package and VSIX version defaults.",
            RestoreRazorVersioningPropsAsync),
        new(
            "restore-razor-vsix-dev-assets",
            "Restore Razor's local VSIX packaging targets and strip Roslyn-only VSIX content so developer builds match standalone Razor.",
            "Restore Razor VSIX dev assets",
            @"Standalone Razor uses its own VSIX packaging targets to keep ServiceHub assets under ServiceHubCore, generate the brokered-services pkgdef/clientenabledpkg artifacts, and avoid Roslyn-only VSIX content, so the merged Razor subtree should restore those local targets instead of inheriting Roslyn's generic VSIX layout.",
            RestoreRazorVsixDevAssetsAsync),
        new(
            "ensure-origin-remote",
            "Ensure the merged target repo exposes Roslyn's target remote as origin for clean source-package SourceLink generation.",
            "Ensure origin remote for source packages",
            "Arcade source-package packing regenerates SourceLink targets from the repo's Git metadata on clean runs. RepoMerger's merged worktree keeps Roslyn under a target remote, so adding a matching origin remote lets SourceLink populate the repo-root ScmRepositoryUrl metadata that clean build.cmd -pack runs require.",
            EnsureOriginRemoteAsync),
        new(
            "disable-visualstudio-razor-ca2007",
            "Disable CA2007 across Razor's Visual Studio-facing projects via local editorconfigs.",
            "Disable Visual Studio Razor CA2007",
            "The merged Roslyn build surfaces CA2007 across Microsoft.VisualStudio.LanguageServer.ContainedLanguage, Microsoft.VisualStudio.RazorExtension, and Microsoft.VisualStudio.LanguageServices.Razor, so Razor should suppress that rule in project-local editorconfigs instead of churning established Visual Studio async code during post-merge cleanup.",
            DisableVisualStudioRazorCA2007Async),
        new(
            "fix-razor-formattingservice-ca1802",
            "Rewrite RazorFormattingService.FirstTriggerCharacter from static readonly to const.",
            "Fix RazorFormattingService CA1802",
            "RazorFormattingService.FirstTriggerCharacter is a simple string literal used as a trigger-character value, so the merged Roslyn tree can safely make it const to satisfy CA1802 without changing behavior.",
            FixRazorFormattingServiceCA1802Async),
        new(
            "suppress-semantic-token-field-ca1802",
            "Suppress CA1802 in semantic-token field containers that are discovered via reflection.",
            "Suppress semantic token field CA1802",
            "AbstractRazorSemanticTokensLegendService.GetStaticFieldValues reflects over SemanticTokenModifiers and SemanticTokenTypes static fields, so the merged Roslyn tree should suppress CA1802 in those files instead of converting the fields to const and changing the reflected field set.",
            SuppressSemanticTokenFieldCA1802Async),
        new(
            "use-formattingoptions2-for-razor-formatting",
            "Route Razor's C# formatting interaction path through a Razor-facing indent-style wrapper.",
            "Use FormattingOptions2 for Razor formatting",
            "Roslyn bans FormattingOptions in workspaces code, but FormattingOptions2 is not visible to Razor's workspaces layer. The merged tree should route the indent-style choice through a Razor-facing enum in the external-access bridge and convert to FormattingOptions2 inside that bridge.",
            UseFormattingOptions2ForRazorFormattingAsync),
        new(
            "fix-roslyn-codeactionhelpers-rs0030",
            "Use SourceText when creating the temporary document in RoslynCodeActionHelpers.",
            "Fix RoslynCodeActionHelpers RS0030",
            "Roslyn bans Project.AddDocument overloads that take raw strings because they lose encoding and checksum information. The merged tree should create SourceText with explicit UTF-8 and SHA-256 metadata and pass that to AddDocument instead.",
            FixRoslynCodeActionHelpersRS0030Async),
        new(
            "fix-renameprojecttreehandler-rs0030",
            "Switch RenameProjectTreeHandler from ThreadHelper to JoinableTaskContext.",
            "Fix RenameProjectTreeHandler RS0030",
            "Roslyn bans ThreadHelper.JoinableTaskFactory in Visual Studio code. This Razor project already uses JoinableTaskContext elsewhere, so the merged tree should inject JoinableTaskContext into RenameProjectTreeHandler and use its Factory for the UI-thread switch instead.",
            FixRenameProjectTreeHandlerRS0030Async),
        new(
            "fix-razor-readonly-fields-ide0044",
            "Make Razor fields readonly where they are only assigned during initialization.",
            "Fix Razor readonly fields IDE0044",
            "Several Razor fields in SyntaxVisualizerControl, AbstractMemoryLoggerProvider.Buffer, SnippetCache, and RenameProjectTreeHandler.WaitIndicator are assigned only during initialization and only their contents are mutated afterward, so the merged Roslyn tree can safely mark them readonly to satisfy IDE0044 without changing behavior.",
            FixRazorReadonlyFieldsIDE0044Async),
        new(
            "remove-razor-unused-usings",
            "Remove known stale using directives left behind by earlier Razor compatibility cleanups.",
            "Remove Razor unused usings",
            "A few Razor files end up with redundant using directives after the Moq and Visual Studio service-lookup normalizations, so the merged Roslyn tree should remove those exact stale usings instead of carrying IDE0005 warnings.",
            RemoveRazorUnusedUsingsAsync),
        new(
            "ensure-razor-codeowners",
            "Ensure Roslyn's .github\\CODEOWNERS routes src/Razor changes to @dotnet/razor-tooling.",
            "Ensure Razor CODEOWNERS ownership",
            "Standalone Razor routes src/Razor changes to @dotnet/razor-tooling, and the merged Roslyn tree should preserve that reviewer ownership in .github\\CODEOWNERS so Razor changes still request the correct team.",
            EnsureRazorCodeOwnersAsync),
        new(
            "copy-razor-skills",
            "Copy selected Razor Copilot skills into Roslyn's .github\\skills tree and rewrite them for the merged layout.",
            "Copy Razor skills into Roslyn",
            "Razor carries repo-local Copilot skills for toolset validation and formatting-log investigations, and the merged Roslyn tree should preserve those workflows under .github\\skills with their paths rewritten to Roslyn's nested src\\Razor layout and RepoMerger source-checkout workflow.",
            CopyRazorSkillsAsync),
        new(
            "fix-razor-language-configuration-test-path",
            "Normalize Razor test path probes for the merged src\\Razor layout.",
            "Fix Razor test path probes",
            "The merged Roslyn tree nests Razor sources under src\\Razor and renames some test projects to UnitTests, so Razor tests should normalize hard-coded file paths and shared project-directory probes instead of assuming the standalone layout and old Tests names.",
            FixRazorLanguageConfigurationTestPathAsync),
    ];

    public static IReadOnlyList<string> StepNames { get; } = Steps
        .Select(static step => step.Name)
        .ToArray();

    public static bool ContainsStep(string stepName)
        => StepNames.Contains(stepName, StringComparer.OrdinalIgnoreCase);

    public static async Task<string> RunAsync(StageContext context)
    {
        var targetRoot = context.TargetRoot;
        var stepsToRun = ResolveStepsToRun(context.Settings.PostMergeCleanupStep);

        if (context.Settings.DryRun)
        {
            foreach (var step in stepsToRun)
            {
                context.State.CleanupResults.Add(new CleanupExecutionResult(
                    step.Name,
                    step.CommitMessage,
                    TimeSpan.Zero,
                    "dry-run",
                    "Dry run: not executed."));
            }

            return
                $"Dry run: would apply {stepsToRun.Length} post-merge cleanup step(s) under '{targetRoot}', " +
                "committing each cleanup separately.";
        }

        if (!Directory.Exists(targetRoot))
        {
            throw new InvalidOperationException(
                $"The merged target path '{targetRoot}' does not exist. Run the merge-into-target stage first.");
        }

        var summaries = new List<string>();
        for (var i = 0; i < stepsToRun.Length; i++)
        {
            var step = stepsToRun[i];
            Console.WriteLine($"Running post-merge cleanup step {i + 1}/{stepsToRun.Length}: {step.Name}");
            var stepStopwatch = Stopwatch.StartNew();

            try
            {
                var stepSummary = await step.ExecuteAsync(context).ConfigureAwait(false);
                var committed = await GitRunner.CommitTrackedChangesAsync(
                    context.TargetRepoRoot,
                    step.CommitMessage,
                    step.CommitRationale).ConfigureAwait(false);
                stepStopwatch.Stop();

                if (committed)
                {
                    context.State.TargetHeadCommit = await GitRunner.GetHeadCommitAsync(context.TargetRepoRoot).ConfigureAwait(false);
                    summaries.Add($"{stepSummary} Committed as '{context.State.TargetHeadCommit}'.");
                }
                else
                {
                    summaries.Add($"{stepSummary} No commit was needed.");
                }

                context.State.CleanupResults.Add(new CleanupExecutionResult(
                    step.Name,
                    step.CommitMessage,
                    stepStopwatch.Elapsed,
                    committed ? "committed" : "no commit",
                    stepSummary));
            }
            catch (Exception ex)
            {
                stepStopwatch.Stop();
                context.State.CleanupResults.Add(new CleanupExecutionResult(
                    step.Name,
                    step.CommitMessage,
                    stepStopwatch.Elapsed,
                    "failed",
                    ex.Message));
                throw;
            }
        }

        return $"Applied post-merge cleanup stage.{Environment.NewLine}- {string.Join($"{Environment.NewLine}- ", summaries)}";
    }

    private static CleanupStep[] ResolveStepsToRun(string? postMergeCleanupStep)
    {
        if (string.IsNullOrWhiteSpace(postMergeCleanupStep))
            return Steps;

        var selectedStep = Steps.FirstOrDefault(step =>
            string.Equals(step.Name, postMergeCleanupStep, StringComparison.OrdinalIgnoreCase));

        if (selectedStep is null)
        {
            throw new InvalidOperationException(
                $"Unknown post-merge cleanup step '{postMergeCleanupStep}'. " +
                $"Available steps: {string.Join(", ", StepNames)}");
        }

        return [selectedStep];
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

        var sourceSdkEntries = sourceMsbuildSdks
            .Select(static sdkEntry => new KeyValuePair<string, string?>(
                sdkEntry.Key,
                sdkEntry.Value?.ToJsonString()))
            .ToList();

        var addedSdkEntries = new List<string>();
        foreach (var sdkEntry in sourceSdkEntries)
        {
            if (targetMsbuildSdks.ContainsKey(sdkEntry.Key))
                continue;

            targetMsbuildSdks[sdkEntry.Key] = sdkEntry.Value is null
                ? null
                : JsonNode.Parse(sdkEntry.Value);
            addedSdkEntries.Add($"{sdkEntry.Key}={sdkEntry.Value ?? "null"}");
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

    private static async Task<string> MergeRazorPublishDataAsync(StageContext context)
    {
        var targetRepoRoot = context.TargetRepoRoot;
        var sourcePublishDataPath = Path.Combine(context.State.SourceCloneDirectory, "eng", "config", "PublishData.json");
        if (!File.Exists(sourcePublishDataPath))
            return "No Razor PublishData.json file was found for publish metadata cleanup.";

        var targetPublishDataPath = Path.Combine(targetRepoRoot, "eng", "config", "PublishData.json");
        if (!File.Exists(targetPublishDataPath))
            return "No repo-root eng\\config\\PublishData.json file was found for publish metadata cleanup.";

        var sourcePublishData = await LoadJsonObjectAsync(sourcePublishDataPath).ConfigureAwait(false);
        if (sourcePublishData["packages"] is not JsonObject sourcePackages || sourcePackages.Count == 0)
            return "No Razor package publish entries were found in source PublishData.json.";

        var targetPublishData = await LoadJsonObjectAsync(targetPublishDataPath).ConfigureAwait(false);
        if (targetPublishData["packages"] is not JsonObject targetPackages)
        {
            targetPackages = [];
            targetPublishData["packages"] = targetPackages;
        }

        var sourceFeeds = sourcePublishData["feeds"] as JsonObject;
        var targetFeeds = targetPublishData["feeds"] as JsonObject;
        var addedPackages = new List<string>();
        var addedFeeds = new List<string>();

        foreach (var packageEntry in EnumeratePublishDataPackageEntries(sourcePackages))
        {
            if (targetPackages.ContainsKey(packageEntry.Key))
                continue;

            targetPackages[packageEntry.Key] = packageEntry.Value?.DeepClone();
            addedPackages.Add(packageEntry.Key);

            if (sourceFeeds is null || targetFeeds is null || packageEntry.Value is not JsonValue feedNameNode)
                continue;

            if (!feedNameNode.TryGetValue<string>(out var feedName) || string.IsNullOrWhiteSpace(feedName))
                continue;

            if (targetFeeds.ContainsKey(feedName) || !sourceFeeds.TryGetPropertyValue(feedName, out var sourceFeedValue))
                continue;

            targetFeeds[feedName] = sourceFeedValue?.DeepClone();
            addedFeeds.Add(feedName);
        }

        if (addedPackages.Count == 0 && addedFeeds.Count == 0)
            return "No Razor PublishData package merge was needed.";

        await SaveJsonAsync(targetPublishData, targetPublishDataPath).ConfigureAwait(false);

        var summaryParts = new List<string>();
        if (addedPackages.Count > 0)
        {
            summaryParts.Add(
                $"Added {addedPackages.Count} Razor package publish entr{(addedPackages.Count == 1 ? "y" : "ies")} to '{Path.GetRelativePath(targetRepoRoot, targetPublishDataPath)}': {string.Join(", ", addedPackages)}.");
        }

        if (addedFeeds.Count > 0)
        {
            summaryParts.Add(
                $"Also added {addedFeeds.Count} missing feed definition{(addedFeeds.Count == 1 ? string.Empty : "s")}: {string.Join(", ", addedFeeds)}.");
        }

        return string.Join(" ", summaryParts);
    }

    private static async Task<string> OverlayRazorRootEditorConfigAsync(StageContext context)
    {
        var targetRepoRoot = context.TargetRepoRoot;
        var targetRoot = context.TargetRoot;
        var sourceRoot = context.State.SourceCloneDirectory;
        var copiedFiles = new List<string>();

        foreach (var fileName in new[] { ".editorconfig", "SpellingExclusions.dic" })
        {
            var sourcePath = Path.Combine(sourceRoot, fileName);
            var targetPath = Path.Combine(targetRoot, fileName);
            if (!await CopyFileIfDifferentAsync(sourcePath, targetPath).ConfigureAwait(false))
                continue;

            await GitRunner.RunGitAsync(
                targetRepoRoot,
                "add",
                "--",
                Path.GetRelativePath(targetRepoRoot, targetPath)).ConfigureAwait(false);
            copiedFiles.Add(Path.GetRelativePath(targetRepoRoot, targetPath));
        }

        return copiedFiles.Count == 0
            ? "No Razor .editorconfig overlay changes were needed."
            : $"Copied or updated {copiedFiles.Count} Razor root config file(s): {string.Join(", ", copiedFiles)}.";
    }

    private static async Task<string> OverlayRazorBannedSymbolsAsync(StageContext context)
    {
        var targetRepoRoot = context.TargetRepoRoot;
        var targetRoot = context.TargetRoot;
        var sourceEngRoot = Path.Combine(context.State.SourceCloneDirectory, "eng");
        if (!Directory.Exists(sourceEngRoot))
            return "No Razor eng folder was found for banned-symbol cleanup.";

        var copiedFiles = new List<string>();
        foreach (var fileName in new[] { "BannedSymbols.txt", "BannedSymbols.MEFv1.txt", "BannedSymbols.MEFv2.txt" })
        {
            var sourcePath = Path.Combine(sourceEngRoot, fileName);
            var targetPath = Path.Combine(targetRoot, fileName);
            if (!await CopyFileIfDifferentAsync(sourcePath, targetPath).ConfigureAwait(false))
                continue;

            await GitRunner.RunGitAsync(
                targetRepoRoot,
                "add",
                "--",
                Path.GetRelativePath(targetRepoRoot, targetPath)).ConfigureAwait(false);
            copiedFiles.Add(Path.GetRelativePath(targetRepoRoot, targetPath));
        }

        var rewrittenFiles = await RewriteRazorBannedSymbolReferencesAsync(context).ConfigureAwait(false);
        var removedLegacyFiles = await RemoveLegacyRazorBannedSymbolOverlayFilesAsync(context).ConfigureAwait(false);

        if (copiedFiles.Count == 0 && rewrittenFiles.Count == 0 && removedLegacyFiles.Count == 0)
            return "No Razor banned-symbol overlay files were needed.";

        var summaryParts = new List<string>();
        if (copiedFiles.Count > 0)
            summaryParts.Add($"Copied or updated {copiedFiles.Count} Razor banned-symbol file(s): {string.Join(", ", copiedFiles)}.");
        if (rewrittenFiles.Count > 0)
            summaryParts.Add($"Updated Razor banned-symbol references in {rewrittenFiles.Count} file(s): {string.Join(", ", rewrittenFiles)}.");
        if (removedLegacyFiles.Count > 0)
            summaryParts.Add($"Removed {removedLegacyFiles.Count} legacy Razor banned-symbol file(s): {string.Join(", ", removedLegacyFiles)}.");

        return string.Join(" ", summaryParts);
    }

    private static async Task<List<string>> RewriteRazorBannedSymbolReferencesAsync(StageContext context)
    {
        var targetRepoRoot = context.TargetRepoRoot;
        var targetRoot = context.TargetRoot;
        var changedFiles = new List<string>();

        foreach (var path in EnumerateMsBuildFiles(targetRoot))
        {
            var originalContent = await File.ReadAllTextAsync(path).ConfigureAwait(false);
            var updatedContent = originalContent
                .Replace(@"$(MSBuildThisFileDirectory)eng\BannedSymbols.txt", @"$(RepositoryRoot)BannedSymbols.txt", StringComparison.Ordinal)
                .Replace(@"$(RepositoryEngineeringDir)BannedSymbols.txt", @"$(RepositoryRoot)BannedSymbols.txt", StringComparison.Ordinal)
                .Replace(@"$(RepositoryEngineeringDir)BannedSymbols.MEFv1.txt", @"$(RepositoryRoot)BannedSymbols.MEFv1.txt", StringComparison.Ordinal)
                .Replace(@"$(RepositoryEngineeringDir)BannedSymbols.MEFv2.txt", @"$(RepositoryRoot)BannedSymbols.MEFv2.txt", StringComparison.Ordinal);

            if (string.Equals(originalContent, updatedContent, StringComparison.Ordinal))
                continue;

            await WriteTextPreservingUtf8BomAsync(path, updatedContent, templatePath: path).ConfigureAwait(false);
            var relativePath = Path.GetRelativePath(targetRepoRoot, path);
            await GitRunner.RunGitAsync(targetRepoRoot, "add", "--", relativePath).ConfigureAwait(false);
            changedFiles.Add(relativePath);
        }

        return changedFiles;
    }

    private static async Task<List<string>> RemoveLegacyRazorBannedSymbolOverlayFilesAsync(StageContext context)
    {
        var targetRepoRoot = context.TargetRepoRoot;
        var targetRoot = context.TargetRoot;
        var removedFiles = new List<string>();

        foreach (var path in new[]
        {
            Path.Combine(targetRoot, "eng", "BannedSymbols.txt"),
            Path.Combine(targetRepoRoot, "eng", "BannedSymbols.MEFv1.txt"),
            Path.Combine(targetRepoRoot, "eng", "BannedSymbols.MEFv2.txt"),
        })
        {
            if (!File.Exists(path))
                continue;

            File.Delete(path);
            var relativePath = Path.GetRelativePath(targetRepoRoot, path);
            await GitRunner.RunGitAsync(targetRepoRoot, "add", "--all", "--", relativePath).ConfigureAwait(false);
            removedFiles.Add(relativePath);
        }

        return removedFiles;
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

    private static async Task<string> EnsureOriginRemoteAsync(StageContext context)
    {
        var targetRepoRoot = context.TargetRepoRoot;
        var remoteList = await GitRunner.RunGitAsync(targetRepoRoot, "remote").ConfigureAwait(false);
        var remotes = remoteList
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (!remotes.Contains("target"))
        {
            return remotes.Contains("origin")
                ? "Origin remote already existed and no target remote needed mirroring."
                : "No target remote was found to mirror as origin for clean SourceLink generation.";
        }

        var targetRemoteUrl = await GitRunner.GetRemoteUrlAsync(targetRepoRoot, "target").ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(targetRemoteUrl))
            return "The target remote did not have a URL to mirror as origin for clean SourceLink generation.";

        var originExisted = remotes.Contains("origin");
        var existingOriginUrl = originExisted
            ? await GitRunner.GetRemoteUrlAsync(targetRepoRoot, "origin").ConfigureAwait(false)
            : null;

        await GitRunner.EnsureRemoteAsync(targetRepoRoot, "origin", targetRemoteUrl).ConfigureAwait(false);

        if (!originExisted)
        {
            return $"Added origin remote pointing to '{targetRemoteUrl}' so Arcade source packages can populate repo-root SourceLink metadata on clean pack runs.";
        }

        if (PathHelper.RepositoryLocationsMatch(existingOriginUrl!, targetRemoteUrl))
            return $"Origin remote already pointed to '{targetRemoteUrl}', so no SourceLink remote update was needed.";

        return $"Updated origin remote from '{existingOriginUrl}' to '{targetRemoteUrl}' so Arcade source packages can populate repo-root SourceLink metadata on clean pack runs.";
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

    private static async Task<string> DisableRazorNonShippingPublicApiAnalyzersAsync(StageContext context)
    {
        var targetRepoRoot = context.TargetRepoRoot;
        var targetRoot = context.TargetRoot;
        var changedFiles = new List<string>();

        foreach (var path in Directory.EnumerateFiles(targetRoot, "*.csproj", SearchOption.AllDirectories))
        {
            var relativePath = NormalizeRelativePath(Path.GetRelativePath(targetRoot, path));
            var projectName = Path.GetFileNameWithoutExtension(path);
            if (!ShouldDisableRazorPublicApiAnalyzers(relativePath, projectName))
                continue;

            var originalContent = await File.ReadAllTextAsync(path).ConfigureAwait(false);
            var updatedContent = EnsureBooleanPropertyValue(originalContent, "AddPublicApiAnalyzers", false);
            updatedContent = EnsureBooleanPropertyValue(updatedContent, "IsShipping", false);
            updatedContent = EnsureNoWarnContains(updatedContent, "RS0016", "RS0017", "RS0018", "RS0022", "RS0026", "RS0027");

            if (string.Equals(originalContent, updatedContent, StringComparison.Ordinal))
                continue;

            await WriteTextPreservingUtf8BomAsync(path, updatedContent, templatePath: path).ConfigureAwait(false);
            changedFiles.Add(Path.GetRelativePath(targetRepoRoot, path));
        }

        return changedFiles.Count == 0
            ? "No Razor non-shipping public API analyzer changes were needed."
            : $"Disabled Roslyn public API analyzers for {changedFiles.Count} Razor non-shipping project(s): {string.Join(", ", changedFiles)}.";
    }

    private static async Task<string> SuppressRazorSpecializedTasksVSTHRD200Async(StageContext context)
    {
        var targetRepoRoot = context.TargetRepoRoot;
        var specializedTasksPath = Path.Combine(
            context.TargetRoot,
            "src",
            "Shared",
            "Microsoft.AspNetCore.Razor.Utilities.Shared",
            "Threading",
            "SpecializedTasks.cs");

        if (!File.Exists(specializedTasksPath))
            return "No Razor SpecializedTasks.cs file was found for VSTHRD200 suppression.";

        const string suppressionAttribute = """[SuppressMessage("Style", "VSTHRD200:Use \"Async\" suffix for async methods", Justification = "This is a Task wrapper, not an asynchronous method.")]""";
        var originalContent = await File.ReadAllTextAsync(specializedTasksPath).ConfigureAwait(false);
        var updatedContent = originalContent;

        if (!updatedContent.Contains("using System.Diagnostics.CodeAnalysis;", StringComparison.Ordinal))
        {
            updatedContent = updatedContent.Replace(
                "using System.Collections.Immutable;" + Environment.NewLine,
                "using System.Collections.Immutable;" + Environment.NewLine +
                "using System.Diagnostics.CodeAnalysis;" + Environment.NewLine,
                StringComparison.Ordinal);
        }

        foreach (var signature in new[]
        {
            "public static Task<T?> AsNullable<T>(this Task<T> task) where T : class",
            "public static Task<T?> Default<T>()",
            "public static Task<T?> Null<T>() where T : class",
            "public static Task<IReadOnlyList<T>> EmptyReadOnlyList<T>()",
            "public static Task<IList<T>> EmptyList<T>()",
            "public static Task<ImmutableArray<T>> EmptyImmutableArray<T>()",
            "public static Task<IEnumerable<T>> EmptyEnumerable<T>()",
            "public static Task<T[]> EmptyArray<T>()",
        })
        {
            updatedContent = updatedContent.Replace(
                "    " + signature,
                "    " + suppressionAttribute + Environment.NewLine + "    " + signature,
                StringComparison.Ordinal);
        }

        if (string.Equals(originalContent, updatedContent, StringComparison.Ordinal))
            return "No Razor SpecializedTasks VSTHRD200 suppression changes were needed.";

        await WriteTextPreservingUtf8BomAsync(specializedTasksPath, updatedContent, templatePath: specializedTasksPath).ConfigureAwait(false);
        return $"Added VSTHRD200 suppressions to '{Path.GetRelativePath(targetRepoRoot, specializedTasksPath)}' for Razor's cached Task helper APIs.";
    }

    private static async Task<string> FixRazorEnumGetHashCodeBanAsync(StageContext context)
    {
        var targetRepoRoot = context.TargetRepoRoot;
        var documentationDescriptorPath = Path.Combine(
            context.TargetRoot,
            "src",
            "Compiler",
            "Microsoft.CodeAnalysis.Razor.Compiler",
            "src",
            "Language",
            "DocumentationDescriptor.SimpleDescriptor.cs");

        if (!File.Exists(documentationDescriptorPath))
            return "No Razor DocumentationDescriptor.SimpleDescriptor.cs file was found for enum hash-code cleanup.";

        var originalContent = await File.ReadAllTextAsync(documentationDescriptorPath).ConfigureAwait(false);
        var updatedContent = originalContent.Replace(
            "            => Id.GetHashCode();",
            "            => (int)Id;",
            StringComparison.Ordinal);

        if (string.Equals(originalContent, updatedContent, StringComparison.Ordinal))
            return "No Razor enum hash-code cleanup changes were needed.";

        await WriteTextPreservingUtf8BomAsync(documentationDescriptorPath, updatedContent, templatePath: documentationDescriptorPath).ConfigureAwait(false);
        return $"Rewrote DocumentationId hash-code calculation in '{Path.GetRelativePath(targetRepoRoot, documentationDescriptorPath)}' to avoid Enum.GetHashCode() boxing.";
    }

    private static async Task<string> MakeRazorSyntaxGeneratorProgramStaticAsync(StageContext context)
    {
        var targetRepoRoot = context.TargetRepoRoot;
        var programPath = Path.Combine(
            context.TargetRoot,
            "src",
            "Compiler",
            "tools",
            "RazorSyntaxGenerator",
            "Program.cs");

        if (!File.Exists(programPath))
            return "No RazorSyntaxGenerator Program.cs file was found for static-class cleanup.";

        var originalContent = await File.ReadAllTextAsync(programPath).ConfigureAwait(false);
        var updatedContent = originalContent.Replace(
            "public class Program",
            "public static class Program",
            StringComparison.Ordinal);

        if (string.Equals(originalContent, updatedContent, StringComparison.Ordinal))
            return "No RazorSyntaxGenerator Program static-class cleanup changes were needed.";

        await WriteTextPreservingUtf8BomAsync(programPath, updatedContent, templatePath: programPath).ConfigureAwait(false);
        return $"Made '{Path.GetRelativePath(targetRepoRoot, programPath)}' static to satisfy Roslyn's analyzer expectations.";
    }

    private static async Task<string> FixRazorModifierOrderingAsync(StageContext context)
    {
        var targetRepoRoot = context.TargetRepoRoot;
        var targetRoot = context.TargetRoot;
        var (changedFiles, replacementCount) = await ApplyKnownFileTextReplacementsAsync(
            targetRepoRoot,
            (Path.Combine(targetRoot, "src", "Shared", "Microsoft.AspNetCore.Razor.Utilities.Shared", "StringExtensions.cs"),
            [
                (
                    "public unsafe static string Create<TState>(int length, TState state, SpanAction<char, TState> action)",
                    "public static unsafe string Create<TState>(int length, TState state, SpanAction<char, TState> action)")
            ]),
            (Path.Combine(targetRoot, "src", "Razor", "src", "Microsoft.VisualStudio.LanguageServer.ContainedLanguage", "DefaultLSPRequestInvoker.cs"),
            [
                (
                    "    public async override Task<ReinvokeResponse<TOut>> ReinvokeRequestOnServerAsync<TIn, TOut>(",
                    "    public override async Task<ReinvokeResponse<TOut>> ReinvokeRequestOnServerAsync<TIn, TOut>(")
            ]),
            (Path.Combine(targetRoot, "src", "Razor", "src", "Microsoft.VisualStudio.LanguageServer.ContainedLanguage", "InviolableEditTag.cs"),
            [
                (
                    "    public readonly static IInviolableEditTag Instance = new InviolableEditTag();",
                    "    public static readonly IInviolableEditTag Instance = new InviolableEditTag();")
            ]),
            (Path.Combine(targetRoot, "src", "Razor", "src", "Microsoft.VisualStudio.LanguageServer.ContainedLanguage", "MessageInterception", "DefaultInterceptorManager.cs"),
            [
                (
                    "    public async override Task<TJsonToken?> ProcessGenericInterceptorsAsync<TJsonToken>(string methodName, TJsonToken message, string contentType, CancellationToken cancellationToken)",
                    "    public override async Task<TJsonToken?> ProcessGenericInterceptorsAsync<TJsonToken>(string methodName, TJsonToken message, string contentType, CancellationToken cancellationToken)")
            ])).ConfigureAwait(false);

        return changedFiles.Count == 0
            ? "No Razor modifier-order cleanup changes were needed."
            : $"Fixed {replacementCount} modifier-order declaration(s) in {changedFiles.Count} Razor file(s): {string.Join(", ", changedFiles)}.";
    }

    private static async Task<string> SuppressRazorPackageVSSDK003Async(StageContext context)
    {
        var targetRepoRoot = context.TargetRepoRoot;
        var razorPackagePath = Path.Combine(
            targetRepoRoot,
            "src",
            "Razor",
            "src",
            "Razor",
            "src",
            "Microsoft.VisualStudio.RazorExtension",
            "RazorPackage.cs");

        if (!File.Exists(razorPackagePath))
            return "No RazorPackage.cs file was found for VSSDK003 suppression.";

        var originalText =
            "[ProvideMenuResource(\"Menus.ctmenu\", 1)]" + Environment.NewLine +
            "[ProvideToolWindow(typeof(SyntaxVisualizerToolWindow))]" + Environment.NewLine +
            "[ProvideSettingsManifest(PackageRelativeManifestFile = @\"UnifiedSettings\\razor.registration.json\")]";
        var updatedText =
            "[ProvideMenuResource(\"Menus.ctmenu\", 1)]" + Environment.NewLine +
            "#pragma warning disable VSSDK003 // Tool windows should support async construction" + Environment.NewLine +
            "[ProvideToolWindow(typeof(SyntaxVisualizerToolWindow))]" + Environment.NewLine +
            "#pragma warning restore VSSDK003 // Tool windows should support async construction" + Environment.NewLine +
            "[ProvideSettingsManifest(PackageRelativeManifestFile = @\"UnifiedSettings\\razor.registration.json\")]";

        var originalContent = await File.ReadAllTextAsync(razorPackagePath).ConfigureAwait(false);
        var updatedContent = originalContent.Replace(originalText, updatedText, StringComparison.Ordinal);

        if (string.Equals(originalContent, updatedContent, StringComparison.Ordinal))
            return "No RazorPackage VSSDK003 suppression changes were needed.";

        await WriteTextPreservingUtf8BomAsync(razorPackagePath, updatedContent, templatePath: razorPackagePath).ConfigureAwait(false);
        return $"Suppressed VSSDK003 in '{Path.GetRelativePath(targetRepoRoot, razorPackagePath)}' at Razor's syntax visualizer tool-window registration.";
    }

    private static async Task<string> SuppressNestedFileThreadHelperRS0030Async(StageContext context)
    {
        var targetRepoRoot = context.TargetRepoRoot;
        var nestedFileCommandHandlerPath = Path.Combine(
            targetRepoRoot,
            "src",
            "Razor",
            "src",
            "Razor",
            "src",
            "Microsoft.VisualStudio.RazorExtension",
            "NestedFiles",
            "NestedFileCommandHandler.cs");

        if (!File.Exists(nestedFileCommandHandlerPath))
            return "No NestedFileCommandHandler.cs file was found for RS0030 suppression.";

        var originalContent = await File.ReadAllTextAsync(nestedFileCommandHandlerPath).ConfigureAwait(false);
        var updatedContent = originalContent;

        var runAsyncOriginalText =
            "#pragma warning disable VSSDK007 // Fire-and-forget from synchronous EventHandler is intentional" + Environment.NewLine +
            "            ThreadHelper.JoinableTaskFactory.RunAsync(" + Environment.NewLine +
            "                () => CreateAndOpenNestedFileAsync(razorFilePath, nestedFilePath, CancellationToken.None)).FileAndForget(\"NestedFileCommandHandler.Execute\");" + Environment.NewLine +
            "#pragma warning restore VSSDK007";
        var runAsyncUpdatedText =
            "#pragma warning disable VSSDK007 // Fire-and-forget from synchronous EventHandler is intentional" + Environment.NewLine +
            "#pragma warning disable RS0030 // NestedFileCommandHandler does not currently flow IThreadingContext." + Environment.NewLine +
            "            ThreadHelper.JoinableTaskFactory.RunAsync(" + Environment.NewLine +
            "                () => CreateAndOpenNestedFileAsync(razorFilePath, nestedFilePath, CancellationToken.None)).FileAndForget(\"NestedFileCommandHandler.Execute\");" + Environment.NewLine +
            "#pragma warning restore RS0030" + Environment.NewLine +
            "#pragma warning restore VSSDK007";
        updatedContent = updatedContent.Replace(runAsyncOriginalText, runAsyncUpdatedText, StringComparison.Ordinal);

        var switchToMainThreadOriginalText =
            "            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);";
        var switchToMainThreadUpdatedText =
            "#pragma warning disable RS0030 // NestedFileCommandHandler does not currently flow IThreadingContext." + Environment.NewLine +
            "            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);" + Environment.NewLine +
            "#pragma warning restore RS0030";
        updatedContent = updatedContent.Replace(switchToMainThreadOriginalText, switchToMainThreadUpdatedText, StringComparison.Ordinal);

        if (string.Equals(originalContent, updatedContent, StringComparison.Ordinal))
            return "No NestedFileCommandHandler RS0030 suppression changes were needed.";

        await WriteTextPreservingUtf8BomAsync(nestedFileCommandHandlerPath, updatedContent, templatePath: nestedFileCommandHandlerPath).ConfigureAwait(false);
        return $"Suppressed RS0030 in '{Path.GetRelativePath(targetRepoRoot, nestedFileCommandHandlerPath)}' at Razor's legacy ThreadHelper.JoinableTaskFactory call sites.";
    }

    private static async Task<string> DisableVisualStudioRazorCA2007Async(StageContext context)
    {
        var targetRepoRoot = context.TargetRepoRoot;
        var targetRoot = context.TargetRoot;
        var changedEditorConfigs = new List<string>();
        var projectSuppressions = new (string ProjectDirectory, string ProjectDisplayName)[]
        {
            (
                Path.Combine(targetRoot, "src", "Razor", "src", "Microsoft.VisualStudio.LanguageServer.ContainedLanguage"),
                "Microsoft.VisualStudio.LanguageServer.ContainedLanguage"),
            (
                Path.Combine(targetRoot, "src", "Razor", "src", "Microsoft.VisualStudio.RazorExtension"),
                "Microsoft.VisualStudio.RazorExtension"),
            (
                Path.Combine(targetRoot, "src", "Razor", "src", "Microsoft.VisualStudio.LanguageServices.Razor"),
                "Microsoft.VisualStudio.LanguageServices.Razor"),
        };

        foreach (var projectSuppression in projectSuppressions)
        {
            var result = await EnsureProjectLocalEditorConfigSuppressionAsync(
                context,
                projectSuppression.ProjectDirectory,
                projectSuppression.ProjectDisplayName,
                "CA2007",
                "Call ConfigureAwait").ConfigureAwait(false);

            if (!result.StartsWith("Disabled ", StringComparison.Ordinal))
                continue;

            changedEditorConfigs.Add(Path.GetRelativePath(targetRepoRoot, Path.Combine(projectSuppression.ProjectDirectory, ".editorconfig")));
        }

        return changedEditorConfigs.Count == 0
            ? "No Visual Studio Razor CA2007 editorconfig changes were needed."
            : $"Disabled CA2007 in {changedEditorConfigs.Count} Visual Studio Razor editorconfig file(s): {string.Join(", ", changedEditorConfigs)}.";
    }

    private static async Task<string> FixRazorFormattingServiceCA1802Async(StageContext context)
    {
        var targetRepoRoot = context.TargetRepoRoot;
        var razorFormattingServicePath = Path.Combine(
            targetRepoRoot,
            "src",
            "Razor",
            "src",
            "Razor",
            "src",
            "Microsoft.CodeAnalysis.Razor.Workspaces",
            "Formatting",
            "RazorFormattingService.cs");

        if (!File.Exists(razorFormattingServicePath))
            return "No RazorFormattingService.cs file was found for CA1802 cleanup.";

        var originalContent = await File.ReadAllTextAsync(razorFormattingServicePath).ConfigureAwait(false);
        var updatedContent = originalContent.Replace(
            "    public static readonly string FirstTriggerCharacter = \"}\";",
            "    public const string FirstTriggerCharacter = \"}\";",
            StringComparison.Ordinal);

        if (string.Equals(originalContent, updatedContent, StringComparison.Ordinal))
            return "No RazorFormattingService CA1802 cleanup changes were needed.";

        await WriteTextPreservingUtf8BomAsync(razorFormattingServicePath, updatedContent, templatePath: razorFormattingServicePath).ConfigureAwait(false);
        return $"Rewrote '{Path.GetRelativePath(targetRepoRoot, razorFormattingServicePath)}' to use a const trigger-character field for CA1802 compliance.";
    }

    private static async Task<string> SuppressSemanticTokenFieldCA1802Async(StageContext context)
    {
        var targetRepoRoot = context.TargetRepoRoot;
        var targetRoot = context.TargetRoot;
        var changedFiles = new List<string>();
        var suppressions = new (string FilePath, string TypeName)[]
        {
            (
                Path.Combine(targetRoot, "src", "Razor", "src", "Microsoft.CodeAnalysis.Razor.Workspaces", "SemanticTokens", "SemanticTokenModifiers.cs"),
                "SemanticTokenModifiers"),
            (
                Path.Combine(targetRoot, "src", "Razor", "src", "Microsoft.CodeAnalysis.Razor.Workspaces", "SemanticTokens", "SemanticTokenTypes.cs"),
                "SemanticTokenTypes"),
        };

        foreach (var suppression in suppressions)
        {
            if (!File.Exists(suppression.FilePath))
                continue;

            var originalContent = await File.ReadAllTextAsync(suppression.FilePath).ConfigureAwait(false);
            if (originalContent.Contains("#pragma warning disable CA1802", StringComparison.Ordinal))
                continue;

            var usingIndex = originalContent.IndexOf("using ", StringComparison.Ordinal);
            if (usingIndex < 0)
                return $"No {suppression.TypeName} using-directive anchor was found for CA1802 suppression.";

            var pragmaBlock = string.Join(
                Environment.NewLine,
                [
                    "// AbstractRazorSemanticTokensLegendService.GetStaticFieldValues reflects over",
                    $"// {suppression.TypeName}' static fields, so they must remain fields instead of consts.",
                    "#pragma warning disable CA1802",
                ]) + Environment.NewLine + Environment.NewLine;
            var updatedContent = originalContent.Insert(usingIndex, pragmaBlock);

            await WriteTextPreservingUtf8BomAsync(suppression.FilePath, updatedContent, templatePath: suppression.FilePath).ConfigureAwait(false);
            changedFiles.Add(Path.GetRelativePath(targetRepoRoot, suppression.FilePath));
        }

        return changedFiles.Count == 0
            ? "No semantic-token CA1802 suppression changes were needed."
            : $"Suppressed CA1802 in {changedFiles.Count} semantic-token field file(s): {string.Join(", ", changedFiles)}.";
    }

    private static async Task<string> UseFormattingOptions2ForRazorFormattingAsync(StageContext context)
    {
        var targetRepoRoot = context.TargetRepoRoot;
        var formattingPassPath = Path.Combine(
            targetRepoRoot,
            "src",
            "Razor",
            "src",
            "Razor",
            "src",
            "Microsoft.CodeAnalysis.Razor.Workspaces",
            "Formatting",
            "Passes",
            "CSharpOnTypeFormattingPass.cs");
        var formattingInteractionServicePath = Path.Combine(
            targetRepoRoot,
            "src",
            "Tools",
            "ExternalAccess",
            "Razor",
            "Features",
            "RazorCSharpFormattingInteractionService.cs");

        if (!File.Exists(formattingPassPath))
            return "No CSharpOnTypeFormattingPass.cs file was found for RS0030 cleanup.";

        if (!File.Exists(formattingInteractionServicePath))
            return "No RazorCSharpFormattingInteractionService.cs file was found for RS0030 cleanup.";

        var changedPaths = new List<string>();

        var formattingPassOriginalContent = await File.ReadAllTextAsync(formattingPassPath).ConfigureAwait(false);
        var formattingPassUpdatedContent = formattingPassOriginalContent
            .Replace(
                "                indentStyle: CodeAnalysis.Formatting.FormattingOptions.IndentStyle.Smart,",
                "                indentStyle: RazorIndentStyle.Smart,",
                StringComparison.Ordinal)
            .Replace(
                "                indentStyle: CodeAnalysis.Formatting.FormattingOptions2.IndentStyle.Smart,",
                "                indentStyle: RazorIndentStyle.Smart,",
                StringComparison.Ordinal);

        if (!string.Equals(formattingPassOriginalContent, formattingPassUpdatedContent, StringComparison.Ordinal))
        {
            await WriteTextPreservingUtf8BomAsync(formattingPassPath, formattingPassUpdatedContent, templatePath: formattingPassPath).ConfigureAwait(false);
            changedPaths.Add(Path.GetRelativePath(targetRepoRoot, formattingPassPath));
        }

        var formattingInteractionServiceOriginalContent = await File.ReadAllTextAsync(formattingInteractionServicePath).ConfigureAwait(false);
        var formattingInteractionServiceUpdatedContent = formattingInteractionServiceOriginalContent
            .Replace(
                "            FormattingOptions.IndentStyle indentStyle,",
                "            RazorIndentStyle indentStyle,",
                StringComparison.Ordinal)
            .Replace(
                "            FormattingOptions2.IndentStyle indentStyle,",
                "            RazorIndentStyle indentStyle,",
                StringComparison.Ordinal);

        if (!formattingInteractionServiceUpdatedContent.Contains("internal enum RazorIndentStyle", StringComparison.Ordinal))
        {
            var summaryAnchor = "    /// <summary>";
            var summaryIndex = formattingInteractionServiceUpdatedContent.IndexOf(summaryAnchor, StringComparison.Ordinal);
            if (summaryIndex < 0)
                return "No RazorCSharpFormattingInteractionService summary anchor was found for RS0030 cleanup.";

            var enumBlock = string.Join(
                Environment.NewLine,
                [
                    "    internal enum RazorIndentStyle",
                    "    {",
                    "        None = 0,",
                    "        Block = 1,",
                    "        Smart = 2,",
                    "    }",
                    "",
                ]);
            formattingInteractionServiceUpdatedContent = formattingInteractionServiceUpdatedContent.Insert(summaryIndex, enumBlock);
        }

        formattingInteractionServiceUpdatedContent = formattingInteractionServiceUpdatedContent
            .Replace(
                "                IndentStyle = indentStyle",
                "                IndentStyle = (FormattingOptions2.IndentStyle)indentStyle",
                StringComparison.Ordinal);

        if (!string.Equals(formattingInteractionServiceOriginalContent, formattingInteractionServiceUpdatedContent, StringComparison.Ordinal))
        {
            await WriteTextPreservingUtf8BomAsync(formattingInteractionServicePath, formattingInteractionServiceUpdatedContent, templatePath: formattingInteractionServicePath).ConfigureAwait(false);
            changedPaths.Add(Path.GetRelativePath(targetRepoRoot, formattingInteractionServicePath));
        }

        if (changedPaths.Count == 0)
            return "No Razor FormattingOptions2 cleanup changes were needed.";

        return $"Switched Razor's C# formatting path to use a Razor-facing indent-style wrapper that bridges to FormattingOptions2 in {string.Join(" and ", changedPaths.Select(path => $"'{path}'"))}.";
    }

    private static async Task<string> FixRoslynCodeActionHelpersRS0030Async(StageContext context)
    {
        var targetRepoRoot = context.TargetRepoRoot;
        var roslynCodeActionHelpersPath = Path.Combine(
            targetRepoRoot,
            "src",
            "Razor",
            "src",
            "Razor",
            "src",
            "Microsoft.CodeAnalysis.Remote.Razor",
            "CodeActions",
            "RoslynCodeActionHelpers.cs");

        if (!File.Exists(roslynCodeActionHelpersPath))
            return "No RoslynCodeActionHelpers.cs file was found for RS0030 cleanup.";

        var originalContent = await File.ReadAllTextAsync(roslynCodeActionHelpersPath).ConfigureAwait(false);
        var updatedContent = originalContent;

        if (!updatedContent.Contains("using System.Text;", StringComparison.Ordinal))
        {
            updatedContent = updatedContent.Replace(
                "using System.Linq;" + Environment.NewLine,
                "using System.Linq;" + Environment.NewLine + "using System.Text;" + Environment.NewLine,
                StringComparison.Ordinal);
        }

        if (!updatedContent.Contains("using Microsoft.CodeAnalysis.Text;", StringComparison.Ordinal))
        {
            updatedContent = updatedContent.Replace(
                "using Microsoft.CodeAnalysis.Remote.Razor.ProjectSystem;" + Environment.NewLine,
                "using Microsoft.CodeAnalysis.Remote.Razor.ProjectSystem;" + Environment.NewLine + "using Microsoft.CodeAnalysis.Text;" + Environment.NewLine,
                StringComparison.Ordinal);
        }

        updatedContent = updatedContent.Replace(
            "        var document = project.AddDocument(RazorUri.GetDocumentFilePathFromUri(csharpFileUri), newFileContent);" + Environment.NewLine + Environment.NewLine +
            "        return ExternalHandlers.CodeActions.GetFormattedNewFileContentAsync(document, cancellationToken);",
            "        var filePath = RazorUri.GetDocumentFilePathFromUri(csharpFileUri);" + Environment.NewLine +
            "        var source = SourceText.From(newFileContent, Encoding.UTF8, checksumAlgorithm: SourceHashAlgorithm.Sha256);" + Environment.NewLine +
            "        var document = project.AddDocument(filePath, source, filePath: filePath);" + Environment.NewLine + Environment.NewLine +
            "        return ExternalHandlers.CodeActions.GetFormattedNewFileContentAsync(document, cancellationToken);",
            StringComparison.Ordinal);

        if (string.Equals(originalContent, updatedContent, StringComparison.Ordinal))
            return "No RoslynCodeActionHelpers RS0030 cleanup changes were needed.";

        await WriteTextPreservingUtf8BomAsync(roslynCodeActionHelpersPath, updatedContent, templatePath: roslynCodeActionHelpersPath).ConfigureAwait(false);
        return $"Updated '{Path.GetRelativePath(targetRepoRoot, roslynCodeActionHelpersPath)}' to add the temporary document from SourceText with explicit encoding and checksum metadata.";
    }

    private static async Task<string> FixRenameProjectTreeHandlerRS0030Async(StageContext context)
    {
        var targetRepoRoot = context.TargetRepoRoot;
        var renameProjectTreeHandlerPath = Path.Combine(
            targetRepoRoot,
            "src",
            "Razor",
            "src",
            "Razor",
            "src",
            "Microsoft.VisualStudio.LanguageServices.Razor",
            "ProjectSystem",
            "RenameProjectTreeHandler.cs");

        if (!File.Exists(renameProjectTreeHandlerPath))
            return "No RenameProjectTreeHandler.cs file was found for RS0030 cleanup.";

        var originalContent = await File.ReadAllTextAsync(renameProjectTreeHandlerPath).ConfigureAwait(false);
        var updatedContent = originalContent;

        updatedContent = updatedContent
            .Replace(
                "using Microsoft.CodeAnalysis.Editor.Shared.Utilities;" + Environment.NewLine,
                string.Empty,
                StringComparison.Ordinal)
            .Replace(
                "    Lazy<LSPRequestInvokerWrapper> requestInvoker," + Environment.NewLine +
                "    ILoggerFactory loggerFactory) : ProjectTreeActionHandlerBase",
                "    Lazy<LSPRequestInvokerWrapper> requestInvoker," + Environment.NewLine +
                "    JoinableTaskContext joinableTaskContext," + Environment.NewLine +
                "    ILoggerFactory loggerFactory) : ProjectTreeActionHandlerBase",
                StringComparison.Ordinal)
            .Replace(
                "    Lazy<LSPRequestInvokerWrapper> requestInvoker," + Environment.NewLine +
                "    IThreadingContext threadingContext," + Environment.NewLine +
                "    ILoggerFactory loggerFactory) : ProjectTreeActionHandlerBase",
                "    Lazy<LSPRequestInvokerWrapper> requestInvoker," + Environment.NewLine +
                "    JoinableTaskContext joinableTaskContext," + Environment.NewLine +
                "    ILoggerFactory loggerFactory) : ProjectTreeActionHandlerBase",
                StringComparison.Ordinal)
            .Replace(
                "    private readonly Lazy<LSPRequestInvokerWrapper> _requestInvoker = requestInvoker;" + Environment.NewLine +
                "    private readonly ILogger _logger = loggerFactory.GetOrCreateLogger<RenameProjectTreeHandler>();",
                "    private readonly Lazy<LSPRequestInvokerWrapper> _requestInvoker = requestInvoker;" + Environment.NewLine +
                "    private readonly JoinableTaskFactory _jtf = joinableTaskContext.Factory;" + Environment.NewLine +
                "    private readonly ILogger _logger = loggerFactory.GetOrCreateLogger<RenameProjectTreeHandler>();",
                StringComparison.Ordinal)
            .Replace(
                "    private readonly Lazy<LSPRequestInvokerWrapper> _requestInvoker = requestInvoker;" + Environment.NewLine +
                "    private readonly IThreadingContext _threadingContext = threadingContext;" + Environment.NewLine +
                "    private readonly ILogger _logger = loggerFactory.GetOrCreateLogger<RenameProjectTreeHandler>();",
                "    private readonly Lazy<LSPRequestInvokerWrapper> _requestInvoker = requestInvoker;" + Environment.NewLine +
                "    private readonly JoinableTaskFactory _jtf = joinableTaskContext.Factory;" + Environment.NewLine +
                "    private readonly ILogger _logger = loggerFactory.GetOrCreateLogger<RenameProjectTreeHandler>();",
                StringComparison.Ordinal)
            .Replace(
                "            await _threadingContext.JoinableTaskFactory.SwitchToMainThreadAsync();",
                "            await _jtf.SwitchToMainThreadAsync();",
                StringComparison.Ordinal)
            .Replace(
                "            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();",
                "            await _jtf.SwitchToMainThreadAsync();",
                StringComparison.Ordinal)
            .Replace(
                "            await _jtf.SwitchToMainThreadAsync();" + Environment.NewLine +
                "            await _jtf.SwitchToMainThreadAsync();",
                "            await _jtf.SwitchToMainThreadAsync();",
                StringComparison.Ordinal);

        if (string.Equals(originalContent, updatedContent, StringComparison.Ordinal))
            return "No RenameProjectTreeHandler RS0030 cleanup changes were needed.";

        await WriteTextPreservingUtf8BomAsync(renameProjectTreeHandlerPath, updatedContent, templatePath: renameProjectTreeHandlerPath).ConfigureAwait(false);
        return $"Updated '{Path.GetRelativePath(targetRepoRoot, renameProjectTreeHandlerPath)}' to switch from ThreadHelper.JoinableTaskFactory to an injected JoinableTaskContext.Factory.";
    }

    private static async Task<string> FixRazorReadonlyFieldsIDE0044Async(StageContext context)
    {
        var targetRepoRoot = context.TargetRepoRoot;
        var targetRoot = context.TargetRoot;
        var (changedFiles, replacementCount) = await ApplyKnownFileTextReplacementsAsync(
            targetRepoRoot,
            (Path.Combine(targetRoot, "src", "Razor", "src", "Microsoft.VisualStudio.RazorExtension", "SyntaxVisualizer", "SyntaxVisualizerControl.xaml.cs"),
            [
                (
                    "    private static string s_baseTempPath = Path.Combine(Path.GetTempPath(), \"RazorDevTools\");",
                    "    private static readonly string s_baseTempPath = Path.Combine(Path.GetTempPath(), \"RazorDevTools\");")
            ]),
            (Path.Combine(targetRoot, "src", "Razor", "src", "Microsoft.CodeAnalysis.Razor.Workspaces", "Logging", "AbstractMemoryLoggerProvider.Buffer.cs"),
            [
                (
                    "        private string[] _memory = new string[bufferSize];",
                    "        private readonly string[] _memory = new string[bufferSize];")
            ]),
            (Path.Combine(targetRoot, "src", "Razor", "src", "Microsoft.VisualStudio.LanguageServices.Razor", "Snippets", "SnippetCache.cs"),
            [
                (
                    "    private Dictionary<SnippetLanguage, ImmutableArray<SnippetInfo>> _snippetCache = new();",
                    "    private readonly Dictionary<SnippetLanguage, ImmutableArray<SnippetInfo>> _snippetCache = new();"),
                (
                    "    private ReadWriterLocker _lock = new();",
                    "    private readonly ReadWriterLocker _lock = new();")
            ]),
            (Path.Combine(targetRoot, "src", "Razor", "src", "Microsoft.VisualStudio.LanguageServices.Razor", "ProjectSystem", "RenameProjectTreeActionHandler.WaitIndicator.cs"),
            [
                (
                    "        private string _message;",
                    "        private readonly string _message;")
            ])).ConfigureAwait(false);

        return changedFiles.Count == 0
            ? "No Razor IDE0044 readonly-field cleanup changes were needed."
            : $"Marked {replacementCount} Razor field{(replacementCount == 1 ? string.Empty : "s")} readonly across {changedFiles.Count} file(s): {string.Join(", ", changedFiles)}.";
    }

    private static async Task<string> RemoveRazorUnusedUsingsAsync(StageContext context)
    {
        var targetRepoRoot = context.TargetRepoRoot;
        var targetRoot = context.TargetRoot;
        var viewCodeCommandHandlerTestsPath = GetExistingPath(
            Path.Combine(targetRoot, "src", "Razor", "test", "Microsoft.VisualStudio.LanguageServices.Razor.UnitTests", "LanguageClient", "ViewCodeCommandHandlerTests.cs"),
            Path.Combine(targetRoot, "src", "Razor", "test", "Microsoft.VisualStudio.LanguageServices.Razor.Test", "LanguageClient", "ViewCodeCommandHandlerTests.cs"));
        var defaultLspDocumentTestPath = GetExistingPath(
            Path.Combine(targetRoot, "src", "Razor", "test", "Microsoft.VisualStudio.LanguageServer.ContainedLanguage.UnitTests", "DefaultLSPDocumentTest.cs"),
            Path.Combine(targetRoot, "src", "Razor", "test", "Microsoft.VisualStudio.LanguageServer.ContainedLanguage.Test", "DefaultLSPDocumentTest.cs"));
        var (changedFiles, replacementCount) = await ApplyKnownFileTextReplacementsAsync(
            targetRepoRoot,
            (viewCodeCommandHandlerTestsPath,
            [
                ("using Moq;" + Environment.NewLine, string.Empty)
            ]),
            (Path.Combine(targetRoot, "src", "Razor", "src", "Microsoft.VisualStudio.LanguageServices.Razor", "VisualStudioLanguageServerFeatureOptions.cs"),
            [
                ("using Microsoft.VisualStudio.Shell;" + Environment.NewLine, string.Empty)
            ]),
            (defaultLspDocumentTestPath,
            [
                ("using Moq;" + Environment.NewLine, string.Empty)
            ])).ConfigureAwait(false);

        return changedFiles.Count == 0
            ? "No Razor unused-using cleanup changes were needed."
            : $"Removed {replacementCount} known unused using directive{(replacementCount == 1 ? string.Empty : "s")} from {changedFiles.Count} Razor file(s): {string.Join(", ", changedFiles)}.";
    }

    private static async Task<string> EnsureRazorCodeOwnersAsync(StageContext context)
    {
        var targetRepoRoot = context.TargetRepoRoot;
        var codeOwnersPath = Path.Combine(targetRepoRoot, ".github", "CODEOWNERS");
        if (!File.Exists(codeOwnersPath))
        {
            throw new InvalidOperationException(
                $"Expected Roslyn CODEOWNERS file at '{codeOwnersPath}', but it was not found.");
        }

        var originalContent = await File.ReadAllTextAsync(codeOwnersPath).ConfigureAwait(false);
        var updatedContent = EnsureRazorCodeOwnersEntry(originalContent);

        if (string.Equals(originalContent, updatedContent, StringComparison.Ordinal))
            return @"No .github\CODEOWNERS changes were needed for src\Razor.";

        await WriteTextPreservingUtf8BomAsync(codeOwnersPath, updatedContent, templatePath: codeOwnersPath).ConfigureAwait(false);
        await GitRunner.RunGitAsync(
            targetRepoRoot,
            "add",
            "--",
            Path.GetRelativePath(targetRepoRoot, codeOwnersPath)).ConfigureAwait(false);

        return @"Updated .github\CODEOWNERS so src\Razor is owned by @dotnet/razor-tooling.";
    }

    private static async Task<string> CopyRazorSkillsAsync(StageContext context)
    {
        var targetRepoRoot = context.TargetRepoRoot;
        var sourceSkillsRoot = Path.Combine(context.State.SourceCloneDirectory, ".github", "skills");
        var changedFiles = new List<string>();

        foreach (var skillName in ImportedRazorSkillNames)
        {
            var skillChanged = await SyncImportedRazorSkillAsync(
                targetRepoRoot,
                sourceSkillsRoot,
                skillName,
                changedFiles).ConfigureAwait(false);
            if (!skillChanged)
                continue;

            await GitRunner.RunGitAsync(
                targetRepoRoot,
                "add",
                "--",
                Path.Combine(".github", "skills", skillName)).ConfigureAwait(false);
        }

        return changedFiles.Count == 0
            ? @"No Razor skill copy changes were needed."
            : $"Copied Razor skill updates into {changedFiles.Count} file(s): {string.Join(", ", changedFiles)}.";
    }

    private static async Task<string> FixRazorLanguageConfigurationTestPathAsync(StageContext context)
    {
        var targetRepoRoot = context.TargetRepoRoot;
        var targetRoot = context.TargetRoot;
        var summaries = new List<string>();
        var languageConfigurationTestPath = GetExistingPath(
            Path.Combine(targetRoot, "src", "Razor", "test", "Microsoft.VisualStudio.LanguageServices.Razor.UnitTests", "LanguageConfigurationTest.cs"),
            Path.Combine(targetRoot, "src", "Razor", "test", "Microsoft.VisualStudio.LanguageServices.Razor.Test", "LanguageConfigurationTest.cs"));
        if (File.Exists(languageConfigurationTestPath))
        {
            var originalContent = await File.ReadAllTextAsync(languageConfigurationTestPath).ConfigureAwait(false);
            var updatedContent = RazorLanguageConfigurationTestPathPattern.Replace(
                originalContent,
                RazorLanguageConfigurationMergedPath);
            if (!string.Equals(originalContent, updatedContent, StringComparison.Ordinal))
            {
                await WriteTextPreservingUtf8BomAsync(
                    languageConfigurationTestPath,
                    updatedContent,
                    templatePath: languageConfigurationTestPath).ConfigureAwait(false);
                summaries.Add($"Normalized Razor language-configuration test path in '{Path.GetRelativePath(targetRepoRoot, languageConfigurationTestPath)}'.");
            }
        }

        var testProjectPath = Path.Combine(
            targetRoot,
            "src",
            "Shared",
            "Microsoft.AspNetCore.Razor.Test.Common",
            "Language",
            "TestProject.cs");
        if (File.Exists(testProjectPath))
        {
            var originalContent = await File.ReadAllTextAsync(testProjectPath).ConfigureAwait(false);
            var updatedContent = NormalizeRazorTestProjectHelperContent(originalContent);
            if (!string.Equals(originalContent, updatedContent, StringComparison.Ordinal))
            {
                await WriteTextPreservingUtf8BomAsync(
                    testProjectPath,
                    updatedContent,
                    templatePath: testProjectPath).ConfigureAwait(false);
                summaries.Add($"Normalized Razor test project directory probes in '{Path.GetRelativePath(targetRepoRoot, testProjectPath)}'.");
            }
        }

        return summaries.Count == 0
            ? "No Razor test path cleanup changes were needed."
            : string.Join(" ", summaries);
    }

    private static string NormalizeRazorTestProjectHelperContent(string content)
    {
        content = content.Replace(
            """
                public static string GetProjectDirectory(string directoryHint, Layer layer, bool testDirectoryFirst = false)
                {
                    var repoRoot = SearchUp(AppContext.BaseDirectory, "global.json");
                    var layerFolderName = GetLayerFolderName(layer);

                    Debug.Assert(!testDirectoryFirst || layer != Layer.Tooling, "If testDirectoryFirst is true and we're in the tooling layer, that means the project directory ternary needs to be updated to handle the false case");
                    var projectDirectory = testDirectoryFirst || layer == Layer.Tooling
                        ? Path.Combine(repoRoot, "src", layerFolderName, "test", directoryHint)
                        : Path.Combine(repoRoot, "src", layerFolderName, directoryHint, "test");

                    if (string.Equals(directoryHint, "Microsoft.AspNetCore.Razor.Language.Test", StringComparison.Ordinal))
                    {
                        Debug.Assert(!testDirectoryFirst);
                        Debug.Assert(layer == Layer.Compiler);
                        projectDirectory = Path.Combine(repoRoot, "src", "Compiler", "Microsoft.AspNetCore.Razor.Language", "test");
                    }

                    if (!Directory.Exists(projectDirectory))
                    {
                        throw new InvalidOperationException(
                            $@"Could not locate project directory for type {directoryHint}. Directory probe path: {projectDirectory}.");
                    }

                    return projectDirectory;
                }
            """,
            """
                public static string GetProjectDirectory(string directoryHint, Layer layer, bool testDirectoryFirst = false)
                {
                    var repoRoot = SearchUp(AppContext.BaseDirectory, "global.json");
                    var razorRepoRoot = Directory.Exists(Path.Combine(repoRoot, "src", "Razor", "src"))
                        ? Path.Combine(repoRoot, "src", "Razor")
                        : repoRoot;
                    var layerFolderName = GetLayerFolderName(layer);
                    var normalizedDirectoryHint = layer == Layer.Compiler && testDirectoryFirst && directoryHint.EndsWith(".Tests", StringComparison.Ordinal)
                        ? directoryHint[..^".Tests".Length] + ".UnitTests"
                        : directoryHint;

                    Debug.Assert(!testDirectoryFirst || layer != Layer.Tooling, "If testDirectoryFirst is true and we're in the tooling layer, that means the project directory ternary needs to be updated to handle the false case");
                    var projectDirectory = testDirectoryFirst || layer == Layer.Tooling
                        ? Path.Combine(razorRepoRoot, "src", layerFolderName, "test", normalizedDirectoryHint)
                        : Path.Combine(razorRepoRoot, "src", layerFolderName, normalizedDirectoryHint, "test");

                    if (string.Equals(directoryHint, "Microsoft.AspNetCore.Razor.Language.Test", StringComparison.Ordinal))
                    {
                        Debug.Assert(!testDirectoryFirst);
                        Debug.Assert(layer == Layer.Compiler);
                        projectDirectory = Path.Combine(razorRepoRoot, "src", "Compiler", "Microsoft.AspNetCore.Razor.Language", "test");
                    }

                    if (!Directory.Exists(projectDirectory))
                    {
                        throw new InvalidOperationException(
                            $@"Could not locate project directory for type {directoryHint}. Directory probe path: {projectDirectory}.");
                    }

                    return projectDirectory;
                }
            """,
            StringComparison.Ordinal);

        content = content.Replace(
            """
                public static string GetProjectDirectory(Type type, Layer layer, bool useCurrentDirectory = false)
                {
                    var baseDir = useCurrentDirectory ? Directory.GetCurrentDirectory() : AppContext.BaseDirectory;
                    var layerFolderName = GetLayerFolderName(layer);
                    var repoRoot = SearchUp(baseDir, "global.json");
                    var assemblyName = type.Assembly.GetName().Name;
                    var projectDirectory = layer == Layer.Compiler
                        ? Path.Combine(repoRoot, "src", layerFolderName, assemblyName, "test")
                        : Path.Combine(repoRoot, "src", layerFolderName, "test", assemblyName);

                    if (string.Equals(assemblyName, "Microsoft.AspNetCore.Razor.Language.Test", StringComparison.Ordinal))
                    {
                        Debug.Assert(layer == Layer.Compiler);
                        projectDirectory = Path.Combine(repoRoot, "src", "Compiler", "Microsoft.AspNetCore.Razor.Language", "test");
                    }
                    else if (string.Equals(assemblyName, "Microsoft.AspNetCore.Razor.Language.Legacy.Test", StringComparison.Ordinal))
                    {
                        Debug.Assert(layer == Layer.Compiler);
                        projectDirectory = Path.Combine(repoRoot, "src", "Compiler", "Microsoft.AspNetCore.Razor.Language", "legacyTest");
                    }

                    if (!Directory.Exists(projectDirectory))
                    {
                        throw new InvalidOperationException(
                            $@"Could not locate project directory for type {type.FullName}. Directory probe path: {projectDirectory}.");
                    }

                    return projectDirectory;
                }
            """,
            """
                public static string GetProjectDirectory(Type type, Layer layer, bool useCurrentDirectory = false)
                {
                    var baseDir = useCurrentDirectory ? Directory.GetCurrentDirectory() : AppContext.BaseDirectory;
                    var layerFolderName = GetLayerFolderName(layer);
                    var repoRoot = SearchUp(baseDir, "global.json");
                    var razorRepoRoot = Directory.Exists(Path.Combine(repoRoot, "src", "Razor", "src"))
                        ? Path.Combine(repoRoot, "src", "Razor")
                        : repoRoot;
                    var assemblyName = type.Assembly.GetName().Name;
                    var normalizedAssemblyName = layer == Layer.Compiler && assemblyName.EndsWith(".UnitTests", StringComparison.Ordinal)
                        ? assemblyName[..^".UnitTests".Length]
                        : assemblyName;
                    var projectDirectory = layer == Layer.Compiler
                        ? Path.Combine(razorRepoRoot, "src", layerFolderName, normalizedAssemblyName, "test")
                        : Path.Combine(razorRepoRoot, "src", layerFolderName, "test", assemblyName);

                    if (string.Equals(assemblyName, "Microsoft.AspNetCore.Razor.Language.Test", StringComparison.Ordinal))
                    {
                        Debug.Assert(layer == Layer.Compiler);
                        projectDirectory = Path.Combine(razorRepoRoot, "src", "Compiler", "Microsoft.AspNetCore.Razor.Language", "test");
                    }
                    else if (string.Equals(assemblyName, "Microsoft.AspNetCore.Razor.Language.Legacy.Test", StringComparison.Ordinal) ||
                             string.Equals(assemblyName, "Microsoft.AspNetCore.Razor.Language.Legacy.UnitTests", StringComparison.Ordinal))
                    {
                        Debug.Assert(layer == Layer.Compiler);
                        projectDirectory = Path.Combine(razorRepoRoot, "src", "Compiler", "Microsoft.AspNetCore.Razor.Language", "legacyTest");
                    }

                    if (layer == Layer.Compiler &&
                        !Directory.Exists(projectDirectory) &&
                        assemblyName.EndsWith(".UnitTests", StringComparison.Ordinal))
                    {
                        var testDirectoryFirstProjectDirectory = Path.Combine(razorRepoRoot, "src", layerFolderName, "test", assemblyName);
                        if (Directory.Exists(testDirectoryFirstProjectDirectory))
                        {
                            projectDirectory = testDirectoryFirstProjectDirectory;
                        }
                    }

                    if (!Directory.Exists(projectDirectory))
                    {
                        throw new InvalidOperationException(
                            $@"Could not locate project directory for type {type.FullName}. Directory probe path: {projectDirectory}.");
                    }

                    return projectDirectory;
                }
            """,
            StringComparison.Ordinal);

        return content;
    }

    private static async Task<bool> SyncImportedRazorSkillAsync(
        string targetRepoRoot,
        string sourceSkillsRoot,
        string skillName,
        List<string> changedFiles)
    {
        var sourceSkillRoot = Path.Combine(sourceSkillsRoot, skillName);
        if (!Directory.Exists(sourceSkillRoot))
        {
            throw new InvalidOperationException(
                $"Expected Razor skill directory at '{sourceSkillRoot}', but it was not found in the preserved source checkout.");
        }

        var targetSkillRoot = Path.Combine(targetRepoRoot, ".github", "skills", skillName);
        Directory.CreateDirectory(targetSkillRoot);

        var skillChanged = false;
        foreach (var sourcePath in Directory.EnumerateFiles(sourceSkillRoot, "*", SearchOption.AllDirectories))
        {
            var skillRelativePath = Path.GetRelativePath(sourceSkillRoot, sourcePath);
            var targetPath = Path.Combine(targetSkillRoot, skillRelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);

            var changed = await CopyImportedRazorSkillFileAsync(
                sourcePath,
                targetPath,
                skillName,
                skillRelativePath).ConfigureAwait(false);
            if (!changed)
                continue;

            skillChanged = true;
            changedFiles.Add(Path.GetRelativePath(targetRepoRoot, targetPath));
        }

        return skillChanged;
    }

    private static async Task<bool> CopyImportedRazorSkillFileAsync(
        string sourcePath,
        string targetPath,
        string skillName,
        string skillRelativePath)
    {
        var normalizedSkillRelativePath = skillRelativePath.Replace('\\', '/');
        if (ShouldRewriteImportedRazorSkillFile(normalizedSkillRelativePath))
        {
            var sourceContent = await File.ReadAllTextAsync(sourcePath).ConfigureAwait(false);
            var rewrittenContent = RewriteImportedRazorSkillContent(skillName, normalizedSkillRelativePath, sourceContent);
            var targetContent = File.Exists(targetPath)
                ? await File.ReadAllTextAsync(targetPath).ConfigureAwait(false)
                : null;
            if (string.Equals(rewrittenContent, targetContent, StringComparison.Ordinal))
                return false;

            await WriteTextPreservingUtf8BomAsync(targetPath, rewrittenContent, templatePath: sourcePath).ConfigureAwait(false);
            return true;
        }

        return await CopyFileIfDifferentAsync(sourcePath, targetPath).ConfigureAwait(false);
    }

    private static bool ShouldRewriteImportedRazorSkillFile(string skillRelativePath)
        => skillRelativePath.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
            || skillRelativePath.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase);

    private static string RewriteImportedRazorSkillContent(string skillName, string skillRelativePath, string content)
        => skillName switch
        {
            "formatting-log" => RewriteFormattingLogSkillContent(content),
            "run-toolset-tests" when string.Equals(skillRelativePath, "SKILL.md", StringComparison.OrdinalIgnoreCase)
                => RewriteRunToolsetTestsSkillContent(content),
            _ => content
        };

    private static string RewriteFormattingLogSkillContent(string content)
        => content
            .Replace(
                @"src\Razor\test\Microsoft.VisualStudio.LanguageServices.Razor.Test",
                @"src\Razor\src\Razor\test\Microsoft.VisualStudio.LanguageServices.Razor.UnitTests",
                StringComparison.Ordinal)
            .Replace(
                "Microsoft.VisualStudio.LanguageServices.Razor.Test.csproj",
                "Microsoft.VisualStudio.LanguageServices.Razor.UnitTests.csproj",
                StringComparison.Ordinal)
            .Replace(
                @"src\Razor\test\Microsoft.CodeAnalysis.Razor.CohostingShared.Test",
                @"src\Razor\src\Razor\test\Microsoft.CodeAnalysis.Razor.CohostingShared.UnitTests",
                StringComparison.Ordinal);

    private static string RewriteRunToolsetTestsSkillContent(string content)
        => content
            .Replace("`dotnet/razor`", "`dotnet/roslyn`", StringComparison.Ordinal)
            .Replace("https://github.com/dotnet/razor.git", "https://github.com/dotnet/roslyn.git", StringComparison.Ordinal)
            .Replace("github.com/dotnet/razor", "github.com/dotnet/roslyn", StringComparison.Ordinal)
            .Replace(
                "1. Builds `Microsoft.Net.Compilers.Razor.Toolset` from the Razor repo",
                "1. Builds `Microsoft.Net.Compilers.Razor.Toolset` from the merged Roslyn repo",
                StringComparison.Ordinal)
            .Replace(
                "- Confirm we're in the razor repository (look for `src/Compiler` or check git remotes).",
                "- Confirm we're in the merged Roslyn repository (look for `src/Razor/src/Compiler` or check git remotes).",
                StringComparison.Ordinal);

    private static async Task<string> NormalizeRazorUnitTestDetectionAsync(StageContext context)
    {
        var targetRepoRoot = context.TargetRepoRoot;
        var targetRoot = context.TargetRoot;
        var summaryParts = new List<string>();
        var changedFiles = new List<string>();

        Console.WriteLine("  - scanning Razor test project/artifact names");
        var renamedProjectArtifacts = await RenameRazorUnitTestProjectsAsync(context).ConfigureAwait(false);
        Console.WriteLine($"  - rename scan complete ({renamedProjectArtifacts.Count} artifact rename(s))");
        if (renamedProjectArtifacts.Count > 0)
        {
            summaryParts.Add(
                $"Renamed {renamedProjectArtifacts.Count} Razor test project artifact(s) to Roslyn-style UnitTests names: {string.Join(", ", renamedProjectArtifacts)}.");
        }

        var directoryBuildPropsPath = Path.Combine(targetRoot, "Directory.Build.props");
        if (File.Exists(directoryBuildPropsPath))
        {
            Console.WriteLine("  - normalizing Razor Directory.Build.props test metadata");
            var originalContent = await File.ReadAllTextAsync(directoryBuildPropsPath).ConfigureAwait(false);
            var updatedContent = NormalizeRazorUnitTestPropertyGroupContent(originalContent);

            if (!string.Equals(originalContent, updatedContent, StringComparison.Ordinal))
            {
                await WriteTextPreservingUtf8BomAsync(directoryBuildPropsPath, updatedContent, templatePath: directoryBuildPropsPath).ConfigureAwait(false);
                changedFiles.Add(Path.GetRelativePath(targetRepoRoot, directoryBuildPropsPath));
            }
        }

        var analyzerTestProjectPath = GetExistingPath(
            Path.Combine(
                targetRoot,
                "src",
                "Analyzers",
                "Razor.Diagnostics.Analyzers.UnitTests",
                "Razor.Diagnostics.Analyzers.UnitTests.csproj"),
            Path.Combine(
                targetRoot,
                "src",
                "Analyzers",
                "Razor.Diagnostics.Analyzers.Test",
                "Razor.Diagnostics.Analyzers.Test.csproj"));
        if (File.Exists(analyzerTestProjectPath))
        {
            Console.WriteLine("  - checking analyzer unit test project reference metadata");
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
            Console.WriteLine("  - checking microbenchmark generator test metadata");
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

        Console.WriteLine("  - updating tracked integration test project metadata");
        foreach (var integrationTestProjectPath in await GetTrackedProjectPathsAsync(targetRepoRoot, targetRoot).ConfigureAwait(false))
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

        if (changedFiles.Count > 0)
        {
            summaryParts.Add(
                $"Updated Razor test infrastructure metadata in {changedFiles.Count} file(s): {string.Join(", ", changedFiles)}.");
        }

        Console.WriteLine("  - Razor unit test detection normalization complete");
        return summaryParts.Count == 0
            ? "No Razor unit test detection cleanup was needed."
            : string.Join(" ", summaryParts);
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
        var testSessionPath = GetExistingPath(
            Path.Combine(
                context.TargetRoot,
                "src",
                "Razor",
                "test",
                "Microsoft.VisualStudio.LanguageServices.Razor.UnitTests",
                "LiveShare",
                "TestCollaborationSession.cs"),
            Path.Combine(
                context.TargetRoot,
                "src",
                "Razor",
                "test",
                "Microsoft.VisualStudio.LanguageServices.Razor.Test",
                "LiveShare",
                "TestCollaborationSession.cs"));

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
            return $"Razor Microsoft.NET.Sdk.Razor already uses {RazorSdkPackageVersion}.";

        await SaveXmlAsync(document, razorPackagesPath).ConfigureAwait(false);
        return
            $"Updated {updatedCount} Razor Microsoft.NET.Sdk.Razor package version entr{(updatedCount == 1 ? "y" : "ies")} " +
            $"in '{Path.GetRelativePath(targetRepoRoot, razorPackagesPath)}' to use {RazorSdkPackageVersion}.";
    }

    private static async Task<string> AlignRazorDirectoryPackagesVersionsAsync(StageContext context)
    {
        var razorPackagesPath = Path.Combine(context.TargetRoot, "Directory.Packages.props");
        if (!File.Exists(razorPackagesPath))
            return "No Razor Directory.Packages.props file was found for package-version alignment.";

        var summaries = new List<string>();

        AddSummaryIfChanged(
            summaries,
            await NormalizeSdkRazorPackageVersionAsync(context).ConfigureAwait(false),
            $"Razor Microsoft.NET.Sdk.Razor already uses {RazorSdkPackageVersion}.",
            "No Razor Microsoft.NET.Sdk.Razor package version entry was found.");
        AddSummaryIfChanged(
            summaries,
            await NormalizeObjectPoolPackageVersionAsync(context).ConfigureAwait(false),
            "Razor Microsoft.Extensions.ObjectPool already uses the shared Microsoft.Extensions version.",
            "No Razor Microsoft.Extensions.ObjectPool package version entry was found.");
        AddSummaryIfChanged(
            summaries,
            await NormalizeBasicReferenceAssembliesVersionAsync(context).ConfigureAwait(false),
            "No Razor Basic.Reference.Assemblies version override was found.");
        AddSummaryIfChanged(
            summaries,
            await RemoveRoslynTestingPackageOverridesAsync(context).ConfigureAwait(false),
            "No Razor Microsoft.CodeAnalysis.Analyzer.Testing version override was found.");

        return summaries.Count == 0
            ? "No Razor Directory.Packages.props version alignment changes were needed."
            : string.Join(" ", summaries);
    }

    private static async Task<string> RewriteSdkRazorPackagePathsAsync(StageContext context)
    {
        var targetRepoRoot = context.TargetRepoRoot;
        var targetRoot = context.TargetRoot;
        var changedFiles = new List<string>();
        var updatedReferenceCount = 0;

        foreach (var path in EnumerateMsBuildFiles(targetRoot))
        {
            var originalContent = await File.ReadAllTextAsync(path).ConfigureAwait(false);
            var matchCount = SdkRazorBuildNetstandardPathPattern.Matches(originalContent).Count;
            if (matchCount == 0)
                continue;

            var updatedContent = SdkRazorBuildNetstandardPathPattern.Replace(
                originalContent,
                match => $"$(PkgMicrosoft_NET_Sdk_Razor){match.Groups["separator"].Value}targets");

            await WriteTextPreservingUtf8BomAsync(path, updatedContent, templatePath: path).ConfigureAwait(false);
            changedFiles.Add(Path.GetRelativePath(targetRepoRoot, path));
            updatedReferenceCount += matchCount;
        }

        return changedFiles.Count == 0
            ? @"No Razor SDK package paths referencing $(PkgMicrosoft_NET_Sdk_Razor)\build\netstandard2.0 were found."
            : $@"Updated {updatedReferenceCount} Razor SDK package path reference(s) in {changedFiles.Count} file(s) to use $(PkgMicrosoft_NET_Sdk_Razor)\targets: {string.Join(", ", changedFiles)}.";
    }

    private static async Task<string> RemoveDuplicateRazorBannedMoqConstructorAsync(StageContext context)
    {
        var targetRepoRoot = context.TargetRepoRoot;
        var bannedSymbolsPath = Path.Combine(context.TargetRoot, "BannedSymbols.txt");
        if (!File.Exists(bannedSymbolsPath))
            return "No Razor BannedSymbols.txt file was found for duplicate Moq cleanup.";

        var originalContent = await File.ReadAllTextAsync(bannedSymbolsPath).ConfigureAwait(false);
        var updatedContent = RazorBannedMoqConstructorPattern.Replace(originalContent, string.Empty, count: 1);
        if (string.Equals(originalContent, updatedContent, StringComparison.Ordinal))
            return "No duplicate Razor-local Moq constructor banned-symbol entry was found.";

        await WriteTextPreservingUtf8BomAsync(bannedSymbolsPath, updatedContent, templatePath: bannedSymbolsPath).ConfigureAwait(false);
        return $"Removed Razor's duplicate Moq constructor banned-symbol entry from '{Path.GetRelativePath(targetRepoRoot, bannedSymbolsPath)}'.";
    }

    private static async Task<string> NormalizeRazorMockOfStrictnessAsync(StageContext context)
    {
        var targetRepoRoot = context.TargetRepoRoot;
        var targetRoot = context.TargetRoot;
        var changedFiles = new List<string>();
        var rewrittenCallCount = 0;

        foreach (var path in Directory.EnumerateFiles(targetRoot, "*.cs", SearchOption.AllDirectories))
        {
            var originalContent = await File.ReadAllTextAsync(path).ConfigureAwait(false);
            var (updatedContent, replacementCount) = RewriteMockOfCallsWithExplicitStrictness(originalContent);
            if (replacementCount == 0)
                continue;

            await WriteTextPreservingUtf8BomAsync(path, updatedContent, templatePath: path).ConfigureAwait(false);
            changedFiles.Add(Path.GetRelativePath(targetRepoRoot, path));
            rewrittenCallCount += replacementCount;
        }

        return changedFiles.Count == 0
            ? "No Razor Mock.Of strictness rewrites were needed."
            : $"Rewrote {rewrittenCallCount} Razor Mock.Of call(s) in {changedFiles.Count} file(s) to use explicit strict Moq constructs: {string.Join(", ", changedFiles)}.";
    }

    private static async Task<string> NormalizeRazorMoqCompatibilityAsync(StageContext context)
    {
        var summaries = new List<string>();

        AddSummaryIfChanged(
            summaries,
            await NormalizeRazorMoqApisAsync(context).ConfigureAwait(false),
            "No Razor Moq compatibility rewrites were needed.");
        AddSummaryIfChanged(
            summaries,
            await RemoveDuplicateRazorBannedMoqConstructorAsync(context).ConfigureAwait(false),
            "No duplicate Razor-local Moq constructor banned-symbol entry was found.");
        AddSummaryIfChanged(
            summaries,
            await NormalizeRazorMockOfStrictnessAsync(context).ConfigureAwait(false),
            "No Razor Mock.Of strictness rewrites were needed.");

        return summaries.Count == 0
            ? "No Razor Moq compatibility cleanup changes were needed."
            : string.Join(" ", summaries);
    }

    private static async Task<string> FixRazorXunit2031AssertSingleWhereAsync(StageContext context)
    {
        var targetRepoRoot = context.TargetRepoRoot;
        var targetRoot = context.TargetRoot;
        var replacements = new (string Path, string OldText, string NewText)[]
        {
            (
                Path.Combine(targetRoot, "src", "Razor", "test", "Microsoft.CodeAnalysis.Razor.Workspaces.UnitTests", "Utilities", "MemoryCacheTest.cs"),
                "Assert.Single(keys.Where(key => cache.TryGetValue(key, out _)));",
                "Assert.Single(keys, key => cache.TryGetValue(key, out _));"
            ),
            (
                Path.Combine(targetRoot, "src", "Razor", "test", "Microsoft.CodeAnalysis.Razor.CohostingShared.UnitTests", "Endpoints", "CohostFindAllReferencesEndpointTest.cs"),
                "Assert.Single(input.Spans.Where(s => inputText.GetRange(s).Equals(location.Range)));",
                "Assert.Single(input.Spans, s => inputText.GetRange(s).Equals(location.Range));"
            ),
            (
                Path.Combine(targetRoot, "src", "Razor", "test", "Microsoft.CodeAnalysis.Razor.CohostingShared.UnitTests", "Endpoints", "CohostFindAllReferencesEndpointTest.cs"),
                "var (fileName, testCode) = Assert.Single(additionalFiles.Where(f => FilePathNormalizingComparer.Instance.Equals(f.fileName, location.DocumentUri.GetRequiredParsedUri().AbsolutePath)));",
                "var (fileName, testCode) = Assert.Single(additionalFiles, f => FilePathNormalizingComparer.Instance.Equals(f.fileName, location.DocumentUri.GetRequiredParsedUri().AbsolutePath));"
            ),
            (
                Path.Combine(targetRoot, "src", "Razor", "test", "Microsoft.CodeAnalysis.Razor.CohostingShared.UnitTests", "Endpoints", "CohostFindAllReferencesEndpointTest.cs"),
                "Assert.Single(testCode.Spans.Where(s => text.GetRange(s).Equals(location.Range)));",
                "Assert.Single(testCode.Spans, s => text.GetRange(s).Equals(location.Range));"
            ),
            (
                Path.Combine(targetRoot, "src", "Razor", "test", "Microsoft.CodeAnalysis.Razor.CohostingShared.UnitTests", "Endpoints", "CohostDocumentCompletionEndpointTest.cs"),
                "var item = Assert.Single(result.Items.Where(i => i.Label == itemToResolve));",
                "var item = Assert.Single(result.Items, i => i.Label == itemToResolve);"
            ),
            (
                Path.Combine(targetRoot, "src", "Compiler", "Microsoft.AspNetCore.Razor.Language", "test", "RazorSyntaxTreeTest.cs"),
                "Assert.Single(root.DescendantNodes().OfType<RazorDirectiveBodySyntax>().Where(body => body.Keyword.GetContent() == \"tagHelperPrefix\"));",
                "Assert.Single(root.DescendantNodes().OfType<RazorDirectiveBodySyntax>(), body => body.Keyword.GetContent() == \"tagHelperPrefix\");"
            ),
        };

        var changedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var replacementCount = 0;

        foreach (var replacement in replacements)
        {
            if (!File.Exists(replacement.Path))
                continue;

            var originalContent = await File.ReadAllTextAsync(replacement.Path).ConfigureAwait(false);
            var updatedContent = originalContent.Replace(replacement.OldText, replacement.NewText, StringComparison.Ordinal);
            if (string.Equals(originalContent, updatedContent, StringComparison.Ordinal))
                continue;

            await WriteTextPreservingUtf8BomAsync(replacement.Path, updatedContent, templatePath: replacement.Path).ConfigureAwait(false);
            changedFiles.Add(Path.GetRelativePath(targetRepoRoot, replacement.Path));
            replacementCount++;
        }

        return changedFiles.Count == 0
            ? "No Razor xUnit2031 Assert.Single rewrites were needed."
            : $"Rewrote {replacementCount} Razor Assert.Single(...Where(...)) call(s) in {changedFiles.Count} file(s): {string.Join(", ", changedFiles)}.";
    }

    private static async Task<string> FixRazorXunit2029AssertEmptyWhereAsync(StageContext context)
    {
        var targetRepoRoot = context.TargetRepoRoot;
        var targetRoot = context.TargetRoot;
        var replacements = new (string Path, string OldText, string NewText)[]
        {
            (
                Path.Combine(targetRoot, "src", "Razor", "test", "Microsoft.CodeAnalysis.Razor.CohostingShared.UnitTests", "CodeActions", "CohostCodeActionsEndpointTestBase.cs"),
                "Assert.Empty(input.NamedSpans.Where(kvp => kvp.Key.Length > 0));",
                "Assert.DoesNotContain(input.NamedSpans, kvp => kvp.Key.Length > 0);"
            ),
            (
                Path.Combine(targetRoot, "src", "Compiler", "Microsoft.CodeAnalysis.Razor", "test", "DefaultTagHelperProducerTest.cs"),
                "Assert.Empty(result.Where(f => f.TypeName == testTagHelper));",
                "Assert.DoesNotContain(result, f => f.TypeName == testTagHelper);"
            ),
            (
                Path.Combine(targetRoot, "src", "Compiler", "Microsoft.CodeAnalysis.Razor", "test", "ComponentTagHelperProducerTest.cs"),
                "Assert.Empty(result.Where(f => f.TypeName == testComponent));",
                "Assert.DoesNotContain(result, f => f.TypeName == testComponent);"
            ),
            (
                Path.Combine(targetRoot, "src", "Compiler", "Microsoft.CodeAnalysis.Razor", "test", "ComponentTagHelperProducerTest.cs"),
                "Assert.Empty(result.Where(f => f.TypeName == routerComponent));",
                "Assert.DoesNotContain(result, f => f.TypeName == routerComponent);"
            ),
        };

        var changedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var replacementCount = 0;

        foreach (var replacement in replacements)
        {
            if (!File.Exists(replacement.Path))
                continue;

            var originalContent = await File.ReadAllTextAsync(replacement.Path).ConfigureAwait(false);
            var updatedContent = originalContent.Replace(replacement.OldText, replacement.NewText, StringComparison.Ordinal);
            if (string.Equals(originalContent, updatedContent, StringComparison.Ordinal))
                continue;

            await WriteTextPreservingUtf8BomAsync(replacement.Path, updatedContent, templatePath: replacement.Path).ConfigureAwait(false);
            changedFiles.Add(Path.GetRelativePath(targetRepoRoot, replacement.Path));
            replacementCount++;
        }

        return changedFiles.Count == 0
            ? "No Razor xUnit2029 Assert.Empty rewrites were needed."
            : $"Rewrote {replacementCount} Razor Assert.Empty(...Where(...)) call(s) in {changedFiles.Count} file(s): {string.Join(", ", changedFiles)}.";
    }

    private static async Task<string> RewriteRazorPackContentPathsAsync(StageContext context)
    {
        var targetRepoRoot = context.TargetRepoRoot;
        var targetRoot = context.TargetRoot;
        var replacements = new (string Path, string OldText, string NewText)[]
        {
            (
                Path.Combine(targetRoot, "src", "Compiler", "tools", "Microsoft.CodeAnalysis.Razor.Tooling.Internal", "Microsoft.CodeAnalysis.Razor.Tooling.Internal.csproj"),
                "    <Content Include=\"$(OutDir)Microsoft.CodeAnalysis.Razor.Compiler.dll\" PackagePath=\"lib\\$(TargetFramework)\" />" + Environment.NewLine +
                "    <Content Include=\"$(OutDir)Microsoft.AspNetCore.Razor.Utilities.Shared.dll\" PackagePath=\"lib\\$(TargetFramework)\" />",
                "    <Content Include=\"$(ArtifactsDir)bin\\Microsoft.CodeAnalysis.Razor.Compiler\\$(Configuration)\\$(TargetFramework)\\Microsoft.CodeAnalysis.Razor.Compiler.dll\" PackagePath=\"lib\\$(TargetFramework)\" />" + Environment.NewLine +
                "    <Content Include=\"$(ArtifactsDir)bin\\Microsoft.AspNetCore.Razor.Utilities.Shared\\$(Configuration)\\$(TargetFramework)\\Microsoft.AspNetCore.Razor.Utilities.Shared.dll\" PackagePath=\"lib\\$(TargetFramework)\" />"
            ),
            (
                Path.Combine(targetRoot, "src", "Razor", "src", "Microsoft.VisualStudioCode.RazorExtension", "Microsoft.VisualStudioCode.RazorExtension.csproj"),
                "      <Content Include=\"$(PublishDir)\\Microsoft.AspNetCore.Razor.Utilities.Shared.dll\" Pack=\"true\" PackagePath=\"content\" CopyToOutputDirectory=\"PreserveNewest\" />" + Environment.NewLine +
                "      <Content Include=\"$(PublishDir)\\Microsoft.CodeAnalysis.Razor.Compiler.dll\" Pack=\"true\" PackagePath=\"content\" CopyToOutputDirectory=\"PreserveNewest\" />" + Environment.NewLine +
                "      <Content Include=\"$(PublishDir)\\Microsoft.CodeAnalysis.Razor.Workspaces.dll\" Pack=\"true\" PackagePath=\"content\" CopyToOutputDirectory=\"PreserveNewest\" />" + Environment.NewLine +
                "      <Content Include=\"$(PublishDir)\\Microsoft.CodeAnalysis.Remote.Razor.dll\" Pack=\"true\" PackagePath=\"content\" CopyToOutputDirectory=\"PreserveNewest\" />",
                "      <Content Include=\"$(ArtifactsDir)bin\\Microsoft.AspNetCore.Razor.Utilities.Shared\\$(Configuration)\\$(TargetFramework)\\Microsoft.AspNetCore.Razor.Utilities.Shared.dll\" Pack=\"true\" PackagePath=\"content\" CopyToOutputDirectory=\"PreserveNewest\" />" + Environment.NewLine +
                "      <Content Include=\"$(ArtifactsDir)bin\\Microsoft.CodeAnalysis.Razor.Compiler\\$(Configuration)\\$(TargetFramework)\\Microsoft.CodeAnalysis.Razor.Compiler.dll\" Pack=\"true\" PackagePath=\"content\" CopyToOutputDirectory=\"PreserveNewest\" />" + Environment.NewLine +
                "      <Content Include=\"$(ArtifactsDir)bin\\Microsoft.CodeAnalysis.Razor.Workspaces\\$(Configuration)\\$(TargetFramework)\\Microsoft.CodeAnalysis.Razor.Workspaces.dll\" Pack=\"true\" PackagePath=\"content\" CopyToOutputDirectory=\"PreserveNewest\" />" + Environment.NewLine +
                "      <Content Include=\"$(ArtifactsDir)bin\\Microsoft.CodeAnalysis.Remote.Razor\\$(Configuration)\\$(TargetFramework)\\Microsoft.CodeAnalysis.Remote.Razor.dll\" Pack=\"true\" PackagePath=\"content\" CopyToOutputDirectory=\"PreserveNewest\" />"
            ),
        };

        var changedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var replacementCount = 0;

        foreach (var replacement in replacements)
        {
            if (!File.Exists(replacement.Path))
                continue;

            var originalContent = await File.ReadAllTextAsync(replacement.Path).ConfigureAwait(false);
            var updatedContent = originalContent.Replace(replacement.OldText, replacement.NewText, StringComparison.Ordinal);
            if (string.Equals(originalContent, updatedContent, StringComparison.Ordinal))
                continue;

            await WriteTextPreservingUtf8BomAsync(replacement.Path, updatedContent, templatePath: replacement.Path).ConfigureAwait(false);
            changedFiles.Add(Path.GetRelativePath(targetRepoRoot, replacement.Path));
            replacementCount++;
        }

        return changedFiles.Count == 0
            ? "No Razor pack content path rewrites were needed."
            : $"Rewrote Razor pack content paths in {changedFiles.Count} file(s) to use Roslyn artifact outputs: {string.Join(", ", changedFiles)}.";
    }

    private static async Task<string> MoveRazorShippingSymbolPackagesAsync(StageContext context)
    {
        var targetRepoRoot = context.TargetRepoRoot;
        var targetRoot = context.TargetRoot;
        var directoryBuildTargetsPath = Path.Combine(targetRoot, "Directory.Build.targets");
        if (!File.Exists(directoryBuildTargetsPath))
            return "No Razor root Directory.Build.targets file was found for shipping symbols cleanup.";

        var targetBlock = string.Join(
            Environment.NewLine,
            [
                "  <Target Name=\"MoveRazorShippingSymbolsPackageToNonShipping\" AfterTargets=\"Pack\" Condition=\"'$(IsPackable)' == 'true' and '$(PackageOutputPath)' != '' and $([System.String]::Copy('$(PackageOutputPath)').Contains('\\Shipping\\'))\">",
                "    <ItemGroup>",
                "      <_RazorShippingSymbolsPackage Include=\"$(PackageOutputPath)$(PackageId).$(PackageVersion).symbols.nupkg\"",
                "                                   Condition=\"Exists('$(PackageOutputPath)$(PackageId).$(PackageVersion).symbols.nupkg')\" />",
                "      <_MovedRazorShippingSymbolsPackage Include=\"@(_RazorShippingSymbolsPackage)\">",
                "        <TargetPath>$(ArtifactsNonShippingPackagesDir)%(_RazorShippingSymbolsPackage.Filename)%(_RazorShippingSymbolsPackage.Extension)</TargetPath>",
                "      </_MovedRazorShippingSymbolsPackage>",
                "    </ItemGroup>",
                "    <Delete Files=\"@(_MovedRazorShippingSymbolsPackage->'%(TargetPath)')\" Condition=\"'@(_MovedRazorShippingSymbolsPackage)' != ''\" />",
                "    <Move SourceFiles=\"@(_RazorShippingSymbolsPackage)\"",
                "          DestinationFiles=\"@(_MovedRazorShippingSymbolsPackage->'%(TargetPath)')\"",
                "          Condition=\"'@(_RazorShippingSymbolsPackage)' != ''\">",
                "      <Output TaskParameter=\"DestinationFiles\" ItemName=\"_FilesWritten\" />",
                "    </Move>",
                "  </Target>",
            ]) + Environment.NewLine;

        var anchor = string.Join(
            Environment.NewLine,
            [
                "  <PropertyGroup>",
                "    <PackageVersion Condition=\" '$(PackageVersion)' == '' \">$(Version)</PackageVersion>",
                "  </PropertyGroup>",
            ]);

        var originalContent = await File.ReadAllTextAsync(directoryBuildTargetsPath).ConfigureAwait(false);
        if (originalContent.Contains("MoveRazorShippingSymbolsPackageToNonShipping", StringComparison.Ordinal))
            return "No Razor shipping symbols package move was needed.";

        var updatedContent = originalContent.Replace(
            anchor,
            anchor + Environment.NewLine + Environment.NewLine + targetBlock.TrimEnd('\r', '\n'),
            StringComparison.Ordinal);

        if (string.Equals(originalContent, updatedContent, StringComparison.Ordinal))
            return "No Razor shipping symbols package move was needed.";

        await WriteTextPreservingUtf8BomAsync(directoryBuildTargetsPath, updatedContent, templatePath: directoryBuildTargetsPath).ConfigureAwait(false);
        return $"Added a shipping symbols package move target to '{Path.GetRelativePath(targetRepoRoot, directoryBuildTargetsPath)}'.";
    }

    private static async Task<string> RestoreRazorVersionPropsAsync(StageContext context)
    {
        var targetRepoRoot = context.TargetRepoRoot;
        var targetRoot = context.TargetRoot;
        var directoryBuildPropsPath = Path.Combine(targetRoot, "Directory.Build.props");
        if (!File.Exists(directoryBuildPropsPath))
            return "No Razor root Directory.Build.props file was found for version cleanup.";

        var versionBlock = string.Join(
            Environment.NewLine,
            [
                "  <PropertyGroup Label=\"Razor Versioning\">",
                "    <MajorVersion>10</MajorVersion>",
                "    <MinorVersion>0</MinorVersion>",
                "    <PatchVersion>0</PatchVersion>",
                "    <PreReleaseVersionLabel>preview</PreReleaseVersionLabel>",
                "  </PropertyGroup>",
            ]);

        var anchor = string.Join(
            Environment.NewLine,
            [
                "  <Import",
                "    Project=\"$([MSBuild]::GetDirectoryNameOfFileAbove($(MSBuildThisFileDirectory), AspNetCoreSettings.props))\\AspNetCoreSettings.props\"",
                "    Condition=\" '$(CI)' != 'true' AND '$([MSBuild]::GetDirectoryNameOfFileAbove($(MSBuildThisFileDirectory), AspNetCoreSettings.props))' != '' \" />",
            ]);

        var originalContent = await File.ReadAllTextAsync(directoryBuildPropsPath).ConfigureAwait(false);
        if (originalContent.Contains("<PropertyGroup Label=\"Razor Versioning\">", StringComparison.Ordinal))
            return "No Razor version props restore was needed.";

        var updatedContent = originalContent.Replace(
            anchor,
            anchor + Environment.NewLine + Environment.NewLine + versionBlock,
            StringComparison.Ordinal);

        if (string.Equals(originalContent, updatedContent, StringComparison.Ordinal))
            return "No Razor version props restore was needed.";

        await WriteTextPreservingUtf8BomAsync(directoryBuildPropsPath, updatedContent, templatePath: directoryBuildPropsPath).ConfigureAwait(false);
        return $"Restored Razor's local version props in '{Path.GetRelativePath(targetRepoRoot, directoryBuildPropsPath)}'.";
    }

    private static async Task<string> RestoreRazorVsixVersionPropsAsync(StageContext context)
    {
        var targetRepoRoot = context.TargetRepoRoot;
        var targetRoot = context.TargetRoot;
        var directoryBuildPropsPath = Path.Combine(targetRoot, "Directory.Build.props");
        if (!File.Exists(directoryBuildPropsPath))
            return "No Razor root Directory.Build.props file was found for VSIX version cleanup.";

        var toolingVersionBlock = string.Join(
            Environment.NewLine,
            [
                "  <PropertyGroup Label=\"Razor Tooling Versioning\">",
                "    <VsixVersionPrefix>18.7.1</VsixVersionPrefix>",
                "    <AddinMajorVersion>18.7</AddinMajorVersion>",
                "    <AddinVersion>$(AddinMajorVersion)</AddinVersion>",
                "    <AddinVersion Condition=\"'$(OfficialBuildId)' != ''\">$(AddinVersion).$(OfficialBuildId)</AddinVersion>",
                "    <AddinVersion Condition=\"'$(OfficialBuildId)' == ''\">$(AddinVersion).42424242.42</AddinVersion>",
                "  </PropertyGroup>",
            ]);

        var anchor = string.Join(
            Environment.NewLine,
            [
                "  <PropertyGroup Label=\"Razor Versioning\">",
                "    <MajorVersion>10</MajorVersion>",
                "    <MinorVersion>0</MinorVersion>",
                "    <PatchVersion>0</PatchVersion>",
                "    <PreReleaseVersionLabel>preview</PreReleaseVersionLabel>",
                "  </PropertyGroup>",
            ]);

        var originalContent = await File.ReadAllTextAsync(directoryBuildPropsPath).ConfigureAwait(false);
        if (originalContent.Contains("<PropertyGroup Label=\"Razor Tooling Versioning\">", StringComparison.Ordinal))
            return "No Razor VSIX version props restore was needed.";

        var updatedContent = originalContent.Replace(
            anchor,
            anchor + Environment.NewLine + Environment.NewLine + toolingVersionBlock,
            StringComparison.Ordinal);

        if (string.Equals(originalContent, updatedContent, StringComparison.Ordinal))
            return "No Razor VSIX version props restore was needed.";

        await WriteTextPreservingUtf8BomAsync(directoryBuildPropsPath, updatedContent, templatePath: directoryBuildPropsPath).ConfigureAwait(false);
        return $"Restored Razor's local VSIX version props in '{Path.GetRelativePath(targetRepoRoot, directoryBuildPropsPath)}'.";
    }

    private static async Task<string> RestoreRazorVersioningPropsAsync(StageContext context)
    {
        var directoryBuildPropsPath = Path.Combine(context.TargetRoot, "Directory.Build.props");
        if (!File.Exists(directoryBuildPropsPath))
            return "No Razor root Directory.Build.props file was found for versioning-props restoration.";

        var summaries = new List<string>();

        AddSummaryIfChanged(
            summaries,
            await RestoreRazorVersionPropsAsync(context).ConfigureAwait(false),
            "No Razor version props restore was needed.");
        AddSummaryIfChanged(
            summaries,
            await RestoreRazorVsixVersionPropsAsync(context).ConfigureAwait(false),
            "No Razor VSIX version props restore was needed.");

        return summaries.Count == 0
            ? "No Razor versioning-props restoration changes were needed."
            : string.Join(" ", summaries);
    }

    private static async Task<string> RestoreRazorVsixDevAssetsAsync(StageContext context)
    {
        var targetRepoRoot = context.TargetRepoRoot;
        var targetRoot = context.TargetRoot;
        var sourceRepoRoot = context.State.SourceCloneDirectory;
        var sourceRazorRoot = Path.Combine(sourceRepoRoot, "src", "Razor");
        var sourceEngTargetsRoot = Path.Combine(sourceRepoRoot, "eng", "targets");
        if (!Directory.Exists(sourceRazorRoot) || !Directory.Exists(sourceEngTargetsRoot))
            return "No Razor source checkout was found for VSIX developer asset cleanup.";

        var changedFiles = new List<string>();

        async Task RecordChangedFileAsync(string path)
        {
            var relativePath = Path.GetRelativePath(targetRepoRoot, path);
            await GitRunner.RunGitAsync(targetRepoRoot, "add", "--", relativePath).ConfigureAwait(false);
            changedFiles.Add(relativePath);
        }

        foreach (var fileName in new[]
        {
            "GenerateServiceHubConfigurationFiles.targets",
            "ReplaceServiceHubAssetsInVsixManifest.targets",
        })
        {
            var sourcePath = Path.Combine(sourceEngTargetsRoot, fileName);
            var targetPath = Path.Combine(targetRoot, "eng", "targets", fileName);
            if (!await CopyFileIfDifferentAsync(sourcePath, targetPath).ConfigureAwait(false))
                continue;

            await RecordChangedFileAsync(targetPath).ConfigureAwait(false);
        }

        var legacyBrokeredServicesTargetPath = Path.Combine(targetRoot, "eng", "targets", "GenerateBrokeredServicesPkgDef.targets");
        if (File.Exists(legacyBrokeredServicesTargetPath))
        {
            File.Delete(legacyBrokeredServicesTargetPath);
            await GitRunner.RunGitAsync(
                targetRepoRoot,
                "rm",
                "--ignore-unmatch",
                "--",
                Path.GetRelativePath(targetRepoRoot, legacyBrokeredServicesTargetPath)).ConfigureAwait(false);
            changedFiles.Add(Path.GetRelativePath(targetRepoRoot, legacyBrokeredServicesTargetPath));
        }

        var sourceBrokeredServicesTargetPath = Path.Combine(sourceEngTargetsRoot, "GenerateBrokeredServicesPkgDef.targets");
        var targetBrokeredServicesTargetPath = Path.Combine(targetRoot, "eng", "targets", "GenerateRazorBrokeredServicesPkgDef.targets");
        if (File.Exists(sourceBrokeredServicesTargetPath))
        {
            var desiredContent = await BuildRazorBrokeredServicesPkgDefTargetsContentAsync(sourceBrokeredServicesTargetPath).ConfigureAwait(false);
            var existingContent = File.Exists(targetBrokeredServicesTargetPath)
                ? await File.ReadAllTextAsync(targetBrokeredServicesTargetPath).ConfigureAwait(false)
                : null;
            if (!string.Equals(existingContent, desiredContent, StringComparison.Ordinal))
            {
                await WriteTextPreservingUtf8BomAsync(
                    targetBrokeredServicesTargetPath,
                    desiredContent,
                    templatePath: sourceBrokeredServicesTargetPath).ConfigureAwait(false);
                await RecordChangedFileAsync(targetBrokeredServicesTargetPath).ConfigureAwait(false);
            }
        }

        var sourceVsixProjectRoot = Path.Combine(sourceRazorRoot, "src", "Razor", "src", "Microsoft.VisualStudio.RazorExtension");
        var targetVsixProjectRoot = Path.Combine(targetRoot, "src", "Razor", "src", "Microsoft.VisualStudio.RazorExtension");
        var sourceDirectoryBuildTargetsPath = Path.Combine(sourceVsixProjectRoot, "Directory.Build.targets");
        var targetDirectoryBuildTargetsPath = Path.Combine(targetVsixProjectRoot, "Directory.Build.targets");
        if (File.Exists(sourceDirectoryBuildTargetsPath) && File.Exists(targetDirectoryBuildTargetsPath))
        {
            var desiredContent = await BuildRazorVsixDirectoryBuildTargetsContentAsync(sourceDirectoryBuildTargetsPath).ConfigureAwait(false);
            var originalContent = await File.ReadAllTextAsync(targetDirectoryBuildTargetsPath).ConfigureAwait(false);
            if (!string.Equals(originalContent, desiredContent, StringComparison.Ordinal))
            {
                await WriteTextPreservingUtf8BomAsync(
                    targetDirectoryBuildTargetsPath,
                    desiredContent,
                    templatePath: sourceDirectoryBuildTargetsPath).ConfigureAwait(false);
                await RecordChangedFileAsync(targetDirectoryBuildTargetsPath).ConfigureAwait(false);
            }
        }

        return changedFiles.Count == 0
            ? "No Razor VSIX developer asset cleanup changes were needed."
            : $"Restored Razor VSIX developer-build packaging behavior in {changedFiles.Count} file(s): {string.Join(", ", changedFiles)}.";
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
        var commentedReferenceCount = 0;
        const string todoComment = """
    <!-- TODO: Re-enable the Razor.Diagnostics.Analyzers project reference once the merged Roslyn build can load it cleanly again.
    <ProjectReference Include="$(MSBuildThisFileDirectory)..\Analyzers\Razor.Diagnostics.Analyzers\Razor.Diagnostics.Analyzers.csproj"
                      PrivateAssets="all"
                      ReferenceOutputAssembly="false"
                      OutputItemType="Analyzer" />
    -->
""";

        foreach (var propsPath in Directory.EnumerateFiles(targetRoot, "Directory.Build.props", SearchOption.AllDirectories))
        {
            var originalContent = await File.ReadAllTextAsync(propsPath).ConfigureAwait(false);
            var updatedContent = RazorDiagnosticsAnalyzerTodoCommentPattern.Replace(originalContent, string.Empty);
            var activeReferenceCount = RazorDiagnosticsAnalyzerProjectReferencePattern.Matches(updatedContent).Count;
            if (activeReferenceCount > 0)
            {
                updatedContent = RazorDiagnosticsAnalyzerProjectReferencePattern.Replace(updatedContent, string.Empty);
                commentedReferenceCount += activeReferenceCount;
            }

            var relativePath = Path.GetRelativePath(targetRoot, propsPath);
            var sourcePath = Path.Combine(sourceRazorRoot, relativePath);
            var sourceHasAnalyzerReference = File.Exists(sourcePath)
                && RazorDiagnosticsAnalyzerProjectReferencePattern.IsMatch(await File.ReadAllTextAsync(sourcePath).ConfigureAwait(false));

            if ((activeReferenceCount > 0 || sourceHasAnalyzerReference) &&
                updatedContent.Contains("Microsoft.CodeAnalysis.Analyzers", StringComparison.Ordinal))
            {
                updatedContent = EnsureRawItemAfterPackageReference(
                    updatedContent,
                    "Microsoft.CodeAnalysis.Analyzers",
                    todoComment);
            }

            if (string.Equals(originalContent, updatedContent, StringComparison.Ordinal))
                continue;

            await WriteTextPreservingUtf8BomAsync(propsPath, updatedContent, templatePath: propsPath).ConfigureAwait(false);
            changedFiles.Add(Path.GetRelativePath(targetRepoRoot, propsPath));
        }

        return commentedReferenceCount == 0
            ? "No Razor.Diagnostics.Analyzers TODO comment rewrites were needed in Razor Directory.Build.props files."
            : $"Commented out {commentedReferenceCount} Razor.Diagnostics.Analyzers project reference(s) in {changedFiles.Count} file(s) and replaced them with a TODO note: {string.Join(", ", changedFiles)}.";
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
            GetExistingPath(
                Path.Combine(targetRoot, "src", "Razor", "test", "Microsoft.VisualStudio.LanguageServices.Razor.UnitTests", "LiveShare", "Guest", "RazorGuestInitializationServiceTest.cs"),
                Path.Combine(targetRoot, "src", "Razor", "test", "Microsoft.VisualStudio.LanguageServices.Razor.Test", "LiveShare", "Guest", "RazorGuestInitializationServiceTest.cs")),
            Path.Combine(targetRoot, "src", "Razor", "test", "Microsoft.VisualStudio.Razor.IntegrationTests", "CodeFoldingTests.cs"),
            Path.Combine(targetRoot, "src", "Compiler", "Microsoft.AspNetCore.Razor.Language", "test", "Legacy", "CSharpCodeParserTest.cs"),
            Path.Combine(targetRoot, "src", "Compiler", "Microsoft.AspNetCore.Razor.Language", "test", "TagHelperMatchingConventionsTest.cs"),
            Path.Combine(targetRoot, "src", "Compiler", "Microsoft.AspNetCore.Razor.Language", "test", "Legacy", "TagHelperParseTreeRewriterTest.cs"),
            Path.Combine(targetRoot, "src", "Compiler", "Microsoft.AspNetCore.Razor.Language", "legacyTest", "Legacy", "TagHelperParseTreeRewriterTest.cs"),
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

    private static async Task<string> NormalizeRazorParseTextSourceTextAsync(StageContext context)
    {
        var targetRepoRoot = context.TargetRepoRoot;
        var targetRoot = context.TargetRoot;
        var changedFiles = new List<string>();
        var replacements = new (string Path, string OldText, string NewText)[]
        {
            (
                Path.Combine(targetRoot, "src", "Compiler", "Microsoft.AspNetCore.Mvc.Razor.Extensions", "test", "ViewComponentTagHelperProducerTest.cs"),
                "CSharpSyntaxTree.ParseText(code)",
                "CSharpSyntaxTree.ParseText(Microsoft.CodeAnalysis.Text.SourceText.From(code, System.Text.Encoding.UTF8))"
            ),
            (
                Path.Combine(targetRoot, "src", "Compiler", "Microsoft.AspNetCore.Mvc.Razor.Extensions", "test", "ViewComponentTagHelperDescriptorFactoryTest.cs"),
                "CSharpSyntaxTree.ParseText(AdditionalCode)",
                "CSharpSyntaxTree.ParseText(Microsoft.CodeAnalysis.Text.SourceText.From(AdditionalCode, System.Text.Encoding.UTF8))"
            ),
            (
                Path.Combine(targetRoot, "src", "Compiler", "Microsoft.AspNetCore.Mvc.Razor.Extensions.Version1_X", "test", "ViewComponentTagHelperProducerTest.cs"),
                "CSharpSyntaxTree.ParseText(code)",
                "CSharpSyntaxTree.ParseText(Microsoft.CodeAnalysis.Text.SourceText.From(code, System.Text.Encoding.UTF8))"
            ),
            (
                Path.Combine(targetRoot, "src", "Compiler", "Microsoft.AspNetCore.Mvc.Razor.Extensions.Version2_X", "test", "ViewComponentTagHelperProducerTest.cs"),
                "CSharpSyntaxTree.ParseText(code)",
                "CSharpSyntaxTree.ParseText(Microsoft.CodeAnalysis.Text.SourceText.From(code, System.Text.Encoding.UTF8))"
            ),
            (
                Path.Combine(targetRoot, "src", "Compiler", "Microsoft.AspNetCore.Razor.Language", "test", "IntegrationTests", "CodeGenerationIntegrationTest.cs"),
                "CSharpSyntaxTree.ParseText(TestTagHelperDescriptors.Code)",
                "CSharpSyntaxTree.ParseText(Microsoft.CodeAnalysis.Text.SourceText.From(TestTagHelperDescriptors.Code, System.Text.Encoding.UTF8))"
            ),
            (
                Path.Combine(targetRoot, "src", "Compiler", "Microsoft.CodeAnalysis.Razor", "test", "BaseTagHelperProducerTest.cs"),
                "CSharpSyntaxTree.ParseText(text, CSharpParseOptions)",
                "CSharpSyntaxTree.ParseText(Microsoft.CodeAnalysis.Text.SourceText.From(text, System.Text.Encoding.UTF8), CSharpParseOptions)"
            ),
            (
                Path.Combine(targetRoot, "src", "Shared", "Microsoft.AspNetCore.Razor.Test.Common", "Language", "IntegrationTests", "IntegrationTestBase.cs"),
                "CSharpSyntaxTree.ParseText(text, CSharpParseOptions, path: filePath ?? string.Empty)",
                "CSharpSyntaxTree.ParseText(Microsoft.CodeAnalysis.Text.SourceText.From(text, System.Text.Encoding.UTF8), CSharpParseOptions, path: filePath ?? string.Empty)"
            ),
            (
                Path.Combine(targetRoot, "src", "Razor", "src", "Razor", "test", "Microsoft.CodeAnalysis.Razor.CohostingShared.UnitTests", "Mapping", "RazorEditServiceTest.cs"),
                "CSharpSyntaxTree.ParseText(csharpSource)",
                "CSharpSyntaxTree.ParseText(Microsoft.CodeAnalysis.Text.SourceText.From(csharpSource, System.Text.Encoding.UTF8))"
            ),
            (
                Path.Combine(targetRoot, "src", "Razor", "src", "Razor", "test", "Microsoft.CodeAnalysis.Razor.CohostingShared.UnitTests", "Mapping", "RazorEditServiceTest.cs"),
                "CSharpSyntaxTree.ParseText(newCSharpSource)",
                "CSharpSyntaxTree.ParseText(Microsoft.CodeAnalysis.Text.SourceText.From(newCSharpSource, System.Text.Encoding.UTF8))"
            ),
            (
                Path.Combine(targetRoot, "src", "Compiler", "test", "Microsoft.NET.Sdk.Razor.SourceGenerators.UnitTests", "RazorSourceGeneratorTests.cs"),
                """
                    .AddSyntaxTrees(CSharpSyntaxTree.ParseText(@"
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
namespace SurveyPromptRootNamspace;
public class SurveyPrompt : ComponentBase
{
    protected override void BuildRenderTree(RenderTreeBuilder builder) {}
}"));
""".ReplaceLineEndings(),
                """
                    .AddSyntaxTrees(CSharpSyntaxTree.ParseText(Microsoft.CodeAnalysis.Text.SourceText.From(@"
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
namespace SurveyPromptRootNamspace;
public class SurveyPrompt : ComponentBase
{
    protected override void BuildRenderTree(RenderTreeBuilder builder) {}
}", System.Text.Encoding.UTF8)));
""".ReplaceLineEndings()
            ),
        };

        foreach (var group in replacements.GroupBy(static replacement => replacement.Path, StringComparer.OrdinalIgnoreCase))
        {
            var path = group.Key;
            if (!File.Exists(path))
                continue;

            var originalContent = await File.ReadAllTextAsync(path).ConfigureAwait(false);
            var updatedContent = originalContent;

            foreach (var (_, oldText, newText) in group)
                updatedContent = updatedContent.Replace(oldText, newText, StringComparison.Ordinal);

            if (string.Equals(originalContent, updatedContent, StringComparison.Ordinal))
                continue;

            await WriteTextPreservingUtf8BomAsync(path, updatedContent, templatePath: path).ConfigureAwait(false);
            changedFiles.Add(Path.GetRelativePath(targetRepoRoot, path));
        }

        return changedFiles.Count == 0
            ? "No Razor ParseText SourceText rewrites were needed."
            : $"Normalized Razor ParseText calls to use SourceText in {changedFiles.Count} file(s): {string.Join(", ", changedFiles)}.";
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
            GetExistingPath(
                Path.Combine(targetRoot, "src", "Razor", "test", "Microsoft.VisualStudio.LanguageServices.Razor.UnitTests", "ProjectSystem", "TestProjectSystemServices.cs"),
                Path.Combine(targetRoot, "src", "Razor", "test", "Microsoft.VisualStudio.LanguageServices.Razor.Test", "ProjectSystem", "TestProjectSystemServices.cs")),
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

    private static async Task<string> NormalizeRazorXunitTheoryDataAsync(StageContext context)
    {
        var targetRepoRoot = context.TargetRepoRoot;
        var razorRoot = context.TargetRoot;
        if (!Directory.Exists(razorRoot))
            return "No Razor source tree was found for xUnit TheoryData cleanup.";

        var changedFiles = new List<string>();
        foreach (var path in Directory.EnumerateFiles(razorRoot, "*Test.cs", SearchOption.AllDirectories))
        {
            var originalContent = await File.ReadAllTextAsync(path).ConfigureAwait(false);
            var updatedContent = originalContent;

            if (path.EndsWith("CSharpCodeParserTest.cs", StringComparison.OrdinalIgnoreCase))
            {
                updatedContent = updatedContent.Replace(
                    "    public static TheoryData InvalidTagHelperPrefixData",
                    "    public static TheoryData<string, SourceLocation, IEnumerable<RazorDiagnostic>> InvalidTagHelperPrefixData",
                    StringComparison.Ordinal);
                updatedContent = updatedContent.Replace(
                    "    public static TheoryData<string, SourceLocation, object> InvalidTagHelperPrefixData",
                    "    public static TheoryData<string, SourceLocation, IEnumerable<RazorDiagnostic>> InvalidTagHelperPrefixData",
                    StringComparison.Ordinal);
                updatedContent = updatedContent.Replace(
                    "        object expectedErrors)",
                    "        IEnumerable<RazorDiagnostic> expectedErrors)",
                    StringComparison.Ordinal);
                updatedContent = updatedContent.Replace(
                    "        var expectedDiagnostics = (IEnumerable<RazorDiagnostic>)expectedErrors;" + Environment.NewLine,
                    "        var expectedDiagnostics = expectedErrors;" + Environment.NewLine,
                    StringComparison.Ordinal);
            }

            if (path.EndsWith("TagHelperMatchingConventionsTest.cs", StringComparison.OrdinalIgnoreCase))
            {
                updatedContent = updatedContent.Replace(
                    "    public static TheoryData RequiredAttributeDescriptorData",
                    "    public static TheoryData<Action<RequiredAttributeDescriptorBuilder>, string, string, bool> RequiredAttributeDescriptorData",
                    StringComparison.Ordinal);
            }

            if (path.EndsWith("TagHelperParseTreeRewriterTest.cs", StringComparison.OrdinalIgnoreCase))
            {
                updatedContent = updatedContent.Replace(
                    "    public static TheoryData GetAttributeNameValuePairsData",
                    "    public static TheoryData<string, IEnumerable<KeyValuePair<string, string>>> GetAttributeNameValuePairsData",
                    StringComparison.Ordinal);
            }

            if (string.Equals(originalContent, updatedContent, StringComparison.Ordinal))
                continue;

            await WriteTextPreservingUtf8BomAsync(path, updatedContent, templatePath: path).ConfigureAwait(false);
            changedFiles.Add(Path.GetRelativePath(targetRepoRoot, path));
        }

        return changedFiles.Count == 0
            ? "No Razor xUnit TheoryData cleanup changes were needed."
            : $"Normalized Razor xUnit TheoryData declarations in {changedFiles.Count} file(s): {string.Join(", ", changedFiles)}.";
    }

    private static async Task<string> NormalizeRazorXunitAnalyzersAsync(StageContext context)
    {
        var summaries = new List<string>();

        AddSummaryIfChanged(
            summaries,
            await NormalizeRazorXunitTheoryDataAsync(context).ConfigureAwait(false),
            "No Razor xUnit TheoryData cleanup changes were needed.");
        AddSummaryIfChanged(
            summaries,
            await FixRazorXunit2031AssertSingleWhereAsync(context).ConfigureAwait(false),
            "No Razor xUnit2031 Assert.Single rewrites were needed.");
        AddSummaryIfChanged(
            summaries,
            await FixRazorXunit2029AssertEmptyWhereAsync(context).ConfigureAwait(false),
            "No Razor xUnit2029 Assert.Empty rewrites were needed.");

        return summaries.Count == 0
            ? "No Razor xUnit analyzer cleanup changes were needed."
            : string.Join(" ", summaries);
    }

    private static async Task<string> NormalizeRazorWarningBaselineAsync(StageContext context)
    {
        var targetRepoRoot = context.TargetRepoRoot;
        var targetRoot = context.TargetRoot;
        var changedFiles = new List<string>();

        var editorConfigPaths = new[]
        {
            Path.Combine(targetRoot, ".editorconfig"),
            Path.Combine(targetRoot, "src", "Compiler", ".editorconfig"),
            Path.Combine(targetRoot, "src", "Razor", "src", ".editorconfig"),
            Path.Combine(targetRoot, "src", "Razor", "test", ".editorconfig"),
        };

        var warningCodes = new[]
        {
            "CA1802",
            "IDE0036",
            "CA2007",
            "RS0030",
            "RS0031",
            "VSTHRD200",
            "IDE0005",
            "xUnit2031",
            "IDE0044",
            "IDE0055",
            "IDE2003",
            "CA1052",
            "IDE2000",
            "IDE0073",
            "xUnit2029",
            "VSSDK003",
            "IDE0052",
            "IDE0060",
        };

        foreach (var path in editorConfigPaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(path))
                continue;

            var originalContent = await File.ReadAllTextAsync(path).ConfigureAwait(false);
            var updatedContent = originalContent;
            foreach (var warningCode in warningCodes)
                updatedContent = SetEditorConfigSeverity(updatedContent, warningCode, "none");

            if (string.Equals(originalContent, updatedContent, StringComparison.Ordinal))
                continue;

            await WriteTextPreservingUtf8BomAsync(path, updatedContent, templatePath: path).ConfigureAwait(false);
            changedFiles.Add(Path.GetRelativePath(targetRepoRoot, path));
        }

        var propsPaths = Directory.EnumerateFiles(targetRoot, "Directory.Build.props", SearchOption.AllDirectories)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var path in propsPaths)
        {
            var originalContent = await File.ReadAllTextAsync(path).ConfigureAwait(false);
            var updatedContent = EnsureBooleanPropertyValue(originalContent, "EnforceCodeStyleInBuild", false);
            updatedContent = EnsureNoWarnContains(updatedContent, warningCodes);

            if (string.Equals(originalContent, updatedContent, StringComparison.Ordinal))
                continue;

            await WriteTextPreservingUtf8BomAsync(path, updatedContent, templatePath: path).ConfigureAwait(false);
            changedFiles.Add(Path.GetRelativePath(targetRepoRoot, path));
        }

        return changedFiles.Count == 0
            ? "No Razor warning baseline normalization changes were needed."
            : $"Normalized Razor warning baseline settings in {changedFiles.Count} file(s): {string.Join(", ", changedFiles.Distinct(StringComparer.OrdinalIgnoreCase))}.";
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

    private static IEnumerable<string> EnumerateMsBuildAndSolutionFiles(string root)
        => Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
            .Where(static path =>
                path.EndsWith(".props", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith(".targets", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith(".vbproj", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith(".projitems", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith(".shproj", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase));

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

    private static async Task<List<string>> RenameRazorUnitTestProjectsAsync(StageContext context)
    {
        var targetRepoRoot = context.TargetRepoRoot;
        var targetRoot = context.TargetRoot;
        var renamedArtifacts = new List<string>();
        var replacements = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var directoryReplacements = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var pathsToStage = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Console.WriteLine("    - collecting tracked Razor files");
        var trackedRelativePaths = await GetTrackedRelativePathsAsync(targetRepoRoot, targetRoot).ConfigureAwait(false);
        var trackedPaths = trackedRelativePaths
            .Select(relativePath => Path.Combine(targetRepoRoot, relativePath))
            .ToList();
        Console.WriteLine($"    - collected {trackedPaths.Count} tracked file(s)");

        Console.WriteLine("    - checking tracked directories for Roslyn-style UnitTests rename");
        foreach (var oldDirectoryPath in trackedPaths
            .Select(Path.GetDirectoryName)
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Select(static path => path!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(static path => IsRazorTestDirectoryName(Path.GetFileName(path)))
            .OrderByDescending(static path => path.Length))
        {
            var oldDirectoryName = Path.GetFileName(oldDirectoryPath);
            var newDirectoryName = RenameRazorTestIdentifier(oldDirectoryName);
            if (string.Equals(oldDirectoryName, newDirectoryName, StringComparison.OrdinalIgnoreCase))
                continue;

            var newDirectoryPath = Path.Combine(Path.GetDirectoryName(oldDirectoryPath)!, newDirectoryName);
            replacements[oldDirectoryName] = newDirectoryName;
            directoryReplacements[oldDirectoryName] = newDirectoryName;

            if (!Directory.Exists(oldDirectoryPath) || Directory.Exists(newDirectoryPath))
                continue;

            Directory.Move(oldDirectoryPath, newDirectoryPath);
            var oldRelativePath = Path.GetRelativePath(targetRepoRoot, oldDirectoryPath);
            var newRelativePath = Path.GetRelativePath(targetRepoRoot, newDirectoryPath);
            pathsToStage.Add(oldRelativePath);
            pathsToStage.Add(newRelativePath);
            renamedArtifacts.Add($"{oldRelativePath} -> {newRelativePath}");
        }

        Console.WriteLine("    - checking tracked project artifacts for rename");
        foreach (var oldFilePath in trackedPaths
            .Where(static path =>
                path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith(".projitems", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith(".shproj", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(static path => path.Length))
        {
            var currentDirectoryPath = ApplyPathReplacements(Path.GetDirectoryName(oldFilePath)!, directoryReplacements);
            var oldFileName = Path.GetFileName(oldFilePath);
            var currentFilePath = Path.Combine(currentDirectoryPath, oldFileName);
            var newFileName = RenameRazorTestArtifactFileName(oldFileName);
            if (string.Equals(oldFileName, newFileName, StringComparison.OrdinalIgnoreCase))
                continue;

            var newFilePath = Path.Combine(Path.GetDirectoryName(currentFilePath)!, newFileName);
            AddUnitTestReplacementVariants(replacements, oldFileName, newFileName);

            if (!File.Exists(currentFilePath) || File.Exists(newFilePath))
                continue;

            File.Move(currentFilePath, newFilePath);
            var oldRelativePath = Path.GetRelativePath(targetRepoRoot, oldFilePath);
            var newRelativePath = Path.GetRelativePath(targetRepoRoot, newFilePath);
            pathsToStage.Add(oldRelativePath);
            pathsToStage.Add(newRelativePath);
            renamedArtifacts.Add($"{oldRelativePath} -> {newRelativePath}");
        }

        foreach (var trackedPath in trackedPaths)
        {
            var fileName = Path.GetFileName(trackedPath);
            AddLegacyUnitTestAssemblyReplacements(replacements, fileName);
        }

        if (replacements.Count == 0)
            return renamedArtifacts;

        Console.WriteLine($"    - rewriting references for {replacements.Count} rename mapping(s)");
        var referenceFiles = EnumerateMsBuildAndSolutionFiles(targetRepoRoot)
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var path in referenceFiles)
        {
            var originalContent = await File.ReadAllTextAsync(path).ConfigureAwait(false);
            var updatedContent = originalContent;
            foreach (var replacement in replacements)
                updatedContent = updatedContent.Replace(replacement.Key, replacement.Value, StringComparison.Ordinal);

            if (string.Equals(originalContent, updatedContent, StringComparison.Ordinal))
                continue;

            await WriteTextPreservingUtf8BomAsync(path, updatedContent, templatePath: path).ConfigureAwait(false);
            pathsToStage.Add(Path.GetRelativePath(targetRepoRoot, path));
        }

        if (pathsToStage.Count > 0)
        {
            var stageablePaths = await GetStageableRelativePathsAsync(targetRepoRoot, pathsToStage).ConfigureAwait(false);
            if (stageablePaths.Count == 0)
                return renamedArtifacts;

            await GitRunner.RunGitAsync(
                targetRepoRoot,
                ["add", "--all", "--", .. stageablePaths]).ConfigureAwait(false);
        }

        return renamedArtifacts;
    }

    private static string RenameRazorTestIdentifier(string name)
    {
        if (name.EndsWith(".UnitTests", StringComparison.OrdinalIgnoreCase))
            return name;

        if (name.EndsWith(".Tests", StringComparison.OrdinalIgnoreCase))
            return name[..^".Tests".Length] + ".UnitTests";

        return name.EndsWith(".Test", StringComparison.OrdinalIgnoreCase)
            ? name[..^".Test".Length] + ".UnitTests"
            : name;
    }

    private static string RenameRazorTestArtifactFileName(string fileName)
    {
        if (fileName.EndsWith(".Test.csproj", StringComparison.OrdinalIgnoreCase))
            return fileName[..^".Test.csproj".Length] + ".UnitTests.csproj";
        if (fileName.EndsWith(".Tests.csproj", StringComparison.OrdinalIgnoreCase))
            return fileName[..^".Tests.csproj".Length] + ".UnitTests.csproj";
        if (fileName.EndsWith(".Test.projitems", StringComparison.OrdinalIgnoreCase))
            return fileName[..^".Test.projitems".Length] + ".UnitTests.projitems";
        if (fileName.EndsWith(".Tests.projitems", StringComparison.OrdinalIgnoreCase))
            return fileName[..^".Tests.projitems".Length] + ".UnitTests.projitems";
        if (fileName.EndsWith(".Test.shproj", StringComparison.OrdinalIgnoreCase))
            return fileName[..^".Test.shproj".Length] + ".UnitTests.shproj";
        if (fileName.EndsWith(".Tests.shproj", StringComparison.OrdinalIgnoreCase))
            return fileName[..^".Tests.shproj".Length] + ".UnitTests.shproj";

        return fileName;
    }

    private static void AddUnitTestReplacementVariants(IDictionary<string, string> replacements, string oldName, string newName)
    {
        replacements[oldName] = newName;

        var oldIdentifier = Path.GetFileNameWithoutExtension(oldName);
        var newIdentifier = Path.GetFileNameWithoutExtension(newName);
        if (!string.Equals(oldIdentifier, newIdentifier, StringComparison.OrdinalIgnoreCase))
            replacements[oldIdentifier] = newIdentifier;
    }

    private static void AddLegacyUnitTestAssemblyReplacements(IDictionary<string, string> replacements, string fileName)
    {
        var identifier = Path.GetFileNameWithoutExtension(fileName);
        if (!identifier.EndsWith(".UnitTests", StringComparison.OrdinalIgnoreCase))
            return;

        var prefix = identifier[..^".UnitTests".Length];
        replacements.TryAdd(prefix + ".Test", identifier);
        replacements.TryAdd(prefix + ".Tests", identifier);
    }

    private static string NormalizeRazorUnitTestPropertyGroupContent(string content)
    {
        const string propertyGroupAnchor = "<PropertyGroup Condition=\"'$(IsUnitTestProject)' == ''";
        var propertyGroupStart = content.IndexOf(propertyGroupAnchor, StringComparison.Ordinal);
        if (propertyGroupStart < 0)
            return content;

        var replaceStart = propertyGroupStart;
        var commentStart = content.LastIndexOf("<!--", propertyGroupStart, StringComparison.Ordinal);
        if (commentStart >= 0)
        {
            var commentEnd = content.IndexOf("-->", commentStart, StringComparison.Ordinal);
            if (commentEnd >= 0)
            {
                var betweenCommentAndPropertyGroup = content[(commentEnd + 3)..propertyGroupStart];
                if (betweenCommentAndPropertyGroup.All(char.IsWhiteSpace))
                    replaceStart = commentStart;
            }
        }

        var propertyGroupEnd = content.IndexOf("</PropertyGroup>", propertyGroupStart, StringComparison.Ordinal);
        if (propertyGroupEnd < 0)
            return content;

        var replaceEnd = propertyGroupEnd + "</PropertyGroup>".Length;
        while (replaceEnd < content.Length && (content[replaceEnd] == '\r' || content[replaceEnd] == '\n'))
            replaceEnd++;

        return content[..replaceStart] + RazorUnitTestPropertyGroupBlock + content[replaceEnd..];
    }

    private static bool IsRazorTestDirectoryName(string directoryName)
        => directoryName.EndsWith(".Test", StringComparison.OrdinalIgnoreCase)
            || directoryName.EndsWith(".Tests", StringComparison.OrdinalIgnoreCase);

    private static bool ShouldDisableRazorPublicApiAnalyzers(string relativePath, string projectName)
        => relativePath.Contains($"{Path.DirectorySeparatorChar}test{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
            || relativePath.Contains($"{Path.DirectorySeparatorChar}legacyTest{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
            || relativePath.Contains($"{Path.DirectorySeparatorChar}benchmarks{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
            || relativePath.Contains($"{Path.DirectorySeparatorChar}perf{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
            || projectName.EndsWith(".Test", StringComparison.OrdinalIgnoreCase)
            || projectName.EndsWith(".Tests", StringComparison.OrdinalIgnoreCase)
            || projectName.Contains("UnitTest", StringComparison.OrdinalIgnoreCase)
            || projectName.Contains("IntegrationTest", StringComparison.OrdinalIgnoreCase)
            || projectName.Contains("Test.Common", StringComparison.OrdinalIgnoreCase)
            || projectName.Contains("MvcShim", StringComparison.OrdinalIgnoreCase)
            || projectName.Contains("Benchmark", StringComparison.OrdinalIgnoreCase);

    private static bool IsMsBuildOrSolutionPath(string path)
        => path.EndsWith(".props", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".targets", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".vbproj", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".projitems", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".shproj", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase);

    private static string ApplyPathReplacements(string path, IReadOnlyDictionary<string, string> replacements)
    {
        var updatedPath = path;
        foreach (var replacement in replacements
                     .Where(static replacement => !string.IsNullOrWhiteSpace(replacement.Key))
                     .OrderByDescending(static replacement => replacement.Key.Length)
                     .ThenBy(static replacement => replacement.Key, StringComparer.Ordinal))
        {
            updatedPath = updatedPath.Replace(replacement.Key, replacement.Value, StringComparison.Ordinal);
        }

        return updatedPath;
    }

    private static async Task<List<string>> GetStageableRelativePathsAsync(string repositoryRoot, IEnumerable<string> relativePaths)
    {
        var stageablePaths = new List<string>();

        foreach (var relativePath in relativePaths
                     .Where(static path => !string.IsNullOrWhiteSpace(path))
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase))
        {
            var fullPath = Path.Combine(repositoryRoot, relativePath);
            if (File.Exists(fullPath) || Directory.Exists(fullPath))
            {
                stageablePaths.Add(relativePath);
                continue;
            }

            var pendingDiff = (await GitRunner.RunGitAsync(repositoryRoot, "diff", "--name-only", "--", relativePath).ConfigureAwait(false)).Trim();
            if (!string.IsNullOrWhiteSpace(pendingDiff))
            {
                stageablePaths.Add(relativePath);
                continue;
            }

            var pendingCachedDiff = (await GitRunner.RunGitAsync(repositoryRoot, "diff", "--cached", "--name-only", "--", relativePath).ConfigureAwait(false)).Trim();
            if (!string.IsNullOrWhiteSpace(pendingCachedDiff))
                stageablePaths.Add(relativePath);
        }

        return stageablePaths;
    }

    private static string RemoveTypeDeclarationBlock(string content, string typeDeclaration)
    {
        var declarationIndex = content.IndexOf(typeDeclaration, StringComparison.Ordinal);
        if (declarationIndex < 0)
            return content;

        var blockStart = content.LastIndexOf(Environment.NewLine, declarationIndex, StringComparison.Ordinal);
        blockStart = blockStart < 0 ? 0 : blockStart + Environment.NewLine.Length;

        var openBraceIndex = content.IndexOf('{', declarationIndex);
        if (openBraceIndex < 0)
            return content;

        var depth = 0;
        for (var i = openBraceIndex; i < content.Length; i++)
        {
            if (content[i] == '{')
            {
                depth++;
            }
            else if (content[i] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    var blockEnd = i + 1;
                    while (blockEnd < content.Length && (content[blockEnd] == '\r' || content[blockEnd] == '\n'))
                        blockEnd++;

                    return content[..blockStart] + content[blockEnd..];
                }
            }
        }

        return content;
    }

    private static async Task<List<string>> GetTrackedProjectPathsAsync(string targetRepoRoot, string scopedRoot)
        => (await GetTrackedRelativePathsAsync(targetRepoRoot, scopedRoot).ConfigureAwait(false))
            .Where(static path => path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            .Select(path => Path.Combine(targetRepoRoot, path))
            .Where(File.Exists)
            .ToList();

    private static async Task<List<string>> GetTrackedRelativePathsAsync(string targetRepoRoot, string scopedRoot)
    {
        var relativeRoot = NormalizeRelativePath(Path.GetRelativePath(targetRepoRoot, scopedRoot));
        var gitRelativeRoot = relativeRoot.Replace(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var result = await ProcessRunner.RunProcessAsync(
            "git",
            ["ls-files", "--", gitRelativeRoot],
            targetRepoRoot,
            logOutput: false).ConfigureAwait(false);
        ProcessRunner.EnsureCommandSucceeded(result, $"git ls-files -- {gitRelativeRoot}");

        return result.Output
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormalizeRelativePath)
            .ToList();
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

    private static string GetExistingPath(params string[] candidatePaths)
        => candidatePaths.FirstOrDefault(File.Exists)
            ?? candidatePaths[0];

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

        if (path.EndsWith("CSharpCodeParserTest.cs", StringComparison.OrdinalIgnoreCase))
        {
            content = content.Replace(
                "    public static TheoryData InvalidTagHelperPrefixData",
                "    public static TheoryData<string, SourceLocation, IEnumerable<RazorDiagnostic>> InvalidTagHelperPrefixData",
                StringComparison.Ordinal);
            content = content.Replace(
                "    public static TheoryData<string, SourceLocation, object> InvalidTagHelperPrefixData",
                "    public static TheoryData<string, SourceLocation, IEnumerable<RazorDiagnostic>> InvalidTagHelperPrefixData",
                StringComparison.Ordinal);
            content = content.Replace(
                "        object expectedErrors)",
                "        IEnumerable<RazorDiagnostic> expectedErrors)",
                StringComparison.Ordinal);
            content = content.Replace(
                "        var expectedDiagnostics = (IEnumerable<RazorDiagnostic>)expectedErrors;" + Environment.NewLine,
                "        var expectedDiagnostics = expectedErrors;" + Environment.NewLine,
                StringComparison.Ordinal);
        }

        if (path.EndsWith("TagHelperMatchingConventionsTest.cs", StringComparison.OrdinalIgnoreCase))
        {
            content = content.Replace(
                "    public static TheoryData RequiredAttributeDescriptorData",
                "    public static TheoryData<Action<RequiredAttributeDescriptorBuilder>, string, string, bool> RequiredAttributeDescriptorData",
                StringComparison.Ordinal);
        }

        if (path.EndsWith("TagHelperParseTreeRewriterTest.cs", StringComparison.OrdinalIgnoreCase))
        {
            content = content.Replace(
                "    public static TheoryData GetAttributeNameValuePairsData",
                "    public static TheoryData<string, IEnumerable<KeyValuePair<string, string>>> GetAttributeNameValuePairsData",
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

    private static (string UpdatedContent, int ReplacementCount) RewriteMockOfCallsWithExplicitStrictness(string content)
    {
        var builder = new StringBuilder(content.Length);
        var replacementCount = 0;
        var copyStart = 0;
        var searchIndex = 0;

        while (TryFindNextMockOfCallToRewrite(content, searchIndex, out var invocationStart, out var invocationEnd, out var replacement))
        {
            builder.Append(content, copyStart, invocationStart - copyStart);
            builder.Append(replacement);
            copyStart = invocationEnd;
            searchIndex = invocationEnd;
            replacementCount++;
        }

        if (replacementCount == 0)
            return (content, 0);

        builder.Append(content, copyStart, content.Length - copyStart);
        return (builder.ToString(), replacementCount);
    }

    private static bool TryFindNextMockOfCallToRewrite(
        string content,
        int startIndex,
        out int invocationStart,
        out int invocationEnd,
        out string replacement)
    {
        const string token = "Mock.Of<";

        for (var candidateIndex = content.IndexOf(token, startIndex, StringComparison.Ordinal);
             candidateIndex >= 0;
             candidateIndex = content.IndexOf(token, candidateIndex + 1, StringComparison.Ordinal))
        {
            if (candidateIndex > 0)
            {
                var precedingCharacter = content[candidateIndex - 1];
                if (char.IsLetterOrDigit(precedingCharacter) || precedingCharacter is '_' or '.' or ':')
                    continue;
            }

            if (!TryFindMatchingDelimiter(content, candidateIndex + token.Length - 1, '<', '>', out var typeCloseIndex))
                continue;

            var typeStartIndex = candidateIndex + token.Length;
            var currentIndex = typeCloseIndex + 1;
            while (currentIndex < content.Length && char.IsWhiteSpace(content[currentIndex]))
                currentIndex++;

            if (currentIndex >= content.Length || content[currentIndex] != '(')
                continue;

            if (!TryFindMatchingDelimiter(content, currentIndex, '(', ')', out var invocationCloseIndex))
                continue;

            var argumentsText = content[(currentIndex + 1)..invocationCloseIndex];
            if (!TrySplitTopLevelArguments(argumentsText, out var arguments))
                continue;

            var typeArguments = content[typeStartIndex..typeCloseIndex];
            if (!TryBuildMockOfReplacement(typeArguments, arguments, out replacement))
                continue;

            invocationStart = candidateIndex;
            invocationEnd = invocationCloseIndex + 1;
            return true;
        }

        invocationStart = -1;
        invocationEnd = -1;
        replacement = string.Empty;
        return false;
    }

    private static bool TryBuildMockOfReplacement(string typeArguments, IReadOnlyList<string> arguments, out string replacement)
    {
        switch (arguments.Count)
        {
            case 0:
                replacement = $"new Mock<{typeArguments}>(MockBehavior.Strict).Object";
                return true;

            case 1:
                if (string.Equals(arguments[0].Trim(), "MockBehavior.Strict", StringComparison.Ordinal))
                {
                    replacement = $"new Mock<{typeArguments}>(MockBehavior.Strict).Object";
                    return true;
                }

                replacement = $"new MockRepository(MockBehavior.Strict).OneOf<{typeArguments}>({arguments[0]})";
                return true;

            case 2:
                if (string.Equals(arguments[1].Trim(), "MockBehavior.Strict", StringComparison.Ordinal))
                {
                    replacement = $"new MockRepository(MockBehavior.Strict).OneOf<{typeArguments}>({arguments[0]})";
                    return true;
                }

                break;
        }

        replacement = string.Empty;
        return false;
    }

    private static bool TryFindMatchingDelimiter(string content, int openIndex, char openDelimiter, char closeDelimiter, out int closeIndex)
    {
        var delimiterDepth = 0;
        var inSingleLineComment = false;
        var inMultiLineComment = false;
        var inString = false;
        var inCharLiteral = false;
        var isVerbatimString = false;

        for (var i = openIndex; i < content.Length; i++)
        {
            var currentCharacter = content[i];
            var nextCharacter = i + 1 < content.Length ? content[i + 1] : '\0';

            if (inSingleLineComment)
            {
                if (currentCharacter is '\r' or '\n')
                    inSingleLineComment = false;

                continue;
            }

            if (inMultiLineComment)
            {
                if (currentCharacter == '*' && nextCharacter == '/')
                {
                    inMultiLineComment = false;
                    i++;
                }

                continue;
            }

            if (inString)
            {
                if (isVerbatimString)
                {
                    if (currentCharacter == '"' && nextCharacter == '"')
                    {
                        i++;
                        continue;
                    }

                    if (currentCharacter == '"')
                    {
                        inString = false;
                        isVerbatimString = false;
                    }

                    continue;
                }

                if (currentCharacter == '\\')
                {
                    i++;
                    continue;
                }

                if (currentCharacter == '"')
                    inString = false;

                continue;
            }

            if (inCharLiteral)
            {
                if (currentCharacter == '\\')
                {
                    i++;
                    continue;
                }

                if (currentCharacter == '\'')
                    inCharLiteral = false;

                continue;
            }

            if (currentCharacter == '/' && nextCharacter == '/')
            {
                inSingleLineComment = true;
                i++;
                continue;
            }

            if (currentCharacter == '/' && nextCharacter == '*')
            {
                inMultiLineComment = true;
                i++;
                continue;
            }

            if (currentCharacter == '@' && nextCharacter == '"')
            {
                inString = true;
                isVerbatimString = true;
                i++;
                continue;
            }

            if ((currentCharacter == '$' && nextCharacter == '"')
                || (currentCharacter == '$' && nextCharacter == '@' && i + 2 < content.Length && content[i + 2] == '"')
                || (currentCharacter == '@' && nextCharacter == '$' && i + 2 < content.Length && content[i + 2] == '"'))
            {
                inString = true;
                isVerbatimString = currentCharacter == '@' || nextCharacter == '@';
                if (i + 2 < content.Length && content[i + 2] == '"')
                    i += 2;
                else
                    i++;

                continue;
            }

            if (currentCharacter == '"')
            {
                inString = true;
                isVerbatimString = false;
                continue;
            }

            if (currentCharacter == '\'')
            {
                inCharLiteral = true;
                continue;
            }

            if (currentCharacter == openDelimiter)
            {
                delimiterDepth++;
                continue;
            }

            if (currentCharacter != closeDelimiter)
                continue;

            delimiterDepth--;
            if (delimiterDepth == 0)
            {
                closeIndex = i;
                return true;
            }
        }

        closeIndex = -1;
        return false;
    }

    private static bool TrySplitTopLevelArguments(string argumentsText, out List<string> arguments)
    {
        arguments = [];

        var segmentStart = 0;
        var parenthesisDepth = 0;
        var angleDepth = 0;
        var bracketDepth = 0;
        var braceDepth = 0;
        var inSingleLineComment = false;
        var inMultiLineComment = false;
        var inString = false;
        var inCharLiteral = false;
        var isVerbatimString = false;

        for (var i = 0; i < argumentsText.Length; i++)
        {
            var currentCharacter = argumentsText[i];
            var nextCharacter = i + 1 < argumentsText.Length ? argumentsText[i + 1] : '\0';

            if (inSingleLineComment)
            {
                if (currentCharacter is '\r' or '\n')
                    inSingleLineComment = false;

                continue;
            }

            if (inMultiLineComment)
            {
                if (currentCharacter == '*' && nextCharacter == '/')
                {
                    inMultiLineComment = false;
                    i++;
                }

                continue;
            }

            if (inString)
            {
                if (isVerbatimString)
                {
                    if (currentCharacter == '"' && nextCharacter == '"')
                    {
                        i++;
                        continue;
                    }

                    if (currentCharacter == '"')
                    {
                        inString = false;
                        isVerbatimString = false;
                    }

                    continue;
                }

                if (currentCharacter == '\\')
                {
                    i++;
                    continue;
                }

                if (currentCharacter == '"')
                    inString = false;

                continue;
            }

            if (inCharLiteral)
            {
                if (currentCharacter == '\\')
                {
                    i++;
                    continue;
                }

                if (currentCharacter == '\'')
                    inCharLiteral = false;

                continue;
            }

            if (currentCharacter == '/' && nextCharacter == '/')
            {
                inSingleLineComment = true;
                i++;
                continue;
            }

            if (currentCharacter == '/' && nextCharacter == '*')
            {
                inMultiLineComment = true;
                i++;
                continue;
            }

            if (currentCharacter == '@' && nextCharacter == '"')
            {
                inString = true;
                isVerbatimString = true;
                i++;
                continue;
            }

            if ((currentCharacter == '$' && nextCharacter == '"')
                || (currentCharacter == '$' && nextCharacter == '@' && i + 2 < argumentsText.Length && argumentsText[i + 2] == '"')
                || (currentCharacter == '@' && nextCharacter == '$' && i + 2 < argumentsText.Length && argumentsText[i + 2] == '"'))
            {
                inString = true;
                isVerbatimString = currentCharacter == '@' || nextCharacter == '@';
                if (i + 2 < argumentsText.Length && argumentsText[i + 2] == '"')
                    i += 2;
                else
                    i++;

                continue;
            }

            switch (currentCharacter)
            {
                case '"':
                    inString = true;
                    isVerbatimString = false;
                    continue;

                case '\'':
                    inCharLiteral = true;
                    continue;

                case '(':
                    parenthesisDepth++;
                    continue;

                case ')':
                    parenthesisDepth--;
                    continue;

                case '<':
                    angleDepth++;
                    continue;

                case '>':
                    angleDepth = Math.Max(0, angleDepth - 1);
                    continue;

                case '[':
                    bracketDepth++;
                    continue;

                case ']':
                    bracketDepth--;
                    continue;

                case '{':
                    braceDepth++;
                    continue;

                case '}':
                    braceDepth--;
                    continue;

                case ',' when parenthesisDepth == 0 && angleDepth == 0 && bracketDepth == 0 && braceDepth == 0:
                    arguments.Add(argumentsText[segmentStart..i]);
                    segmentStart = i + 1;
                    continue;
            }
        }

        if (parenthesisDepth != 0 || bracketDepth != 0 || braceDepth != 0)
            return false;

        if (angleDepth < 0)
            return false;

        var trailingSegment = argumentsText[segmentStart..];
        if (arguments.Count == 0 && string.IsNullOrWhiteSpace(trailingSegment))
            return true;

        arguments.Add(trailingSegment);
        return true;
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
        content = RemoveTypeDeclarationBlock(content, "public class TestActiveConfigurationGroupSubscriptionService");

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

    private static string SetEditorConfigSeverity(string content, string diagnosticId, string severity)
    {
        var escapedDiagnosticId = Regex.Escape(diagnosticId);
        var pattern = new Regex(
            $@"^(?<indent>[ \t]*)dotnet_diagnostic\.{escapedDiagnosticId}\.severity\s*=\s*\w+\s*$",
            RegexOptions.Multiline | RegexOptions.CultureInvariant);

        if (pattern.IsMatch(content))
        {
            return pattern.Replace(
                content,
                $"${{indent}}dotnet_diagnostic.{diagnosticId}.severity = {severity}");
        }

        var trailingNewLine = content.EndsWith(Environment.NewLine, StringComparison.Ordinal) ? string.Empty : Environment.NewLine;
        return content + trailingNewLine + $"dotnet_diagnostic.{diagnosticId}.severity = {severity}" + Environment.NewLine;
    }

    private static async Task<string> EnsureProjectLocalEditorConfigSuppressionAsync(
        StageContext context,
        string projectDirectory,
        string projectDisplayName,
        string diagnosticId,
        string comment)
    {
        var targetRepoRoot = context.TargetRepoRoot;
        if (!Directory.Exists(projectDirectory))
            return $"No {projectDisplayName} project directory was found for {diagnosticId} suppression.";

        var editorConfigPath = Path.Combine(projectDirectory, ".editorconfig");
        var relativeEditorConfigPath = Path.GetRelativePath(targetRepoRoot, editorConfigPath);
        var templatePath = File.Exists(editorConfigPath)
            ? editorConfigPath
            : Path.Combine(targetRepoRoot, "src", "Razor", "src", "Razor", "src", ".editorconfig");

        var existingStatus = (await GitRunner.RunGitAsync(
            targetRepoRoot,
            "status",
            "--short",
            "--",
            relativeEditorConfigPath).ConfigureAwait(false)).Trim();
        var wasUntracked = existingStatus.StartsWith("??", StringComparison.Ordinal);
        var originalContent = File.Exists(editorConfigPath)
            ? await File.ReadAllTextAsync(editorConfigPath).ConfigureAwait(false)
            : "[*.cs]" + Environment.NewLine + Environment.NewLine + $"# {comment}" + Environment.NewLine;
        var updatedContent = SetEditorConfigSeverity(originalContent, diagnosticId, "none");

        if (string.Equals(originalContent, updatedContent, StringComparison.Ordinal) && !wasUntracked)
            return $"No {projectDisplayName} {diagnosticId} editorconfig changes were needed.";

        if (!string.Equals(originalContent, updatedContent, StringComparison.Ordinal))
            await WriteTextPreservingUtf8BomAsync(editorConfigPath, updatedContent, templatePath: templatePath).ConfigureAwait(false);

        await GitRunner.RunGitAsync(targetRepoRoot, "add", "--", relativeEditorConfigPath).ConfigureAwait(false);
        return $"Disabled {diagnosticId} in '{relativeEditorConfigPath}' via Razor's local editorconfig.";
    }

    private static async Task<(IReadOnlyList<string> ChangedFiles, int ReplacementCount)> ApplyKnownFileTextReplacementsAsync(
        string targetRepoRoot,
        params (string FilePath, (string OldText, string NewText)[] Replacements)[] fileReplacements)
    {
        var changedFiles = new List<string>();
        var replacementCount = 0;

        foreach (var fileReplacement in fileReplacements)
        {
            if (!File.Exists(fileReplacement.FilePath))
                continue;

            var originalContent = await File.ReadAllTextAsync(fileReplacement.FilePath).ConfigureAwait(false);
            var updatedContent = originalContent;
            var fileChanged = false;

            foreach (var replacement in fileReplacement.Replacements)
            {
                var nextContent = updatedContent.Replace(replacement.OldText, replacement.NewText, StringComparison.Ordinal);
                if (string.Equals(updatedContent, nextContent, StringComparison.Ordinal))
                    continue;

                updatedContent = nextContent;
                replacementCount++;
                fileChanged = true;
            }

            if (!fileChanged)
                continue;

            await WriteTextPreservingUtf8BomAsync(fileReplacement.FilePath, updatedContent, templatePath: fileReplacement.FilePath).ConfigureAwait(false);
            changedFiles.Add(Path.GetRelativePath(targetRepoRoot, fileReplacement.FilePath));
        }

        return (changedFiles, replacementCount);
    }

    private static void AddSummaryIfChanged(List<string> summaries, string summary, params string[] noChangeSummaries)
    {
        foreach (var noChangeSummary in noChangeSummaries)
        {
            if (string.Equals(summary, noChangeSummary, StringComparison.Ordinal))
                return;
        }

        summaries.Add(summary);
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

    private static string EnsureBooleanPropertyValue(string content, string propertyName, bool value)
    {
        var updatedContent = SetBooleanPropertyValue(content, propertyName, value);
        if (!string.Equals(updatedContent, content, StringComparison.Ordinal))
            return updatedContent;

        var insertion = $"    <{propertyName}>{value.ToString().ToLowerInvariant()}</{propertyName}>{Environment.NewLine}";
        var propertyGroupPattern = new Regex(@"<PropertyGroup(?<attributes>[^>]*)>\r?\n", RegexOptions.CultureInvariant);
        return propertyGroupPattern.Replace(
            content,
            match => $"{match.Value}{insertion}",
            1);
    }

    private static string EnsureNoWarnContains(string content, params string[] warningCodes)
    {
        var pattern = new Regex(
            @"^(?<indent>[ \t]*)<NoWarn>(?<value>.*?)</NoWarn>\s*$",
            RegexOptions.Multiline | RegexOptions.CultureInvariant);
        var match = pattern.Match(content);
        if (match.Success)
        {
            var existingValue = match.Groups["value"].Value.Trim();
            var existingCodes = existingValue
                .Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var missingCodes = warningCodes
                .Where(code => !existingCodes.Contains(code))
                .ToList();
            if (missingCodes.Count == 0)
                return content;

            var separator = string.IsNullOrWhiteSpace(existingValue) || existingValue.EndsWith(';') || existingValue.EndsWith(',')
                ? string.Empty
                : ";";
            var updatedValue = existingValue + separator + string.Join(';', missingCodes);
            return pattern.Replace(
                content,
                $"{match.Groups["indent"].Value}<NoWarn>{updatedValue}</NoWarn>",
                1);
        }

        var insertion = $"    <NoWarn>$(NoWarn);{string.Join(';', warningCodes)}</NoWarn>{Environment.NewLine}";
        var propertyGroupPattern = new Regex(@"<PropertyGroup(?<attributes>[^>]*)>\r?\n", RegexOptions.CultureInvariant);
        return propertyGroupPattern.Replace(
            content,
            match => $"{match.Value}{insertion}",
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

    private static async Task<string> BuildRazorVsixDirectoryBuildTargetsContentAsync(string sourcePath)
    {
        var sourceContent = await File.ReadAllTextAsync(sourcePath).ConfigureAwait(false);
        var updatedContent = sourceContent
            .Replace(
                "BeforeTargets=\"GenerateBrokeredServicesPkgDef\"",
                "BeforeTargets=\"GeneratePkgDef;GenerateRazorBrokeredServicesPkgDef\"",
                StringComparison.Ordinal)
            .Replace(
                @"$(RepositoryEngineeringDir)targets\ReplaceServiceHubAssetsInVsixManifest.targets",
                @"$(RepositoryRoot)eng\targets\ReplaceServiceHubAssetsInVsixManifest.targets",
                StringComparison.Ordinal)
            .Replace(
                @"$(RepositoryEngineeringDir)targets\GenerateBrokeredServicesPkgDef.targets",
                @"$(RepositoryRoot)eng\targets\GenerateRazorBrokeredServicesPkgDef.targets",
                StringComparison.Ordinal);

        var mergedRoslynCleanupBlock = string.Join(
            Environment.NewLine,
            [
                "  <ItemGroup>",
                @"    <Content Remove=""$(RepoRoot)src\Setup\Roslyn.VsixLicense\EULA.rtf"" />",
                @"    <Content Remove=""$(RepoRoot)src\Setup\Roslyn.ThirdPartyNotices\ThirdPartyNotices.rtf"" />",
                "  </ItemGroup>",
                string.Empty,
                "  <PropertyGroup>",
                "    <GetVsixSourceItemsDependsOn>$(GetVsixSourceItemsDependsOn);GenerateRazorBrokeredServicesPkgDef;GenerateRazorClientEnabledPkg;IncludeMissingRazorServiceHubPayload</GetVsixSourceItemsDependsOn>",
                "    <IncludeVSIXItemsDependsOn>$(IncludeVSIXItemsDependsOn);RemoveMergedRoslynVsixSourceItems</IncludeVSIXItemsDependsOn>",
                "  </PropertyGroup>",
                string.Empty,
                "  <Target Name=\"GenerateRazorClientEnabledPkg\">",
                "    <ItemGroup>",
                "      <_RazorClientEnabledPkgLine Include=\"$(TargetName).pkgdef\" />",
                "      <_RazorClientEnabledPkgLine Include=\"$(TargetName).Custom.pkgdef\" />",
                "      <_RazorClientEnabledPkgLine Include=\"$(TargetName).BrokeredServices.pkgdef\" />",
                "    </ItemGroup>",
                "    <PropertyGroup>",
                "      <_RazorClientEnabledPkgPath>$(IntermediateOutputPath)$(TargetName).clientenabledpkg</_RazorClientEnabledPkgPath>",
                "    </PropertyGroup>",
                "    <WriteLinesToFile File=\"$(_RazorClientEnabledPkgPath)\"",
                "                      Lines=\"@(_RazorClientEnabledPkgLine)\"",
                "                      Overwrite=\"true\"",
                "                      Encoding=\"UTF-8\"",
                "                      WriteOnlyWhenDifferent=\"true\" />",
                "    <ItemGroup>",
                "      <FileWrites Include=\"$(_RazorClientEnabledPkgPath)\" />",
                "      <VSIXSourceItem Include=\"$(_RazorClientEnabledPkgPath)\" />",
                "    </ItemGroup>",
                "  </Target>",
                string.Empty,
                "  <Target Name=\"IncludeMissingRazorServiceHubPayload\">",
                "    <ItemGroup>",
                "      <_MissingRazorServiceHubPayload Include=\"$(TargetDir)Microsoft.AspNetCore.Razor.Utilities.Shared.dll\" Condition=\"Exists('$(TargetDir)Microsoft.AspNetCore.Razor.Utilities.Shared.dll')\" />",
                "      <_MissingRazorServiceHubPayload Include=\"$(TargetDir)Microsoft.CodeAnalysis.Razor.Compiler.dll\" Condition=\"Exists('$(TargetDir)Microsoft.CodeAnalysis.Razor.Compiler.dll')\" />",
                "      <_MissingRazorServiceHubPayload Include=\"$(TargetDir)Microsoft.CodeAnalysis.Razor.Workspaces.dll\" Condition=\"Exists('$(TargetDir)Microsoft.CodeAnalysis.Razor.Workspaces.dll')\" />",
                "      <_MissingRazorServiceHubPayload Include=\"$(TargetDir)*\\Microsoft.AspNetCore.Razor.Utilities.Shared.resources.dll\" />",
                "      <_MissingRazorServiceHubPayload Include=\"$(TargetDir)*\\Microsoft.CodeAnalysis.Razor.Workspaces.resources.dll\" />",
                "      <VSIXSourceItem Include=\"@(_MissingRazorServiceHubPayload)\">",
                "        <VSIXSubPath Condition=\"'%(_MissingRazorServiceHubPayload.RecursiveDir)' == ''\">$(ServiceHubCoreSubPath)</VSIXSubPath>",
                "        <VSIXSubPath Condition=\"'%(_MissingRazorServiceHubPayload.RecursiveDir)' != ''\">$(ServiceHubCoreSubPath)\\%(_MissingRazorServiceHubPayload.RecursiveDir)</VSIXSubPath>",
                "      </VSIXSourceItem>",
                "    </ItemGroup>",
                "  </Target>",
                string.Empty,
                "  <Target Name=\"RemoveMergedRoslynVsixSourceItems\">",
                "    <ItemGroup>",
                "      <_MergedRoslynVsixSourceItemToRemove Include=\"@(VSIXSourceItem)\"",
                "                                            Condition=\"'%(VSIXSourceItem.Filename)%(VSIXSourceItem.Extension)' == 'EULA.rtf'",
                "                                                    Or '%(VSIXSourceItem.Filename)%(VSIXSourceItem.Extension)' == 'ThirdPartyNotices.rtf'",
                "                                                    Or '%(VSIXSourceItem.Filename)%(VSIXSourceItem.Extension)' == 'roslynSettings.registration.json'",
                "                                                    Or '%(VSIXSourceItem.Filename)%(VSIXSourceItem.Extension)' == 'Microsoft.VisualStudio.LanguageServer.Client.Implementation.dll'\" />",
                "      <VSIXSourceItem Remove=\"@(_MergedRoslynVsixSourceItemToRemove)\" />",
                "      <_MergedRoslynVsixCopyLocalToRemove Include=\"@(VSIXCopyLocalReferenceSourceItem)\"",
                "                                           Condition=\"'%(VSIXCopyLocalReferenceSourceItem.Filename)%(VSIXCopyLocalReferenceSourceItem.Extension)' == 'Microsoft.VisualStudio.LanguageServer.Client.Implementation.dll'\" />",
                "      <VSIXCopyLocalReferenceSourceItem Remove=\"@(_MergedRoslynVsixCopyLocalToRemove)\" />",
                "      <_MergedRoslynVsixLocalOnlyToRemove Include=\"@(VSIXSourceItemLocalOnly)\"",
                "                                           Condition=\"'%(VSIXSourceItemLocalOnly.Filename)%(VSIXSourceItemLocalOnly.Extension)' == 'roslynSettings.registration.json'\" />",
                "      <VSIXSourceItemLocalOnly Remove=\"@(_MergedRoslynVsixLocalOnlyToRemove)\" />",
                "      <_MergedRoslynContentToRemove Include=\"@(Content)\"",
                "                                     Condition=\"'%(Content.Filename)%(Content.Extension)' == 'EULA.rtf'",
                "                                             Or '%(Content.Filename)%(Content.Extension)' == 'ThirdPartyNotices.rtf'",
                "                                             Or '%(Content.Link)' == 'UnifiedSettings\\roslynSettings.registration.json'\" />",
                "      <Content Remove=\"@(_MergedRoslynContentToRemove)\" />",
                "    </ItemGroup>",
                "  </Target>",
            ]);

        return updatedContent.Replace(
            $"{Environment.NewLine}</Project>",
            $"{Environment.NewLine}{Environment.NewLine}{mergedRoslynCleanupBlock}{Environment.NewLine}</Project>",
            StringComparison.Ordinal);
    }

    private static async Task<string> BuildRazorBrokeredServicesPkgDefTargetsContentAsync(string sourcePath)
    {
        var sourceContent = await File.ReadAllTextAsync(sourcePath).ConfigureAwait(false);
        return sourceContent
            .Replace(
                "Name=\"_InitializeBrokeredServiceEntries\"",
                "Name=\"_InitializeRazorBrokeredServiceEntries\"",
                StringComparison.Ordinal)
            .Replace(
                "BeforeTargets=\"GenerateBrokeredServicesPkgDef\"",
                "BeforeTargets=\"GenerateRazorBrokeredServicesPkgDef\"",
                StringComparison.Ordinal)
            .Replace(
                "Name=\"GenerateBrokeredServicesPkgDef\"",
                "Name=\"GenerateRazorBrokeredServicesPkgDef\"",
                StringComparison.Ordinal)
            .Replace(
                "DependsOnTargets=\"$(GeneratePkgDefDependsOn)\"",
                string.Empty,
                StringComparison.Ordinal)
            .Replace(
                "BeforeTargets=\"GeneratePkgDef\"",
                "BeforeTargets=\"CreateVsixContainer\"",
                StringComparison.Ordinal)
            .Replace(
                "_GeneratePkgDefOutputFile",
                "_RazorBrokeredServicesPkgDefOutputFile",
                StringComparison.Ordinal);
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

    private static IEnumerable<KeyValuePair<string, JsonNode?>> EnumeratePublishDataPackageEntries(JsonObject packages)
    {
        foreach (var entry in packages)
        {
            if (entry.Value is JsonObject nestedPackages)
            {
                foreach (var nestedEntry in nestedPackages)
                    yield return nestedEntry;

                continue;
            }

            yield return entry;
        }
    }

    private static async Task SaveJsonAsync(JsonObject jsonObject, string path)
    {
        var content = jsonObject.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine;
        await File.WriteAllTextAsync(path, content).ConfigureAwait(false);
    }

    private static async Task<bool> CopyFileIfDifferentAsync(string sourcePath, string targetPath)
    {
        if (!File.Exists(sourcePath))
            return false;

        var sourceContent = await File.ReadAllTextAsync(sourcePath).ConfigureAwait(false);
        var targetContent = File.Exists(targetPath)
            ? await File.ReadAllTextAsync(targetPath).ConfigureAwait(false)
            : null;

        if (string.Equals(sourceContent, targetContent, StringComparison.Ordinal))
            return false;

        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        File.Copy(sourcePath, targetPath, overwrite: true);
        return true;
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

    private static string EnsureRazorCodeOwnersEntry(string content)
    {
        var lines = NormalizeLineEndings(content, "\n").Split('\n').ToList();

        for (var i = 0; i < lines.Count; i++)
        {
            var path = TryGetCodeOwnersPath(lines[i]);
            if (path is null)
                continue;

            if (IsRazorCodeOwnersPath(path))
            {
                lines[i] = RazorCodeOwnersEntry;
                return string.Join("\n", lines);
            }
        }

        var insertIndex = lines.Count;
        var lastSrcEntryIndex = -1;

        for (var i = 0; i < lines.Count; i++)
        {
            var path = TryGetCodeOwnersPath(lines[i]);
            if (path is null)
                continue;

            var normalizedPath = NormalizeCodeOwnersPath(path);
            if (!normalizedPath.StartsWith("src/", StringComparison.OrdinalIgnoreCase) || normalizedPath.Contains('*'))
                continue;

            lastSrcEntryIndex = i;
            if (string.Compare(normalizedPath, RazorCodeOwnersPath, StringComparison.OrdinalIgnoreCase) > 0)
            {
                insertIndex = i;
                break;
            }
        }

        if (insertIndex == lines.Count)
        {
            if (lastSrcEntryIndex >= 0)
            {
                insertIndex = lastSrcEntryIndex + 1;
            }
            else if (lines.Count > 0 && lines[^1].Length == 0)
            {
                insertIndex = lines.Count - 1;
            }
        }

        lines.Insert(insertIndex, RazorCodeOwnersEntry);
        return string.Join("\n", lines);
    }

    private static string? TryGetCodeOwnersPath(string line)
    {
        var trimmedLine = line.Trim();
        if (trimmedLine.Length == 0 || trimmedLine.StartsWith("#", StringComparison.Ordinal))
            return null;

        var separatorIndex = trimmedLine.IndexOfAny([' ', '\t']);
        return separatorIndex <= 0
            ? null
            : trimmedLine[..separatorIndex];
    }

    private static bool IsRazorCodeOwnersPath(string path)
    {
        var normalizedPath = NormalizeCodeOwnersPath(path);
        return string.Equals(normalizedPath, RazorCodeOwnersPath, StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalizedPath, RazorCodeOwnersPath.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeCodeOwnersPath(string path)
        => path.Trim().TrimStart('/');

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

    private static readonly Regex SdkRazorBuildNetstandardPathPattern = new(
        Regex.Escape("$(PkgMicrosoft_NET_Sdk_Razor)") + @"(?<separator>[\\/])build\k<separator>netstandard2\.0",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex RazorBannedMoqConstructorPattern = new(
        @"^[ \t]*M:Moq\.Mock`1\.\#ctor;.*\r?\n?",
        RegexOptions.Multiline | RegexOptions.CultureInvariant);

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
        @"^[ \t]*<ProjectReference Include=""[^""]*Razor\.Diagnostics\.Analyzers\.csproj""[\s\S]*?\s*/>\s*\r?\n?",
        RegexOptions.Multiline | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex RazorDiagnosticsAnalyzerTodoCommentPattern = new(
        @"^[ \t]*<!-- TODO: Re-enable the Razor\.Diagnostics\.Analyzers project reference once the merged Roslyn build can load it cleanly again\.[\s\S]*?-->[ \t]*\r?\n?",
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

    private static readonly Regex RazorLanguageConfigurationTestPathPattern = new(
        @"src\\Razor\\(?:src\\Razor\\)*src\\Microsoft\.VisualStudio\.RazorExtension",
        RegexOptions.CultureInvariant);

    private static readonly string[] ImportedRazorSkillNames =
    [
        "run-toolset-tests",
        "formatting-log",
    ];

    private const string RazorLanguageConfigurationMergedPath =
        @"src\Razor\src\Razor\src\Microsoft.VisualStudio.RazorExtension";
    private const string RazorCodeOwnersPath = "src/Razor/";
    private const string RazorCodeOwnersEntry = "src/Razor/ @dotnet/razor-tooling";
    private const string RazorSdkPackageVersion = "11.0.100-preview.4.26215.114";

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
            "    Razor unit test projects are renamed to Roslyn's UnitTests convention during post-merge cleanup.",
            "  -->",
            "  <PropertyGroup Condition=\"'$(IsUnitTestProject)' == '' OR '$(IsIntegrationTestProject)' == ''\">",
            "    <IsUnitTestProject>false</IsUnitTestProject>",
            "    <IsUnitTestProject Condition=\"$(MSBuildProjectName.EndsWith('.UnitTests')) OR $(MSBuildProjectName.EndsWith('.Tests'))\">true</IsUnitTestProject>",
            "    <IsIntegrationTestProject>false</IsIntegrationTestProject>",
            "    <IsIntegrationTestProject Condition=\"$(MSBuildProjectName.EndsWith('.IntegrationTests'))\">true</IsIntegrationTestProject>",
            "    <AddPublicApiAnalyzers Condition=\"'$(IsTestProject)' == 'true' OR '$(IsUnitTestProject)' == 'true' OR '$(IsIntegrationTestProject)' == 'true'\">false</AddPublicApiAnalyzers>",
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
