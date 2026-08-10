namespace DevBrain.Core.Models;

public sealed record ConditionalWriteResult(
    bool Applied,
    string? CurrentContentHash,
    BrainDocument? Document,
    string Message);
