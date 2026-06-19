#nullable enable

using System.Text;

namespace Sollatek.DataSync.Storage.Relational;

internal static class RelationalColumnName
{
    public static string FromMetadataPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        return string.Join("_", path
            .Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(ToSnakeCase));
    }

    private static string ToSnakeCase(string value)
    {
        var builder = new StringBuilder(value.Length + 8);

        for (var index = 0; index < value.Length; index++)
        {
            var current = value[index];
            if (char.IsUpper(current) &&
                index > 0 &&
                builder.Length > 0 &&
                builder[^1] != '_')
            {
                builder.Append('_');
            }

            builder.Append(char.ToLowerInvariant(current));
        }

        return builder.ToString();
    }
}
