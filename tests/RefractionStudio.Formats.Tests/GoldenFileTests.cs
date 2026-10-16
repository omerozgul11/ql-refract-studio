using RefractionStudio.Formats;

namespace RefractionStudio.Formats.Tests;

/// <summary>Golden files are produced by Golden/make_golden.py, an independent byte-level implementation.</summary>
public class GoldenFileTests
{
    private static readonly double[] IntSamples = [0, 1, -1, 1000, -1000, 32767, -32768, 12345];
    private static readonly double[] BigSamples = [0, 100000, -100000, 2000000, -2000000, 7, -7, 65536];

    private static string Golden(string name) => Path.Combine(AppContext.BaseDirectory, "Golden", name);

    private static async Task<DecodedFile> Read(string name, bool samples = true)
    {
        await using var fs = File.OpenRead(Golden(name));
        var reader = new Seg2Reader();
        return samples ? await reader.ReadAsync(fs, name) : await reader.ReadMetadataAsync(fs, name);
    }

    [Fact]
    public async Task Little_endian_int16_with_full_metadata()
    {
        var file = await Read("le_int16.sg2");
        Assert.Equal("seg2", file.FormatId);
        Assert.Contains("little-endian", file.FormatDescription);
        var record = Assert.Single(file.Records);
        Assert.Equal(3, record.Traces.Length);
        Assert.Equal(7, record.FieldRecordNumber);
        Assert.Equal(new DateTimeOffset(2026, 10, 10, 14, 5, 30, TimeSpan.Zero), record.AcquiredAt);
        Assert.Equal("GOLDEN TEST 24", record.Keywords["INSTRUMENT"]);
        Assert.Equal(-1.0, record.SourceX);

        for (var i = 0; i < 3; i++)
        {
            var t = record.Traces[i];
            Assert.Equal(i + 1, t.Header.Channel);
            Assert.Equal(0.00025, t.Header.SampleInterval);
            Assert.Equal(-0.005, t.Header.Delay);
            Assert.Equal(i * 2.0, t.Header.ReceiverX);
            Assert.Equal(-1.0, t.Header.SourceX);
            Assert.Equal(3, t.Header.Stack);
            Assert.Equal(8, t.Header.SampleCount);
            Assert.Equal("int16", t.Header.SampleFormat);
            Assert.Equal(IntSamples, t.Samples);
        }
        Assert.Equal(-0.005 + 7 * 0.00025, record.Traces[0].Header.EndTime, 12);
    }

    [Fact]
    public async Task Big_endian_int32_applies_the_descaling_factor()
    {
        var file = await Read("be_int32.sg2");
        Assert.Contains("big-endian", file.FormatDescription);
        foreach (var t in file.Records[0].Traces)
            Assert.Equal(BigSamples.Select(v => v * 0.5).ToArray(), t.Samples);
    }

    [Fact]
    public async Task Float_encodings()
    {
        var f32 = await Read("le_float32.sg2");
        Assert.Equal([0.5, -0.25, (double)1.5e-3f, 3.0], f32.Records[0].Traces[1].Samples);
        var f64 = await Read("be_float64.sg2");
        Assert.Equal([1e-9, -2.5, 3.25, 0.0], f64.Records[0].Traces[0].Samples);
    }

    [Fact]
    public async Task Packed_20_bit_encoding()
    {
        var file = await Read("le_20bit.sg2");
        Assert.Equal("20-bit float", file.Records[0].Traces[0].Header.SampleFormat);
        Assert.Equal(BigSamples, file.Records[0].Traces[0].Samples);
    }

    [Fact]
    public async Task Metadata_read_returns_headers_without_samples()
    {
        var file = await Read("le_int16.sg2", samples: false);
        Assert.All(file.Records[0].Traces, t => Assert.Empty(t.Samples));
        Assert.Equal(8, file.Records[0].Traces[0].Header.SampleCount);
    }

    [Theory]
    [InlineData("truncated.sg2", "truncated")]
    [InlineData("bad_descriptor.sg2", "trace descriptor")]
    [InlineData("unsupported_code.sg2", "data format code 9")]
    [InlineData("no_sample_interval.sg2", "SAMPLE_INTERVAL")]
    [InlineData("not_seg2.sg2", "not a SEG-2 file")]
    public async Task Malformed_files_fail_gracefully_with_a_user_message(string name, string expected)
    {
        var ex = await Assert.ThrowsAsync<SeismicFormatException>(() => Read(name));
        Assert.Contains(expected, ex.Message);
    }

    [Fact]
    public async Task Segy_ibm_big_endian_groups_traces_by_field_record()
    {
        await using var fs = File.OpenRead(Golden("segy_ibm_be.sgy"));
        var file = await new SegYReader().ReadAsync(fs, "segy_ibm_be.sgy");
        Assert.Equal(2, file.Records.Length);
        Assert.Equal(101, file.Records[0].FieldRecordNumber);
        Assert.Equal(-2.0, file.Records[0].SourceX);
        Assert.Equal(50.0, file.Records[1].SourceX);
        var t = file.Records[1].Traces[2];
        Assert.Equal(3, t.Header.Channel);
        Assert.Equal(5.0, t.Header.ReceiverX);
        Assert.Equal(0.0005, t.Header.SampleInterval, 12);
        Assert.Equal(0.010, t.Header.Delay, 12);
        Assert.Equal(2, t.Header.Stack);
        Assert.Equal([3.0, -7.5, 0.46875, 300.0], t.Samples);
    }

    [Fact]
    public void Registry_detects_formats_by_content()
    {
        var registry = FormatRegistry.CreateDefault();
        var seg2 = File.ReadAllBytes(Golden("le_int16.sg2"));
        Assert.Equal("seg2", registry.Detect(seg2, "anything.dat")!.Id);
        var segy = File.ReadAllBytes(Golden("segy_ibm_be.sgy"));
        Assert.Equal("segy", registry.Detect(segy, "x.sgy")!.Id);
        Assert.Null(registry.Detect(File.ReadAllBytes(Golden("not_seg2.sg2")), "not_seg2.sg2"));
    }
}

public class Seg2WriterRoundTripTests
{
    [Theory]
    [InlineData((byte)1, true)]
    [InlineData((byte)1, false)]
    [InlineData((byte)2, true)]
    [InlineData((byte)2, false)]
    [InlineData((byte)3, true)]
    [InlineData((byte)3, false)]
    [InlineData((byte)4, true)]
    [InlineData((byte)5, false)]
    public void Writer_output_is_read_back_exactly(byte code, bool little)
    {
        var samples = new double[] { 0, 5, -5, 1200, -1200, 30000, -30000, 17 };
        if (code is 2 or 3 or 5) samples = [.. samples.Select(s => s * 37)];
        var traces = Enumerable.Range(1, 4).Select(c => new Seg2TraceToWrite(
            new Dictionary<string, string> { ["SAMPLE_INTERVAL"] = "0.000125", ["DELAY"] = "0", ["RECEIVER_LOCATION"] = (c * 1.5).ToString(System.Globalization.CultureInfo.InvariantCulture) },
            samples)).ToList();
        var bytes = Seg2Writer.Write(new Dictionary<string, string> { ["NOTE"] = "round trip" }, traces, code, little);
        var file = Seg2Reader.Parse(bytes, "rt.sg2", includeSamples: true);
        Assert.Equal("round trip", file.Records[0].Keywords["NOTE"]);
        Assert.Equal(4, file.Records[0].Traces.Length);
        foreach (var t in file.Records[0].Traces)
        {
            Assert.Equal(0.000125, t.Header.SampleInterval);
            var expected = code == 4 ? samples.Select(s => (double)(float)s).ToArray() : samples;
            if (code == 3)
            {
                // 20-bit encoding keeps a 16-bit mantissa: quantisation of at most 2^-15 of the value.
                for (var i = 0; i < samples.Length; i++) Assert.Equal(samples[i], t.Samples[i], Math.Abs(samples[i]) / 32768.0);
            }
            else
            {
                Assert.Equal(expected, t.Samples);
            }
        }
        Assert.Equal(6.0, file.Records[0].Traces[3].Header.ReceiverX);
    }

    [Fact]
    public void Ibm_float_conversion_round_trips()
    {
        foreach (var v in new[] { 1.0, -2.5, 0.15625, 118.625, -1e-5, 3.0e6 })
            Assert.Equal(v, SegYReader.IbmToDouble(SegYReader.DoubleToIbm(v)), Math.Abs(v) * 1e-6);
        Assert.Equal(118.625, SegYReader.IbmToDouble(0x4276A000)); // textbook example
    }
}
