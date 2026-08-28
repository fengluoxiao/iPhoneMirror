using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Security;
using System.Text;
using System.Xml.Linq;

namespace IPhoneMirror.App.Services;

/// <summary>
/// Minimal C# port of the Core usbmux client (Transport/UsbMuxClient.cpp).
/// Speaks the XML-plist message protocol of Apple's Windows usbmuxd on the
/// loopback TCP ports published by Apple Mobile Device Service, mirroring the
/// same message shapes the C++ capture pipeline already uses in production.
/// </summary>
internal static class UsbmuxTunnelClient
{
    private const int PlistMessageType = 8;
    private const uint ProtocolVersion = 1;
    private const int MaxPayloadBytes = 16 * 1024 * 1024;
    internal const ushort WebDriverAgentPort = 8100;
    private static readonly int[] MuxPorts = [27015, 37015];

    internal sealed class UsbmuxException(string message) : Exception(message);

    internal sealed class MuxTunnel : IDisposable
    {
        private readonly TcpClient _client;
        internal MuxTunnel(TcpClient client, NetworkStream stream)
        {
            _client = client;
            Stream = stream;
        }
        internal NetworkStream Stream { get; }
        public void Dispose()
        {
            Stream.Dispose();
            _client.Dispose();
        }
    }

    internal readonly record struct MuxDeviceLocation(int MuxPort, uint DeviceId);

    /// <summary>Resolves the usbmux DeviceID for a UDID, probing both known mux ports.</summary>
    internal static async Task<MuxDeviceLocation> FindDeviceAsync(
        string udid, CancellationToken cancellationToken)
    {
        foreach (var muxPort in MuxPorts)
        {
            try
            {
                await using var connection = await OpenAsync(muxPort, cancellationToken)
                    .ConfigureAwait(false);
                var response = await RequestAsync(connection, CreateMessage("ListDevices"),
                    cancellationToken).ConfigureAwait(false);
                if (TryFindDeviceId(response, udid, out var deviceId))
                    return new MuxDeviceLocation(muxPort, deviceId);
            }
            catch (Exception error) when (error is SocketException or IOException
                or ObjectDisposedException or UnauthorizedAccessException
                or UsbmuxException)
            {
                // Port not published, not a mux endpoint, or transient IPC
                // failure: try the next mux port.
            }
        }
        throw new UsbmuxException("device_not_found");
    }

    /// <summary>
    /// Opens a raw tunnel to a TCP port on the device through usbmuxd. The
    /// returned stream is the device port itself once the Result is success,
    /// exactly like iproxy: one tunnel per connection.
    /// </summary>
    internal static async Task<MuxTunnel> ConnectDevicePortAsync(
        MuxDeviceLocation location, ushort devicePort, CancellationToken cancellationToken)
    {
        var connection = await OpenAsync(location.MuxPort, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            var message = CreateMessage("Connect");
            message["DeviceID"] = new PListInteger(location.DeviceId);
            // usbmuxd expects the port in network byte order, mirroring
            // htons(device_port) in the C++ client.
            var swapped = (ushort)((devicePort << 8) | (devicePort >> 8));
            message["PortNumber"] = new PListInteger(swapped);
            var response = await RequestAsync(connection, message, cancellationToken)
                .ConfigureAwait(false);
            if (response.TryGetValue("Number", out var value) &&
                value is PListInteger number)
            {
                if (number.Value != 0)
                    throw new UsbmuxException(number.Value == 1
                        ? "port_unreachable"
                        : "connect_rejected");
                connection.Stream.ReadTimeout = Timeout.Infinite;
                connection.Stream.WriteTimeout = Timeout.Infinite;
                return connection.ReleaseTunnel();
            }
            throw new UsbmuxException("connect_rejected");
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static bool TryFindDeviceId(
        IReadOnlyDictionary<string, PListNode> response, string udid, out uint deviceId)
    {
        deviceId = 0;
        if (!response.TryGetValue("DeviceList", out var list) ||
            list is not PListArray array) return false;
        foreach (var entry in array.Values)
        {
            if (entry is not PListDict device ||
                !device.Values.TryGetValue("Properties", out var properties) ||
                properties is not PListDict props ||
                !props.Values.TryGetValue("SerialNumber", out var serial) ||
                serial is not PListString serialText ||
                !string.Equals(serialText.Value, udid, StringComparison.OrdinalIgnoreCase))
                continue;
            if (device.Values.TryGetValue("DeviceID", out var id) &&
                id is PListInteger integer)
            {
                deviceId = (uint)integer.Value;
                return true;
            }
        }
        return false;
    }

    private static Dictionary<string, PListNode> CreateMessage(string messageType) => new()
    {
        ["BundleID"] = new PListString("com.iphonemirror.windows"),
        ["ClientVersionString"] = new PListString("iPhoneMirror 1.9.0"),
        ["MessageType"] = new PListString(messageType),
        ["ProgName"] = new PListString("iPhoneMirror"),
        ["kLibUSBMuxVersion"] = new PListInteger(3),
    };

    private static async Task<PlistConnection> OpenAsync(
        int muxPort, CancellationToken cancellationToken)
    {
        var client = new TcpClient();
        try
        {
            var connect = client.ConnectAsync("127.0.0.1", muxPort, cancellationToken)
                .AsTask();
            var completed = await Task.WhenAny(connect,
                Task.Delay(750, cancellationToken)).ConfigureAwait(false);
            if (completed != connect || !client.Connected)
                throw new SocketException((int)SocketError.TimedOut);
            return new PlistConnection(client);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private static async Task<IReadOnlyDictionary<string, PListNode>> RequestAsync(
        PlistConnection connection, IReadOnlyDictionary<string, PListNode> message,
        CancellationToken cancellationToken)
    {
        var payload = Encoding.UTF8.GetBytes(SerializePlist(message));
        var header = new byte[16];
        WriteUInt32(header, 0, (uint)(16 + payload.Length));
        WriteUInt32(header, 4, ProtocolVersion);
        WriteUInt32(header, 8, PlistMessageType);
        WriteUInt32(header, 12, 1);
        var stream = connection.Stream;
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);

        var responseHeader = await ReadExactAsync(stream, 16, cancellationToken)
            .ConfigureAwait(false);
        var length = ReadUInt32(responseHeader, 0);
        if (length < 16 || length > MaxPayloadBytes)
            throw new UsbmuxException("invalid_response_length");
        if (ReadUInt32(responseHeader, 8) != PlistMessageType)
            throw new UsbmuxException("unexpected_response_protocol");
        var body = await ReadExactAsync(stream, (int)(length - 16), cancellationToken)
            .ConfigureAwait(false);
        var parsed = ParsePlistXml(Encoding.UTF8.GetString(body));
        if (parsed is not PListDict root)
            throw new UsbmuxException("unexpected_response_payload");
        return root.Values;
    }

    private static async Task<byte[]> ReadExactAsync(
        Stream stream, int count, CancellationToken cancellationToken)
    {
        var buffer = new byte[count];
        var offset = 0;
        while (offset < count)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, count - offset),
                cancellationToken).ConfigureAwait(false);
            if (read <= 0) throw new UsbmuxException("connection_closed");
            offset += read;
        }
        return buffer;
    }

    private static void WriteUInt32(byte[] buffer, int offset, uint value)
    {
        buffer[offset] = (byte)value;
        buffer[offset + 1] = (byte)(value >> 8);
        buffer[offset + 2] = (byte)(value >> 16);
        buffer[offset + 3] = (byte)(value >> 24);
    }

    private static uint ReadUInt32(byte[] buffer, int offset) =>
        buffer[offset] | ((uint)buffer[offset + 1] << 8) |
        ((uint)buffer[offset + 2] << 16) | ((uint)buffer[offset + 3] << 24);

    private static string SerializePlist(IReadOnlyDictionary<string, PListNode> dictionary)
    {
        var builder = new StringBuilder(256);
        builder.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
        builder.Append("<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" ");
        builder.Append("\"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n");
        builder.Append("<plist version=\"1.0\">");
        SerializeDict(dictionary, builder);
        builder.Append("</plist>");
        return builder.ToString();
    }

    private static void SerializeDict(
        IReadOnlyDictionary<string, PListNode> dictionary, StringBuilder builder)
    {
        builder.Append("<dict>");
        foreach (var pair in dictionary)
        {
            builder.Append("<key>").Append(EscapeXml(pair.Key)).Append("</key>");
            SerializeValue(pair.Value, builder);
        }
        builder.Append("</dict>");
    }

    private static void SerializeValue(PListNode value, StringBuilder builder)
    {
        switch (value)
        {
            case PListString text:
                builder.Append("<string>").Append(EscapeXml(text.Value))
                    .Append("</string>");
                break;
            case PListInteger integer:
                builder.Append("<integer>")
                    .Append(integer.Value.ToString(CultureInfo.InvariantCulture))
                    .Append("</integer>");
                break;
            case PListDict dict:
                SerializeDict(dict.Values, builder);
                break;
            case PListArray array:
                builder.Append("<array>");
                foreach (var entry in array.Values) SerializeValue(entry, builder);
                builder.Append("</array>");
                break;
            default:
                builder.Append("<string/>");
                break;
        }
    }

    private static string EscapeXml(string text) =>
        SecurityElement.Escape(text) ?? string.Empty;

    private static PListNode ParsePlistXml(string xml)
    {
        var document = XDocument.Parse(xml);
        var root = document.Root ??
            throw new UsbmuxException("invalid_response_payload");
        var children = root.Elements().ToArray();
        if (children.Length != 1)
            throw new UsbmuxException("invalid_response_payload");
        return ParseNode(new PlistReader(children) { Position = 0 });
    }

    private sealed class PlistReader(XElement[] elements)
    {
        internal int Position { get; set; } = -1;

        internal int Count => elements.Length;

        internal XElement Current => elements[Position];
    }

    private static PListNode ParseNode(PlistReader reader)
    {
        var element = reader.Current;
        switch (element.Name.LocalName)
        {
            case "dict":
            {
                var children = element.Elements().ToArray();
                var childReader = new PlistReader(children);
                var dictionary = new Dictionary<string, PListNode>();
                while (childReader.Position + 1 < childReader.Count)
                {
                    childReader.Position++;
                    if (childReader.Current.Name.LocalName != "key") continue;
                    var key = childReader.Current.Value;
                    if (childReader.Position + 1 >= childReader.Count) break;
                    childReader.Position++;
                    dictionary[key] = ParseNode(childReader);
                }
                return new PListDict(dictionary);
            }
            case "array":
            {
                var children = element.Elements().ToArray();
                var childReader = new PlistReader(children);
                var values = new List<PListNode>();
                while (childReader.Position + 1 < childReader.Count)
                {
                    childReader.Position++;
                    values.Add(ParseNode(childReader));
                }
                return new PListArray(values);
            }
            case "integer":
                return long.TryParse(element.Value.Trim(), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out var number)
                    ? new PListInteger(number)
                    : new PListInteger(0);
            case "true":
                return new PListInteger(1);
            case "false":
                return new PListInteger(0);
            default:
                // string, data, date: the mux protocol only needs the raw text.
                return new PListString(element.Value);
        }
    }

    internal abstract record PListNode;

    internal sealed record PListString(string Value) : PListNode;

    internal sealed record PListInteger(long Value) : PListNode;

    internal sealed record PListDict(IReadOnlyDictionary<string, PListNode> Values)
        : PListNode;

    internal sealed record PListArray(IReadOnlyList<PListNode> Values) : PListNode;

    /// <summary>
    /// One usbmuxd message exchange over a dedicated TCP connection. A
    /// successful Connect response converts the connection into the raw
    /// device tunnel, so <see cref="ReleaseTunnel"/> hands back ownership
    /// instead of closing the socket.
    /// </summary>
    private sealed class PlistConnection : IAsyncDisposable, IDisposable
    {
        private readonly NetworkStream _stream;
        private TcpClient? _client;
        private bool _released;

        internal PlistConnection(TcpClient client)
        {
            _client = client;
            _stream = client.GetStream();
        }

        internal NetworkStream Stream => _stream;

        internal MuxTunnel ReleaseTunnel()
        {
            _released = true;
            return new MuxTunnel(_client!, _stream);
        }

        public void Dispose()
        {
            if (!_released) _stream.Dispose();
            _client?.Dispose();
            _client = null;
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
