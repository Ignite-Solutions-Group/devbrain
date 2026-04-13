using DevBrain.Functions.Models;

namespace DevBrain.Functions.Services;

public interface IDocumentStore
{
    Task<BrainDocument> UpsertAsync(BrainDocument document);
    Task<BrainDocument?> GetAsync(string key, string project);
    Task<IReadOnlyList<BrainDocument>> ListAsync(string project, string? prefix = null);
    Task<IReadOnlyList<BrainDocument>> SearchAsync(string query, string project);

    /// <summary>
    /// Returns document metadata (key, project, tags, timestamps, contentHash,
    /// contentLength) without the content body. Returns null when the document
    /// does not exist. Cheap on both RU cost and caller token budget.
    /// </summary>
    Task<BrainDocument?> GetMetadataAsync(string key, string project);

    /// <summary>
    /// Deletes a single document by key within a project. Idempotent: returns false
    /// when the document does not exist. Accepts both colon and slash keys to support
    /// cleanup of legacy slash-keyed documents.
    /// </summary>
    Task<bool> DeleteAsync(string key, string project);

    /// <summary>
    /// Appends content to an existing document, or creates it if missing. New content
    /// is joined to the existing body with <paramref name="separator"/>. Tags are unioned
    /// (not replaced) when appending. Uses Cosmos ETag optimistic concurrency to serialize
    /// concurrent appenders.
    /// </summary>
    Task<BrainDocument> AppendAsync(
        string key,
        string project,
        string content,
        string separator,
        string[] tags,
        string updatedBy);

    /// <summary>
    /// Stages a single chunk of a multi-part upload and, when all chunks have arrived,
    /// finalizes the document atomically (concatenate, upsert real key, delete staging).
    /// </summary>
    Task<ChunkedUpsertResult> UpsertChunkAsync(
        string key,
        string project,
        string content,
        int chunkIndex,
        int totalChunks,
        string[] tags,
        string updatedBy);
}

public sealed record ChunkedUpsertResult(
    string Status,             // "staged" | "finalized"
    int ChunksReceived,
    int TotalChunks,
    BrainDocument? Document);  // populated only on "finalized"
