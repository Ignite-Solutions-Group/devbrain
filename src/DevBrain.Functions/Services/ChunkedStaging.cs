using System.Text.Json;
using System.Text.Json.Serialization;

namespace DevBrain.Functions.Services;

/// <summary>
/// In-memory model of a chunked-upload staging document's payload. The payload is
/// stored as JSON in the staging document's <c>content</c> field so the existing
/// upsert/get/delete plumbing can manage it without any schema changes. Chunks are
/// tracked by index to support out-of-order arrival.
/// </summary>
internal sealed class ChunkedStaging
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public int TotalChunks { get; }
    public IReadOnlyDictionary<int, string> Chunks { get; }

    private ChunkedStaging(int totalChunks, IReadOnlyDictionary<int, string> chunks)
    {
        TotalChunks = totalChunks;
        Chunks = chunks;
    }

    public int ChunksReceived => Chunks.Count;

    public bool IsComplete =>
        TotalChunks > 0 && Chunks.Count == TotalChunks && AllIndicesPresent();

    public static ChunkedStaging Empty(int totalChunks)
        => new(totalChunks, new Dictionary<int, string>());

    public static string EmptyContent(int totalChunks)
        => Empty(totalChunks).Serialize();

    public static ChunkedStaging Parse(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return Empty(0);
        }

        var parsed = JsonSerializer.Deserialize<StagingPayload>(content, SerializerOptions);
        if (parsed is null)
        {
            return Empty(0);
        }

        var dict = new Dictionary<int, string>();
        if (parsed.Chunks is not null)
        {
            foreach (var chunk in parsed.Chunks)
            {
                dict[chunk.Index] = chunk.Content;
            }
        }

        return new ChunkedStaging(parsed.TotalChunks, dict);
    }

    public ChunkedStaging WithChunk(int index, string content)
    {
        var next = new Dictionary<int, string>(Chunks)
        {
            [index] = content
        };
        return new ChunkedStaging(TotalChunks, next);
    }

    public string Serialize()
    {
        var payload = new StagingPayload
        {
            TotalChunks = TotalChunks,
            Chunks = Chunks
                .OrderBy(kv => kv.Key)
                .Select(kv => new StagingChunk { Index = kv.Key, Content = kv.Value })
                .ToArray()
        };
        return JsonSerializer.Serialize(payload, SerializerOptions);
    }

    public string Concatenate()
    {
        if (!IsComplete)
        {
            throw new InvalidOperationException(
                $"Cannot concatenate staging with {ChunksReceived}/{TotalChunks} chunks present.");
        }

        var ordered = Enumerable.Range(0, TotalChunks).Select(i => Chunks[i]);
        return string.Concat(ordered);
    }

    private bool AllIndicesPresent()
    {
        for (var i = 0; i < TotalChunks; i++)
        {
            if (!Chunks.ContainsKey(i))
            {
                return false;
            }
        }
        return true;
    }

    private sealed class StagingPayload
    {
        [JsonPropertyName("totalChunks")]
        public int TotalChunks { get; set; }

        [JsonPropertyName("chunks")]
        public StagingChunk[]? Chunks { get; set; }
    }

    private sealed class StagingChunk
    {
        [JsonPropertyName("index")]
        public int Index { get; set; }

        [JsonPropertyName("content")]
        public string Content { get; set; } = string.Empty;
    }
}
