namespace RefractionStudio.Analysis.Tomography;

/// <summary>A ray from source to receiver: vertices and the cell each segment crosses.</summary>
/// <param name="X">Vertex x, source first.</param>
/// <param name="Z">Vertex elevation.</param>
/// <param name="Cells">Cell of segment i (between vertex i and i+1).</param>
public sealed record Ray(double[] X, double[] Z, int[] Cells)
{
    /// <summary>Length of each segment.</summary>
    public double SegmentLength(int i)
    {
        var dx = X[i + 1] - X[i];
        var dz = Z[i + 1] - Z[i];
        return Math.Sqrt(dx * dx + dz * dz);
    }
}

/// <summary>First-arrival travel time and ray for one receiver.</summary>
public sealed record TraceResult(double Time, Ray? Ray);

/// <summary>A travel-time solver for first arrivals in a gridded model.</summary>
public interface ITravelTimeSolver
{
    /// <summary>Computes first-arrival times (and rays) from one source to several receivers.</summary>
    IReadOnlyList<TraceResult> Solve(ModelGrid grid, ReadOnlySpan<double> slowness, (double X, double Z) source, IReadOnlyList<(double X, double Z)> receivers, bool traceRays);
}

/// <summary>
/// Shortest-path (network) ray tracing (Moser, T.J., 1991, Geophysics 56, 59–67). Graph nodes are the
/// cell corners plus <see cref="SecondaryNodes"/> evenly spaced nodes on every cell edge; every pair of
/// nodes on the boundary of an active cell is connected by a straight segment with travel time
/// length × cell slowness. Segments along an edge shared by two cells therefore use the faster cell,
/// which represents head waves along velocity contrasts. Dijkstra's algorithm gives first-arrival
/// times; rays follow the predecessor chain. Accuracy improves with the number of secondary nodes
/// (validated against analytic solutions in the test suite).
/// </summary>
public sealed class ShortestPathSolver : ITravelTimeSolver
{
    public ShortestPathSolver(int secondaryNodes = 3)
    {
        if (secondaryNodes is < 0 or > 12) throw new ArgumentOutOfRangeException(nameof(secondaryNodes), "Use 0–12 secondary nodes per edge.");
        SecondaryNodes = secondaryNodes;
    }

    public int SecondaryNodes { get; }

    private sealed class Layout
    {
        public required int Nx;
        public required int Nz;
        public required int Ns;
        public required int Corners;
        public required int HEdges;
        public required double[] NodeX;
        public required double[] NodeZ;
        public required int[][] CellNodes;
    }

    private Layout? _layout;
    private ModelGrid? _layoutGrid;
    private readonly Lock _lock = new();

    private Layout GetLayout(ModelGrid grid)
    {
        lock (_lock)
        {
            if (_layout is not null && ReferenceEquals(_layoutGrid, grid)) return _layout;
            int nx = grid.Nx, nz = grid.Nz, ns = SecondaryNodes;
            var corners = (nx + 1) * (nz + 1);
            var hEdges = nx * (nz + 1);
            var vEdges = (nx + 1) * nz;
            var total = corners + (hEdges + vEdges) * ns;
            var nodeX = new double[total];
            var nodeZ = new double[total];
            for (var j = 0; j <= nz; j++)
                for (var i = 0; i <= nx; i++)
                {
                    nodeX[j * (nx + 1) + i] = grid.X0 + i * grid.Dx;
                    nodeZ[j * (nx + 1) + i] = grid.ZTop - j * grid.Dz;
                }
            for (var j = 0; j <= nz; j++)
                for (var i = 0; i < nx; i++)
                    for (var q = 0; q < ns; q++)
                    {
                        var idx = corners + (j * nx + i) * ns + q;
                        nodeX[idx] = grid.X0 + (i + (q + 1.0) / (ns + 1)) * grid.Dx;
                        nodeZ[idx] = grid.ZTop - j * grid.Dz;
                    }
            for (var j = 0; j < nz; j++)
                for (var i = 0; i <= nx; i++)
                    for (var q = 0; q < ns; q++)
                    {
                        var idx = corners + hEdges * ns + (j * (nx + 1) + i) * ns + q;
                        nodeX[idx] = grid.X0 + i * grid.Dx;
                        nodeZ[idx] = grid.ZTop - (j + (q + 1.0) / (ns + 1)) * grid.Dz;
                    }
            var cellNodes = new int[nx * nz][];
            for (var j = 0; j < nz; j++)
                for (var i = 0; i < nx; i++)
                {
                    var list = new int[4 + 4 * ns];
                    var k = 0;
                    list[k++] = j * (nx + 1) + i;
                    list[k++] = j * (nx + 1) + i + 1;
                    list[k++] = (j + 1) * (nx + 1) + i;
                    list[k++] = (j + 1) * (nx + 1) + i + 1;
                    for (var q = 0; q < ns; q++)
                    {
                        list[k++] = corners + (j * nx + i) * ns + q;                       // top edge
                        list[k++] = corners + ((j + 1) * nx + i) * ns + q;                 // bottom edge
                        list[k++] = corners + hEdges * ns + (j * (nx + 1) + i) * ns + q;   // left edge
                        list[k++] = corners + hEdges * ns + (j * (nx + 1) + i + 1) * ns + q; // right edge
                    }
                    cellNodes[j * nx + i] = list;
                }
            _layout = new Layout { Nx = nx, Nz = nz, Ns = ns, Corners = corners, HEdges = hEdges, NodeX = nodeX, NodeZ = nodeZ, CellNodes = cellNodes };
            _layoutGrid = grid;
            return _layout;
        }
    }

    /// <summary>Cells adjacent to a node (up to 4).</summary>
    private static int AdjacentCells(Layout l, int node, Span<int> cells)
    {
        var count = 0;
        void Add(int i, int j, Span<int> c)
        {
            if (i >= 0 && i < l.Nx && j >= 0 && j < l.Nz) c[count++] = j * l.Nx + i;
        }
        if (node < l.Corners)
        {
            var j = node / (l.Nx + 1);
            var i = node % (l.Nx + 1);
            Add(i - 1, j - 1, cells); Add(i, j - 1, cells); Add(i - 1, j, cells); Add(i, j, cells);
        }
        else if (node < l.Corners + l.HEdges * l.Ns)
        {
            var e = (node - l.Corners) / l.Ns;
            var j = e / l.Nx;
            var i = e % l.Nx;
            Add(i, j - 1, cells); Add(i, j, cells);
        }
        else
        {
            var e = (node - l.Corners - l.HEdges * l.Ns) / l.Ns;
            var j = e / (l.Nx + 1);
            var i = e % (l.Nx + 1);
            Add(i - 1, j, cells); Add(i, j, cells);
        }
        return count;
    }

    /// <summary>Active cells whose closed rectangle contains the point; snaps surface points down onto the top active cell.</summary>
    private static List<int> CellsContaining(ModelGrid grid, ReadOnlySpan<double> slowness, double x, ref double z)
    {
        var result = new List<int>(4);
        const double eps = 1e-7;
        var c0 = (int)Math.Floor((x - grid.X0) / grid.Dx);
        var cols = new List<int>();
        for (var c = c0 - 1; c <= c0 + 1; c++)
        {
            if (c < 0 || c >= grid.Nx) continue;
            var left = grid.X0 + c * grid.Dx;
            if (x >= left - eps * grid.Dx && x <= left + grid.Dx + eps * grid.Dx) cols.Add(c);
        }
        if (cols.Count == 0) return result;

        // Snap a point that lies above the active surface of its column(s) down to the highest active cell top.
        var highestTop = double.NegativeInfinity;
        foreach (var c in cols)
        {
            var r = grid.TopActiveRow(c);
            if (r >= 0) highestTop = Math.Max(highestTop, grid.ZTop - r * grid.Dz);
        }
        if (z > highestTop) z = highestTop;

        foreach (var c in cols)
        {
            for (var r = 0; r < grid.Nz; r++)
            {
                var top = grid.ZTop - r * grid.Dz;
                if (z <= top + eps * grid.Dz && z >= top - grid.Dz - eps * grid.Dz)
                {
                    var idx = grid.Index(c, r);
                    if (grid.Active[idx] && double.IsFinite(slowness[idx])) result.Add(idx);
                }
            }
        }
        if (result.Count == 0)
        {
            // Point lies in an inactive cell below a step: use the top active cell of its column.
            var c = grid.ColumnOf(x);
            var r = grid.TopActiveRow(c);
            if (r >= 0)
            {
                z = Math.Min(z, grid.ZTop - r * grid.Dz);
                result.Add(grid.Index(c, r));
            }
        }
        return result;
    }

    public IReadOnlyList<TraceResult> Solve(ModelGrid grid, ReadOnlySpan<double> slowness, (double X, double Z) source, IReadOnlyList<(double X, double Z)> receivers, bool traceRays)
    {
        var l = GetLayout(grid);
        var n = l.NodeX.Length;
        var dist = new double[n];
        var pred = new int[n];
        var predCell = new int[n];
        Array.Fill(dist, double.PositiveInfinity);
        Array.Fill(pred, -2);
        var heap = new PriorityQueue<int, double>();

        var sz = source.Z;
        var sourceCells = CellsContaining(grid, slowness, source.X, ref sz);
        if (sourceCells.Count == 0) return [.. receivers.Select(_ => new TraceResult(double.PositiveInfinity, null))];
        foreach (var c in sourceCells)
        {
            foreach (var v in l.CellNodes[c])
            {
                var dx = l.NodeX[v] - source.X;
                var dz = l.NodeZ[v] - sz;
                var t = Math.Sqrt(dx * dx + dz * dz) * slowness[c];
                if (t < dist[v])
                {
                    dist[v] = t;
                    pred[v] = -1;
                    predCell[v] = c;
                    heap.Enqueue(v, t);
                }
            }
        }

        Span<int> adjacent = stackalloc int[4];
        while (heap.TryDequeue(out var u, out var du))
        {
            if (du > dist[u]) continue;
            var count = AdjacentCells(l, u, adjacent);
            var ux = l.NodeX[u];
            var uz = l.NodeZ[u];
            for (var a = 0; a < count; a++)
            {
                var c = adjacent[a];
                if (!grid.Active[c]) continue;
                var s = slowness[c];
                if (!double.IsFinite(s)) continue;
                foreach (var v in l.CellNodes[c])
                {
                    if (v == u) continue;
                    var dx = l.NodeX[v] - ux;
                    var dz = l.NodeZ[v] - uz;
                    var t = du + Math.Sqrt(dx * dx + dz * dz) * s;
                    if (t < dist[v])
                    {
                        dist[v] = t;
                        pred[v] = u;
                        predCell[v] = c;
                        heap.Enqueue(v, t);
                    }
                }
            }
        }

        var results = new TraceResult[receivers.Count];
        for (var k = 0; k < receivers.Count; k++)
        {
            var (rx, rzIn) = receivers[k];
            var rz = rzIn;
            var cells = CellsContaining(grid, slowness, rx, ref rz);
            var best = double.PositiveInfinity;
            var bestNode = -1;
            var bestCell = -1;
            foreach (var c in cells)
            {
                // Direct segment when source and receiver share a cell.
                if (sourceCells.Contains(c))
                {
                    var t0 = Math.Sqrt((rx - source.X) * (rx - source.X) + (rz - sz) * (rz - sz)) * slowness[c];
                    if (t0 < best) { best = t0; bestNode = -1; bestCell = c; }
                }
                foreach (var v in l.CellNodes[c])
                {
                    var t = dist[v] + Math.Sqrt((l.NodeX[v] - rx) * (l.NodeX[v] - rx) + (l.NodeZ[v] - rz) * (l.NodeZ[v] - rz)) * slowness[c];
                    if (t < best) { best = t; bestNode = v; bestCell = c; }
                }
            }
            Ray? ray = null;
            if (traceRays && double.IsFinite(best) && bestCell >= 0)
            {
                var xs = new List<double> { rx };
                var zs = new List<double> { rz };
                var cs = new List<int> { bestCell };
                var node = bestNode;
                var guard = 0;
                while (node >= 0 && guard++ < n)
                {
                    xs.Add(l.NodeX[node]);
                    zs.Add(l.NodeZ[node]);
                    cs.Add(predCell[node]);
                    node = pred[node];
                }
                xs.Add(source.X);
                zs.Add(sz);
                xs.Reverse(); zs.Reverse(); cs.Reverse();
                // After reversal, cs[i] is the cell of segment i (vertex i → i+1); drop the extra leading entry.
                ray = new Ray([.. xs], [.. zs], [.. cs.Skip(cs.Count - (xs.Count - 1))]);
            }
            results[k] = new TraceResult(best, ray);
        }
        return results;
    }
}
