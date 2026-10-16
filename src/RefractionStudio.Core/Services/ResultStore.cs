using System.Text.Json;
using RefractionStudio.Analysis;
using RefractionStudio.Analysis.Layered;
using RefractionStudio.Analysis.Tomography;
using RefractionStudio.Domain;

namespace RefractionStudio.Core.Services;

/// <summary>Holds computed tomography results by id (in memory, serialised into the project's results folder).</summary>
public sealed class ResultStore : ITomographyResultSource
{
    private readonly Dictionary<Guid, TomographyResult> _results = [];
    private readonly Dictionary<Guid, byte[]> _serialized = [];
    private readonly Lock _lock = new();

    public TomographyResult? Get(TomographyResultRef reference) => Get(reference.ResultId);

    public TomographyResult? Get(Guid id)
    {
        lock (_lock)
        {
            if (_results.TryGetValue(id, out var r)) return r;
            if (_serialized.TryGetValue(id, out var bytes))
            {
                try
                {
                    r = TomographyResultSerializer.Deserialize(bytes);
                    _results[id] = r;
                    return r;
                }
                catch (InvalidDataException)
                {
                    return null;
                }
            }
            return null;
        }
    }

    public Guid Add(TomographyResult result)
    {
        var id = Guid.NewGuid();
        lock (_lock) _results[id] = result;
        return id;
    }

    /// <summary>Loads serialised results from a project (deserialised lazily).</summary>
    public void Load(IReadOnlyDictionary<Guid, byte[]> blobs)
    {
        lock (_lock)
        {
            _results.Clear();
            _serialized.Clear();
            foreach (var (id, b) in blobs) _serialized[id] = b;
        }
    }

    /// <summary>Serialised results referenced by the given states (current state and snapshots).</summary>
    public ImmutableDictionary<Guid, byte[]> Export(IEnumerable<ProjectState> states)
    {
        var ids = states.SelectMany(s => s.Scenarios).SelectMany(sc => sc.Lines.Values).Select(l => l.TomographyResult?.ResultId).Where(id => id is not null).Select(id => id!.Value).ToHashSet();
        var builder = ImmutableDictionary.CreateBuilder<Guid, byte[]>();
        lock (_lock)
        {
            foreach (var id in ids)
            {
                if (_serialized.TryGetValue(id, out var bytes)) builder[id] = bytes;
                else if (_results.TryGetValue(id, out var r))
                {
                    var b = TomographyResultSerializer.Serialize(r);
                    _serialized[id] = b;
                    builder[id] = b;
                }
            }
        }
        return builder.ToImmutable();
    }
}

/// <summary>Versioned binary serialisation of <see cref="TomographyResult"/> (full double precision).</summary>
public static class TomographyResultSerializer
{
    private const int Magic = 0x52534D54; // "RSMT"
    private const int Version = 1;

    public static byte[] Serialize(TomographyResult r)
    {
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms))
        {
            w.Write(Magic);
            w.Write(Version);
            var g = r.Model.Grid;
            w.Write(g.X0); w.Write(g.Dx); w.Write(g.Nx); w.Write(g.ZTop); w.Write(g.Dz); w.Write(g.Nz);
            foreach (var a in g.Active) w.Write(a);
            foreach (var e in g.Ground) w.Write(e);
            WriteDoubles(w, r.Model.Velocity);
            WriteDoubles(w, r.StartingModel.Velocity);
            WriteDoubles(w, r.Coverage);
            w.Write(r.Hits.Length);
            foreach (var h in r.Hits) w.Write(h);
            WriteDoubles(w, r.InvestigationDepth);
            w.Write(r.Iterations.Length);
            foreach (var it in r.Iterations) { w.Write(it.Iteration); w.Write(it.Lambda); w.Write(it.RmsSeconds); w.Write(it.Chi2); w.Write(it.StepLength); }
            w.Write(r.Residuals.Length);
            foreach (var res in r.Residuals) { w.Write(res.Key.ToString()); w.Write(res.Observed); w.Write(res.Predicted); }
            w.Write(r.Rays.Length);
            foreach (var ray in r.Rays)
            {
                w.Write(ray.Key.ToString());
                w.Write(ray.X.Length);
                foreach (var x in ray.X) w.Write(x);
                foreach (var z in ray.Z) w.Write(z);
            }
            w.Write(JsonSerializer.Serialize(r.Settings, DomainJson.Compact));
            w.Write(r.InputFingerprint);
            w.Write(r.Notes.Length);
            foreach (var n in r.Notes) w.Write(n);
            w.Write(r.StopReason);
            w.Write(r.Elapsed.Ticks);
        }
        return ms.ToArray();
    }

    public static TomographyResult Deserialize(byte[] bytes)
    {
        try
        {
            using var r = new BinaryReader(new MemoryStream(bytes));
            if (r.ReadInt32() != Magic) throw new InvalidDataException("Not a tomography result.");
            var version = r.ReadInt32();
            if (version != Version) throw new InvalidDataException($"Unsupported tomography result version {version}.");
            var x0 = r.ReadDouble(); var dx = r.ReadDouble(); var nx = r.ReadInt32(); var zTop = r.ReadDouble(); var dz = r.ReadDouble(); var nz = r.ReadInt32();
            var active = new bool[nx * nz];
            for (var i = 0; i < active.Length; i++) active[i] = r.ReadBoolean();
            var ground = new double[nx];
            for (var i = 0; i < nx; i++) ground[i] = r.ReadDouble();
            var grid = new ModelGrid(x0, dx, nx, zTop, dz, nz, [.. active], [.. ground]);
            var velocity = ReadDoubles(r);
            var start = ReadDoubles(r);
            var coverage = ReadDoubles(r);
            var hits = new int[r.ReadInt32()];
            for (var i = 0; i < hits.Length; i++) hits[i] = r.ReadInt32();
            var doi = ReadDoubles(r);
            var iterations = new TomographyIteration[r.ReadInt32()];
            for (var i = 0; i < iterations.Length; i++) iterations[i] = new TomographyIteration(r.ReadInt32(), r.ReadDouble(), r.ReadDouble(), r.ReadDouble(), r.ReadDouble());
            var residuals = new ModelResidual[r.ReadInt32()];
            for (var i = 0; i < residuals.Length; i++) residuals[i] = new ModelResidual(PickKey.Parse(r.ReadString()), r.ReadDouble(), r.ReadDouble());
            var rays = new RayPath[r.ReadInt32()];
            for (var i = 0; i < rays.Length; i++)
            {
                var key = PickKey.Parse(r.ReadString());
                var n = r.ReadInt32();
                var xs = new double[n];
                var zs = new double[n];
                for (var k = 0; k < n; k++) xs[k] = r.ReadDouble();
                for (var k = 0; k < n; k++) zs[k] = r.ReadDouble();
                rays[i] = new RayPath(key, xs, zs);
            }
            var settings = JsonSerializer.Deserialize<TomographySettings>(r.ReadString(), DomainJson.Options)!;
            var fingerprint = r.ReadString();
            var notes = new string[r.ReadInt32()];
            for (var i = 0; i < notes.Length; i++) notes[i] = r.ReadString();
            var stop = r.ReadString();
            var elapsed = TimeSpan.FromTicks(r.ReadInt64());
            return new TomographyResult(new VelocityModel(grid, [.. velocity]), new VelocityModel(grid, [.. start]), [.. coverage], [.. hits], [.. doi],
                [.. iterations], [.. residuals], [.. rays], settings, fingerprint, [.. notes], stop, elapsed);
        }
        catch (EndOfStreamException ex)
        {
            throw new InvalidDataException("The tomography result is truncated.", ex);
        }
    }

    private static void WriteDoubles(BinaryWriter w, ImmutableArray<double> values)
    {
        w.Write(values.Length);
        foreach (var v in values) w.Write(v);
    }

    private static double[] ReadDoubles(BinaryReader r)
    {
        var a = new double[r.ReadInt32()];
        for (var i = 0; i < a.Length; i++) a[i] = r.ReadDouble();
        return a;
    }
}
