#nullable enable

using Microsoft.Extensions.Configuration;

namespace Sollatek.DataSync.Sync.Metadata;

public static class SwaggerSyncDocumentOptionsReader
{
    private const string SectionName = "SwaggerDocuments";

    public static IReadOnlyList<SwaggerSyncDocumentOptions> FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(SectionName);
        var children = section.GetChildren().ToArray();

        if (children.Length == 0)
        {
            return GetDefaultDocuments(configuration);
        }

        var documents = new List<SwaggerSyncDocumentOptions>(children.Length);
        for (var index = 0; index < children.Length; index++)
        {
            var child = children[index];
            var name = child.GetValue<string>("name");
            var url = child.GetValue<string>("url");

            if (string.IsNullOrWhiteSpace(name))
            {
                throw new InvalidOperationException($"{SectionName}[{index}].name is required.");
            }

            if (string.IsNullOrWhiteSpace(url))
            {
                throw new InvalidOperationException($"{SectionName}[{index}].url is required.");
            }

            documents.Add(new SwaggerSyncDocumentOptions(name.Trim(), url.Trim()));
        }

        return documents;
    }

    private static IReadOnlyList<SwaggerSyncDocumentOptions> GetDefaultDocuments(IConfiguration configuration)
    {
        var apiUrl = configuration.GetRequiredSection("Settings").GetValue<string>("apiUrl");
        if (string.IsNullOrWhiteSpace(apiUrl))
        {
            throw new InvalidOperationException(
                "Settings:apiUrl is required when SwaggerDocuments is not configured.");
        }

        var baseUri = new Uri(apiUrl.Trim(), UriKind.Absolute);

        return
        [
            new SwaggerSyncDocumentOptions("data-v1", new Uri(baseUri, "swagger/data-v1/swagger.json").ToString()),
            new SwaggerSyncDocumentOptions("portal-v1", new Uri(baseUri, "swagger/portal-v1/swagger.json").ToString())
        ];
    }
}
