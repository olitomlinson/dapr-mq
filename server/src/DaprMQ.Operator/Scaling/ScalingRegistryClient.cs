using Dapr.Client;
using DaprMQ.Interfaces;

namespace DaprMQ.Operator.Scaling;

/// <summary>The scaling registry on the workers: written by the scaler, read by the console.</summary>
public interface IScalingRegistryClient
{
    Task RegisterAsync(IReadOnlyList<ScaledObjectRegistration> registrations, CancellationToken cancellationToken);

    Task<IReadOnlyList<ScaledObjectRegistration>> ListAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Calls the workers' scaling registry endpoints via Dapr service invocation, like every other
/// operator read. The list is read from committed state, so the console's sampler never activates
/// the registry actor.
/// </summary>
public sealed class DaprScalingRegistryClient : IScalingRegistryClient
{
    private readonly DaprClient _daprClient;
    private readonly string _workerAppId;

    public DaprScalingRegistryClient(DaprClient daprClient, string workerAppId)
    {
        _daprClient = daprClient ?? throw new ArgumentNullException(nameof(daprClient));
        _workerAppId = workerAppId ?? throw new ArgumentNullException(nameof(workerAppId));
    }

    public Task RegisterAsync(IReadOnlyList<ScaledObjectRegistration> registrations, CancellationToken cancellationToken) =>
        _daprClient.InvokeMethodAsync(HttpMethod.Post, _workerAppId, "internal/scaling/registrations",
            new RegisterScaledObjectsRequest { Registrations = [.. registrations] }, cancellationToken);

    public async Task<IReadOnlyList<ScaledObjectRegistration>> ListAsync(CancellationToken cancellationToken) =>
        (await _daprClient.InvokeMethodAsync<ScaledObjectRegistrations>(HttpMethod.Get, _workerAppId, "internal/admin/scaling", cancellationToken)).Registrations;
}
