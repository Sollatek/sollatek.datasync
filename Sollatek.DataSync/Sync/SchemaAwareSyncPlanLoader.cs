#nullable enable

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Sollatek.DataSync.Config;
using Sollatek.DataSync.State;
using Sollatek.DataSync.Sync.Contract;
using Sollatek.DataSync.Sync.Metadata;

namespace Sollatek.DataSync.Sync;

public static class SchemaAwareSyncPlanLoader
{
    public static async Task<SchemaAwareSyncPlanLoadResult> LoadAsync(
        HttpClient httpClient,
        IConfiguration configuration,
        SchemaContractOptions options,
        ISyncContractStore contractStore,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(contractStore);
        ArgumentNullException.ThrowIfNull(logger);

        var accepted = await contractStore.LoadAsync(cancellationToken);
        accepted?.Validate();

        var configuredSyncPlan = configuration.GetSyncPlan();
        var documents = SwaggerSyncDocumentOptionsReader.FromConfiguration(configuration);
        SwaggerSyncMetadataRegistry registry;
        try
        {
            registry = await SwaggerSyncMetadataLoader.LoadAsync(
                httpClient,
                documents,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (accepted is not null)
        {
            var fallbackPlan = ValidateFallbackPlan(
                accepted,
                options,
                configuredSyncPlan,
                exception);
            logger.LogWarning(
                exception,
                "Could not load the current Swagger sync contract. Continuing with accepted contract {ContractHash} from {AcceptedAtUtc:O}.",
                accepted.ContractHash,
                accepted.AcceptedAtUtc);
            return new SchemaAwareSyncPlanLoadResult(
                fallbackPlan,
                PendingAcceptance: null,
                UsedLastKnownGood: true);
        }

        var candidatePlan = SwaggerBackedSyncPlanResolver.Resolve(
            registry,
            configuredSyncPlan);
        var candidate = SyncContractSnapshot.Create(
            options.Version,
            candidatePlan.MetadataEntities);
        if (accepted is null)
        {
            logger.LogInformation(
                "No accepted DataSync contract exists. Contract {ContractHash} will be accepted only after a successful sync run.",
                candidate.ContractHash);
            return new SchemaAwareSyncPlanLoadResult(
                candidatePlan,
                candidate,
                UsedLastKnownGood: false);
        }

        var comparison = SyncContractCompatibilityAnalyzer.Compare(accepted, candidate);
        if (comparison.IsUnchanged &&
            string.Equals(
                accepted.SchemaVersion,
                options.Version,
                StringComparison.Ordinal))
        {
            return new SchemaAwareSyncPlanLoadResult(
                candidatePlan,
                PendingAcceptance: null,
                UsedLastKnownGood: false);
        }

        if (!comparison.IsCompatible &&
            string.Equals(
                accepted.SchemaVersion,
                options.Version,
                StringComparison.Ordinal))
        {
            throw BuildBreakingChangeException(
                accepted,
                candidate,
                comparison.BreakingChanges);
        }

        if (comparison.BreakingChanges.Count > 0)
        {
            logger.LogWarning(
                "Schema:version changed from {AcceptedVersion} to {CandidateVersion}; allowing {BreakingChangeCount} reviewed breaking contract changes. The new contract will be accepted only after a successful sync run. Changes: {Changes}",
                accepted.SchemaVersion,
                candidate.SchemaVersion,
                comparison.BreakingChanges.Count,
                string.Join(" | ", comparison.BreakingChanges));
        }
        else if (comparison.CompatibleChanges.Count > 0)
        {
            logger.LogInformation(
                "Detected {CompatibleChangeCount} compatible DataSync contract changes. The new contract will be accepted only after a successful sync run. Changes: {Changes}",
                comparison.CompatibleChanges.Count,
                string.Join(" | ", comparison.CompatibleChanges));
        }

        return new SchemaAwareSyncPlanLoadResult(
            candidatePlan,
            candidate,
            UsedLastKnownGood: false);
    }

    private static InvalidOperationException BuildBreakingChangeException(
        SyncContractSnapshot accepted,
        SyncContractSnapshot candidate,
        IReadOnlyList<string> changes)
    {
        var details = string.Join(
            Environment.NewLine,
            changes.Select(change => $" - {change}"));
        return new InvalidOperationException(
            $"The Swagger sync contract contains breaking changes, but Schema:version is still '{candidate.SchemaVersion}'. " +
            $"Accepted contract: {accepted.ContractHash}. Candidate contract: {candidate.ContractHash}.{Environment.NewLine}" +
            $"{details}{Environment.NewLine}" +
            "Review and migrate existing database/file consumers as required, then increment Schema:version to explicitly approve this contract.");
    }

    private static SwaggerBackedSyncPlan ValidateFallbackPlan(
        SyncContractSnapshot accepted,
        SchemaContractOptions options,
        IReadOnlyList<string> configuredSyncPlan,
        Exception swaggerException)
    {
        if (!string.Equals(
                accepted.SchemaVersion,
                options.Version,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Swagger could not be loaded and Schema:version changed from accepted version '{accepted.SchemaVersion}' to '{options.Version}'. DataSync will not apply a configuration change using the old contract.",
                swaggerException);
        }

        IReadOnlyList<SwaggerSyncEntityMetadata> resolvedEntities;
        try
        {
            resolvedEntities = SwaggerSyncPlanResolver.Resolve(
                accepted.Entities,
                configuredSyncPlan);
        }
        catch (Exception configurationException)
        {
            throw new InvalidOperationException(
                "Swagger could not be loaded and the configured SyncPlan cannot be resolved against the accepted contract. DataSync will not hide the configuration error by using the old plan.",
                new AggregateException(swaggerException, configurationException));
        }

        if (!accepted.Entities
                .Select(x => x.Key)
                .SequenceEqual(
                    resolvedEntities.Select(x => x.Key),
                    StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Swagger could not be loaded and the configured SyncPlan differs from the accepted contract. DataSync will not apply a plan change using the old contract.",
                swaggerException);
        }

        return new SwaggerBackedSyncPlan(resolvedEntities);
    }
}

public sealed record SchemaAwareSyncPlanLoadResult(
    SwaggerBackedSyncPlan Plan,
    SyncContractSnapshot? PendingAcceptance,
    bool UsedLastKnownGood);
