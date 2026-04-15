using DevBrain.Functions.Models;
using DevBrain.Functions.Services;

namespace DevBrain.Functions.Tests.Services;

public sealed class TagEditServiceTests
{
    [Fact]
    public async Task EditTagsAsync_AddsNewTags_PreservesExistingOrderAndContent()
    {
        var store = new FakeDocumentStore(Seed("state:current", "devbrain", "keep this body", ["alpha", "beta"]));
        var service = new TagEditService(store);

        var result = await service.EditTagsAsync(
            "state:current",
            "devbrain",
            add: ["gamma", "beta"], // beta already present → no-op on that one
            remove: [],
            updatedBy: "agent@example.com");

        Assert.True(result.Found);
        Assert.True(result.Changed);
        Assert.Equal(["alpha", "beta", "gamma"], result.Tags);
        Assert.Equal(["gamma"], result.Added);
        Assert.Empty(result.Removed);
        Assert.Equal("agent@example.com", result.UpdatedBy);

        var saved = await store.GetAsync("state:current", "devbrain");
        Assert.NotNull(saved);
        Assert.Equal("keep this body", saved.Content); // content untouched
        Assert.Equal(["alpha", "beta", "gamma"], saved.Tags);
    }

    [Fact]
    public async Task EditTagsAsync_RemovesTags_IgnoresMissingOnes()
    {
        var store = new FakeDocumentStore(Seed("state:current", "devbrain", "body", ["alpha", "beta", "gamma"]));
        var service = new TagEditService(store);

        var result = await service.EditTagsAsync(
            "state:current",
            "devbrain",
            add: [],
            remove: ["beta", "never-was-here"],
            updatedBy: "agent@example.com");

        Assert.True(result.Changed);
        Assert.Equal(["alpha", "gamma"], result.Tags);
        Assert.Equal(["beta"], result.Removed);
        Assert.Empty(result.Added);
    }

    [Fact]
    public async Task EditTagsAsync_AddAndRemoveInSameCall_AppliesBoth()
    {
        var store = new FakeDocumentStore(Seed("state:current", "devbrain", "body", ["alpha", "beta"]));
        var service = new TagEditService(store);

        var result = await service.EditTagsAsync(
            "state:current",
            "devbrain",
            add: ["gamma"],
            remove: ["alpha"],
            updatedBy: "agent@example.com");

        Assert.True(result.Changed);
        Assert.Equal(["beta", "gamma"], result.Tags);
        Assert.Equal(["gamma"], result.Added);
        Assert.Equal(["alpha"], result.Removed);
    }

    [Fact]
    public async Task EditTagsAsync_TagInBothAddAndRemove_RejectsAndDoesNotWrite()
    {
        var store = new FakeDocumentStore(Seed("state:current", "devbrain", "body", ["alpha"]));
        var service = new TagEditService(store);

        var result = await service.EditTagsAsync(
            "state:current",
            "devbrain",
            add: ["beta"],
            remove: ["beta"],
            updatedBy: "agent@example.com");

        Assert.False(result.Found);
        Assert.False(result.Changed);
        Assert.Contains("both 'add' and 'remove'", result.Message);

        var saved = await store.GetAsync("state:current", "devbrain");
        Assert.NotNull(saved);
        Assert.Equal(["alpha"], saved.Tags);
    }

    [Fact]
    public async Task EditTagsAsync_EmptyAddAndRemove_ReturnsNoopWithoutWriting()
    {
        var store = new FakeDocumentStore(Seed("state:current", "devbrain", "body", ["alpha"]));
        var service = new TagEditService(store);

        var result = await service.EditTagsAsync(
            "state:current",
            "devbrain",
            add: [],
            remove: [],
            updatedBy: "agent@example.com");

        Assert.False(result.Changed);
        Assert.Contains("nothing to do", result.Message);
        Assert.Equal(0, store.UpsertCount);
    }

    [Fact]
    public async Task EditTagsAsync_NoEffectiveChange_DoesNotWrite()
    {
        var store = new FakeDocumentStore(Seed("state:current", "devbrain", "body", ["alpha", "beta"]));
        var service = new TagEditService(store);

        var result = await service.EditTagsAsync(
            "state:current",
            "devbrain",
            add: ["alpha"], // already present
            remove: ["never-was-here"], // absent
            updatedBy: "agent@example.com");

        Assert.True(result.Found);
        Assert.False(result.Changed);
        Assert.Equal(["alpha", "beta"], result.Tags);
        Assert.Equal(0, store.UpsertCount);
    }

    [Fact]
    public async Task EditTagsAsync_DocumentNotFound_ReturnsNotFound()
    {
        var store = new FakeDocumentStore();
        var service = new TagEditService(store);

        var result = await service.EditTagsAsync(
            "state:missing",
            "devbrain",
            add: ["alpha"],
            remove: [],
            updatedBy: "agent@example.com");

        Assert.False(result.Found);
        Assert.False(result.Changed);
        Assert.Contains("not found", result.Message);
    }

    [Fact]
    public async Task EditTagsAsync_DedupesAndIgnoresBlanksInInput()
    {
        var store = new FakeDocumentStore(Seed("state:current", "devbrain", "body", []));
        var service = new TagEditService(store);

        var result = await service.EditTagsAsync(
            "state:current",
            "devbrain",
            add: ["alpha", "alpha", "  ", "", "beta"],
            remove: [],
            updatedBy: "agent@example.com");

        Assert.True(result.Changed);
        Assert.Equal(["alpha", "beta"], result.Tags);
    }

    private static BrainDocument Seed(string key, string project, string content, string[] tags) => new()
    {
        Id = key,
        Key = key,
        Project = project,
        Content = content,
        Tags = tags,
        UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
        UpdatedBy = "seed"
    };

    private sealed class FakeDocumentStore : IDocumentStore
    {
        private readonly Dictionary<(string Key, string Project), BrainDocument> _documents = new();
        public int UpsertCount { get; private set; }

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
            UpsertCount++;
            document.Id = string.IsNullOrEmpty(document.Id) ? document.Key : document.Id;
            document.ContentHash = ContentHashing.ComputeSha256(document.Content);
            document.ContentLength = document.Content.Length;
            _documents[(document.Key, document.Project)] = Clone(document);
            return Task.FromResult(Clone(document));
        }

        public Task<BrainDocument?> GetAsync(string key, string project)
        {
            _documents.TryGetValue((key, project), out var document);
            return Task.FromResult(document is null ? null : Clone(document));
        }

        public Task<ConditionalWriteResult> ReplaceIfHashMatchesAsync(BrainDocument document, string expectedContentHash) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<BrainDocument>> ListAsync(string project, string? prefix = null) =>
            Task.FromResult<IReadOnlyList<BrainDocument>>([]);

        public Task<IReadOnlyList<BrainDocument>> SearchAsync(string query, string project) =>
            Task.FromResult<IReadOnlyList<BrainDocument>>([]);

        public Task<BrainDocument?> GetMetadataAsync(string key, string project) =>
            GetAsync(key, project);

        public Task<int> TouchAllAsync() => Task.FromResult(_documents.Count);

        public Task<bool> DeleteAsync(string key, string project) =>
            Task.FromResult(_documents.Remove((key, project)));

        public Task<BrainDocument> AppendAsync(string key, string project, string content, string separator, string[] tags, string updatedBy) =>
            throw new NotSupportedException();

        public Task<ChunkedUpsertResult> UpsertChunkAsync(string key, string project, string content, int chunkIndex, int totalChunks, string[] tags, string updatedBy) =>
            throw new NotSupportedException();

        private static BrainDocument Clone(BrainDocument document) => new()
        {
            Id = document.Id,
            Key = document.Key,
            Project = document.Project,
            Content = document.Content,
            Tags = [.. document.Tags],
            UpdatedAt = document.UpdatedAt,
            UpdatedBy = document.UpdatedBy,
            ContentHash = document.ContentHash,
            ContentLength = document.ContentLength,
            Ttl = document.Ttl
        };
    }
}
