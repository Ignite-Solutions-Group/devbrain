namespace DevBrain.Functions.Models;

public sealed class EditApplyResult
{
    public string Key { get; set; } = string.Empty;

    public string Project { get; set; } = "default";

    public bool Applied { get; set; }

    public int MatchCount { get; set; }

    public int ReplacedCount { get; set; }

    public string? PreviousContentHash { get; set; }

    public string? NewContentHash { get; set; }

    public int? NewContentLength { get; set; }

    public DateTimeOffset? UpdatedAt { get; set; }

    public string? UpdatedBy { get; set; }

    public string Message { get; set; } = string.Empty;
}
