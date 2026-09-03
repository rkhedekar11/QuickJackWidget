namespace QuickJack.Core.Models;

/// <summary>
/// A value collected from the user before a command runs.
/// <para>
/// Values are always exposed to the script as environment variables
/// (<c>QJ_&lt;NAME&gt;</c>), which is the safe path and the documented default.
/// A parameter may additionally be spliced into the script text via a
/// <c>{{name}}</c> placeholder, but only if it declares a <see cref="Pattern"/> —
/// see <c>ParameterBinder</c>.
/// </para>
/// </summary>
public sealed record ParameterDef
{
    public required string Name { get; init; }
    public string? Label { get; init; }
    public string? Default { get; init; }
    public bool Required { get; init; }

    /// <summary>
    /// Anchored regex the supplied value must match. Required for any parameter
    /// referenced by a <c>{{name}}</c> placeholder; optional otherwise.
    /// </summary>
    public string? Pattern { get; init; }
}
