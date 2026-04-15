namespace DevBrain.Functions.Services;

public interface ITagEditService
{
    Task<TagEditResult> EditTagsAsync(
        string key,
        string project,
        string[] add,
        string[] remove,
        string updatedBy);
}
