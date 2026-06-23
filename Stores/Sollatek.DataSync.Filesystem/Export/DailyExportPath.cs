#nullable enable

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Sollatek.DataSync.Config;

namespace Sollatek.DataSync.Export;

public static class DailyExportPath
{
    public static string Build(
        string rootPath,
        string entityKey,
        DateOnly day,
        int partNumber)
    {
        return Build(
            new FileExportOptions { RootPath = rootPath },
            entityKey,
            day,
            partNumber);
    }

    public static string Build(
        FileExportOptions options,
        string entityKey,
        DateOnly day,
        int partNumber)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.RootPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(entityKey);

        if (!IsSafeEntityKey(entityKey))
        {
            throw new InvalidOperationException(
                $"Entity key '{entityKey}' is not a safe export folder name.");
        }

        if (partNumber < 0)
        {
            throw new InvalidOperationException("partNumber must be greater than or equal to 0.");
        }

        var relativePath = BuildRelative(options, entityKey, day, partNumber);
        return Path.Combine(
            [options.RootPath, .. relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries)]);
    }

    public static string BuildRelative(
        FileExportOptions options,
        string entityKey,
        DateOnly day,
        int partNumber)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(entityKey);

        if (!IsSafeEntityKey(entityKey))
        {
            throw new InvalidOperationException(
                $"Entity key '{entityKey}' is not a safe export folder name.");
        }

        if (partNumber < 0)
        {
            throw new InvalidOperationException("partNumber must be greater than or equal to 0.");
        }

        var context = CreateContext(options, entityKey, day, partNumber);
        var folder = RenderFolder(options.FolderFormat, context);
        var fileName = RenderTemplate(options.FileNameFormat, context);
        var folderSegments = ValidateFolder(folder);

        return folderSegments.Length == 0
            ? ValidateFileName(fileName)
            : string.Join("/", [.. folderSegments, ValidateFileName(fileName)]);
    }

    public static bool IsEntityExportPath(
        FileExportOptions options,
        string entityKey,
        string path)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(entityKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!IsSafeEntityKey(entityKey))
        {
            return false;
        }

        var relativePath = Path.GetRelativePath(options.RootPath, path);
        if (relativePath.StartsWith("..", StringComparison.Ordinal) ||
            Path.IsPathRooted(relativePath))
        {
            return false;
        }

        return IsEntityExportRelativePath(options, entityKey, relativePath);
    }

    public static bool IsEntityExportRelativePath(
        FileExportOptions options,
        string entityKey,
        string relativePath)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(entityKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);

        if (Path.IsPathRooted(relativePath) ||
            NormalizeSeparators(relativePath).Split('/').Any(segment => segment is "." or ".."))
        {
            return false;
        }

        if (!IsSafeEntityKey(entityKey))
        {
            return false;
        }

        var context = CreateContext(
            options,
            entityKey,
            new DateOnly(2000, 12, 31),
            partNumber: 0);
        var pattern = BuildRelativePathRegex(options, context);
        return Regex.IsMatch(
            NormalizeSeparators(relativePath),
            pattern,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static ExportPathContext CreateContext(
        FileExportOptions options,
        string entityKey,
        DateOnly day,
        int partNumber)
    {
        var entityOptions = options.GetEntityOptions(entityKey);
        var outputName = string.IsNullOrWhiteSpace(entityOptions.OutputName)
            ? entityKey
            : entityOptions.OutputName;

        return new ExportPathContext(entityKey, outputName, day, partNumber, options.Format);
    }

    private static string RenderFolder(string template, ExportPathContext context)
    {
        return template.Contains('{', StringComparison.Ordinal)
            ? RenderTemplate(template, context)
            : context.Day.ToString(template, CultureInfo.InvariantCulture);
    }

    private static string RenderTemplate(string template, ExportPathContext context)
    {
        if (string.IsNullOrWhiteSpace(template))
        {
            throw new InvalidOperationException("FileExport path format values cannot be empty.");
        }

        var builder = new StringBuilder(template.Length);
        for (var index = 0; index < template.Length;)
        {
            if (template[index] != '{')
            {
                builder.Append(template[index]);
                index++;
                continue;
            }

            var end = template.IndexOf('}', index + 1);
            if (end < 0)
            {
                throw new InvalidOperationException(
                    $"FileExport path format '{template}' contains an unterminated token.");
            }

            var token = template.Substring(index + 1, end - index - 1);
            builder.Append(ResolveToken(token, context));
            index = end + 1;
        }

        return builder.ToString();
    }

    private static string ResolveToken(string token, ExportPathContext context)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            throw UnsupportedTokenException(token);
        }

        return token.Trim() switch
        {
            "entity" => context.EntityName,
            "entityKey" => context.EntityKey,
            "date" => context.Day.ToString("yyyyMMdd", CultureInfo.InvariantCulture),
            "yyyy" => context.Day.ToString("yyyy", CultureInfo.InvariantCulture),
            "MM" => context.Day.ToString("MM", CultureInfo.InvariantCulture),
            "dd" => context.Day.ToString("dd", CultureInfo.InvariantCulture),
            "part" => context.PartNumber.ToString(CultureInfo.InvariantCulture),
            "format" => context.Format,
            var dateToken when dateToken.StartsWith("date:", StringComparison.OrdinalIgnoreCase) =>
                context.Day.ToString(dateToken["date:".Length..], CultureInfo.InvariantCulture),
            var partToken when partToken.StartsWith("part:", StringComparison.OrdinalIgnoreCase) =>
                context.PartNumber.ToString(partToken["part:".Length..], CultureInfo.InvariantCulture),
            _ => throw UnsupportedTokenException(token)
        };
    }

    private static string[] ValidateFolder(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
        {
            return [];
        }

        if (Path.IsPathRooted(folder))
        {
            throw new InvalidOperationException("FileExport:folderFormat must render a relative folder path.");
        }

        return NormalizeSeparators(folder)
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(segment => ValidatePathSegment(segment, "FileExport:folderFormat"))
            .ToArray();
    }

    private static string ValidateFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            throw new InvalidOperationException("FileExport:fileNameFormat must render a file name.");
        }

        if (Path.GetFileName(fileName) != fileName)
        {
            throw new InvalidOperationException("FileExport:fileNameFormat must render a file name, not a path.");
        }

        return ValidatePathSegment(fileName, "FileExport:fileNameFormat");
    }

    private static string ValidatePathSegment(string segment, string optionName)
    {
        if (segment is "." or ".." ||
            segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new InvalidOperationException($"{optionName} rendered an unsafe path segment '{segment}'.");
        }

        return segment;
    }

    private static string BuildRelativePathRegex(
        FileExportOptions options,
        ExportPathContext context)
    {
        var folderPattern = BuildFolderRegex(options.FolderFormat, context);
        var fileNamePattern = BuildTemplateRegex(options.FileNameFormat, context);
        return string.IsNullOrEmpty(folderPattern)
            ? $"^{fileNamePattern}$"
            : $"^{folderPattern}/{fileNamePattern}$";
    }

    private static string BuildFolderRegex(string template, ExportPathContext context)
    {
        if (string.IsNullOrWhiteSpace(template))
        {
            return string.Empty;
        }

        if (template.Contains('{', StringComparison.Ordinal))
        {
            return BuildTemplateRegex(template, context);
        }

        var rendered = NormalizeSeparators(
            context.Day.ToString(template, CultureInfo.InvariantCulture));
        return string.Join(
            "/",
            rendered
                .Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Select(_ => "[^/]+"));
    }

    private static string BuildTemplateRegex(string template, ExportPathContext context)
    {
        var builder = new StringBuilder(template.Length);
        var normalized = NormalizeSeparators(template);
        for (var index = 0; index < normalized.Length;)
        {
            if (normalized[index] != '{')
            {
                builder.Append(Regex.Escape(normalized[index].ToString()));
                index++;
                continue;
            }

            var end = normalized.IndexOf('}', index + 1);
            if (end < 0)
            {
                throw new InvalidOperationException(
                    $"FileExport path format '{template}' contains an unterminated token.");
            }

            var token = normalized.Substring(index + 1, end - index - 1);
            builder.Append(ResolveTokenRegex(token, context));
            index = end + 1;
        }

        return builder.ToString();
    }

    private static string ResolveTokenRegex(string token, ExportPathContext context)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            throw UnsupportedTokenException(token);
        }

        return token.Trim() switch
        {
            "entity" => Regex.Escape(context.EntityName),
            "entityKey" => Regex.Escape(context.EntityKey),
            "date" or "yyyy" or "MM" or "dd" => "[^/]+",
            "part" => "[^/]+",
            "format" => Regex.Escape(context.Format),
            var dateToken when dateToken.StartsWith("date:", StringComparison.OrdinalIgnoreCase) => "[^/]+",
            var partToken when partToken.StartsWith("part:", StringComparison.OrdinalIgnoreCase) => "[^/]+",
            _ => throw UnsupportedTokenException(token)
        };
    }

    private static InvalidOperationException UnsupportedTokenException(string token)
    {
        return new InvalidOperationException(
            $"Unsupported FileExport path token '{{{token}}}'. Supported tokens are entity, entityKey, date, date:<format>, yyyy, MM, dd, part, part:<format>, and format.");
    }

    private static string NormalizeSeparators(string path)
    {
        return path.Replace('\\', '/');
    }

    private static bool IsSafeEntityKey(string value)
    {
        return value.All(character =>
            char.IsLetterOrDigit(character) ||
            character == '_' ||
            character == '-');
    }

    private sealed record ExportPathContext(
        string EntityKey,
        string EntityName,
        DateOnly Day,
        int PartNumber,
        string Format);
}
