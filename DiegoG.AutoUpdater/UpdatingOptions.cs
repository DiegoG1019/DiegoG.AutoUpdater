using System.Runtime.Intrinsics.Arm;
using System.Text.Json;

namespace DiegoG.AutoUpdater;

public readonly record struct CommandOptions(string Command, string? Arguments, bool Safe);

/// <summary>
/// Represents the options used by a given app, describing how it should be updated
/// </summary>
public class UpdatingOptions
{
    public required string SourceName { get; init; }
    public required string TargetDirectory { get; init; }
    public required JsonDocument SourceOptions { get; init; }
    public required string ProcessName { get; init; }
    public bool PermitKillProcess { get; init; }
    public CommandOptions[]? BeforeUpdateCommands { get; init; }
    public CommandOptions[]? AfterUpdateCommands { get; init; }
    public string? MessagePipeName { get; init; }
    public HashSet<string>? CleanUpExceptions { get; init; }
    public HashSet<string>? CleanUpList { get; init; }
    public bool CleanupAll { get; init; }
}
