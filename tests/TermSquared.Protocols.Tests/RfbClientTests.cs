using System.Buffers.Binary;
using System.Text;
using TermSquared.Protocols.Vnc;

namespace TermSquared.Protocols.Tests;

public sealed class RfbClientTests
{
    [Fact]
    public async Task ParsesRfb38NoneHandshake()
    {
        var serverBytes = BuildHandshake();
        await using var stream = new DuplexMemoryStream(serverBytes);
        await using var client = new RfbClient(stream, leaveOpen: true);
        var info = await client.HandshakeAsync(default);
        Assert.Equal((ushort)800, info.Width);
        Assert.Equal((ushort)600, info.Height);
        Assert.Equal("TermSquared Test", info.Name);
        Assert.Equal(32, info.PixelFormat.BitsPerPixel);
        Assert.Equal(new RfbPixelFormat(32, 24, false, true, 255, 255, 255, 16, 8, 0), client.ClientPixelFormat);
        Assert.Equal("RFB 003.008\n", Encoding.ASCII.GetString(stream.Written[..12]));
        Assert.Equal(1, stream.Written[12]);
        Assert.Equal(1, stream.Written[13]);
        Assert.Equal(new byte[]
        {
            0, 0, 0, 0, 32, 24, 0, 1, 0, 255, 0, 255, 0, 255, 16, 8, 0, 0, 0, 0
        }, stream.Written[14..]);
    }

    [Fact]
    public async Task SendsRequestedEncodingsAndParsesRawRectangles()
    {
        var serverBytes = BuildHandshake(framebufferWidth: 20, framebufferHeight: 10)
            .Concat(BuildFramebufferUpdate(
                (2, 3, 2, 1, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }),
                (10, 5, 1, 2, new byte[] { 9, 10, 11, 12, 13, 14, 15, 16 })))
            .ToArray();
        await using var stream = new DuplexMemoryStream(serverBytes);
        await using var client = new RfbClient(stream, leaveOpen: true);
        await client.HandshakeAsync(default);

        await client.SetEncodingsAsync([0], default);
        await client.RequestRawFramebufferUpdateAsync(false, 0, 0, 20, 10, default);
        var update = await client.ReadFramebufferUpdateAsync(default);

        Assert.Equal(new byte[] { 2, 0, 0, 1, 0, 0, 0, 0 }, stream.Written[34..42]);
        Assert.Equal(new byte[] { 3, 0, 0, 0, 0, 0, 0, 20, 0, 10 }, stream.Written[42..]);
        Assert.Collection(update.Rectangles,
            rectangle =>
            {
                Assert.Equal((ushort)2, rectangle.X);
                Assert.Equal((ushort)3, rectangle.Y);
                Assert.Equal((ushort)2, rectangle.Width);
                Assert.Equal((ushort)1, rectangle.Height);
                Assert.Equal(0, rectangle.Encoding);
                Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, rectangle.Pixels);
            },
            rectangle =>
            {
                Assert.Equal((ushort)10, rectangle.X);
                Assert.Equal((ushort)5, rectangle.Y);
                Assert.Equal((ushort)1, rectangle.Width);
                Assert.Equal((ushort)2, rectangle.Height);
                Assert.Equal(0, rectangle.Encoding);
                Assert.Equal(new byte[] { 9, 10, 11, 12, 13, 14, 15, 16 }, rectangle.Pixels);
            });
    }

    [Fact]
    public async Task RejectsRawRectangleBeyondDimensionLimitBeforeAllocatingPixels()
    {
        var serverBytes = BuildHandshake(framebufferWidth: 20_000, framebufferHeight: 1)
            .Concat(BuildFramebufferUpdate((0, 0, 20_000, 1, [])))
            .ToArray();
        await using var stream = new DuplexMemoryStream(serverBytes);
        await using var client = new RfbClient(stream, leaveOpen: true);
        await client.HandshakeAsync(default);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => client.ReadFramebufferUpdateAsync(default));

        Assert.Contains("dimensions", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RejectsRawFramebufferBeyondByteLimitBeforeAllocatingPixels()
    {
        const ushort width = 8192;
        const ushort height = 4097;
        var serverBytes = BuildHandshake(framebufferWidth: width, framebufferHeight: height)
            .Concat(BuildFramebufferUpdate((0, 0, width, height, [])))
            .ToArray();
        await using var stream = new DuplexMemoryStream(serverBytes);
        await using var client = new RfbClient(stream, leaveOpen: true);
        await client.HandshakeAsync(default);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => client.ReadFramebufferUpdateAsync(default));

        Assert.Contains("pixel data limit", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static byte[] BuildHandshake(ushort framebufferWidth = 800, ushort framebufferHeight = 600)
    {
        using var stream = new MemoryStream();
        stream.Write(Encoding.ASCII.GetBytes("RFB 003.008\n"));
        stream.Write(new byte[] { 1, 1, 0, 0, 0, 0 });
        Span<byte> init = stackalloc byte[24];
        BinaryPrimitives.WriteUInt16BigEndian(init[0..2], framebufferWidth);
        BinaryPrimitives.WriteUInt16BigEndian(init[2..4], framebufferHeight);
        init[4] = 32;
        init[5] = 24;
        init[7] = 1;
        BinaryPrimitives.WriteUInt16BigEndian(init[8..10], 255);
        BinaryPrimitives.WriteUInt16BigEndian(init[10..12], 255);
        BinaryPrimitives.WriteUInt16BigEndian(init[12..14], 255);
        init[14] = 16;
        init[15] = 8;
        var name = Encoding.UTF8.GetBytes("TermSquared Test");
        BinaryPrimitives.WriteUInt32BigEndian(init[20..24], (uint)name.Length);
        stream.Write(init);
        stream.Write(name);
        return stream.ToArray();
    }

    private static byte[] BuildFramebufferUpdate(params (ushort X, ushort Y, ushort Width, ushort Height, byte[] Pixels)[] rectangles)
    {
        using var stream = new MemoryStream();
        Span<byte> updateHeader = stackalloc byte[4];
        BinaryPrimitives.WriteUInt16BigEndian(updateHeader[2..4], (ushort)rectangles.Length);
        stream.Write(updateHeader);
        Span<byte> rectangleHeader = stackalloc byte[12];
        foreach (var rectangle in rectangles)
        {
            rectangleHeader.Clear();
            BinaryPrimitives.WriteUInt16BigEndian(rectangleHeader[0..2], rectangle.X);
            BinaryPrimitives.WriteUInt16BigEndian(rectangleHeader[2..4], rectangle.Y);
            BinaryPrimitives.WriteUInt16BigEndian(rectangleHeader[4..6], rectangle.Width);
            BinaryPrimitives.WriteUInt16BigEndian(rectangleHeader[6..8], rectangle.Height);
            stream.Write(rectangleHeader);
            stream.Write(rectangle.Pixels);
        }
        return stream.ToArray();
    }

    private sealed class DuplexMemoryStream(byte[] input) : Stream
    {
        private readonly MemoryStream _input = new(input);
        private readonly MemoryStream _output = new();
        public byte[] Written => _output.ToArray();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => _input.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => _input.ReadAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => _output.Write(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => _output.WriteAsync(buffer, cancellationToken);
    }
}
