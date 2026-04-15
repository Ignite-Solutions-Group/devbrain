namespace DevBrain.Functions.Services;

public sealed class TagEditResult
{
    public string Key { get; init; } = string.Empty;
    public string Project { get; init; } = string.Empty;
    public bool Found { get; init; }
    public bool Changed { get; init; }
    public string[] PreviousTags { get; init; } = [];
    public string[] Tags { get; init; } = [];
    public string[] Added { get; init; } = [];
    public string[] Removed { get; init; } = [];
    public DateTimeOffset? UpdatedAt { get; init; }
    public string? UpdatedBy { get; init; }
    public string Message { get; init; } = string.Empty;
}
