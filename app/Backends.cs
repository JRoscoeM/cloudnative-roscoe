using Elastic.Clients.Elasticsearch;
using Elastic.Transport;
using OpenAI;

namespace App;

/// <summary>
/// Clients for the external providers. Each is null until its credentials exist,
/// so the app still starts and deploys before Secret app-credentials is created.
/// </summary>
public sealed record Backends(
    ElasticsearchClient? Elasticsearch,
    OpenAIClient? OpenAI,
    bool TelemetryExportEnabled)
{
    public static Backends FromConfiguration(IConfiguration configuration)
    {
        var elasticUrl = configuration["ELASTICSEARCH_URL"];
        var elasticKey = configuration["ELASTICSEARCH_API_KEY"];
        var openAiKey = configuration["OPENAI_API_KEY"];

        var elastic = string.IsNullOrWhiteSpace(elasticUrl) || string.IsNullOrWhiteSpace(elasticKey)
            ? null
            : new ElasticsearchClient(
                new ElasticsearchClientSettings(new Uri(elasticUrl)).Authentication(new ApiKey(elasticKey)));

        var openAi = string.IsNullOrWhiteSpace(openAiKey) ? null : new OpenAIClient(openAiKey);

        return new Backends(elastic, openAi, AppTelemetry.ExportConfigured(configuration));
    }
}
