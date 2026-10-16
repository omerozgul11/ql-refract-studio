using System.Buffers.Binary;
using RefractionStudio.Formats;

namespace RefractionStudio.Formats.Tests;

/// <summary>SEG-Y files built byte by byte (big-endian, IEEE floats, EBCDIC card-image header).</summary>
public sealed class SegYEdgeCaseTests
{
    private static byte[] Build(int samples, int traces, short delayMs, short timeScalar, int revisionMajor = 1)
    {
        var bytesPerTrace = 240 + samples * 4;
        var data = new byte[3600 + traces * bytesPerTrace];
        for (var i = 0; i < 3200; i++) data[i] = 0x40; // EBCDIC space
        for (var card = 0; card < 40; card++) data[card * 80] = 0xC3; // EBCDIC "C"
        var bin = data.AsSpan(3200, 400);
        BinaryPrimitives.WriteInt16BigEndian(bin[16..], 250); // 250 µs
        BinaryPrimitives.WriteUInt16BigEndian(bin[20..], (ushort)samples);
        BinaryPrimitives.WriteInt16BigEndian(bin[24..], 5); // IEEE float
        BinaryPrimitives.WriteUInt16BigEndian(bin[300..], (ushort)(revisionMajor << 8));
        BinaryPrimitives.WriteInt16BigEndian(bin[302..], 1); // fixed-length traces
        for (var t = 0; t < traces; t++)
        {
            var th = data.AsSpan(3600 + t * bytesPerTrace, 240);
            BinaryPrimitives.WriteInt32BigEndian(th[8..], 1); // FFID
            BinaryPrimitives.WriteInt32BigEndian(th[12..], t + 1); // channel
            BinaryPrimitives.WriteInt16BigEndian(th[108..], delayMs);
            BinaryPrimitives.WriteUInt16BigEndian(th[114..], (ushort)samples);
            BinaryPrimitives.WriteUInt16BigEndian(th[116..], 250);
            BinaryPrimitives.WriteInt16BigEndian(th[214..], timeScalar);
            var payload = data.AsSpan(3600 + t * bytesPerTrace + 240, samples * 4);
            for (var i = 0; i < samples; i++) BinaryPrimitives.WriteSingleBigEndian(payload[(4 * i)..], i % 7);
        }
        return data;
    }

    [Fact]
    public void More_than_32767_samples_per_trace_are_read()
    {
        var file = SegYReader.Parse(Build(40000, 2, 0, 0), "long.sgy", includeSamples: true);
        var trace = file.Records.Single().Traces[0];
        Assert.Equal(40000, trace.Samples.Length);
        Assert.Equal(6, trace.Samples[6]);
    }

    [Fact]
    public void Seg_y_with_another_extension_is_detected_by_content()
    {
        var registry = FormatRegistry.CreateDefault();
        var bytes = Build(100, 3, 0, 0);
        Assert.Equal("segy", registry.Detect(bytes, "shot_0001.dat")!.Id);
        Assert.Equal("segy", registry.Detect(bytes, "shot_0001")!.Id);
        // A random binary file with a .dat extension is not mistaken for SEG-Y.
        var noise = new byte[4096];
        new Random(1).NextBytes(noise);
        Assert.NotEqual("segy", registry.Detect(noise, "noise.dat")?.Id);
    }

    [Fact]
    public void Delay_uses_the_time_scalar_from_revision_1()
    {
        var multiplied = SegYReader.Parse(Build(100, 1, -5, 10), "a.sgy", includeSamples: false).Records.Single().Traces[0];
        Assert.Equal(-0.050, multiplied.Header.Delay, 9);
        var divided = SegYReader.Parse(Build(100, 1, -25, -10), "b.sgy", includeSamples: false).Records.Single().Traces[0];
        Assert.Equal(-0.0025, divided.Header.Delay, 9);
        var revision0 = SegYReader.Parse(Build(100, 1, -5, 10, revisionMajor: 0), "c.sgy", includeSamples: false).Records.Single().Traces[0];
        Assert.Equal(-0.005, revision0.Header.Delay, 9);
    }
}
