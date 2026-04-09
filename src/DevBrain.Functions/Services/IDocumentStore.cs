using DevBrain.Functions.Models;

namespace DevBrain.Functions.Services;

public interface IDocumentStore
{
    Task<BrainDocument> UpsertAsync(BrainDocument document);
    Task<BrainDocument?> GetAsync(string key, string project);
    Task<IReadOnlyList<BrainDocument>> ListAsync(string project, string? prefix = null);
    Task<IReadOnlyList<BrainDocument>> SearchAsync(string query, string project);
}
