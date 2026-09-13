using System.Globalization;

namespace AcDream.Plugins.Agent.Contract;

internal static class Guids
{
    /// <summary>Reads an object id written as <c>0x5000000A</c> or as a decimal number.</summary>
    internal static bool TryParse(string text, out uint id)
    {
        id = 0u;
        if (string.IsNullOrWhiteSpace(text))
            return false;
        string trimmed = text.Trim();
        if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed.Length > 2
                && uint.TryParse(
                    trimmed.AsSpan(2),
                    NumberStyles.AllowHexSpecifier,
                    CultureInfo.InvariantCulture,
                    out id)
                && id != 0u;
        }
        return uint.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out id)
            && id != 0u;
    }
}
