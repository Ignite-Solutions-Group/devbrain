using DevBrain.Functions.Models;

namespace DevBrain.Functions.Services;

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
