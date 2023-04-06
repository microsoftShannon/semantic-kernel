﻿// Copyright (c) Microsoft. All rights reserved.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.SemanticKernel.AI.Embeddings;
using Microsoft.SemanticKernel.Memory;
using Microsoft.SemanticKernel.Memory.Collections;
using Microsoft.SemanticKernel.Memory.Storage;
using Newtonsoft.Json;

namespace Skills.Memory.CosmosDB;

/// <summary>
/// An implementation of <see cref="IMemoryStore{TEmbedding}"/> for Azure Cosmos DB.
/// </summary>
/// <remarks>The Embedding data is saved to the Azure Cosmos DB database container specified in the constructor.
/// The embedding data persists between subsequent instances and has similarity search capability, handled by the client as Azure Cosmos DB is not a vector-native DB.
/// </remarks>
public class CosmosDBMemoryStore<TEmbedding> : IMemoryStore<TEmbedding>
    where TEmbedding : unmanaged
{
    private CosmosClient _client;
    private string _databaseName;
    private string _containerName;
    private ILogger? _log;

    /// <summary>
    /// Constructor for a memory store backed by an Azure Cosmos DB instance.
    /// </summary>
    /// <param name="client"></param>
    /// <param name="databaseName"></param>
    /// <param name="containerName"></param>
    /// <param name="log"></param>
    public CosmosDBMemoryStore(CosmosClient client, string databaseName, string containerName, ILogger? log = null)
    {
        this._client = client;
        this._databaseName = databaseName;
        this._containerName = containerName;
        this._log = log ?? NullLogger<CosmosDBMemoryStore<TEmbedding>>.Instance;
    }

    /// <inheritdoc />
    public async Task<DataEntry<IEmbeddingWithMetadata<TEmbedding>>?> GetAsync(string collection, string key, CancellationToken cancel = default)
    {
        var container = this._client.GetContainer(this._databaseName, this._containerName);

        using (var responseMessage = await container.ReadItemStreamAsync(this._toCosmosFriendlyId(key), new Microsoft.Azure.Cosmos.PartitionKey(collection), cancellationToken: cancel))
        {
            if (!responseMessage.IsSuccessStatusCode)
            {
                this._log?.LogWarning("Failed to get item {0} from collection {1} with status code {2}", key, collection, responseMessage.StatusCode);
                return null;
            }

            using (responseMessage.Content)
            {
                CosmosDBMemoryRecord record;

                if (typeof(Stream).IsAssignableFrom(typeof(CosmosDBMemoryRecord)))
                {
                    record = ((CosmosDBMemoryRecord)(object)responseMessage.Content);
                }
                else
                {
                    record = await System.Text.Json.JsonSerializer.DeserializeAsync<CosmosDBMemoryRecord>(responseMessage.Content!, cancellationToken: cancel);
                }

                var embeddingHost = JsonConvert.DeserializeAnonymousType(
                    record!.EmbeddingString,
                    new { Embedding = new { vector = new List<float>() } });

                var rec = MemoryRecord.FromJson(
                    record.MetadataString,
                    new Embedding<float>(embeddingHost.Embedding.vector));

                return DataEntry.Create<IEmbeddingWithMetadata<TEmbedding>>(
                            rec.Metadata.Id,
                            (IEmbeddingWithMetadata<TEmbedding>)rec,
                            record.Timestamp);
            }
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<string> GetCollectionsAsync([EnumeratorCancellation] CancellationToken cancel = default)
    {
        var container = this._client.GetContainer(this._databaseName, this._containerName);
        var query = new QueryDefinition($"SELECT DISTINCT c.collectionId FROM c");
        var iterator = container.GetItemQueryIterator<CosmosDBMemoryRecord>(query);

        var items = await iterator.ReadNextAsync(cancel).ConfigureAwait(false);

        foreach (var item in items)
        {
            yield return item.CollectionId;
        }
    }

    /// <inheritdoc />
    public IAsyncEnumerable<(IEmbeddingWithMetadata<TEmbedding>, double)> GetNearestMatchesAsync(string collection, Embedding<TEmbedding> embedding, int limit = 1, double minRelevanceScore = 0)
    {
        if (limit <= 0)
        {
            return AsyncEnumerable.Empty<(IEmbeddingWithMetadata<TEmbedding>, double)>();
        }

        var asyncEmbeddingCollection = this.TryGetCollectionAsync(collection);
        var embeddingCollection = asyncEmbeddingCollection.ToEnumerable().ToArray();

        if (embeddingCollection == null || !embeddingCollection.Any())
        {
            return AsyncEnumerable.Empty<(IEmbeddingWithMetadata<TEmbedding>, double)>();
        }

        EmbeddingReadOnlySpan<TEmbedding> embeddingSpan = new(embedding.AsReadOnlySpan());

        TopNCollection<IEmbeddingWithMetadata<TEmbedding>> embeddings = new(limit);

        foreach (var item in embeddingCollection)
        {
            if (item.Value != null)
            {
                EmbeddingReadOnlySpan<TEmbedding> itemSpan = new(item.Value.Embedding.AsReadOnlySpan());
                double similarity = embeddingSpan.CosineSimilarity(itemSpan);
                if (similarity >= minRelevanceScore)
                {
                    embeddings.Add(new(item.Value, similarity));
                }
            }
        }

        embeddings.SortByScore();

        return embeddings.Select(x => (x.Value, x.Score.Value)).ToAsyncEnumerable();
    }

    protected async IAsyncEnumerable<DataEntry<IEmbeddingWithMetadata<TEmbedding>>> TryGetCollectionAsync(string collectionName, [EnumeratorCancellation] CancellationToken cancel = default)
    {
        var container = this._client.GetContainer(this._databaseName, this._containerName);
        var query = new QueryDefinition($"SELECT * FROM c WHERE c.collectionId = @collectionName")
             .WithParameter("@collectionName", collectionName);

        var iterator = container.GetItemQueryIterator<CosmosDBMemoryRecord>(query);

        var items = await iterator.ReadNextAsync(cancel).ConfigureAwait(false);

        foreach (var item in items)
        {
            var embeddingHost = JsonConvert.DeserializeAnonymousType(
                item.EmbeddingString,
                new { Embedding = new { vector = new List<float>() } });

            var rec = MemoryRecord.FromJson(
                item.MetadataString,
                new Embedding<float>(embeddingHost.Embedding.vector));

            yield return DataEntry.Create<IEmbeddingWithMetadata<TEmbedding>>(
                rec.Metadata.Id,
                (IEmbeddingWithMetadata<TEmbedding>)rec,
                item.Timestamp);
        }
    }

    /// <inheritdoc />
    public async Task<DataEntry<IEmbeddingWithMetadata<TEmbedding>>> PutAsync(string collection, DataEntry<IEmbeddingWithMetadata<TEmbedding>> data, CancellationToken cancel = default)
    {
        var entity = new CosmosDBMemoryRecord
        {
            CollectionId = collection,
            Id = this._toCosmosFriendlyId(data.Key),
            Timestamp = data.Timestamp,
            EmbeddingString = data.ValueString!,
            MetadataString = data.Value!.GetSerializedMetadata()
        };

        var container = this._client.GetContainer(this._databaseName, this._containerName);

        await container.UpsertItemAsync(entity, cancellationToken: cancel, requestOptions: new ItemRequestOptions()
        {
            EnableContentResponseOnWrite = false,
        });

        return data;
    }

    /// <inheritdoc />
    public Task RemoveAsync(string collection, string key, CancellationToken cancel = default)
    {
        var container = this._client.GetContainer(this._databaseName, this._containerName);

        return container.DeleteItemAsync<CosmosDBMemoryRecord>(
            this._toCosmosFriendlyId(key),
            new Microsoft.Azure.Cosmos.PartitionKey(collection),
            cancellationToken: cancel);
    }

    private string _toCosmosFriendlyId(string id)
    {
        return $"{id.Trim().Replace(' ', '-').Replace('/', '_').Replace('\\', '_').Replace('?', '_').Replace('#', '_').ToUpperInvariant()}";
    }
}