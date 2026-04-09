using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

return await RepoMergerApp.MainAsync(args);

static class RepoMergerApp
{
    private const string DefaultSourceRepo = "dotnet/razor";
    private const string DefaultSourceBranch = "main";
    private const string DefaultTargetRepo = @"D:\Code\roslyn";
    private const string DefaultTargetPath = @"src\Razor";
    private const string DefaultStateRoot = @"artifacts\repo-merge";
    private const string DefaultWorkRoot = @"..\repo-merge-work";
    private const string DefaultScriptRoot = @"scripts";
    private const int StateSchemaVersion = 1;
    private const string WorkflowVersion = "prepare-stage-v1";

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly MergeStageDefinition[] s_stageDefinitions =
    [
        new(
            "validate-environment",
            "Validate the target repo, input arguments, and required tooling.",
            ValidateEnvironmentAsync),
        new(
            "prepare-state",
            "Create the persisted state and external work-area layout for the run.",
            PrepareStateAsync),
        new(
            "clone-source",
            "Clone or refresh the source repository in an external work area.",
            CloneSourceAsync),
        new(
            "prepare-source",
            "Run the repo-specific prepare.cs and validate.cs scripts against the external clone.",
            PrepareSourceAsync),
        new(
            "merge-into-roslyn",
            "Placeholder stage for importing the prepared repo into the target repo.",
            MergeIntoRoslynAsync),
        new(
            "finalize-scaffold",
            "Write the current run summary and next-step commands.",
            FinalizeScaffoldAsync),
    ];

    public static async Task<int> MainAsync(string[] args)
    {
        MergeSettings settings;

        try
        {
            settings = ParseArgs(args);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ERROR: {ex.Message}");
            Console.WriteLine();
            PrintHelp();
            return 1;
        }

        if (settings.ShowHelp)
        {
            PrintHelp();
            return 0;
        }

        if (settings.ListStages)
        {
            PrintStages();
            return 0;
        }

        try
        {
            return await RunAsync(settings).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ERROR: {ex.Message}");
            return 1;
        }
    }

    private static MergeSettings ParseArgs(string[] args)
    {
        var settings = new MergeSettings(
            SourceRepo: DefaultSourceRepo,
            SourceBranch: DefaultSourceBranch,
            TargetRepo: DefaultTargetRepo,
            TargetPath: DefaultTargetPath,
            StateRoot: DefaultStateRoot,
            WorkRoot: DefaultWorkRoot,
            ScriptRoot: DefaultScriptRoot,
            ScriptSet: null,
            RunName: null,
            Stage: null,
            StartAt: null,
            StopAfter: null,
            ListStages: false,
            DryRun: false,
            Resume: false,
            Rerun: false,
            Reset: false,
            ShowHelp: false);

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];

            if (arg is "--help" or "-h" or "/?")
            {
                settings = settings with { ShowHelp = true };
                continue;
            }

            if (TryGetInlineValue(arg, out var optionName, out var inlineValue))
            {
                settings = ApplyOption(settings, optionName, inlineValue);
                continue;
            }

            settings = arg switch
            {
                "--source-repo" => settings with { SourceRepo = ReadRequiredValue(args, ref i, arg) },
                "--source-branch" => settings with { SourceBranch = ReadRequiredValue(args, ref i, arg) },
                "--target-repo" => settings with { TargetRepo = ReadRequiredValue(args, ref i, arg) },
                "--target-path" => settings with { TargetPath = ReadRequiredValue(args, ref i, arg) },
                "--state-root" => settings with { StateRoot = ReadRequiredValue(args, ref i, arg) },
                "--work-root" => settings with { WorkRoot = ReadRequiredValue(args, ref i, arg) },
                "--script-root" => settings with { ScriptRoot = ReadRequiredValue(args, ref i, arg) },
                "--script-set" => settings with { ScriptSet = ReadRequiredValue(args, ref i, arg) },
                "--run-name" => settings with { RunName = ReadRequiredValue(args, ref i, arg) },
                "--stage" => settings with { Stage = ReadRequiredValue(args, ref i, arg) },
                "--start-at" => settings with { StartAt = ReadRequiredValue(args, ref i, arg) },
                "--stop-after" => settings with { StopAfter = ReadRequiredValue(args, ref i, arg) },
                "--list-stages" => settings with { ListStages = true },
                "--dry-run" => settings with { DryRun = true },
                "--resume" => settings with { Resume = true },
                "--rerun" => settings with { Rerun = true },
                "--reset" => settings with { Reset = true },
                _ => throw new InvalidOperationException($"Unknown argument '{arg}'. Use --help to see the supported options."),
            };
        }

        return settings;
    }

    private static bool TryGetInlineValue(string arg, out string optionName, out string optionValue)
    {
        optionName = string.Empty;
        optionValue = string.Empty;

        if (!arg.StartsWith("--", StringComparison.Ordinal))
            return false;

        var separatorIndex = arg.IndexOf('=');
        if (separatorIndex < 0)
            return false;

        optionName = arg[..separatorIndex];
        optionValue = arg[(separatorIndex + 1)..];
        if (string.IsNullOrWhiteSpace(optionValue))
            throw new InvalidOperationException($"Argument '{optionName}' requires a value.");

        return true;
    }

    private static MergeSettings ApplyOption(MergeSettings settings, string optionName, string optionValue)
        => optionName switch
        {
            "--source-repo" => settings with { SourceRepo = optionValue },
            "--source-branch" => settings with { SourceBranch = optionValue },
            "--target-repo" => settings with { TargetRepo = optionValue },
            "--target-path" => settings with { TargetPath = optionValue },
            "--state-root" => settings with { StateRoot = optionValue },
            "--work-root" => settings with { WorkRoot = optionValue },
            "--script-root" => settings with { ScriptRoot = optionValue },
            "--script-set" => settings with { ScriptSet = optionValue },
            "--run-name" => settings with { RunName = optionValue },
            "--stage" => settings with { Stage = optionValue },
            "--start-at" => settings with { StartAt = optionValue },
            "--stop-after" => settings with { StopAfter = optionValue },
            _ => throw new InvalidOperationException($"Unknown argument '{optionName}'. Use --help to see the supported options."),
        };

    private static string ReadRequiredValue(string[] args, ref int index, string optionName)
    {
        if (index + 1 >= args.Length)
            throw new InvalidOperationException($"Argument '{optionName}' requires a value.");

        var value = args[++index];
        if (string.IsNullOrWhiteSpace(value) || value.StartsWith("--", StringComparison.Ordinal))
            throw new InvalidOperationException($"Argument '{optionName}' requires a value.");

        return value;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("RepoMerger");
        Console.WriteLine("==========");
        Console.WriteLine("Repeatable, resumable merge orchestration for bringing a source repo into a target repo.");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  dotnet run --project . -- [options]");
        Console.WriteLine();
        Console.WriteLine("Key options:");
        Console.WriteLine($"  --source-repo <value>     Source repository or local path. Default: {DefaultSourceRepo}");
        Console.WriteLine($"  --source-branch <value>   Source branch to use. Default: {DefaultSourceBranch}");
        Console.WriteLine($"  --target-repo <path>      Target repository root. Default: {DefaultTargetRepo}");
        Console.WriteLine($"  --target-path <path>      Destination path inside the target repo. Default: {DefaultTargetPath}");
        Console.WriteLine($"  --state-root <path>       Where run state is persisted. Default: {DefaultStateRoot}");
        Console.WriteLine($"  --work-root <path>        Where external working repos live. Default: {DefaultWorkRoot}");
        Console.WriteLine($"  --script-root <path>      Where repo-specific scripts live. Default: {DefaultScriptRoot}");
        Console.WriteLine("  --script-set <name>       Which repo-specific script folder to use.");
        Console.WriteLine("  --run-name <name>         Stable run name for resume/rerun.");
        Console.WriteLine("  --stage <name>            Run a single named stage.");
        Console.WriteLine("  --start-at <name>         Start execution at a specific stage.");
        Console.WriteLine("  --stop-after <name>       Stop after a specific stage.");
        Console.WriteLine("  --resume                  Resume a previous run from state.json.");
        Console.WriteLine("  --rerun                   Re-execute completed stages.");
        Console.WriteLine("  --reset                   Delete previous run state and start fresh.");
        Console.WriteLine("  --dry-run                 Skip side-effecting stage work.");
        Console.WriteLine("  --list-stages             List the available stage names and exit.");
        Console.WriteLine("  --help                    Show this help text.");
    }

    private static async Task<int> RunAsync(MergeSettings settings)
    {
        var toolRoot = GetToolRoot();
        var runName = string.IsNullOrWhiteSpace(settings.RunName)
            ? GetDefaultRunName(settings.SourceRepo, settings.TargetPath)
            : SanitizePathSegment(settings.RunName);
        var targetRepoRoot = GetAbsolutePath(toolRoot, settings.TargetRepo);
        var stateRoot = GetAbsolutePath(toolRoot, settings.StateRoot);
        var workRoot = GetAbsolutePath(toolRoot, settings.WorkRoot);
        EnsurePathIsOutsideRepo(targetRepoRoot, workRoot, "--work-root");
        var scriptRoot = GetAbsolutePath(toolRoot, settings.ScriptRoot);
        var scriptSet = GetScriptSetName(settings);
        var scriptDirectory = Path.Combine(scriptRoot, scriptSet);
        var runDirectory = Path.Combine(stateRoot, runName);
        var workDirectory = Path.Combine(workRoot, runName);

        if (settings.Reset && Directory.Exists(runDirectory))
            Directory.Delete(runDirectory, recursive: true);
        if (settings.Reset && Directory.Exists(workDirectory))
            Directory.Delete(workDirectory, recursive: true);

        Directory.CreateDirectory(runDirectory);
        Directory.CreateDirectory(workDirectory);

        Console.WriteLine($"Starting repo-merge run '{runName}' (workflow version {WorkflowVersion}).");

        var executionPlan = CreateExecutionPlan(settings);
        var statePath = Path.Combine(runDirectory, "state.json");
        var stateExists = File.Exists(statePath);

        if (stateExists && !settings.Resume && !settings.Rerun)
        {
            throw new InvalidOperationException(
                $"State already exists for run '{runName}' at '{runDirectory}'. " +
                "Use --resume to continue, --rerun to execute completed stages again, or --reset to start over.");
        }

        var state = stateExists
            ? await LoadStateAsync(statePath).ConfigureAwait(false)
            : CreateState(settings, targetRepoRoot, runName, runDirectory, executionPlan);

        EnsureCompatibleState(state, settings, targetRepoRoot);
        SyncStageMetadata(state);
        RecoverCompletedStagesFromSentinels(state, runDirectory);

        state.SourceRepo = settings.SourceRepo;
        state.SourceBranch = settings.SourceBranch;
        state.TargetRepoRoot = targetRepoRoot;
        state.TargetPath = settings.TargetPath;
        state.StateRoot = stateRoot;
        state.WorkRoot = workRoot;
        state.ScriptRoot = scriptRoot;
        state.ScriptSet = scriptSet;
        state.ScriptDirectory = scriptDirectory;
        state.RunName = runName;
        state.RunDirectory = runDirectory;
        state.WorkDirectory = workDirectory;
        state.SourceRemoteUri = ResolveSourceRepositoryUri(settings.SourceRepo, toolRoot);
        state.SourceCloneDirectory = Path.Combine(workDirectory, "source");
        state.ImportPreviewDirectory = Path.Combine(workDirectory, "import-preview");
        state.WorkflowVersion = WorkflowVersion;
        state.DryRun = settings.DryRun;
        state.SelectedStartStage = executionPlan.StartStageName;
        state.SelectedStopStage = executionPlan.StopStageName;
        state.UpdatedUtc = DateTimeOffset.UtcNow;

        await SaveStateAsync(statePath, state).ConfigureAwait(false);

        Console.WriteLine($"State file: {statePath}");
        Console.WriteLine($"Selected stages: {executionPlan.StartStageName} -> {executionPlan.StopStageName}");
        if (settings.DryRun)
            Console.WriteLine("Running in dry-run mode.");

        var context = new StageContext(
            Settings: settings,
            ToolRoot: toolRoot,
            TargetRepoRoot: targetRepoRoot,
            RunDirectory: runDirectory,
            StatePath: statePath,
            State: state);

        for (var i = executionPlan.StartIndex; i <= executionPlan.StopIndex; i++)
        {
            var definition = s_stageDefinitions[i];
            var record = GetStageState(state, definition.Name);

            if (record.Status == StageStatus.Completed && !settings.Rerun)
            {
                Console.WriteLine($"Skipping completed stage '{definition.Name}'.");
                continue;
            }

            record.Status = StageStatus.InProgress;
            record.AttemptCount++;
            record.StartedUtc = DateTimeOffset.UtcNow;
            record.LastMessage = null;
            state.CurrentStage = definition.Name;
            state.UpdatedUtc = DateTimeOffset.UtcNow;
            await SaveStateAsync(statePath, state).ConfigureAwait(false);

            try
            {
                Console.WriteLine($"Starting stage '{definition.Name}': {definition.Description}");
                var summary = await definition.ExecuteAsync(context).ConfigureAwait(false);

                record.Status = StageStatus.Completed;
                record.CompletedUtc = DateTimeOffset.UtcNow;
                record.LastMessage = summary;
                state.CurrentStage = string.Empty;
                state.LastCompletedStage = definition.Name;
                state.UpdatedUtc = DateTimeOffset.UtcNow;

                await WriteSentinelAsync(runDirectory, definition, record).ConfigureAwait(false);
                await SaveStateAsync(statePath, state).ConfigureAwait(false);

                Console.WriteLine($"Completed stage '{definition.Name}'.");
            }
            catch (Exception ex)
            {
                record.Status = StageStatus.Failed;
                record.LastMessage = ex.Message;
                state.CurrentStage = definition.Name;
                state.UpdatedUtc = DateTimeOffset.UtcNow;
                await SaveStateAsync(statePath, state).ConfigureAwait(false);

                Console.WriteLine($"Stage '{definition.Name}' failed: {ex.Message}");
                return 1;
            }
        }

        Console.WriteLine("Repo-merge run completed successfully.");
        return 0;
    }

    private static ExecutionPlan CreateExecutionPlan(MergeSettings settings)
    {
        var startStageName = settings.Stage ?? settings.StartAt ?? s_stageDefinitions[0].Name;
        var stopStageName = settings.Stage ?? settings.StopAfter ?? s_stageDefinitions[^1].Name;
        var startIndex = GetStageIndex(startStageName);
        var stopIndex = GetStageIndex(stopStageName);

        if (startIndex > stopIndex)
        {
            throw new InvalidOperationException(
                $"The selected start stage '{startStageName}' comes after the stop stage '{stopStageName}'.");
        }

        return new ExecutionPlan(startIndex, stopIndex, s_stageDefinitions[startIndex].Name, s_stageDefinitions[stopIndex].Name);
    }

    private static void PrintStages()
    {
        Console.WriteLine("Available repo-merge stages:");
        foreach (var stage in s_stageDefinitions)
            Console.WriteLine($"  {stage.Name,-20} {stage.Description}");
    }

    private static int GetStageIndex(string stageName)
    {
        var normalizedName = NormalizeStageName(stageName);
        for (var i = 0; i < s_stageDefinitions.Length; i++)
        {
            if (NormalizeStageName(s_stageDefinitions[i].Name) == normalizedName)
                return i;
        }

        throw new InvalidOperationException(
            $"Unknown stage '{stageName}'. Use --list-stages to view the supported stage names.");
    }

    private static string NormalizeStageName(string stageName)
        => stageName.Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .Trim()
            .ToUpperInvariant();

    private static MergeRunState CreateState(MergeSettings settings, string targetRepoRoot, string runName, string runDirectory, ExecutionPlan executionPlan)
        => new()
        {
            SchemaVersion = StateSchemaVersion,
            WorkflowVersion = WorkflowVersion,
            RunName = runName,
            SourceRepo = settings.SourceRepo,
            SourceBranch = settings.SourceBranch,
            TargetRepoRoot = targetRepoRoot,
            TargetPath = settings.TargetPath,
            StateRoot = string.Empty,
            WorkRoot = string.Empty,
            ScriptRoot = string.Empty,
            ScriptSet = string.Empty,
            ScriptDirectory = string.Empty,
            RunDirectory = runDirectory,
            WorkDirectory = string.Empty,
            SourceRemoteUri = string.Empty,
            SourceCloneDirectory = string.Empty,
            ImportPreviewDirectory = string.Empty,
            DryRun = settings.DryRun,
            SelectedStartStage = executionPlan.StartStageName,
            SelectedStopStage = executionPlan.StopStageName,
            CreatedUtc = DateTimeOffset.UtcNow,
            UpdatedUtc = DateTimeOffset.UtcNow,
            Stages = [.. s_stageDefinitions.Select(static stage => new StageState { Name = stage.Name, Description = stage.Description, Status = StageStatus.Pending })],
        };

    private static void SyncStageMetadata(MergeRunState state)
    {
        foreach (var definition in s_stageDefinitions)
        {
            var stage = GetStageState(state, definition.Name);
            stage.Description = definition.Description;
        }
    }

    private static void RecoverCompletedStagesFromSentinels(MergeRunState state, string runDirectory)
    {
        var sentinelsDirectory = Path.Combine(runDirectory, "sentinels");
        if (!Directory.Exists(sentinelsDirectory))
            return;

        foreach (var definition in s_stageDefinitions)
        {
            var sentinelPath = Path.Combine(sentinelsDirectory, $"{definition.Name}.done");
            if (!File.Exists(sentinelPath))
                continue;

            var stage = GetStageState(state, definition.Name);
            if (stage.Status == StageStatus.Completed)
                continue;

            stage.Status = StageStatus.Completed;
            stage.CompletedUtc ??= File.GetLastWriteTimeUtc(sentinelPath);
            stage.LastMessage ??= "Recovered completion from sentinel file.";
            state.LastCompletedStage = definition.Name;
        }
    }

    private static void EnsureCompatibleState(MergeRunState state, MergeSettings settings, string targetRepoRoot)
    {
        if (state.SchemaVersion != StateSchemaVersion || !string.Equals(state.WorkflowVersion, WorkflowVersion, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Existing state was created for workflow version '{state.WorkflowVersion}' (schema {state.SchemaVersion}). " +
                "Use --reset or choose a new --run-name.");
        }

        ValidateMatchingSetting(state.SourceRepo, settings.SourceRepo, nameof(settings.SourceRepo));
        ValidateMatchingSetting(state.SourceBranch, settings.SourceBranch, nameof(settings.SourceBranch));
        ValidateMatchingSetting(state.TargetRepoRoot, targetRepoRoot, nameof(settings.TargetRepo));
        ValidateMatchingSetting(state.TargetPath, settings.TargetPath, nameof(settings.TargetPath));
    }

    private static void ValidateMatchingSetting(string existingValue, string currentValue, string name)
    {
        if (!string.IsNullOrWhiteSpace(existingValue) && !string.Equals(existingValue, currentValue, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Existing state was created with a different {name} value ('{existingValue}' vs '{currentValue}'). " +
                "Use --reset or a different --run-name.");
        }
    }

    private static StageState GetStageState(MergeRunState state, string stageName)
    {
        foreach (var stage in state.Stages)
        {
            if (string.Equals(stage.Name, stageName, StringComparison.OrdinalIgnoreCase))
                return stage;
        }

        var newStage = new StageState
        {
            Name = stageName,
            Status = StageStatus.Pending,
        };

        state.Stages.Add(newStage);
        return newStage;
    }

    private static async Task WriteSentinelAsync(string runDirectory, MergeStageDefinition definition, StageState record)
    {
        var sentinelsDirectory = Path.Combine(runDirectory, "sentinels");
        Directory.CreateDirectory(sentinelsDirectory);

        var content = $"""
            Stage: {definition.Name}
            Description: {definition.Description}
            CompletedUtc: {record.CompletedUtc:O}
            Attempts: {record.AttemptCount}
            Summary: {record.LastMessage}
            """;

        await File.WriteAllTextAsync(Path.Combine(sentinelsDirectory, $"{definition.Name}.done"), content).ConfigureAwait(false);
    }

    private static async Task<MergeRunState> LoadStateAsync(string statePath)
    {
        await using var stream = File.OpenRead(statePath);
        return await JsonSerializer.DeserializeAsync<MergeRunState>(stream, s_jsonOptions).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Could not deserialize '{statePath}'.");
    }

    private static async Task SaveStateAsync(string statePath, MergeRunState state)
    {
        await using var stream = File.Create(statePath);
        await JsonSerializer.SerializeAsync(stream, state, s_jsonOptions).ConfigureAwait(false);
    }

    private static async Task<string> ValidateEnvironmentAsync(StageContext context)
    {
        if (!Directory.Exists(Path.Combine(context.TargetRepoRoot, ".git")) && !File.Exists(Path.Combine(context.TargetRepoRoot, ".git")))
            throw new InvalidOperationException($"The target repo '{context.TargetRepoRoot}' does not appear to be a git checkout.");

        if (string.IsNullOrWhiteSpace(context.Settings.SourceRepo))
            throw new InvalidOperationException("--source-repo must not be empty.");

        if (string.IsNullOrWhiteSpace(context.Settings.TargetPath))
            throw new InvalidOperationException("--target-path must not be empty.");

        if (string.IsNullOrWhiteSpace(context.Settings.TargetRepo))
            throw new InvalidOperationException("--target-repo must not be empty.");

        var fullTargetPath = GetAbsolutePath(context.TargetRepoRoot, context.Settings.TargetPath);
        var fullWorkRoot = GetAbsolutePath(context.ToolRoot, context.Settings.WorkRoot);
        if (!IsPathWithinRoot(context.TargetRepoRoot, fullTargetPath))
            throw new InvalidOperationException($"The target path '{context.Settings.TargetPath}' escapes the target repo root.");

        if (Path.IsPathRooted(context.Settings.TargetPath))
            throw new InvalidOperationException("--target-path must be relative to the target repo root.");

        EnsurePathIsOutsideRepo(context.TargetRepoRoot, fullWorkRoot, "--work-root");

        if (LooksLikeLocalPath(context.Settings.SourceRepo))
        {
            var fullSourcePath = GetAbsolutePath(context.ToolRoot, context.Settings.SourceRepo);
            if (!Directory.Exists(fullSourcePath))
                throw new InvalidOperationException($"The local source repo path '{fullSourcePath}' does not exist.");
        }
        else if (context.Settings.SourceRepo.Count(static c => c == '/') != 1)
        {
            throw new InvalidOperationException("--source-repo must be in owner/repo format or a valid local path.");
        }

        var gitVersion = await RunProcessAsync("git", ["--version"], context.TargetRepoRoot).ConfigureAwait(false);
        if (gitVersion.ExitCode != 0)
            throw new InvalidOperationException($"`git --version` failed.");

        var status = await RunProcessAsync("git", ["status", "--short", "--untracked-files=no"], context.TargetRepoRoot).ConfigureAwait(false);
        Console.WriteLine($"Resolved target repo: {context.TargetRepoRoot}");
        Console.WriteLine($"Resolved target path: {fullTargetPath}");
        Console.WriteLine($"Resolved external work root: {fullWorkRoot}");
        Console.WriteLine($"Using script directory: {context.State.ScriptDirectory}");
        if (!string.IsNullOrWhiteSpace(status.Output))
            Console.WriteLine("Target repo has existing changes; the repo-merger run will not modify it yet.");

        return $"Validated target repo and inputs. Git: {gitVersion.Output.Trim()}";
    }

    private static async Task<string> PrepareStateAsync(StageContext context)
    {
        Directory.CreateDirectory(Path.Combine(context.RunDirectory, "sentinels"));
        Directory.CreateDirectory(context.State.WorkDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(context.State.SourceCloneDirectory)!);

        var manifest = new
        {
            workflowVersion = WorkflowVersion,
            stateSchemaVersion = StateSchemaVersion,
            context.Settings.SourceRepo,
            context.Settings.SourceBranch,
            context.Settings.TargetRepo,
            context.Settings.TargetPath,
            context.State.WorkRoot,
            context.State.WorkDirectory,
            context.State.SourceCloneDirectory,
            context.State.ScriptRoot,
            context.State.ScriptSet,
            context.State.ScriptDirectory,
            context.Settings.DryRun,
            selectedStages = new
            {
                start = context.State.SelectedStartStage,
                stop = context.State.SelectedStopStage,
            },
        };

        var manifestPath = Path.Combine(context.RunDirectory, "inputs.json");
        await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest, s_jsonOptions)).ConfigureAwait(false);

        return $"Prepared persisted state under '{context.RunDirectory}' and external work area '{context.State.WorkDirectory}'.";
    }

    private static async Task<string> CloneSourceAsync(StageContext context)
    {
        var sourceDirectory = context.State.SourceCloneDirectory;
        var sourceRemoteUri = context.State.SourceRemoteUri;
        Directory.CreateDirectory(Path.GetDirectoryName(sourceDirectory)!);

        if (context.Settings.DryRun)
            return $"Dry run: would clone or refresh '{context.Settings.SourceRepo}' from '{sourceRemoteUri}' into '{sourceDirectory}'.";

        if (!Directory.Exists(sourceDirectory))
        {
            var cloneArguments = new List<string>
            {
                "clone",
                "--origin", "source",
                "--branch", context.Settings.SourceBranch,
            };

            if (LooksLikeLocalPath(context.Settings.SourceRepo))
                cloneArguments.Add("--no-hardlinks");

            cloneArguments.Add(sourceRemoteUri);
            cloneArguments.Add(sourceDirectory);

            Console.WriteLine($"Cloning '{sourceRemoteUri}' into '{sourceDirectory}'.");
            var cloneResult = await RunProcessAsync("git", cloneArguments, context.State.WorkDirectory).ConfigureAwait(false);
            EnsureCommandSucceeded(cloneResult, "git clone");
        }
        else
        {
            if (!IsGitRepository(sourceDirectory))
                throw new InvalidOperationException($"The clone directory '{sourceDirectory}' already exists but is not a git repository.");

            var remoteName = await GetPreferredRemoteNameAsync(sourceDirectory).ConfigureAwait(false);
            var remoteUrlResult = await RunProcessAsync("git", ["remote", "get-url", remoteName], sourceDirectory).ConfigureAwait(false);
            EnsureCommandSucceeded(remoteUrlResult, "git remote get-url");
            var actualRemoteUri = remoteUrlResult.Output.Trim();
            if (!RepositoryLocationsMatch(actualRemoteUri, sourceRemoteUri))
            {
                throw new InvalidOperationException(
                    $"The existing clone at '{sourceDirectory}' points at '{actualRemoteUri}', not '{sourceRemoteUri}'. " +
                    "Use --reset or a different --run-name to create a fresh working copy.");
            }

            Console.WriteLine($"Refreshing existing clone in '{sourceDirectory}' from remote '{remoteName}'.");

            var fetchResult = await RunProcessAsync("git", ["fetch", remoteName, "--prune", "--tags"], sourceDirectory).ConfigureAwait(false);
            EnsureCommandSucceeded(fetchResult, "git fetch");

            var checkoutResult = await RunProcessAsync(
                "git",
                ["checkout", "-B", context.Settings.SourceBranch, $"{remoteName}/{context.Settings.SourceBranch}"],
                sourceDirectory).ConfigureAwait(false);
            EnsureCommandSucceeded(checkoutResult, "git checkout");
        }

        var headCommitResult = await RunProcessAsync("git", ["rev-parse", "HEAD"], sourceDirectory).ConfigureAwait(false);
        EnsureCommandSucceeded(headCommitResult, "git rev-parse");
        context.State.SourceHeadCommit = headCommitResult.Output.Trim();

        return $"Cloned/refreshed '{context.Settings.SourceRepo}' into '{sourceDirectory}' at commit '{context.State.SourceHeadCommit}'.";
    }

    private static async Task<string> PrepareSourceAsync(StageContext context)
    {
        var scriptDirectory = context.State.ScriptDirectory;
        if (!Directory.Exists(scriptDirectory))
        {
            throw new InvalidOperationException(
                $"The script directory '{scriptDirectory}' does not exist. " +
                "Add repo-specific scripts like prepare.cs and validate.cs or override it with --script-set/--script-root.");
        }

        if (!Directory.Exists(context.State.SourceCloneDirectory))
        {
            throw new InvalidOperationException(
                $"The source clone directory '{context.State.SourceCloneDirectory}' does not exist. " +
                "Run the clone-source stage first.");
        }

        var summaries = new List<string>();
        summaries.AddRange(await RunRepoScriptIfPresentAsync(context, "prepare.cs").ConfigureAwait(false));
        summaries.AddRange(await RunRepoScriptIfPresentAsync(context, "validate.cs").ConfigureAwait(false));

        if (summaries.Count == 0)
        {
            throw new InvalidOperationException(
                $"No prepare.cs or validate.cs script was found in '{scriptDirectory}'. " +
                "Add at least one repo-specific script for this stage.");
        }

        return string.Join(" ", summaries);
    }

    private static Task<string> MergeIntoRoslynAsync(StageContext context)
    {
        var importDirectory = context.State.ImportPreviewDirectory;
        Directory.CreateDirectory(importDirectory);

        var fullTargetPath = GetAbsolutePath(context.TargetRepoRoot, context.Settings.TargetPath);
        return Task.FromResult(
            $"Placeholder only. A future milestone will import the prepared repo into '{fullTargetPath}' under target repo '{context.TargetRepoRoot}'.");
    }

    private static async Task<string> FinalizeScaffoldAsync(StageContext context)
    {
        var summary = new StringBuilder();
        summary.AppendLine("RepoMerger run summary");
        summary.AppendLine("=====================");
        summary.AppendLine($"Workflow version : {WorkflowVersion}");
        summary.AppendLine($"Run name         : {context.State.RunName}");
        summary.AppendLine($"Source repo      : {context.Settings.SourceRepo}");
        summary.AppendLine($"Source branch    : {context.Settings.SourceBranch}");
        summary.AppendLine($"Target repo      : {context.TargetRepoRoot}");
        summary.AppendLine($"Target path      : {context.Settings.TargetPath}");
        summary.AppendLine($"Work root        : {context.State.WorkRoot}");
        summary.AppendLine($"Clone directory  : {context.State.SourceCloneDirectory}");
        summary.AppendLine($"Script directory : {context.State.ScriptDirectory}");
        summary.AppendLine($"Source HEAD      : {context.State.SourceHeadCommit}");
        summary.AppendLine($"Dry run          : {context.Settings.DryRun}");
        summary.AppendLine($"State file       : {context.StatePath}");
        summary.AppendLine();
        summary.AppendLine("Available follow-up commands:");
        summary.AppendLine($@"  dotnet run --project . -- --run-name {context.State.RunName} --resume");
        summary.AppendLine($@"  dotnet run --project . -- --run-name {context.State.RunName} --stage clone-source --rerun");
        summary.AppendLine($@"  dotnet run --project . -- --run-name {context.State.RunName} --stage prepare-source --rerun");
        summary.AppendLine();
        summary.AppendLine("Current status: the external clone and repo-specific prepare/validate stages are implemented.");

        var summaryPath = Path.Combine(context.RunDirectory, "summary.txt");
        await File.WriteAllTextAsync(summaryPath, summary.ToString()).ConfigureAwait(false);

        return $"Wrote run summary to '{summaryPath}'.";
    }

    private static string GetToolRoot()
    {
        foreach (var startPath in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var directory = Path.GetFullPath(startPath);
            while (!string.IsNullOrEmpty(directory))
            {
                if (File.Exists(Path.Combine(directory, "RepoMerger.csproj")))
                    return directory;

                directory = Path.GetDirectoryName(directory) ?? string.Empty;
            }
        }

        foreach (var startPath in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var directory = Path.GetFullPath(startPath);
            while (!string.IsNullOrEmpty(directory))
            {
                if (Directory.Exists(Path.Combine(directory, ".git"))
                    && Directory.Exists(Path.Combine(directory, "scripts")))
                {
                    return directory;
                }

                directory = Path.GetDirectoryName(directory) ?? string.Empty;
            }
        }

        throw new InvalidOperationException("Could not locate the RepoMerger repository root.");
    }

    private static string GetDefaultRunName(string sourceRepo, string targetPath)
        => SanitizePathSegment($"{sourceRepo}-to-{targetPath}");

    private static string GetScriptSetName(MergeSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.ScriptSet))
            return SanitizePathSegment(settings.ScriptSet);

        var repoName = Path.GetFileName(settings.SourceRepo.Replace('/', Path.DirectorySeparatorChar));
        return SanitizePathSegment(repoName);
    }

    private static string SanitizePathSegment(string value)
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

    private static string GetAbsolutePath(string rootPath, string path)
        => Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(rootPath, path));

    private static void EnsurePathIsOutsideRepo(string repoRoot, string candidatePath, string optionName)
    {
        if (IsPathWithinRoot(repoRoot, candidatePath))
        {
            throw new InvalidOperationException(
                $"{optionName} resolved to '{candidatePath}', which is inside the target repo. " +
                "Choose a path outside the checkout so folder structure and repo-local configuration cannot interfere.");
        }
    }

    private static bool IsPathWithinRoot(string rootPath, string candidatePath)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath)) + Path.DirectorySeparatorChar;
        var normalizedCandidate = Path.GetFullPath(candidatePath);
        return normalizedCandidate.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase)
            || string.Equals(
                Path.TrimEndingDirectorySeparator(normalizedCandidate),
                Path.TrimEndingDirectorySeparator(rootPath),
                StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeLocalPath(string value)
        => value.Contains('\\')
            || value.Contains(':')
            || value.StartsWith(".", StringComparison.Ordinal);

    private static async Task<IReadOnlyList<string>> RunRepoScriptIfPresentAsync(StageContext context, string scriptFileName)
    {
        var scriptPath = Path.Combine(context.State.ScriptDirectory, scriptFileName);
        if (!File.Exists(scriptPath))
            return [];

        Console.WriteLine($"Running repo-specific script '{scriptPath}' against '{context.State.SourceCloneDirectory}'.");
        var result = await RunProcessAsync(
            "dotnet",
            ["run", "--file", scriptPath, "--", context.State.SourceCloneDirectory],
            context.State.SourceCloneDirectory).ConfigureAwait(false);
        EnsureCommandSucceeded(result, $"dotnet run --file {scriptPath}");

        return [$"Ran {scriptFileName} successfully."];
    }

    private static string ResolveSourceRepositoryUri(string sourceRepo, string toolRoot)
        => LooksLikeLocalPath(sourceRepo)
            ? GetAbsolutePath(toolRoot, sourceRepo)
            : $"https://github.com/{sourceRepo}.git";

    private static bool IsGitRepository(string directory)
        => Directory.Exists(Path.Combine(directory, ".git")) || File.Exists(Path.Combine(directory, ".git"));

    private static bool RepositoryLocationsMatch(string left, string right)
        => string.Equals(NormalizeRepositoryLocation(left), NormalizeRepositoryLocation(right), StringComparison.OrdinalIgnoreCase);

    private static string NormalizeRepositoryLocation(string value)
    {
        value = value.Trim().Replace('\\', '/');

        if (value.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            value = value[..^4];

        return value.TrimEnd('/');
    }

    private static async Task<string> GetPreferredRemoteNameAsync(string repositoryDirectory)
    {
        var remoteList = await RunProcessAsync("git", ["remote"], repositoryDirectory).ConfigureAwait(false);
        EnsureCommandSucceeded(remoteList, "git remote");

        var remotes = remoteList.Output
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (remotes.Contains("source"))
            return "source";

        if (remotes.Contains("origin"))
            return "origin";

        throw new InvalidOperationException(
            $"The repository at '{repositoryDirectory}' does not have a 'source' or 'origin' remote to refresh.");
    }

    private static void EnsureCommandSucceeded(ProcessResult result, string commandName)
    {
        if (result.ExitCode == 0)
            return;

        throw new InvalidOperationException(
            $"{commandName} failed with exit code {result.ExitCode}:{Environment.NewLine}{result.Output.Trim()}");
    }

    private static async Task<ProcessResult> RunProcessAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start '{fileName}'.");

        var output = await ReadProcessOutputAsync(process).ConfigureAwait(false);
        return new ProcessResult(process.ExitCode, output);
    }

    private static async Task<string> ReadProcessOutputAsync(Process process)
    {
        var output = new List<string>();

        Task PumpAsync(StreamReader reader) => Task.Run(async () =>
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                lock (output)
                    output.Add(line);

                Console.WriteLine(line);
            }
        });

        await Task.WhenAll(
            PumpAsync(process.StandardOutput),
            PumpAsync(process.StandardError),
            process.WaitForExitAsync()).ConfigureAwait(false);

        lock (output)
            return string.Join(Environment.NewLine, output);
    }
}

readonly record struct MergeSettings(
    string SourceRepo,
    string SourceBranch,
    string TargetRepo,
    string TargetPath,
    string StateRoot,
    string WorkRoot,
    string ScriptRoot,
    string? ScriptSet,
    string? RunName,
    string? Stage,
    string? StartAt,
    string? StopAfter,
    bool ListStages,
    bool DryRun,
    bool Resume,
    bool Rerun,
    bool Reset,
    bool ShowHelp);

readonly record struct ExecutionPlan(int StartIndex, int StopIndex, string StartStageName, string StopStageName);

readonly record struct MergeStageDefinition(string Name, string Description, Func<StageContext, Task<string>> ExecuteAsync);

readonly record struct StageContext(
    MergeSettings Settings,
    string ToolRoot,
    string TargetRepoRoot,
    string RunDirectory,
    string StatePath,
    MergeRunState State);

readonly record struct ProcessResult(int ExitCode, string Output);

sealed class MergeRunState
{
    public int SchemaVersion { get; set; }
    public string WorkflowVersion { get; set; } = string.Empty;
    public string RunName { get; set; } = string.Empty;
    public string SourceRepo { get; set; } = string.Empty;
    public string SourceBranch { get; set; } = string.Empty;
    public string TargetRepoRoot { get; set; } = string.Empty;
    public string TargetPath { get; set; } = string.Empty;
    public string StateRoot { get; set; } = string.Empty;
    public string WorkRoot { get; set; } = string.Empty;
    public string ScriptRoot { get; set; } = string.Empty;
    public string ScriptSet { get; set; } = string.Empty;
    public string ScriptDirectory { get; set; } = string.Empty;
    public string RunDirectory { get; set; } = string.Empty;
    public string WorkDirectory { get; set; } = string.Empty;
    public string SourceRemoteUri { get; set; } = string.Empty;
    public string SourceCloneDirectory { get; set; } = string.Empty;
    public string ImportPreviewDirectory { get; set; } = string.Empty;
    public string SourceHeadCommit { get; set; } = string.Empty;
    public bool DryRun { get; set; }
    public string CurrentStage { get; set; } = string.Empty;
    public string LastCompletedStage { get; set; } = string.Empty;
    public string SelectedStartStage { get; set; } = string.Empty;
    public string SelectedStopStage { get; set; } = string.Empty;
    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset UpdatedUtc { get; set; }
    public List<StageState> Stages { get; set; } = [];
}

sealed class StageState
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public StageStatus Status { get; set; } = StageStatus.Pending;
    public int AttemptCount { get; set; }
    public DateTimeOffset? StartedUtc { get; set; }
    public DateTimeOffset? CompletedUtc { get; set; }
    public string? LastMessage { get; set; }
}

enum StageStatus
{
    Pending,
    InProgress,
    Completed,
    Failed,
}
