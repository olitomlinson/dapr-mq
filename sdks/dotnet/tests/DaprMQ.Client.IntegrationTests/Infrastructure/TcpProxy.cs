using System.Net;
using System.Net.Sockets;

namespace DaprMQ.Client.IntegrationTests.Infrastructure;

/// <summary>
/// Forwards a local port to the server so a test can break every open connection (QC-06) without
/// restarting a container, which would re-map its host ports.
/// </summary>
internal sealed class TcpProxy : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly Uri _target;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<TcpClient> _open = [];
    private readonly Task _accepting;

    public TcpProxy(string targetUrl)
    {
        _target = new Uri(targetUrl);
        _listener.Start();
        _accepting = AcceptAsync();
    }

    public string Url => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";

    /// <summary>Drops every connection open now; new ones are still accepted.</summary>
    public void BreakConnections()
    {
        lock (_open)
        {
            foreach (var client in _open)
            {
                client.Client.LingerState = new LingerOption(true, 0); // reset, not a clean close
                client.Dispose();
            }
            _open.Clear();
        }
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (true)
            {
                var inbound = await _listener.AcceptTcpClientAsync(_cts.Token);
                var outbound = new TcpClient();
                await outbound.ConnectAsync(_target.Host, _target.Port, _cts.Token);
                lock (_open)
                {
                    _open.Add(inbound);
                    _open.Add(outbound);
                }
                _ = PipeAsync(inbound, outbound);
                _ = PipeAsync(outbound, inbound);
            }
        }
        catch (Exception) when (_cts.IsCancellationRequested)
        {
        }
    }

    private static async Task PipeAsync(TcpClient from, TcpClient to)
    {
        try
        {
            await from.GetStream().CopyToAsync(to.GetStream());
        }
        catch (Exception)
        {
        }
        finally
        {
            from.Dispose();
            to.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        BreakConnections();
        await _accepting;
    }
}
