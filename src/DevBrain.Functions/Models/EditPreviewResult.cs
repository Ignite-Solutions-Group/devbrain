namespace DevBrain.Functions.Models;

public sealed class EditPreviewResult
{
    public string Key { get; set; } = string.Empty;

    public string Project { get; set; } = "default";

    public bool Found { get; set; }

    public int MatchCount { get; set; }

    public int ExpectedOccurrences { get; set; }

    public bool Ambiguous { get; set; }

    public bool WouldReplace { get; set; }

    public string? CurrentContentHash { get; set; }

    public int? CurrentContentLength { get; set; }

    public int ReplacementDelta { get; set; }

    public string? PreviewBefore { get; set; }

    public string? PreviewAfter { get; set; }

    public string Message { get; set; } = string.Empty;
}
