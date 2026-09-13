using System.Text;

namespace AcDream.Plugins.Agent.Contract;

internal static class WireNames
{
    /// <summary>Kebab-cases an identifier: <c>MeleeWeapon</c> becomes <c>melee-weapon</c>.</summary>
    internal static string Kebab(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var builder = new StringBuilder(name.Length + 4);
        for (int index = 0; index < name.Length; index++)
        {
            char character = name[index];
            if (!char.IsUpper(character))
            {
                builder.Append(character);
                continue;
            }
            bool afterLower = index > 0 && char.IsLower(name[index - 1]);
            bool endsAcronym = index > 0
                && char.IsUpper(name[index - 1])
                && index + 1 < name.Length
                && char.IsLower(name[index + 1]);
            if (afterLower || endsAcronym)
                builder.Append('-');
            builder.Append(char.ToLowerInvariant(character));
        }
        return builder.ToString();
    }
}
