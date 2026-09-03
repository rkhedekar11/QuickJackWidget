namespace QuickJack.Core.Models;

/// <summary>A saved command, as persisted and as shown on the widget.</summary>
public sealed record CommandDef
{
    /// <summary>Stable slug, e.g. "flush-dns". Unique within a store.</summary>
    public required string Id { get; init; }

    public required string Name { get; init; }
    public string? Description { get; init; }

    /// <summary>Emoji or Segoe Fluent glyph shown on the button.</summary>
    public string? Icon { get; init; }

    public string? Group { get; init; }

    public ShellKind Shell { get; init; } = ShellKind.PowerShell;

    /// <summary>Script body. May be multi-line; written to a temp file at run time.</summary>
    public required string Script { get; init; }

    public string? WorkingDirectory { get; init; }

    public IReadOnlyList<ParameterDef> Parameters { get; init; } = [];

    public ElevationMode Elevation { get; init; } = ElevationMode.None;
    public OutputMode Output { get; init; } = OutputMode.Capture;

    public bool ConfirmBeforeRun { get; init; }

    public int TimeoutSeconds { get; init; } = 120;

    /// <summary>"user" for UI-created, "api:&lt;client&gt;" for API-registered.</summary>
    public string Source { get; init; } = "user";

    /// <summary>
    /// API-registered commands land unapproved and cannot run until the user approves
    /// them once, so a rogue caller cannot silently plant a button.
    /// </summary>
    public bool Approved { get; init; } = true;

    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }

    /// <summary>Which store this came from. Not persisted; set on load.</summary>
    public CommandOrigin Origin { get; init; } = CommandOrigin.User;

    public bool IsElevated => Elevation is ElevationMode.Uac or ElevationMode.Agent;
    public bool CanRun => Approved;
}
