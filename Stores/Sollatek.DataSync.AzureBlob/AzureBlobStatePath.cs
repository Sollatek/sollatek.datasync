#nullable enable

namespace Sollatek.DataSync.AzureBlob;

internal static class AzureBlobStatePath
{
    public static string Combine(params string[] parts)
    {
        return string.Join(
            "/",
            parts
                .Where(part => !string.IsNullOrWhiteSpace(part))
                .Select(part => part.Trim().Trim('/', '\\')));
    }

    public static string ValidateDocumentKey(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (key.Contains('/', StringComparison.Ordinal) ||
            key.Contains('\\', StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Blob state key '{key}' must not contain path separators.");
        }

        return key;
    }
}
