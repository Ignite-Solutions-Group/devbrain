using System.Text;
using DevBrain.Functions.Models;

namespace DevBrain.Functions.Services;

public sealed class DocumentEditService : IDocumentEditService
{
    private const int PreviewContextChars = 120;

    private readonly IDocumentStore _store;

    public DocumentEditService(IDocumentStore store)
    {
        _store = store;
    }

    public async Task<EditPreviewResult> PreviewAsync(
        string key,
        string project,
        string oldText,
        string newText,
        int expectedOccurrences,
        bool caseSensitive)
    {
        var document = await _store.GetAsync(key, project);
        if (document is null)
        {
            return new EditPreviewResult
            {
                Key = key,
                Project = project,
                Found = false,
                ExpectedOccurrences = expectedOccurrences,
                WouldReplace = false,
                Message = $"Document not found: '{key}'"
            };
        }

        var currentHash = document.ContentHash ?? ContentHashing.ComputeSha256(document.Content);
        var matchIndexes = FindMatchIndexes(document.Content, oldText, caseSensitive);
        var matchCount = matchIndexes.Count;

        if (matchCount == 0)
        {
            return new EditPreviewResult
            {
                Key = key,
                Project = project,
                Found = true,
                MatchCount = 0,
                ExpectedOccurrences = expectedOccurrences,
                CurrentContentHash = currentHash,
                CurrentContentLength = document.Content.Length,
                WouldReplace = false,
                Message = $"No matches found for the provided text in '{key}'."
            };
        }

        if (matchCount != expectedOccurrences)
        {
            return new EditPreviewResult
            {
                Key = key,
                Project = project,
                Found = true,
                MatchCount = matchCount,
                ExpectedOccurrences = expectedOccurrences,
                Ambiguous = matchCount > 1,
                CurrentContentHash = currentHash,
                CurrentContentLength = document.Content.Length,
                WouldReplace = false,
                Message = $"Expected {expectedOccurrences} match(es), found {matchCount}. Refusing ambiguous edit."
            };
        }

        var replacedContent = ReplaceMatches(document.Content, oldText, newText, matchIndexes);

        return new EditPreviewResult
        {
            Key = key,
            Project = project,
            Found = true,
            MatchCount = matchCount,
            ExpectedOccurrences = expectedOccurrences,
            Ambiguous = false,
            WouldReplace = true,
            CurrentContentHash = currentHash,
            CurrentContentLength = document.Content.Length,
            ReplacementDelta = replacedContent.Length - document.Content.Length,
            PreviewBefore = BuildPreview(document.Content, matchIndexes[0], oldText.Length),
            PreviewAfter = BuildPreview(replacedContent, matchIndexes[0], newText.Length),
            Message = "Edit preview ready."
        };
    }

    public async Task<EditApplyResult> ApplyAsync(
        string key,
        string project,
        string oldText,
        string newText,
        int expectedOccurrences,
        bool caseSensitive,
        string expectedContentHash,
        string updatedBy)
    {
        var document = await _store.GetAsync(key, project);
        if (document is null)
        {
            return new EditApplyResult
            {
                Key = key,
                Project = project,
                Applied = false,
                Message = $"Document not found: '{key}'"
            };
        }

        var currentHash = document.ContentHash ?? ContentHashing.ComputeSha256(document.Content);
        if (!string.Equals(currentHash, expectedContentHash, StringComparison.OrdinalIgnoreCase))
        {
            return new EditApplyResult
            {
                Key = key,
                Project = project,
                Applied = false,
                PreviousContentHash = currentHash,
                Message = "Document changed since preview. Re-run PreviewEditDocument and try again."
            };
        }

        var matchIndexes = FindMatchIndexes(document.Content, oldText, caseSensitive);
        var matchCount = matchIndexes.Count;
        if (matchCount != expectedOccurrences)
        {
            return new EditApplyResult
            {
                Key = key,
                Project = project,
                Applied = false,
                MatchCount = matchCount,
                PreviousContentHash = currentHash,
                Message = matchCount == 0
                    ? $"No matches found for the provided text in '{key}'."
                    : $"Expected {expectedOccurrences} match(es), found {matchCount}. Refusing ambiguous edit."
            };
        }

        var replacedContent = ReplaceMatches(document.Content, oldText, newText, matchIndexes);
        var updated = new BrainDocument
        {
            Id = document.Id,
            Key = document.Key,
            Project = document.Project,
            Content = replacedContent,
            Tags = document.Tags,
            UpdatedAt = DateTimeOffset.UtcNow,
            UpdatedBy = updatedBy,
            Ttl = document.Ttl
        };

        var writeResult = await _store.ReplaceIfHashMatchesAsync(updated, expectedContentHash);
        if (!writeResult.Applied)
        {
            return new EditApplyResult
            {
                Key = key,
                Project = project,
                Applied = false,
                MatchCount = matchCount,
                ReplacedCount = 0,
                PreviousContentHash = writeResult.CurrentContentHash ?? currentHash,
                Message = writeResult.Message
            };
        }

        var saved = writeResult.Document!;
        return new EditApplyResult
        {
            Key = saved.Key,
            Project = saved.Project,
            Applied = true,
            MatchCount = matchCount,
            ReplacedCount = matchCount,
            PreviousContentHash = currentHash,
            NewContentHash = saved.ContentHash,
            NewContentLength = saved.ContentLength ?? saved.Content.Length,
            UpdatedAt = saved.UpdatedAt,
            UpdatedBy = saved.UpdatedBy,
            Message = "Edit applied."
        };
    }

    private static List<int> FindMatchIndexes(string content, string oldText, bool caseSensitive)
    {
        var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var indexes = new List<int>();
        var startIndex = 0;

        while (startIndex <= content.Length - oldText.Length)
        {
            var matchIndex = content.IndexOf(oldText, startIndex, comparison);
            if (matchIndex < 0)
            {
                break;
            }

            indexes.Add(matchIndex);
            startIndex = matchIndex + oldText.Length;
        }

        return indexes;
    }

    private static string ReplaceMatches(string content, string oldText, string newText, IReadOnlyList<int> matchIndexes)
    {
        if (matchIndexes.Count == 0)
        {
            return content;
        }

        var builder = new StringBuilder(content.Length + (newText.Length - oldText.Length) * matchIndexes.Count);
        var cursor = 0;

        foreach (var matchIndex in matchIndexes)
        {
            builder.Append(content, cursor, matchIndex - cursor);
            builder.Append(newText);
            cursor = matchIndex + oldText.Length;
        }

        builder.Append(content, cursor, content.Length - cursor);
        return builder.ToString();
    }

    private static string BuildPreview(string content, int matchIndex, int matchLength)
    {
        var previewStart = Math.Max(0, matchIndex - PreviewContextChars);
        var previewEnd = Math.Min(content.Length, matchIndex + matchLength + PreviewContextChars);
        var preview = content[previewStart..previewEnd];

        if (previewStart > 0)
        {
            preview = "..." + preview;
        }

        if (previewEnd < content.Length)
        {
            preview += "...";
        }

        return preview;
    }
}
