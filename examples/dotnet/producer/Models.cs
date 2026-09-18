public record ConfigDto(
    string HttpBaseUrl,
    string GrpcAddress,
    string QueuePrefix,
    string Language,
    string Role,
    string Source);

public record ConfigUpdateRequest(
    string? HttpBaseUrl,
    string? GrpcAddress,
    string? QueuePrefix);

public record ScenarioDescriptor(string Name, string Role, string Description);

public record StepDto(string Action, string Detail);

public record ScenarioRunResult(
    string Scenario,
    string Role,
    string QueueId,
    string StartedAt,
    string FinishedAt,
    IReadOnlyList<StepDto> Steps);
