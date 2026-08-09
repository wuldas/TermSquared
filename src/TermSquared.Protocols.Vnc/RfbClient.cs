using System.Buffers.Binary;
using System.Text;

namespace TermSquared.Protocols.Vnc;

public readonly record struct RfbPixelFormat(
    byte BitsPerPixel,
    byte Depth,
    bool BigEndian,
    bool TrueColor,
    ushort RedMax,
    ushort GreenMax,
    ushort BlueMax,
    byte RedShift,
    byte GreenShift,
    byte BlueShift);

public sealed record RfbServerInfo(ushort Width, ushort Height, RfbPixelFormat PixelFormat, string Name);

public sealed record RfbRectangle(
    ushort X,
    ushort Y,
    ushort Width,
    ushort Height,
    int Encoding,
    byte[] Pixels);

public sealed record RfbFramebufferUpdate(IReadOnlyList<RfbRectangle> Rectangles);

public sealed class RfbClient(Stream stream, bool leaveOpen = false) : IAsyncDisposable
{
    private const int RawEncoding = 0;
    private const int MaximumRectangleCount = 4096;
    private const int MaximumRectangleDimension = 16 * 1024;
    private const int MaximumFramebufferBytes = 128 * 1024 * 1024;
    private static readonly RfbPixelFormat StandardPixelFormat = new(
        32, 24, BigEndian: false, TrueColor: true, 255, 255, 255, 16, 8, 0);

    public RfbServerInfo? ServerInfo { get; private set; }
    public RfbPixelFormat ClientPixelFormat { get; private set; }

    public async Task<RfbServerInfo> HandshakeAsync(CancellationToken cancellationToken)
    {
        var versionBytes = new byte[12];
        await ReadExactlyAsync(versionBytes, cancellationToken).ConfigureAwait(false);
        var version = Encoding.ASCII.GetString(versionBytes);
        if (version != "RFB 003.008\n") throw new InvalidDataException($"Unsupported RFB version: {version.Trim()}");
        await stream.WriteAsync(versionBytes, cancellationToken).ConfigureAwait(false);

        var securityCount = await ReadByteAsync(cancellationToken).ConfigureAwait(false);
        if (securityCount == 0)
        {
            var reason = await ReadStringAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidDataException(reason);
        }
        var securityTypes = new byte[securityCount];
        await ReadExactlyAsync(securityTypes, cancellationToken).ConfigureAwait(false);
        if (!securityTypes.Contains((byte)1)) throw new NotSupportedException("The server does not offer RFB None security.");
        await stream.WriteAsync(new byte[] { 1 }, cancellationToken).ConfigureAwait(false);

        var securityResult = new byte[4];
        await ReadExactlyAsync(securityResult, cancellationToken).ConfigureAwait(false);
        if (BinaryPrimitives.ReadUInt32BigEndian(securityResult) != 0)
            throw new UnauthorizedAccessException(await ReadStringAsync(cancellationToken).ConfigureAwait(false));

        await stream.WriteAsync(new byte[] { 1 }, cancellationToken).ConfigureAwait(false);
        var header = new byte[24];
        await ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        var pixelFormat = new RfbPixelFormat(
            header[4], header[5], header[6] != 0, header[7] != 0,
            BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(8, 2)),
            BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(10, 2)),
            BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(12, 2)),
            header[14], header[15], header[16]);
        var nameLength = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(20, 4));
        if (nameLength > 1024 * 1024) throw new InvalidDataException("RFB desktop name is too large.");
        var nameBytes = new byte[nameLength];
        await ReadExactlyAsync(nameBytes, cancellationToken).ConfigureAwait(false);
        ServerInfo = new RfbServerInfo(
            BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(0, 2)),
            BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(2, 2)),
            pixelFormat,
            Encoding.UTF8.GetString(nameBytes));
        await SetPixelFormatAsync(StandardPixelFormat, cancellationToken).ConfigureAwait(false);
        return ServerInfo;
    }

    public async Task SetEncodingsAsync(IReadOnlyList<int> encodings, CancellationToken cancellationToken)
    {
        if (ServerInfo is null) throw new InvalidOperationException("Handshake has not completed.");
        ArgumentNullException.ThrowIfNull(encodings);
        if (encodings.Count > ushort.MaxValue) throw new ArgumentOutOfRangeException(nameof(encodings));

        var message = new byte[checked(4 + encodings.Count * 4)];
        message[0] = 2;
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(2, 2), (ushort)encodings.Count);
        for (var index = 0; index < encodings.Count; index++)
            BinaryPrimitives.WriteInt32BigEndian(message.AsSpan(4 + index * 4, 4), encodings[index]);
        await stream.WriteAsync(message, cancellationToken).ConfigureAwait(false);
    }

    public async Task RequestRawFramebufferUpdateAsync(bool incremental, ushort x, ushort y, ushort width, ushort height, CancellationToken cancellationToken)
    {
        if (ServerInfo is null) throw new InvalidOperationException("Handshake has not completed.");
        if (width == 0 || height == 0 || (uint)x + width > ServerInfo.Width || (uint)y + height > ServerInfo.Height)
            throw new ArgumentOutOfRangeException(nameof(width), "The requested region must be within the framebuffer.");
        var request = new byte[10];
        request[0] = 3;
        request[1] = incremental ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(2, 2), x);
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(4, 2), y);
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(6, 2), width);
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(8, 2), height);
        await stream.WriteAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public async Task<RfbFramebufferUpdate> ReadFramebufferUpdateAsync(CancellationToken cancellationToken)
    {
        if (ServerInfo is null) throw new InvalidOperationException("Handshake has not completed.");
        var header = new byte[4];
        await ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        if (header[0] != 0) throw new InvalidDataException($"Expected framebuffer update, received message type {header[0]}.");
        var rectangleCount = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(2, 2));
        if (rectangleCount > MaximumRectangleCount) throw new InvalidDataException("RFB framebuffer update has too many rectangles.");

        var rectangles = new List<RfbRectangle>(rectangleCount);
        var totalPixelBytes = 0;
        for (var index = 0; index < rectangleCount; index++)
        {
            var rectangle = new byte[12];
            await ReadExactlyAsync(rectangle, cancellationToken).ConfigureAwait(false);
            var x = BinaryPrimitives.ReadUInt16BigEndian(rectangle.AsSpan(0, 2));
            var y = BinaryPrimitives.ReadUInt16BigEndian(rectangle.AsSpan(2, 2));
            var width = BinaryPrimitives.ReadUInt16BigEndian(rectangle.AsSpan(4, 2));
            var height = BinaryPrimitives.ReadUInt16BigEndian(rectangle.AsSpan(6, 2));
            var encoding = BinaryPrimitives.ReadInt32BigEndian(rectangle.AsSpan(8, 4));
            if (encoding != RawEncoding) throw new NotSupportedException($"RFB encoding {encoding} is not supported.");
            if (width == 0 || height == 0 || width > MaximumRectangleDimension || height > MaximumRectangleDimension)
                throw new InvalidDataException("RFB rectangle dimensions are invalid or exceed the configured limit.");
            if ((uint)x + width > ServerInfo.Width || (uint)y + height > ServerInfo.Height)
                throw new InvalidDataException("RFB rectangle lies outside the framebuffer.");

            var bytesPerPixel = ClientPixelFormat.BitsPerPixel / 8;
            var pixelByteCount = (long)width * height * bytesPerPixel;
            if (pixelByteCount > MaximumFramebufferBytes - totalPixelBytes)
                throw new InvalidDataException("RFB framebuffer update exceeds the configured pixel data limit.");
            var pixelBytes = (int)pixelByteCount;
            totalPixelBytes += pixelBytes;
            var pixels = new byte[pixelBytes];
            await ReadExactlyAsync(pixels, cancellationToken).ConfigureAwait(false);
            rectangles.Add(new RfbRectangle(x, y, width, height, encoding, pixels));
        }
        return new RfbFramebufferUpdate(rectangles);
    }

    public ValueTask DisposeAsync()
    {
        if (!leaveOpen) stream.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task<byte> ReadByteAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[1];
        await ReadExactlyAsync(buffer, cancellationToken).ConfigureAwait(false);
        return buffer[0];
    }

    private async Task<string> ReadStringAsync(CancellationToken cancellationToken)
    {
        var lengthBytes = new byte[4];
        await ReadExactlyAsync(lengthBytes, cancellationToken).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadUInt32BigEndian(lengthBytes);
        if (length > 1024 * 1024) throw new InvalidDataException("RFB string is too large.");
        var bytes = new byte[length];
        await ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        return Encoding.UTF8.GetString(bytes);
    }

    private async Task SetPixelFormatAsync(RfbPixelFormat pixelFormat, CancellationToken cancellationToken)
    {
        var message = new byte[20];
        WritePixelFormat(message.AsSpan(4), pixelFormat);
        await stream.WriteAsync(message, cancellationToken).ConfigureAwait(false);
        ClientPixelFormat = pixelFormat;
    }

    private static void WritePixelFormat(Span<byte> destination, RfbPixelFormat pixelFormat)
    {
        destination[0] = pixelFormat.BitsPerPixel;
        destination[1] = pixelFormat.Depth;
        destination[2] = pixelFormat.BigEndian ? (byte)1 : (byte)0;
        destination[3] = pixelFormat.TrueColor ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteUInt16BigEndian(destination.Slice(4, 2), pixelFormat.RedMax);
        BinaryPrimitives.WriteUInt16BigEndian(destination.Slice(6, 2), pixelFormat.GreenMax);
        BinaryPrimitives.WriteUInt16BigEndian(destination.Slice(8, 2), pixelFormat.BlueMax);
        destination[10] = pixelFormat.RedShift;
        destination[11] = pixelFormat.GreenShift;
        destination[12] = pixelFormat.BlueShift;
    }

    private Task ReadExactlyAsync(byte[] buffer, CancellationToken cancellationToken) =>
        stream.ReadExactlyAsync(buffer, cancellationToken).AsTask();
}
