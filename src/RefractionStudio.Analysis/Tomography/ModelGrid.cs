using RefractionStudio.Domain;

namespace RefractionStudio.Analysis.Tomography;

/// <summary>
/// A regular 2-D grid of rectangular cells covering a line. Row 0 is the top row; z is elevation
/// (positive up). Cells whose centre lies above the ground surface are inactive (air) and carry no
/// velocity. Cell index = row·Nx + column.
/// </summary>
public sealed record ModelGrid(double X0, double Dx, int Nx, double ZTop, double Dz, int Nz, ImmutableArray<bool> Active, ImmutableArray<double> Ground)
{
    public int CellCount => Nx * Nz;

    public int Index(int column, int row) => row * Nx + column;

    public (int Column, int Row) ColumnRow(int index) => (index % Nx, index / Nx);

    public double CenterX(int column) => X0 + (column + 0.5) * Dx;

    public double CenterZ(int row) => ZTop - (row + 0.5) * Dz;

    public double XMax => X0 + Nx * Dx;

    public double ZBottom => ZTop - Nz * Dz;

    /// <summary>Depth below ground of a cell centre.</summary>
    public double DepthOf(int column, int row) => Ground[column] - CenterZ(row);

    /// <summary>Column containing x (clamped to the grid).</summary>
    public int ColumnOf(double x) => Math.Clamp((int)Math.Floor((x - X0) / Dx), 0, Nx - 1);

    /// <summary>Row containing elevation z (clamped).</summary>
    public int RowOf(double z) => Math.Clamp((int)Math.Floor((ZTop - z) / Dz), 0, Nz - 1);

    /// <summary>Top active row of a column, or −1 if none.</summary>
    public int TopActiveRow(int column)
    {
        for (var r = 0; r < Nz; r++)
            if (Active[Index(column, r)]) return r;
        return -1;
    }

    /// <summary>
    /// Builds a grid for a line: extent from the outermost shot/receiver, top at the highest ground
    /// point, bottom <paramref name="maxDepth"/> below the lowest ground point.
    /// </summary>
    public static ModelGrid Create(Topography topography, double xMin, double xMax, double dx, double dz, double maxDepth)
    {
        if (!(dx > 0 && dz > 0 && maxDepth > 0 && xMax > xMin)) throw new ArgumentException("Invalid grid dimensions.");
        var nx = Math.Max(1, (int)Math.Ceiling((xMax - xMin) / dx - 1e-9));
        var x0 = 0.5 * (xMin + xMax) - nx * dx / 2;
        var ground = Enumerable.Range(0, nx).Select(i => topography.ElevationAt(x0 + (i + 0.5) * dx)).ToImmutableArray();
        // Top: highest ground over the extent including the cell edges (so that every surface point is inside).
        var maxGround = Enumerable.Range(0, nx + 1).Select(i => topography.ElevationAt(x0 + i * dx)).Max();
        var minGround = ground.Min();
        var zTop = maxGround;
        var nz = Math.Max(1, (int)Math.Ceiling((zTop - (minGround - maxDepth)) / dz - 1e-9));
        var active = new bool[nx * nz];
        for (var j = 0; j < nz; j++)
            for (var i = 0; i < nx; i++)
                active[j * nx + i] = zTop - (j + 0.5) * dz <= ground[i] + 1e-9;
        // Every column needs at least one active cell.
        for (var i = 0; i < nx; i++)
            if (!Enumerable.Range(0, nz).Any(j => active[j * nx + i])) active[(nz - 1) * nx + i] = true;
        return new ModelGrid(x0, dx, nx, zTop, dz, nz, [.. active], ground);
    }
}

/// <summary>A cell-based velocity model on a grid (NaN in inactive cells).</summary>
public sealed record VelocityModel(ModelGrid Grid, ImmutableArray<double> Velocity)
{
    public double VelocityAt(int column, int row) => Velocity[Grid.Index(column, row)];

    /// <summary>Velocity at depth below ground at position x (nearest cell), or null outside/inactive.</summary>
    public double? VelocityAtDepth(double x, double depth)
    {
        if (x < Grid.X0 || x > Grid.XMax) return null;
        var c = Grid.ColumnOf(x);
        var z = Grid.Ground[c] - depth;
        if (z < Grid.ZBottom || z > Grid.ZTop) return null;
        var r = Grid.RowOf(z);
        var v = Velocity[Grid.Index(c, r)];
        return Grid.Active[Grid.Index(c, r)] && double.IsFinite(v) ? v : null;
    }
}
