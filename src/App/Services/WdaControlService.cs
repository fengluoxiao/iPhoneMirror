using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace IPhoneMirror.App.Services;

internal enum WdaControlState { Off, Waiting, Connected, Failed }

/// <summary>
/// Drives a WebDriverAgent app running on the connected iPhone/iPad. The WDA
/// HTTP endpoint is reached through <see cref="WdaPortForwarder"/>, which
/// tunnels device port 8100 over the same usbmux link the wired capture
/// already uses, so control and mirroring coexist on one cable.
/// </summary>
internal sealed class WdaControlService : IAsyncDisposable
{
    private const string PointerId = "iPhoneMirrorPointer";
    private const int StatusPollWaitingMs = 1500;
    private const int StatusPollConnectedMs = 4000;
    private const int GestureTimeoutMs = 4000;

    private readonly SemaphoreSlim _sessionGate = new(1, 1);
    private readonly SemaphoreSlim _directCallGate = new(1, 1);
    private readonly GoIosLauncher _launcher = new();
    private Channel<WdaGesture> _gestures =
        Channel.CreateBounded<WdaGesture>(new BoundedChannelOptions(2)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });
    private CancellationTokenSource? _lifecycle;
    private HttpClient? _httpClient;
    private WdaPortForwarder? _forwarder;
    private Task? _pollLoop;
    private Task? _gestureLoop;
    private Task? _launchPipeline;
    private string? _sessionId;
    private string? _udid;
    private int _logicalWidth;
    private int _logicalHeight;

    internal WdaControlState State { get; private set; } = WdaControlState.Off;

    internal string? LastError { get; private set; }

    internal (double Width, double Height)? LogicalSize =>
        _logicalWidth > 0 && _logicalHeight > 0 ? (_logicalWidth, _logicalHeight) : null;

    internal event Action<WdaControlState, string?>? StateChanged;

    /// <summary>Receives launch-pipeline progress for the diagnostic log.</summary>
    internal Action<string>? DiagnosticSink;

    internal async Task StartAsync(string udid, CancellationToken shutdown)
    {
        if (State is not (WdaControlState.Off or WdaControlState.Failed)) return;
        LastError = null;
        SetState(WdaControlState.Waiting, notifyError: false);
        _lifecycle = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
        var cancellationToken = _lifecycle.Token;
        _gestures = Channel.CreateBounded<WdaGesture>(new BoundedChannelOptions(2)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });
        try
        {
            _udid = udid;
            _forwarder = new WdaPortForwarder(udid, WdaPortForwarder.WebDriverAgentPort);
            await _forwarder.StartAsync(cancellationToken).ConfigureAwait(false);
            _httpClient = new HttpClient(new SocketsHttpHandler
            {
                UseProxy = false,
                AllowAutoRedirect = false,
                ConnectTimeout = TimeSpan.FromSeconds(3),
            })
            {
                BaseAddress = new Uri($"http://127.0.0.1:{_forwarder.LocalPort}/"),
                Timeout = TimeSpan.FromSeconds(8),
            };
            _gestureLoop = Task.Run(() => GestureLoopAsync(cancellationToken), cancellationToken);
            _pollLoop = Task.Run(() => PollLoopAsync(cancellationToken), cancellationToken);
            _launchPipeline = Task.Run(() => LaunchPipelineAsync(cancellationToken),
                cancellationToken);
        }
        catch (Exception error)
        {
            await StopInternalAsync().ConfigureAwait(false);
            LastError = DescribeError(error);
            SetState(WdaControlState.Failed, notifyError: true);
        }
    }

    internal async Task StopAsync()
    {
        if (State == WdaControlState.Off) return;
        await StopInternalAsync().ConfigureAwait(false);
        SetState(WdaControlState.Off, notifyError: false);
    }

    /// <summary>Queues a tap; dropped silently when the gesture channel is saturated.</summary>
    internal void EnqueueTap(float pointX, float pointY) =>
        _gestures.Writer.TryWrite(new WdaGestureTap(pointX, pointY));

    internal void EnqueueLongPress(float pointX, float pointY) =>
        _gestures.Writer.TryWrite(new WdaGestureLongPress(pointX, pointY));

    /// <summary>
    /// Queues one drag segment. Segments are dropped when one is still
    /// executing, so a fast mouse always chases the newest position.
    /// </summary>
    internal void EnqueueDrag(float fromX, float fromY, float toX, float toY) =>
        _gestures.Writer.TryWrite(new WdaGestureDrag(fromX, fromY, toX, toY));

    internal void EnqueueFlick(float fromX, float fromY, float toX, float toY) =>
        _gestures.Writer.TryWrite(new WdaGestureFlick(fromX, fromY, toX, toY));

    internal async Task<bool> SendTextAsync(string text)
    {
        if (State != WdaControlState.Connected || string.IsNullOrEmpty(text)) return false;
        await _directCallGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var sessionId = await EnsureSessionAsync(CancellationToken.None)
                .ConfigureAwait(false);
            var body = new Dictionary<string, object?[]>
            {
                ["value"] = [text],
            };
            var response = await SendAsync(
                HttpMethod.Post, $"session/{sessionId}/wda/keys", body,
                TimeSpan.FromSeconds(4)).ConfigureAwait(false);
            return response is not null;
        }
        catch (WdaRequestException error)
        {
            if (error.IsInvalidSession) _sessionId = null;
            LastError = error.Message;
            return false;
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            _directCallGate.Release();
        }
    }

    internal async Task<bool> PressButtonAsync(string name)
    {
        if (State != WdaControlState.Connected || string.IsNullOrEmpty(name)) return false;
        await _directCallGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var sessionId = await EnsureSessionAsync(CancellationToken.None)
                .ConfigureAwait(false);
            var body = new Dictionary<string, object?> { ["name"] = name };
            var response = await SendAsync(
                HttpMethod.Post, $"session/{sessionId}/wda/pressButton", body,
                TimeSpan.FromSeconds(4)).ConfigureAwait(false);
            return response is not null;
        }
        catch (WdaRequestException error)
        {
            if (error.IsInvalidSession) _sessionId = null;
            LastError = error.Message;
            return false;
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            _directCallGate.Release();
        }
    }

    /// <summary>
    /// Maps a source-pixel coordinate to WDA logical points. Source frames and
    /// WDA's window size always describe the same interface orientation, so a
    /// proportional mapping is exact for both portrait and landscape.
    /// </summary>
    internal bool TryConvertSourceToPoints(
        int sourceX, int sourceY, int sourceWidth, int sourceHeight,
        out float pointX, out float pointY)
    {
        pointX = 0;
        pointY = 0;
        if (_logicalWidth <= 0 || _logicalHeight <= 0 || sourceWidth <= 0 ||
            sourceHeight <= 0) return false;
        pointX = Math.Clamp(sourceX * (float)_logicalWidth / sourceWidth,
            0, _logicalWidth - 1);
        pointY = Math.Clamp(sourceY * (float)_logicalHeight / sourceHeight,
            0, _logicalHeight - 1);
        return true;
    }

    private async Task PollLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var delay = State == WdaControlState.Connected
                ? StatusPollConnectedMs
                : StatusPollWaitingMs;
            try
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                if (!await ProbeStatusAsync(cancellationToken).ConfigureAwait(false))
                {
                    if (State == WdaControlState.Connected) MarkDisconnected();
                    continue;
                }
                if (State == WdaControlState.Waiting)
                {
                    await _sessionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        await CreateSessionAsync(cancellationToken).ConfigureAwait(false);
                        LastError = null;
                        SetState(WdaControlState.Connected, notifyError: false);
                    }
                    finally
                    {
                        _sessionGate.Release();
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception error)
            {
                if (State == WdaControlState.Connected) MarkDisconnected();
                LastError = DescribeError(error);
            }
        }
    }

    private async Task<bool> ProbeStatusAsync(CancellationToken cancellationToken)
    {
        var response = await SendAsync(HttpMethod.Get, "status", null,
            TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
        return response is not null;
    }

    /// <summary>
    /// A manually tapped runner aborts in dyld because iOS only mounts the
    /// XCTest runtime while a developer session is active, so WDA must be
    /// launched through go-ios (CoreDevice tunnel + testmanagerd). The
    /// pipeline probes first and launches with a cooldown whenever WDA is
    /// still unreachable, which also self-heals after a cable replug.
    /// </summary>
    private async Task LaunchPipelineAsync(CancellationToken cancellationToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
        while (!cancellationToken.IsCancellationRequested &&
               State == WdaControlState.Waiting)
        {
            if (await ProbeStatusAsync(cancellationToken).ConfigureAwait(false)) return;
            try
            {
                if (_launcher.IsAvailable)
                {
                    var launched = await _launcher.LaunchAsync(_udid ?? string.Empty,
                        cancellationToken).ConfigureAwait(false);
                    ReportDiagnostic(launched
                        ? "wda_launch_requested"
                        : $"wda_launch_failed error={LastError}");
                }
                else
                {
                    if (string.IsNullOrEmpty(LastError)) LastError = "goios_missing";
                    ReportDiagnostic($"wda_launch_failed error={LastError}");
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception error)
            {
                LastError = $"goios_error:{error.Message}";
            }
            await Task.Delay(TimeSpan.FromSeconds(15), cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private void MarkDisconnected()
    {
        _sessionId = null;
        _logicalWidth = 0;
        _logicalHeight = 0;
        SetState(WdaControlState.Waiting, notifyError: false);
    }

    private async Task<string> EnsureSessionAsync(CancellationToken cancellationToken)
    {
        await _sessionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_sessionId is not null) return _sessionId;
            await CreateSessionAsync(cancellationToken).ConfigureAwait(false);
            return _sessionId ?? throw new WdaRequestException("session_unavailable");
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    private async Task CreateSessionAsync(CancellationToken cancellationToken)
    {
        var body = new Dictionary<string, object?>
        {
            ["capabilities"] = new Dictionary<string, object?>
            {
                ["alwaysMatch"] = new Dictionary<string, object?>(),
            },
        };
        var response = await SendAsync(HttpMethod.Post, "session", body,
            TimeSpan.FromSeconds(6), cancellationToken).ConfigureAwait(false)
            ?? throw new WdaRequestException("session_unavailable");
        _sessionId = response.SessionId;
        if (_sessionId is null) throw new WdaRequestException("session_unavailable");
        var size = await SendAsync(HttpMethod.Get, $"session/{_sessionId}/window/size",
            null, TimeSpan.FromSeconds(6), cancellationToken).ConfigureAwait(false);
        var width = size?.Width;
        var height = size?.Height;
        if (width is > 0 && height is > 0)
        {
            _logicalWidth = width.Value;
            _logicalHeight = height.Value;
        }
    }

    private async Task GestureLoopAsync(CancellationToken cancellationToken)
    {
        await foreach (var gesture in _gestures.Reader.ReadAllAsync(cancellationToken)
            .ConfigureAwait(false))
        {
            try
            {
                await ExecuteGestureAsync(gesture, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception error)
            {
                LastError = DescribeError(error);
            }
        }
    }

    private async Task ExecuteGestureAsync(
        WdaGesture gesture, CancellationToken cancellationToken)
    {
        if (State != WdaControlState.Connected) return;
        var steps = gesture switch
        {
            WdaGestureTap tap => BuildTapSteps(tap.X, tap.Y),
            WdaGestureLongPress press => BuildPressSteps(press.X, press.Y, 700),
            WdaGestureDrag drag => BuildDragSteps(drag.FromX, drag.FromY,
                drag.ToX, drag.ToY, 90),
            WdaGestureFlick flick => BuildDragSteps(flick.FromX, flick.FromY,
                flick.ToX, flick.ToY, 140),
            _ => null,
        };
        if (steps is null) return;

        var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(GestureTimeoutMs);
        try
        {
            await _sessionGate.WaitAsync(timeout.Token).ConfigureAwait(false);
            try
            {
                var sessionId = _sessionId;
                if (sessionId is null) return;
                var body = new Dictionary<string, object?>
                {
                    ["actions"] = new object[]
                    {
                        new Dictionary<string, object?>
                        {
                            ["type"] = "pointer",
                            ["id"] = PointerId,
                            ["parameters"] = new Dictionary<string, object?>
                            {
                                ["pointerType"] = "touch",
                            },
                            ["actions"] = steps,
                        },
                    },
                };
                var response = await SendAsync(HttpMethod.Post,
                    $"session/{sessionId}/actions", body, null, timeout.Token)
                    .ConfigureAwait(false);
                if (response is not null) return;
            }
            finally
            {
                _sessionGate.Release();
            }
        }
        catch (WdaRequestException error) when (
            error.IsInvalidSession && !cancellationToken.IsCancellationRequested)
        {
            // The session expired between gestures; the next one recreates it.
            _sessionId = null;
            LastError = error.Message;
        }
        finally
        {
            timeout.Dispose();
        }
    }

    private static object[] BuildTapSteps(float x, float y) =>
    [
        MoveStep(x, y, 0),
        DownStep(),
        new Dictionary<string, object?> { ["type"] = "pause", ["duration"] = 60 },
        UpStep(),
    ];

    private static object[] BuildPressSteps(float x, float y, int holdMs) =>
    [
        MoveStep(x, y, 0),
        DownStep(),
        new Dictionary<string, object?> { ["type"] = "pause", ["duration"] = holdMs },
        UpStep(),
    ];

    private static object[] BuildDragSteps(
        float fromX, float fromY, float toX, float toY, int moveMs) =>
    [
        MoveStep(fromX, fromY, 0),
        DownStep(),
        MoveStep(toX, toY, moveMs),
        UpStep(),
    ];

    private static Dictionary<string, object?> MoveStep(float x, float y, int durationMs) =>
        new()
        {
            ["type"] = "pointerMove",
            ["duration"] = durationMs,
            ["x"] = x,
            ["y"] = y,
            ["origin"] = "viewport",
        };

    private static Dictionary<string, object?> DownStep() => new()
    {
        ["type"] = "pointerDown",
        ["button"] = 0,
    };

    private static Dictionary<string, object?> UpStep() => new()
    {
        ["type"] = "pointerUp",
        ["button"] = 0,
    };

    private async Task<WdaResponse?> SendAsync(
        HttpMethod method, string relativePath, object? body,
        TimeSpan? timeout, CancellationToken cancellationToken = default)
    {
        var client = _httpClient;
        if (client is null) return null;
        using var request = new HttpRequestMessage(method, relativePath);
        if (body is not null)
        {
            request.Content = new StringContent(
                JsonSerializer.Serialize(body),
                Encoding.UTF8, "application/json");
        }
        using var timedOut = timeout.HasValue
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : null;
        if (timedOut is not null && timeout is { } requestTimeout)
            timedOut.CancelAfter(requestTimeout);
        try
        {
            using var response = await client.SendAsync(request,
                HttpCompletionOption.ResponseHeadersRead,
                timedOut?.Token ?? cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            var payload = await response.Content.ReadAsStringAsync(
                timedOut?.Token ?? cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(payload)) return WdaResponse.Empty;
            return WdaResponse.Parse(payload);
        }
        catch (WdaRequestException)
        {
            throw;
        }
        catch (Exception error) when (error is HttpRequestException or IOException
            or TaskCanceledException or OperationCanceledException)
        {
            if (cancellationToken.IsCancellationRequested) throw;
            return null;
        }
    }

    private static string DescribeError(Exception error) => error.Message;

    private void ReportDiagnostic(string detail) => DiagnosticSink?.Invoke(detail);

    private void SetState(WdaControlState state, bool notifyError)
    {
        if (notifyError && string.IsNullOrEmpty(LastError)) LastError = "unknown";
        if (State == state) return;
        State = state;
        StateChanged?.Invoke(state, LastError);
    }

    private async Task StopInternalAsync()
    {
        _lifecycle?.Cancel();
        _gestures.Writer.TryComplete();
        if (_pollLoop is not null)
        {
            try
            {
                await _pollLoop.ConfigureAwait(false);
            }
            catch
            {
                // Poll loop cancellation is expected during teardown.
            }
            _pollLoop = null;
        }
        if (_gestureLoop is not null)
        {
            try
            {
                await _gestureLoop.ConfigureAwait(false);
            }
            catch
            {
                // Gesture loop cancellation is expected during teardown.
            }
            _gestureLoop = null;
        }
        if (_launchPipeline is not null)
        {
            try
            {
                await _launchPipeline.ConfigureAwait(false);
            }
            catch
            {
                // Launch pipeline cancellation is expected during teardown.
            }
            _launchPipeline = null;
        }
        if (_forwarder is not null)
        {
            await _forwarder.DisposeAsync().ConfigureAwait(false);
            _forwarder = null;
        }
        _launcher.Stop();
        _httpClient?.Dispose();
        _httpClient = null;
        _sessionId = null;
        _logicalWidth = 0;
        _logicalHeight = 0;
        _lifecycle?.Dispose();
        _lifecycle = null;
    }

    public async ValueTask DisposeAsync()
    {
        await StopInternalAsync().ConfigureAwait(false);
        _sessionGate.Dispose();
        _directCallGate.Dispose();
        State = WdaControlState.Off;
    }

    private sealed record WdaGestureTap(float X, float Y) : WdaGesture;

    private sealed record WdaGestureLongPress(float X, float Y) : WdaGesture;

    private sealed record WdaGestureDrag(
        float FromX, float FromY, float ToX, float ToY) : WdaGesture;

    private sealed record WdaGestureFlick(
        float FromX, float FromY, float ToX, float ToY) : WdaGesture;

    private abstract record WdaGesture;

    internal sealed class WdaRequestException(string code) : Exception(code)
    {
        internal bool IsInvalidSession =>
            string.Equals(Code, "invalid session id", StringComparison.OrdinalIgnoreCase);

        internal string Code => Message;
    }

    /// <summary>Minimal view of a WDA JSON payload ({value: ...} or legacy root).</summary>
    internal sealed class WdaResponse
    {
        internal static readonly WdaResponse Empty = new(null, null, null);

        private WdaResponse(string? sessionId, int? width, int? height)
        {
            SessionId = sessionId;
            Width = width;
            Height = height;
        }

        internal string? SessionId { get; }

        internal int? Width { get; }

        internal int? Height { get; }

        internal static WdaResponse Parse(string payload)
        {
            try
            {
                using var document = JsonDocument.Parse(payload);
                var root = document.RootElement;
                string? error = null;
                if (root.ValueKind == JsonValueKind.Object &&
                    root.TryGetProperty("value", out var value) &&
                    value.ValueKind == JsonValueKind.Object &&
                    value.TryGetProperty("error", out var errorElement) &&
                    errorElement.ValueKind == JsonValueKind.String)
                {
                    error = errorElement.GetString();
                }
                if (error is not null) throw new WdaRequestException(error);

                var sessionId = TryGetString(root, "sessionId") ??
                    (root.ValueKind == JsonValueKind.Object &&
                     root.TryGetProperty("value", out value) &&
                     value.ValueKind == JsonValueKind.Object
                        ? TryGetString(value, "sessionId")
                        : null);
                var (width, height) = TryGetSize(root) ??
                    (root.ValueKind == JsonValueKind.Object &&
                     root.TryGetProperty("value", out value)
                        ? TryGetSize(value)
                        : null)
                    ?? ((int?)null, (int?)null);
                return new WdaResponse(sessionId, width, height);
            }
            catch (JsonException)
            {
                // A 200 with a non-JSON body still proves liveness (probe path).
                return Empty;
            }
        }

        private static string? TryGetString(JsonElement element, string name) =>
            element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(name, out var property) &&
            property.ValueKind == JsonValueKind.String
                ? property.GetString()
                : null;

        private static (int?, int?)? TryGetSize(JsonElement element)
        {
            if (element.ValueKind != JsonValueKind.Object ||
                !element.TryGetProperty("width", out var widthElement) ||
                !element.TryGetProperty("height", out var heightElement) ||
                widthElement.ValueKind is not (JsonValueKind.Number) ||
                heightElement.ValueKind is not (JsonValueKind.Number)) return null;
            int? width = widthElement.ValueKind == JsonValueKind.Number
                ? widthElement.GetInt32() : null;
            int? height = heightElement.ValueKind == JsonValueKind.Number
                ? heightElement.GetInt32() : null;
            return (width, height);
        }
    }
}
