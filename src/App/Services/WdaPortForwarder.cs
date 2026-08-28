using System.IO;
using System.Net;
using System.Net.Sockets;

namespace IPhoneMirror.App.Services;

/// <summary>
/// Bridges local loopback TCP connections to a device port through usbmuxd,
/// the same role iproxy plays: every accepted client gets its own
/// usbmux Connect tunnel, so HttpClient connection pooling simply maps to
/// parallel device tunnels.
/// </summary>
internal sealed class WdaPortForwarder : IAsyncDisposable
{
    private const int MaxConcurrentBridges = 8;
    private readonly UsbmuxTunnelClient.MuxDeviceLocation _device;
    private readonly ushort _devicePort;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly object _bridgeSync = new();
    private readonly List<Task> _bridges = [];
    private TcpListener? _listener;
    private Task? _acceptLoop;

    internal WdaPortForwarder(
        UsbmuxTunnelClient.MuxDeviceLocation device, ushort devicePort)
    {
        _device = device;
        _devicePort = devicePort;
    }

    internal int LocalPort { get; private set; }

    internal Task StartAsync(CancellationToken cancellationToken)
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        LocalPort = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = AcceptLoopAsync(_cancellation.Token);
        return Task.CompletedTask;
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener!.AcceptTcpClientAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception error) when (error is OperationCanceledException
                or SocketException or ObjectDisposedException or IOException)
            {
                break;
            }
            lock (_bridgeSync)
            {
                _bridges.RemoveAll(static task => task.IsCompleted);
                if (_bridges.Count >= MaxConcurrentBridges)
                {
                    client.Dispose();
                    continue;
                }
                _bridges.Add(Task.Run(() => BridgeAsync(client),
                    CancellationToken.None));
            }
        }
    }

    private async Task BridgeAsync(TcpClient accepted)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            _cancellation.Token);
        try
        {
            using var tunnel = await UsbmuxTunnelClient.ConnectDevicePortAsync(
                _device, _devicePort, linked.Token).ConfigureAwait(false);
            using var _ = accepted;
            var clientStream = accepted.GetStream();
            var upstream = CopyAsync(clientStream, tunnel.Stream, linked.Token);
            var downstream = CopyAsync(tunnel.Stream, clientStream, linked.Token);
            await Task.WhenAny(upstream, downstream).ConfigureAwait(false);
            linked.Cancel();
        }
        catch
        {
            // Tunnel setup or I/O failure simply ends this bridge; the HTTP
            // client retries with a fresh connection on its next request.
        }
        finally
        {
            linked.Dispose();
        }
    }

    private static async Task CopyAsync(
        Stream source, Stream destination, CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        try
        {
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken)
                .ConfigureAwait(false)) > 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                    .ConfigureAwait(false);
                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            // Either side closing ends the bridge; the other direction observes it.
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cancellation.Cancel();
        try
        {
            _listener?.Stop();
        }
        catch
        {
            // Listener already stopped or never started.
        }
        if (_acceptLoop is not null)
        {
            try
            {
                await _acceptLoop.ConfigureAwait(false);
            }
            catch
            {
                // Accept loop failures are expected during teardown.
            }
        }
        Task[] pending;
        lock (_bridgeSync)
        {
            pending = [.. _bridges];
            _bridges.Clear();
        }
        foreach (var bridge in pending)
        {
            try
            {
                await bridge.ConfigureAwait(false);
            }
            catch
            {
                // Bridges are best-effort during teardown.
            }
        }
        _cancellation.Dispose();
    }
}
