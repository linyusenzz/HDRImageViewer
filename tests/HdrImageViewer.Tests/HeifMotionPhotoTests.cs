using System.Buffers.Binary;
using System.Text;
using HdrImageViewer.Models;
using HdrImageViewer.Services;
using Xunit;

namespace HdrImageViewer.Tests;

public sealed class HeifMotionPhotoTests
{
    [Theory]
    [InlineData("avif", false)]
    [InlineData("heic", false)]
    [InlineData("avif", true)]
    [InlineData("heic", true)]
    public async Task FinalMpvd_DetectsExactVideoRange(string brand, bool extendedHeader)
    {
        var video = Box("ftyp", Encoding.ASCII.GetBytes("isom\0\0\0\0isom"));
        var image = Image(brand, Box("mdat", new byte[128]), Box("mpvd", video, extendedHeader));
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + "." + brand);
        try
        {
            await File.WriteAllBytesAsync(path, image);
            var kind = await FileSignatureProbe.DetectAsync(path);
            Assert.Equal(FileContainerKind.HeifFamily, kind);
            foreach (var bytes in new byte[]?[] { null, image })
            {
                var media = await LivePhotoProbe.ProbeAsync(path, kind, bytes);
                Assert.NotNull(media);
                Assert.Equal(CompanionMediaKind.AndroidMotionPhoto, media.Kind);
                Assert.True(media.IsEmbedded);
                Assert.Equal(image.Length - video.Length, media.EmbeddedOffset);
                Assert.Equal(video.Length, media.EmbeddedLength);
                Assert.NotNull(media.VideoProbe);
                Assert.Equal(video, image.AsSpan((int)media.EmbeddedOffset!.Value).ToArray());
            }
        }
        finally { File.Delete(path); }
    }

    public static IEnumerable<object[]> InvalidContainers()
    {
        var video = Box("ftyp", Encoding.ASCII.GetBytes("isom\0\0\0\0"));
        var mpvd = Box("mpvd", video);
        yield return [Image("avif", Box("mdat", mpvd))]; // A signature inside pixels is not a box.
        yield return [Image("avif", mpvd, Box("free", []))]; // Must be last.
        yield return [Image("avif", Box("mpvd", Encoding.ASCII.GetBytes("not a video file")))];
        yield return [Image("avif", Box("mpvd", []))];
        yield return [Image("avif", mpvd[..^1])]; // Truncated payload.
        var zero = (byte[])mpvd.Clone();
        BinaryPrimitives.WriteUInt32BigEndian(zero, 0);
        yield return [Image("avif", zero)];
        var undersized = (byte[])mpvd.Clone();
        BinaryPrimitives.WriteUInt32BigEndian(undersized, 4);
        yield return [Image("avif", undersized)];
        var overflow = Box("mpvd", video, extended: true);
        BinaryPrimitives.WriteUInt64BigEndian(overflow.AsSpan(8), ulong.MaxValue);
        yield return [Image("avif", overflow)];
        yield return [Image("avif", overflow[..12])]; // Incomplete extended size.
        var badFtyp = (byte[])video.Clone();
        BinaryPrimitives.WriteUInt32BigEndian(badFtyp, 1024);
        yield return [Image("avif", Box("mpvd", badFtyp))];
        yield return [Image("avif", Box("mpvd", video), mpvd)]; // Multiple mpvd boxes.
        var consumesRest = Box("mdat", mpvd);
        BinaryPrimitives.WriteUInt32BigEndian(consumesRest, 0);
        yield return [Image("avif", consumesRest)];
    }

    [Theory]
    [MemberData(nameof(InvalidContainers))]
    public async Task InvalidOrNestedMpvd_IsNotMotionPhoto(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        Assert.Null(await LivePhotoProbe.ReadHeifMotionVideoRangeAsync(stream, CancellationToken.None));
    }

    [Fact]
    public async Task LargeImagePayload_IsSkippedWithoutReadingOrAllocatingIt()
    {
        var video = Box("ftyp", Encoding.ASCII.GetBytes("isom\0\0\0\0"));
        var prefix = Image("avif", Box("mdat", [], extended: true));
        const long mdatLength = (long)int.MaxValue + 1024;
        BinaryPrimitives.WriteUInt64BigEndian(prefix.AsSpan(prefix.Length - 8), (ulong)mdatLength);
        var tailOffset = prefix.Length - 16 + mdatLength;
        using var stream = new SegmentedStream(prefix, tailOffset, Box("mpvd", video));
        var range = await LivePhotoProbe.ReadHeifMotionVideoRangeAsync(stream, CancellationToken.None);
        Assert.NotNull(range);
        Assert.Equal(tailOffset + 8, range.Value.Offset);
        Assert.Equal(video.Length, range.Value.Length);
        Assert.InRange(stream.BytesRead, 1, 64);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            LivePhotoProbe.ReadHeifMotionVideoRangeAsync(stream, cancellation.Token));
    }

    private static byte[] Image(string brand, params byte[][] boxes) =>
        [.. Box("ftyp", Encoding.ASCII.GetBytes(brand + "\0\0\0\0" + brand)), .. boxes.SelectMany(box => box)];

    private static byte[] Box(string type, byte[] payload, bool extended = false)
    {
        var header = extended ? 16 : 8;
        var result = new byte[header + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(result, extended ? 1u : (uint)result.Length);
        Encoding.ASCII.GetBytes(type, result.AsSpan(4, 4));
        if (extended) BinaryPrimitives.WriteUInt64BigEndian(result.AsSpan(8), (ulong)result.Length);
        payload.CopyTo(result, header);
        return result;
    }

    private sealed class SegmentedStream(byte[] prefix, long tailOffset, byte[] tail) : Stream
    {
        public int BytesRead { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => tailOffset + tail.Length;
        public override long Position { get; set; }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var segment = Position < prefix.Length ? prefix : tail;
            var index = Position < prefix.Length ? Position : Position - tailOffset;
            if (index < 0) throw new InvalidOperationException("The image payload must never be read.");
            var read = (int)Math.Min(count, segment.Length - index);
            segment.AsSpan((int)index, read).CopyTo(buffer.AsSpan(offset, read));
            Position += read;
            BytesRead += read;
            return read;
        }
        public override long Seek(long offset, SeekOrigin origin) => Position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => Position + offset,
            SeekOrigin.End => Length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };
        public override void Flush() => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
