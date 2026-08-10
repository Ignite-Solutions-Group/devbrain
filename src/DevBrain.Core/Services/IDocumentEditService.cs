using DevBrain.Core.Models;

namespace DevBrain.Core.Services;

public interface IDocumentEditService
{
    Task<EditPreviewResult> PreviewAsync(
        string key,
        string project,
        string oldText,
        string newText,
        int expectedOccurrences,
        bool caseSensitive);

    Task<EditApplyResult> ApplyAsync(
        string key,
        string project,
        string oldText,
        string newText,
        int expectedOccurrences,
        bool caseSensitive,
        string expectedContentHash,
        string updatedBy);
}
