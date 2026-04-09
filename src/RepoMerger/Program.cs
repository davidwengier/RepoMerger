namespace RepoMerger;

internal static class Program
{
    private const string DefaultSourceRepo = "dotnet/razor";
    private const string DefaultSourceBranch = "main";
    private const string DefaultTargetRepo = "dotnet/roslyn";
    private const string DefaultTargetPath = @"src\Razor";
    private const string DefaultWorkRoot = @"..\repo-merge-work";
    private const string DefaultStateRoot = DefaultWorkRoot;

    public static async Task<int> Main(string[] args)
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
            return await Merger.RunAsync(settings).ConfigureAwait(false);
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
        Console.WriteLine(@"  dotnet run --project src\RepoMerger\RepoMerger.csproj -- [options]");
        Console.WriteLine();
        Console.WriteLine("Key options:");
        Console.WriteLine($"  --source-repo <value>     Source repository or local path. Default: {DefaultSourceRepo}");
        Console.WriteLine($"  --source-branch <value>   Source branch to use. Default: {DefaultSourceBranch}");
        Console.WriteLine($"  --target-repo <value>     Target repository in owner/repo format. Default: {DefaultTargetRepo}");
        Console.WriteLine($"  --target-path <path>      Destination path inside the target repo. Default: {DefaultTargetPath}");
        Console.WriteLine($"  --state-root <path>       Where run state is persisted. Default: {DefaultStateRoot}");
        Console.WriteLine($"  --work-root <path>        Where the cloned working repos live. Default: {DefaultWorkRoot}");
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

    private static void PrintStages()
    {
        Console.WriteLine("Available repo-merge stages:");
        foreach (var stage in Stages.Definitions)
            Console.WriteLine($"  {stage.Name,-20} {stage.Description}");
    }

    private static int GetStageIndex(string stageName)
    {
        var normalizedName = NormalizeStageName(stageName);
        for (var i = 0; i < Stages.Definitions.Length; i++)
        {
            if (NormalizeStageName(Stages.Definitions[i].Name) == normalizedName)
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

}
