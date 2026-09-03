using System.Text;

namespace QuickJack.Core.Storage;

/// <summary>Turns a display name into a stable, URL- and filename-safe id.</summary>
public static class Slug
{
    public const int MaxLength = 64;

    /// <summary>"Flush DNS!" -> "flush-dns". Returns "command" if nothing survives.</summary>
    public static string From(string text)
    {
        var sb = new StringBuilder(text.Length);
        var lastWasDash = false;

        foreach (var raw in text.Trim())
        {
            var ch = char.ToLowerInvariant(raw);
            if (ch is >= 'a' and <= 'z' || ch is >= '0' and <= '9')
            {
                sb.Append(ch);
                lastWasDash = false;
            }
            else if (!lastWasDash && sb.Length > 0)
            {
                sb.Append('-');
                lastWasDash = true;
            }
        }

        if (lastWasDash) sb.Length--;
        if (sb.Length == 0) return "command";
        if (sb.Length > MaxLength) sb.Length = MaxLength;
        return sb.ToString().TrimEnd('-');
    }

    /// <summary>
    /// A slug not already present in <paramref name="taken"/>, suffixing -2, -3, ...
    /// </summary>
    public static string Unique(string text, Func<string, bool> taken)
    {
        var baseSlug = From(text);
        if (!taken(baseSlug)) return baseSlug;

        for (var n = 2; n < 10_000; n++)
        {
            var candidate = $"{baseSlug}-{n}";
            if (!taken(candidate)) return candidate;
        }

        return $"{baseSlug}-{Guid.NewGuid():N}";
    }

    /// <summary>True if the value is already a well-formed slug (safe as a filename / route segment).</summary>
    public static bool IsValid(string? value) =>
        !string.IsNullOrEmpty(value)
        && value.Length <= MaxLength
        && value == From(value);
}
