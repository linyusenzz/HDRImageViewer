using System.Buffers.Binary;
using HdrImageViewer.Services;
using Xunit;

namespace HdrImageViewer.Tests;

public class HeifAvifProbeTests
{
    [Fact]
    public async Task ProbeAsync_LargeHeif_SkipsWholeContainerAllocation()
    {
        var path = Path.Combine(Path.GetTempPath(), $"hdrimageviewer-{Guid.NewGuid():N}.avif");
        try
        {
            var header = new byte[16];
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0, 4), 16);
            "ftyp"u8.CopyTo(header.AsSpan(4, 4));
            "avif"u8.CopyTo(header.AsSpan(8, 4));
            await using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize: 4096, useAsync: true))
            {
                await stream.WriteAsync(header);
                stream.SetLength((64L * 1024L * 1024L) + 1L);
            }

            var probe = await HeifAvifProbe.ProbeAsync(path);

            Assert.True(probe.IsHeifFamily);
            Assert.True(probe.IsProbeLimited);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
