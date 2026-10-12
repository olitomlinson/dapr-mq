# DaprMQ.Client

.NET client SDK for [DaprMQ](https://github.com/olitomlinson/dapr-mq), a FIFO queue built on Dapr actors.

```sh
dotnet add package DaprMQ.Client --prerelease
```

```csharp
using DaprMQ.Client;

await using var client = new DaprMQClient(new DaprMQClientOptions
{
    HttpBaseAddress = new Uri("http://localhost:8002/"),
    GrpcAddress = "http://localhost:8003"
});
await client.EnqueueAsync("orders", [new EnqueueItemDto(new { Id = 1 })]);
```

DaprMQ is pre-release: versions are `0.0.0-alpha.N`, so `dotnet add package` needs `--prerelease`.

See the [SDK guide](https://github.com/olitomlinson/dapr-mq/blob/main/sdks/dotnet/docs/CLIENT_SDK.md) for
consumers, sessions, retries and the full API. Licensed under Apache-2.0.
