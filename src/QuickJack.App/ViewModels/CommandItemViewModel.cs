using CommunityToolkit.Mvvm.ComponentModel;
using QuickJack.Core.Models;

namespace QuickJack.App.ViewModels;

/// <summary>One command as a row in the palette.</summary>
public sealed partial class CommandItemViewModel(CommandDef command) : ObservableObject
{
    public CommandDef Command { get; } = command;

    public string Id => Command.Id;
    public string Name => Command.Name;
    public string? Description => Command.Description;
    public string? Group => Command.Group;
    public string Icon => string.IsNullOrWhiteSpace(Command.Icon) ? "▸" : Command.Icon!;

    public bool NeedsApproval => !Command.Approved;
    public bool HasParameters => Command.Parameters.Count > 0;

    /// <summary>Short badge shown on the right of the row.</summary>
    public string? Badge => Command.Elevation switch
    {
        ElevationMode.Uac => "admin",
        ElevationMode.Agent => "admin*", // no prompt — pinned by an administrator
        _ => null,
    };

    public string? BadgeTooltip => Command.Elevation switch
    {
        ElevationMode.Uac => "Runs elevated. Windows will ask for consent each time.",
        ElevationMode.Agent => "Runs elevated with no prompt, via the QuickJack agent.",
        _ => null,
    };

    /// <summary>Where this command came from, shown so an API-planted button is not anonymous.</summary>
    public string SourceText => Command.Source == "user"
        ? "added here"
        : $"registered by {Command.Source[4..]}";

    public bool IsFromApi => Command.Source.StartsWith("api:", StringComparison.Ordinal);
}
