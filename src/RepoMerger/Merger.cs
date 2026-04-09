namespace RepoMerger;

internal static class Merger
{
    public static async Task<int> RunAsync(Settings settings)
    {
        var toolRoot = PathHelper.GetToolRoot();
        var runName = string.IsNullOrWhiteSpace(settings.RunName)
            ? PathHelper.GetDefaultRunName(settings.SourceRepo, settings.TargetRepo, settings.TargetPath)
            : PathHelper.SanitizePathSegment(settings.RunName);
        var stateRoot = PathHelper.GetAbsolutePath(toolRoot, settings.StateRoot);
        var workRoot = PathHelper.GetAbsolutePath(toolRoot, settings.WorkRoot);
        PathHelper.EnsurePathIsOutsideRepo(toolRoot, workRoot, "--work-root");
        var runDirectory = Path.Combine(stateRoot, runName);
        var workDirectory = Path.Combine(workRoot, runName);
        var targetRepoRoot = Path.Combine(workDirectory, "target");

        if (settings.Reset && Directory.Exists(runDirectory))
            Directory.Delete(runDirectory, recursive: true);
        if (settings.Reset && Directory.Exists(workDirectory))
            Directory.Delete(workDirectory, recursive: true);

        Directory.CreateDirectory(runDirectory);
        Directory.CreateDirectory(workDirectory);

        Console.WriteLine($"Starting repo-merge run '{runName}' (workflow version {Constants.WorkflowVersion}).");

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
            ? await RunStateStore.LoadAsync(statePath).ConfigureAwait(false)
            : CreateState(settings, targetRepoRoot, runName, runDirectory, executionPlan);

        EnsureCompatibleState(state, settings);
        SyncStageMetadata(state);
        RecoverCompletedStagesFromSentinels(state, runDirectory);

        state.SourceRepo = settings.SourceRepo;
        state.SourceBranch = settings.SourceBranch;
        state.TargetRepo = settings.TargetRepo;
        state.TargetRepoRoot = targetRepoRoot;
        state.TargetPath = settings.TargetPath;
        state.StateRoot = stateRoot;
        state.WorkRoot = workRoot;
        state.RunName = runName;
        state.RunDirectory = runDirectory;
        state.WorkDirectory = workDirectory;
        state.SourceRemoteUri = PathHelper.ResolveRepositoryUri(settings.SourceRepo, toolRoot);
        state.TargetRemoteUri = PathHelper.ResolveRepositoryUri(settings.TargetRepo, toolRoot);
        state.SourceCloneDirectory = Path.Combine(workDirectory, "source");
        state.ImportPreviewDirectory = Path.Combine(workDirectory, "import-preview");
        state.WorkflowVersion = Constants.WorkflowVersion;
        state.DryRun = settings.DryRun;
        state.SelectedStartStage = executionPlan.StartStageName;
        state.SelectedStopStage = executionPlan.StopStageName;
        state.UpdatedUtc = DateTimeOffset.UtcNow;

        await RunStateStore.SaveAsync(statePath, state).ConfigureAwait(false);

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
            var definition = Stages.Definitions[i];
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
            await RunStateStore.SaveAsync(statePath, state).ConfigureAwait(false);

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

                await RunStateStore.WriteSentinelAsync(runDirectory, definition, record).ConfigureAwait(false);
                await RunStateStore.SaveAsync(statePath, state).ConfigureAwait(false);

                Console.WriteLine($"Completed stage '{definition.Name}'.");
            }
            catch (Exception ex)
            {
                record.Status = StageStatus.Failed;
                record.LastMessage = ex.Message;
                state.CurrentStage = definition.Name;
                state.UpdatedUtc = DateTimeOffset.UtcNow;
                await RunStateStore.SaveAsync(statePath, state).ConfigureAwait(false);

                Console.WriteLine($"Stage '{definition.Name}' failed: {ex.Message}");
                return 1;
            }
        }

        Console.WriteLine("Repo-merge run completed successfully.");
        return 0;
    }

    private static ExecutionPlan CreateExecutionPlan(Settings settings)
    {
        var stageDefinitions = Stages.Definitions;
        var startStageName = settings.Stage ?? settings.StartAt ?? stageDefinitions[0].Name;
        var stopStageName = settings.Stage ?? settings.StopAfter ?? stageDefinitions[^1].Name;
        var startIndex = GetStageIndex(stageDefinitions, startStageName);
        var stopIndex = GetStageIndex(stageDefinitions, stopStageName);

        if (startIndex > stopIndex)
        {
            throw new InvalidOperationException(
                $"The selected start stage '{startStageName}' comes after the stop stage '{stopStageName}'.");
        }

        return new ExecutionPlan(startIndex, stopIndex, stageDefinitions[startIndex].Name, stageDefinitions[stopIndex].Name);
    }

    private static RunState CreateState(Settings settings, string targetRepoRoot, string runName, string runDirectory, ExecutionPlan executionPlan)
        => new()
        {
            SchemaVersion = Constants.StateSchemaVersion,
            WorkflowVersion = Constants.WorkflowVersion,
            RunName = runName,
            SourceRepo = settings.SourceRepo,
            SourceBranch = settings.SourceBranch,
            TargetRepo = settings.TargetRepo,
            TargetRepoRoot = targetRepoRoot,
            TargetPath = settings.TargetPath,
            StateRoot = string.Empty,
            WorkRoot = string.Empty,
            RunDirectory = runDirectory,
            WorkDirectory = string.Empty,
            SourceRemoteUri = string.Empty,
            TargetRemoteUri = string.Empty,
            SourceCloneDirectory = string.Empty,
            ImportPreviewDirectory = string.Empty,
            TargetHeadCommit = string.Empty,
            DryRun = settings.DryRun,
            SelectedStartStage = executionPlan.StartStageName,
            SelectedStopStage = executionPlan.StopStageName,
            CreatedUtc = DateTimeOffset.UtcNow,
            UpdatedUtc = DateTimeOffset.UtcNow,
            Stages = [.. Stages.Definitions.Select(static stage => new StageState { Name = stage.Name, Description = stage.Description, Status = StageStatus.Pending })],
        };

    private static void SyncStageMetadata(RunState state)
    {
        foreach (var definition in Stages.Definitions)
        {
            var stage = GetStageState(state, definition.Name);
            stage.Description = definition.Description;
        }
    }

    private static void RecoverCompletedStagesFromSentinels(RunState state, string runDirectory)
    {
        var sentinelsDirectory = Path.Combine(runDirectory, "sentinels");
        if (!Directory.Exists(sentinelsDirectory))
            return;

        foreach (var definition in Stages.Definitions)
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

    private static void EnsureCompatibleState(RunState state, Settings settings)
    {
        if (state.SchemaVersion != Constants.StateSchemaVersion
            || !string.Equals(state.WorkflowVersion, Constants.WorkflowVersion, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Existing state was created for workflow version '{state.WorkflowVersion}' (schema {state.SchemaVersion}). " +
                "Use --reset or choose a new --run-name.");
        }

        ValidateMatchingSetting(state.SourceRepo, settings.SourceRepo, nameof(settings.SourceRepo));
        ValidateMatchingSetting(state.SourceBranch, settings.SourceBranch, nameof(settings.SourceBranch));
        ValidateMatchingSetting(state.TargetRepo, settings.TargetRepo, nameof(settings.TargetRepo));
        ValidateMatchingSetting(state.TargetPath, settings.TargetPath, nameof(settings.TargetPath));
    }

    private static StageState GetStageState(RunState state, string stageName)
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

    private static int GetStageIndex(IReadOnlyList<StageDefinition> stageDefinitions, string stageName)
    {
        var normalizedName = NormalizeStageName(stageName);
        for (var i = 0; i < stageDefinitions.Count; i++)
        {
            if (NormalizeStageName(stageDefinitions[i].Name) == normalizedName)
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

    private static void ValidateMatchingSetting(string existingValue, string currentValue, string name)
    {
        if (!string.IsNullOrWhiteSpace(existingValue) && !string.Equals(existingValue, currentValue, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Existing state was created with a different {name} value ('{existingValue}' vs '{currentValue}'). " +
                "Use --reset or a different --run-name.");
        }
    }
}
