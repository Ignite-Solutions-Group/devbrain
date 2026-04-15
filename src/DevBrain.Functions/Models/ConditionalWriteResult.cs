namespace DevBrain.Functions.Models;

public sealed record ConditionalWriteResult(
    bool Applied,
    string? CurrentContentHash,
    BrainDocument? Document,
    string Message);
