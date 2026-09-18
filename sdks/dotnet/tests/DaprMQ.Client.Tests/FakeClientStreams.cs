using System.Threading.Channels;
using Grpc.Core;

namespace DaprMQ.Client.Tests;

/// <summary>
/// Client-side counterparts to the server-side FakeAsyncStreamReader/FakeServerStreamWriter in
/// DaprMQGrpcServiceConsumeSessionTests.cs - those fake the *server* half of the duplex stream
/// (IAsyncStreamReader/IServerStreamWriter used by the generated service base class); a client
/// test instead needs to fake the *client* half (IClientStreamWriter for requests read back by
/// the test, IAsyncStreamReader for responses fed into the client under test).
/// </summary>
internal sealed class FakeClientStreamWriter<T> : IClientStreamWriter<T>
{
    private readonly Channel<T> _written = Channel.CreateUnbounded<T>();

    public WriteOptions? WriteOptions { get; set; }

    public IReadOnlyList<T> WrittenSoFar => _written.Reader.ReadAllAsync().ToBlockingEnumerable().ToList();

    public Task WriteAsync(T message)
    {
        _written.Writer.TryWrite(message);
        return Task.CompletedTask;
    }

    public Task CompleteAsync()
    {
        _written.Writer.TryComplete();
        return Task.CompletedTask;
    }
}

internal sealed class FakeAsyncStreamReader<T> : IAsyncStreamReader<T> where T : class
{
    private readonly Channel<T> _channel = Channel.CreateUnbounded<T>();

    public T Current { get; private set; } = null!;

    public void Add(T item) => _channel.Writer.TryWrite(item);

    public void Complete() => _channel.Writer.TryComplete();

    public async Task<bool> MoveNext(CancellationToken cancellationToken)
    {
        if (await _channel.Reader.WaitToReadAsync(cancellationToken))
        {
            if (_channel.Reader.TryRead(out var item))
            {
                Current = item;
                return true;
            }
        }
        return false;
    }
}
