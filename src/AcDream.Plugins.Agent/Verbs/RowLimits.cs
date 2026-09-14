using System.Globalization;
using System.Text.Json.Nodes;

namespace AcDream.Plugins.Agent.Verbs;

/// <summary>
/// A read's trailing <c>limit &lt;n&gt;</c>, and the counts that tell a reader a
/// short list is a limited one: how many rows matched, how many are shown, and
/// how many the limit held back.
/// </summary>
internal static class RowLimits
{
    internal const int MaximumRows = 500;

    internal static readonly string Problem = $"limit must be a whole number from 1 to {MaximumRows}";

    /// <summary>
    /// Splits a trailing <c>limit &lt;n&gt;</c> from the arguments, or gives
    /// <paramref name="defaultRows"/> when there is none. False when the limit is
    /// not a whole number from 1 to <see cref="MaximumRows"/>.
    /// </summary>
    internal static bool TrySplit(string arguments, int defaultRows, out string rest, out int limit)
    {
        rest = arguments.Trim();
        limit = defaultRows;
        string[] words = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < 2 || !words[^2].Equals("limit", StringComparison.OrdinalIgnoreCase))
            return true;
        if (!int.TryParse(words[^1], NumberStyles.None, CultureInfo.InvariantCulture, out limit)
            || limit is < 1 or > MaximumRows)
        {
            return false;
        }
        rest = string.Join(' ', words[..^2]);
        return true;
    }

    /// <summary>Adds how many rows matched, how many are shown and how many the limit held back.</summary>
    internal static void Count(JsonObject fields, int matched, int shown)
    {
        fields["matched"] = matched;
        fields["shown"] = shown;
        fields["beyondLimit"] = matched - shown;
    }
}
