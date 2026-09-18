using System.Globalization;
using System.Text.Json;
using DaprMQ.Client.Exceptions;

// gRPC to the DaprMQ gateway is plaintext HTTP/2 (h2c) in local/demo deployments -
// Grpc.Net.Client needs this switch to allow an "http://" (non-TLS) channel target.
AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);

const string Language = "dotnet";
const string Role = "consumer";

var controlPort = 8080;
var controlPortEnv = Environment.GetEnvironmentVariable("CONTROL_PORT");
if (!string.IsNullOrEmpty(controlPortEnv) && int.TryParse(controlPortEnv, out var parsedPort))
{
    controlPort = parsedPort;
}

void Log(string level, string message)
{
    var ts = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
    Console.WriteLine($"[{Language}] [{Role}] {ts} {level} {message}");
}

string FormatTimestamp(DateTime utc) => utc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls($"http://+:{controlPort}");
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
});

var state = new AppState(Language, Role);

var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { status = "ok", language = Language, role = Role }));

app.MapGet("/config", () => Results.Ok(state.GetConfigDto()));

app.MapPut("/config", (ConfigUpdateRequest? body) =>
{
    var (ok, error) = state.TryUpdateConfig(body);
    if (!ok)
    {
        return Results.Json(
            new { error = new { code = "INVALID_CONFIG", message = error } },
            statusCode: StatusCodes.Status400BadRequest);
    }

    return Results.Ok(state.GetConfigDto());
});

app.MapPost("/reset", () =>
{
    state.Reset();
    return Results.Ok(new { success = true, message = "State reset to defaults" });
});

app.MapGet("/scenarios", () => Results.Ok(Scenarios.Descriptors));

app.MapPost("/scenarios/{name}/run", async (string name) =>
{
    if (!Scenarios.IsKnown(name))
    {
        return Results.Json(
            new { error = new { code = "UNKNOWN_SCENARIO", message = $"unknown scenario '{name}'" } },
            statusCode: StatusCodes.Status404NotFound);
    }

    if (!state.TryBeginRun())
    {
        return Results.Json(
            new { error = new { code = "SCENARIO_IN_PROGRESS", message = "a scenario run is already in progress on this pod" } },
            statusCode: StatusCodes.Status409Conflict);
    }

    try
    {
        var (client, queuePrefix) = state.Snapshot();
        var startedAt = DateTime.UtcNow;

        List<StepDto> steps;
        string queueId;
        try
        {
            (queueId, steps) = await Scenarios.RunAsync(name, client, queuePrefix, Log);
        }
        catch (DaprMQException ex)
        {
            Log("ERROR", $"scenario {name} failed: {ex.Message}");
            return Results.Json(
                new { error = new { code = "UPSTREAM_ERROR", message = ex.Message } },
                statusCode: StatusCodes.Status502BadGateway);
        }

        var finishedAt = DateTime.UtcNow;
        return Results.Ok(new ScenarioRunResult(
            name, Role, queueId, FormatTimestamp(startedAt), FormatTimestamp(finishedAt), steps));
    }
    finally
    {
        state.EndRun();
    }
});

app.Run();
