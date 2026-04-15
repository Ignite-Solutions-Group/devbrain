using DevBrain.Functions.Models;

namespace DevBrain.Functions.Services;

public sealed class TagEditService : ITagEditService
{
    private readonly IDocumentStore _store;

    public TagEditService(IDocumentStore store)
    {
        _store = store;
    }

    public async Task<TagEditResult> EditTagsAsync(
        string key,
        string project,
        string[] add,
        string[] remove,
        string updatedBy)
    {
        var addSet = Normalize(add);
        var removeSet = Normalize(remove);

        var conflicts = addSet.Intersect(removeSet, StringComparer.Ordinal).ToArray();
        if (conflicts.Length > 0)
        {
            return new TagEditResult
            {
                Key = key,
                Project = project,
                Message = $"Tag(s) appear in both 'add' and 'remove': {string.Join(", ", conflicts)}. A tag cannot be added and removed in the same call."
            };
        }

        if (addSet.Length == 0 && removeSet.Length == 0)
        {
            return new TagEditResult
            {
                Key = key,
                Project = project,
                Message = "'add' and 'remove' are both empty — nothing to do."
            };
        }

        var document = await _store.GetAsync(key, project);
        if (document is null)
        {
            return new TagEditResult
            {
                Key = key,
                Project = project,
                Found = false,
                Message = $"Document not found: '{key}'"
            };
        }

        var existing = document.Tags ?? [];
        var removeLookup = new HashSet<string>(removeSet, StringComparer.Ordinal);
        var existingLookup = new HashSet<string>(existing, StringComparer.Ordinal);

        var resulting = new List<string>(existing.Length + addSet.Length);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tag in existing)
        {
            if (removeLookup.Contains(tag)) continue;
            if (seen.Add(tag)) resulting.Add(tag);
        }
        foreach (var tag in addSet)
        {
            if (seen.Add(tag)) resulting.Add(tag);
        }
        var newTags = resulting.ToArray();

        var actuallyAdded = addSet.Where(t => !existingLookup.Contains(t)).ToArray();
        var actuallyRemoved = removeSet.Where(existingLookup.Contains).ToArray();

        if (actuallyAdded.Length == 0 && actuallyRemoved.Length == 0)
        {
            return new TagEditResult
            {
                Key = document.Key,
                Project = document.Project,
                Found = true,
                Changed = false,
                PreviousTags = existing,
                Tags = existing,
                UpdatedAt = document.UpdatedAt,
                UpdatedBy = document.UpdatedBy,
                Message = "No tag changes needed — document already matches the requested state."
            };
        }

        var updated = new BrainDocument
        {
            Id = document.Id,
            Key = document.Key,
            Project = document.Project,
            Content = document.Content,
            Tags = newTags,
            UpdatedAt = DateTimeOffset.UtcNow,
            UpdatedBy = updatedBy,
            Ttl = document.Ttl
        };

        var saved = await _store.UpsertAsync(updated);

        return new TagEditResult
        {
            Key = saved.Key,
            Project = saved.Project,
            Found = true,
            Changed = true,
            PreviousTags = existing,
            Tags = saved.Tags,
            Added = actuallyAdded,
            Removed = actuallyRemoved,
            UpdatedAt = saved.UpdatedAt,
            UpdatedBy = saved.UpdatedBy,
            Message = $"Tags updated ({actuallyAdded.Length} added, {actuallyRemoved.Length} removed)."
        };
    }

    private static string[] Normalize(string[]? tags)
    {
        if (tags is null || tags.Length == 0) return [];
        return tags
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }
}
