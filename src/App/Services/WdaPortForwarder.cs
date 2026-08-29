using IPhoneMirror.App.Interop;

namespace IPhoneMirror.App.Services;

/// <summary>
/// Bridges a loopback TCP listener to device port 8100 through the native
/// usbmux forwarder (iproxy semantics: one usbmux tunnel per accepted
/// connection, so HttpClient connection pooling maps to parallel tunnels).
/// The tunnel lives in iPhoneMirror.Core, which uses the same proven
/// usbmux client as the QuickTime capture pipeline. Device resolution
/// happens per connection inside Core, so the listener binds immediately
/// even while the phone is re-enumerating for QuickTime or after a replug.
/// </summary>
internal sealed class WdaPortForwarder : IAsyncDisposable
{
    internal const ushort WebDriverAgentPort = 8100;

    private readonly string _udid;
    private readonly ushort _devicePort;
    private ushort _localPort;
    private bool _started;

    internal WdaPortForwarder(string udid, ushort devicePort)
    {
        _udid = udid;
        _devicePort = devicePort;
    }

    internal int LocalPort { get; private set; }

    internal Task StartAsync(CancellationToken cancellationToken)
    {
        var result = NativeCore.MuxForwardStart(_udid, _devicePort, out var localPort);
        if (result != 0) throw new MuxForwardException(result);
        _localPort = localPort;
        LocalPort = localPort;
        _started = true;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        if (_started)
        {
            NativeCore.MuxForwardStop(_localPort);
            _started = false;
        }
        return ValueTask.CompletedTask;
    }
}

/// <summary>Carries the Core result code as a stable string identifier.</summary>
internal sealed class MuxForwardException : Exception
{
    internal MuxForwardException(int result) : base(result switch
    {
        -1 => "invalid_argument",
        -4 => "mux_unavailable",
        -6 => "device_not_found",
        _ => $"mux_forward_failed:{result}",
    })
    {
        Result = result;
    }

    internal int Result { get; }
}
