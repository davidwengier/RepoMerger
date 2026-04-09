using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace RepoMerger;

internal static class RunStateStore
{
    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static async Task<RunState> LoadAsync(string statePath)
    {
        await using var stream = File.OpenRead(statePath);
        return await JsonSerializer.DeserializeAsync<RunState>(stream, JsonOptions).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Could not deserialize '{statePath}'.");
    }

    public static async Task SaveAsync(string statePath, RunState state)
    {
        await using var stream = File.Create(statePath);
        await JsonSerializer.SerializeAsync(stream, state, JsonOptions).ConfigureAwait(false);
    }

    public static async Task WriteSentinelAsync(string runDirectory, StageDefinition definition, StageState record)
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
}
