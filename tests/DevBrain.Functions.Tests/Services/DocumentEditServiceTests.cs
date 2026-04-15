using DevBrain.Functions.Models;
using DevBrain.Functions.Services;

namespace DevBrain.Functions.Tests.Services;

public sealed class DocumentEditServiceTests
{
    [Fact]
    public async Task PreviewAsync_SingleExactMatch_ReturnsPreviewAndHash()
    {
        var store = new FakeDocumentStore(new BrainDocument
        {
            Id = "state:current",
            Key = "state:current",
            Project = "devbrain",
            Content = "Status: draft\nOwner: Derek",
            Tags = ["state"],
            UpdatedAt = DateTimeOffset.UtcNow,
            UpdatedBy = "seed"
        });
        var service = new DocumentEditService(store);

        var result = await service.PreviewAsync(
            "state:current",
            "devbrain",
            "Status: draft",
            "Status: in progress",
            expectedOccurrences: 1,
            caseSensitive: true);

        Assert.True(result.Found);
        Assert.True(result.WouldReplace);
        Assert.False(result.Ambiguous);
        Assert.Equal(1, result.MatchCount);
        Assert.Equal(ContentHashing.ComputeSha256("Status: draft\nOwner: Derek"), result.CurrentContentHash);
        Assert.Contains("Status: draft", result.PreviewBefore);
        Assert.Contains("Status: in progress", result.PreviewAfter);
    }

    [Fact]
    public async Task PreviewAsync_MultipleMatchesWithSingleExpectation_RefusesAmbiguousEdit()
    {
        var store = new FakeDocumentStore(new BrainDocument
        {
            Id = "state:current",
            Key = "state:current",
            Project = "devbrain",
            Content = "draft\ndraft\nfinal",
            UpdatedAt = DateTimeOffset.UtcNow,
            UpdatedBy = "seed"
        });
        var service = new DocumentEditService(store);

        var result = await service.PreviewAsync(
            "state:current",
            "devbrain",
            "draft",
            "published",
            expectedOccurrences: 1,
            caseSensitive: true);

        Assert.True(result.Found);
        Assert.False(result.WouldReplace);
        Assert.True(result.Ambiguous);
        Assert.Equal(2, result.MatchCount);
    }

    [Fact]
    public async Task ApplyAsync_WithMatchingHash_ReplacesContentAndPreservesTags()
    {
        var originalContent = "Status: draft\nOwner: Derek";
        var store = new FakeDocumentStore(new BrainDocument
        {
            Id = "state:current",
            Key = "state:current",
            Project = "devbrain",
            Content = originalContent,
            Tags = ["state", "current"],
            UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            UpdatedBy = "seed"
        });
        var service = new DocumentEditService(store);

        var result = await service.ApplyAsync(
            "state:current",
            "devbrain",
            "Status: draft",
            "Status: in progress",
            expectedOccurrences: 1,
            caseSensitive: true,
            expectedContentHash: ContentHashing.ComputeSha256(originalContent),
            updatedBy: "agent@example.com");

        Assert.True(result.Applied);
        Assert.Equal(1, result.ReplacedCount);
        Assert.Equal(ContentHashing.ComputeSha256(originalContent), result.PreviousContentHash);
        Assert.Equal(ContentHashing.ComputeSha256("Status: in progress\nOwner: Derek"), result.NewContentHash);

        var saved = await store.GetAsync("state:current", "devbrain");
        Assert.NotNull(saved);
        Assert.Equal("Status: in progress\nOwner: Derek", saved.Content);
        Assert.Equal(["state", "current"], saved.Tags);
        Assert.Equal("agent@example.com", saved.UpdatedBy);
    }

    [Fact]
    public async Task ApplyAsync_WithStaleHash_RefusesWrite()
    {
        var store = new FakeDocumentStore(new BrainDocument
        {
            Id = "state:current",
            Key = "state:current",
            Project = "devbrain",
            Content = "Status: draft",
            UpdatedAt = DateTimeOffset.UtcNow,
            UpdatedBy = "seed"
        });
        var service = new DocumentEditService(store);

        var result = await service.ApplyAsync(
            "state:current",
            "devbrain",
            "Status: draft",
            "Status: in progress",
            expectedOccurrences: 1,
            caseSensitive: true,
            expectedContentHash: ContentHashing.ComputeSha256("Status: stale"),
            updatedBy: "agent@example.com");

        Assert.False(result.Applied);
        Assert.Equal(ContentHashing.ComputeSha256("Status: draft"), result.PreviousContentHash);

        var saved = await store.GetAsync("state:current", "devbrain");
        Assert.NotNull(saved);
        Assert.Equal("Status: draft", saved.Content);
    }

    [Fact]
    public async Task ApplyAsync_ReplacementContainingOldText_ReplacesOnlyExpectedMatches()
    {
        var store = new FakeDocumentStore(new BrainDocument
        {
            Id = "state:current",
            Key = "state:current",
            Project = "devbrain",
            Content = "draft once",
            UpdatedAt = DateTimeOffset.UtcNow,
            UpdatedBy = "seed"
        });
        var service = new DocumentEditService(store);

        var result = await service.ApplyAsync(
            "state:current",
            "devbrain",
            "draft",
            "draft v2",
            expectedOccurrences: 1,
            caseSensitive: true,
            expectedContentHash: ContentHashing.ComputeSha256("draft once"),
            updatedBy: "agent@example.com");

        Assert.True(result.Applied);

        var saved = await store.GetAsync("state:current", "devbrain");
        Assert.NotNull(saved);
        Assert.Equal("draft v2 once", saved.Content);
    }

    private sealed class FakeDocumentStore : IDocumentStore
    {
        private readonly Dictionary<(string Key, string Project), BrainDocument> _documents = new();

        public FakeDocumentStore(params BrainDocument[] documents)
        {
            foreach (var document in documents)
            {
                document.ContentHash ??= ContentHashing.ComputeSha256(document.Content);
                document.ContentLength ??= document.Content.Length;
                _documents[(document.Key, document.Project)] = Clone(document);
            }
        }

        public Task<BrainDocument> UpsertAsync(BrainDocument document)
        {
            document.Id = string.IsNullOrEmpty(document.Id) ? document.Key.Replace('/', ':') : document.Id;
            document.ContentHash = ContentHashing.ComputeSha256(document.Content);
            document.ContentLength = document.Content.Length;
            _documents[(document.Key, document.Project)] = Clone(document);
            return Task.FromResult(Clone(document));
        }

        public Task<ConditionalWriteResult> ReplaceIfHashMatchesAsync(BrainDocument document, string expectedContentHash)
        {
            if (!_documents.TryGetValue((document.Key, document.Project), out var existing))
            {
                return Task.FromResult(new ConditionalWriteResult(false, null, null, $"Document not found: '{document.Key}'"));
            }

            var currentHash = existing.ContentHash ?? ContentHashing.ComputeSha256(existing.Content);
            if (!string.Equals(currentHash, expectedContentHash, StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(new ConditionalWriteResult(
                    false,
                    currentHash,
                    Clone(existing),
                    "Document changed since preview. Re-run PreviewEditDocument and try again."));
            }

            document.Id = existing.Id;
            document.ContentHash = ContentHashing.ComputeSha256(document.Content);
            document.ContentLength = document.Content.Length;
            _documents[(document.Key, document.Project)] = Clone(document);

            return Task.FromResult(new ConditionalWriteResult(
                true,
                currentHash,
                Clone(document),
                "Write applied."));
        }

        public Task<BrainDocument?> GetAsync(string key, string project)
        {
            _documents.TryGetValue((key, project), out var document);
            return Task.FromResult(document is null ? null : Clone(document));
        }

        public Task<IReadOnlyList<BrainDocument>> ListAsync(string project, string? prefix = null) =>
            Task.FromResult<IReadOnlyList<BrainDocument>>([]);

        public Task<IReadOnlyList<BrainDocument>> SearchAsync(string query, string project) =>
            Task.FromResult<IReadOnlyList<BrainDocument>>([]);

        public Task<BrainDocument?> GetMetadataAsync(string key, string project)
        {
            _documents.TryGetValue((key, project), out var document);
            if (document is null)
            {
                return Task.FromResult<BrainDocument?>(null);
            }

            return Task.FromResult<BrainDocument?>(new BrainDocument
            {
                Id = document.Id,
                Key = document.Key,
                Project = document.Project,
                Tags = [.. document.Tags],
                UpdatedAt = document.UpdatedAt,
                UpdatedBy = document.UpdatedBy,
                ContentHash = document.ContentHash,
                ContentLength = document.ContentLength
            });
        }

        public Task<int> TouchAllAsync() => Task.FromResult(_documents.Count);

        public Task<bool> DeleteAsync(string key, string project) =>
            Task.FromResult(_documents.Remove((key, project)));

        public Task<BrainDocument> AppendAsync(string key, string project, string content, string separator, string[] tags, string updatedBy) =>
            throw new NotSupportedException();

        public Task<ChunkedUpsertResult> UpsertChunkAsync(string key, string project, string content, int chunkIndex, int totalChunks, string[] tags, string updatedBy) =>
            throw new NotSupportedException();

        private static BrainDocument Clone(BrainDocument document) =>
            new()
            {
                Id = document.Id,
                Key = document.Key,
                Project = document.Project,
                Content = document.Content,
                Tags = [.. document.Tags],
                UpdatedAt = document.UpdatedAt,
                ContentHash = document.ContentHash,
                ContentLength = document.ContentLength,
                UpdatedBy = document.UpdatedBy,
                Ttl = document.Ttl
            };
    }
}
