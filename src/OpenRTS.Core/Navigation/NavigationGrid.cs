namespace OpenRTS.Core.Navigation;

public readonly record struct CellOverride(bool? Traverse = null, bool? CanStop = null, byte? Terrain = null);
public readonly record struct GridCell(int X, int Y);
public readonly record struct SurfaceSample(int Elevation = 0, int Slope = 0, int Depth = 0);
/// <summary>Height change across a cell along each positive ground axis. The sample elevation is at the center.</summary>
public readonly record struct SurfaceRise(int X = 0, int Y = 0);

public enum SurfaceCorner { None, NorthWest, NorthEast, SouthEast, SouthWest }

/// <summary>Two flat triangles separated by a diagonal wall. The sample stores the lower elevation.</summary>
public readonly record struct SurfaceCut(SurfaceCorner UpperCorner, int UpperElevation, byte? UpperTerrain = null)
{
    public bool DescendingDiagonal => UpperCorner is SurfaceCorner.NorthEast or SurfaceCorner.SouthWest;

    public bool IsUpper(int x, int y, int size) => UpperCorner != SurfaceCorner.None && SignedDistance(x, y, size) >= 0;

    /// <summary>Unnormalized signed distance from the diagonal in local coordinates.</summary>
    public long SignedDistance(long x, long y, int size) => UpperCorner switch
    {
        SurfaceCorner.NorthWest => size - x - y,
        SurfaceCorner.NorthEast => x - y,
        SurfaceCorner.SouthEast => x + y - size,
        SurfaceCorner.SouthWest => y - x,
        _ => 0
    };
}

/// <summary>Immutable terrain sampling layer, independent of exact body positions and collision bins.</summary>
public sealed partial class NavigationGrid
{
    public const int MaximumCells = 16 * 1024 * 1024;
    private readonly uint[] _blocked;
    private readonly byte[]? _terrain;
    private readonly uint[]? _noStop;
    private readonly SurfaceSample[]? _surface;
    private readonly SurfaceRise[]? _rises;
    private readonly Dictionary<int, SurfaceCut>? _cuts;
    private readonly HashSet<int>? _paintedCuts;
    private readonly int _maximumUpperTerrain;
    private readonly WorldBox[] _obstacles;
    private readonly Dictionary<int, int[]> _obstacleCells;
    public bool HasGeometry => _surface is not null || _obstacles.Length > 0;
    public bool HasSurface => _surface is not null;
    public bool HasSlopedSurface => _rises is not null;
    public bool HasSurfaceCuts => _cuts is not null;
    public IReadOnlyList<WorldBox> Obstacles => Array.AsReadOnly(_obstacles);
    public bool HasTerrain => _terrain is not null || HasSplitTerrain;
    public bool HasSplitTerrain { get; }
    public bool HasStoppingRestrictions => _noStop is not null;
    public int MaximumTerrain { get; }
    public int Width { get; }
    public int Height { get; }
    public int CellSize { get; }
    public int CellCount => Width * Height;
    public int Revision { get; }

    public NavigationGrid(int width, int height, IEnumerable<GridCell>? blocked = null, int cellSize = WorldPosition.UnitsPerCell,
        IEnumerable<byte>? terrain = null, IEnumerable<SurfaceSample>? surface = null, IEnumerable<WorldBox>? obstacles = null,
        IEnumerable<SurfaceRise>? rises = null, IReadOnlyDictionary<int, SurfaceCut>? cuts = null)
    {
        if (cellSize < 1 || cellSize > WorldPosition.MaximumExtent) throw new ArgumentOutOfRangeException(nameof(cellSize));
        if (width < 1 || width > WorldPosition.MaximumExtent / cellSize) throw new ArgumentOutOfRangeException(nameof(width));
        if (height < 1 || height > WorldPosition.MaximumExtent / cellSize || (long)width * height > MaximumCells)
            throw new ArgumentOutOfRangeException(nameof(height), "Navigation layer exceeds its cell budget.");
        Width = width;
        Height = height;
        CellSize = cellSize;
        _blocked = new uint[(CellCount + 31) / 32];
        if (terrain is not null)
        {
            _terrain = terrain.ToArray();
            if (_terrain.Length != CellCount) throw new ArgumentException("Terrain count must match the grid.", nameof(terrain));
            MaximumTerrain = _terrain.Max();
        }
        _surface = surface?.ToArray();
        if (_surface is not null && (_surface.Length != CellCount || _surface.Any(v => v.Elevation is < -1048576 or > 1048576 || v.Slope < 0 || v.Depth < 0))) throw new ArgumentException("Invalid surface samples.", nameof(surface));
        if (rises is not null)
        {
            var values = rises.ToArray();
            if (_surface is null || values.Length != CellCount)
                throw new ArgumentException("Surface rises require one sample and rise per cell.", nameof(rises));
            for (int i = 0; i < values.Length; i++)
            {
                long x = Math.Abs((long)values[i].X), y = Math.Abs((long)values[i].Y);
                if (2 * Math.Abs((long)_surface[i].Elevation) + x + y > 2L * 1048576)
                    throw new ArgumentException("Sloped surface corners exceed the elevation limits.", nameof(rises));
                long slope = x == 0 || y == 0 ? x + y : (long)Math.Ceiling(Math.Sqrt(x * x + y * y));
                if (slope > _surface[i].Slope) _surface[i] = _surface[i] with { Slope = (int)slope };
            }
            if (values.Any(rise => rise != default)) _rises = values;
        }
        if (cuts is { Count: > 0 })
        {
            _cuts = new(cuts.Count);
            foreach (var (cell, cut) in cuts)
            {
                if (_surface is null || (uint)cell >= CellCount || SurfaceRiseAt(cell) != default ||
                    cut.UpperCorner is < SurfaceCorner.NorthWest or > SurfaceCorner.SouthWest ||
                    cut.UpperElevation < _surface[cell].Elevation || cut.UpperElevation > 1048576 ||
                    cut.UpperElevation == _surface[cell].Elevation && (!cut.UpperTerrain.HasValue || cut.UpperTerrain == TerrainAt(cell)))
                    throw new ArgumentException("A cut requires two bounded flat heights and an upper corner.", nameof(cuts));
                _cuts.Add(cell, cut);
                if (cut.UpperTerrain is { } upperTerrain)
                {
                    HasSplitTerrain = true;
                    _maximumUpperTerrain = Math.Max(_maximumUpperTerrain, upperTerrain);
                }
            }
            MaximumTerrain = Math.Max(MaximumTerrain, _maximumUpperTerrain);
        }
        _obstacles = obstacles?.Distinct().OrderBy(b => b.MinY).ThenBy(b => b.MinX).ThenBy(b => b.MaxY).ThenBy(b => b.MaxX).ToArray() ?? [];
        if (_obstacles.Length > 65536 || _obstacles.Any(b => b.MinX < 0 || b.MinY < 0 || b.MaxX > Width * CellSize || b.MaxY > Height * CellSize || b.MinX >= b.MaxX || b.MinY >= b.MaxY)) throw new ArgumentException("Invalid partial obstacles.", nameof(obstacles));
        var obstacleCells = new Dictionary<int, List<int>>();
        for (int i = 0; i < _obstacles.Length; i++)
        {
            var box = _obstacles[i];
            for (int y = box.MinY / CellSize; y <= Math.Min(Height - 1, box.MaxY / CellSize); y++)
                for (int x = box.MinX / CellSize; x <= Math.Min(Width - 1, box.MaxX / CellSize); x++)
                { int c = y * Width + x; if (!obstacleCells.TryGetValue(c, out var list)) obstacleCells.Add(c, list = []); list.Add(i); }
        }
        _obstacleCells = obstacleCells.ToDictionary(p => p.Key, p => p.Value.ToArray());
        foreach (var cell in blocked ?? [])
        {
            int index = Index(cell);
            _blocked[index >> 5] |= 1u << (index & 31);
        }
    }

    private NavigationGrid(NavigationGrid previous, int cell, bool blocked)
    {
        Width = previous.Width;
        Height = previous.Height;
        CellSize = previous.CellSize;
        Revision = checked(previous.Revision + 1);
        _terrain = previous._terrain; MaximumTerrain = previous.MaximumTerrain; _noStop = previous._noStop;
        _surface = previous._surface; _rises = previous._rises; _cuts = previous._cuts; _obstacles = previous._obstacles; _obstacleCells = previous._obstacleCells;
        _paintedCuts = previous._paintedCuts; HasSplitTerrain = previous.HasSplitTerrain; _maximumUpperTerrain = previous._maximumUpperTerrain;
        _blocked = (uint[])previous._blocked.Clone();
        uint bit = 1u << (cell & 31);
        if (blocked) _blocked[cell >> 5] |= bit;
        else _blocked[cell >> 5] &= ~bit;
    }

    private NavigationGrid(NavigationGrid basis, uint[] blocked, byte[]? terrain, uint[]? noStop, HashSet<int>? paintedCuts, int revision)
    {
        Width = basis.Width; Height = basis.Height; CellSize = basis.CellSize; Revision = revision;
        _blocked = blocked; _terrain = terrain; _noStop = noStop; _paintedCuts = paintedCuts;
        HasSplitTerrain = basis.HasSplitTerrain; _maximumUpperTerrain = basis._maximumUpperTerrain;
        MaximumTerrain = Math.Max(terrain?.Max() ?? 0, _maximumUpperTerrain);
        _surface = basis._surface; _rises = basis._rises; _cuts = basis._cuts; _obstacles = basis._obstacles; _obstacleCells = basis._obstacleCells;
    }
    /// <summary>Overlays valid cell indices on this base grid and compares against the current revision of the same grid.</summary>
    public NavigationGrid Compose(IReadOnlyDictionary<int, CellOverride> patches, NavigationGrid current)
    {
        var blocked = (uint[])_blocked.Clone(); var stop = _noStop is null ? new uint[blocked.Length] : (uint[])_noStop.Clone();
        byte[]? terrain = _terrain is null ? null : (byte[])_terrain.Clone();
        HashSet<int>? paintedCuts = null;
        foreach (var (cell, patch) in patches)
        {
            uint bit = 1u << (cell & 31);
            if (patch.Traverse is { } traverse) { if (traverse) blocked[cell >> 5] &= ~bit; else blocked[cell >> 5] |= bit; }
            if (patch.CanStop is { } canStop) { if (canStop) stop[cell >> 5] &= ~bit; else stop[cell >> 5] |= bit; }
            if (patch.Terrain is { } type)
            {
                terrain ??= new byte[CellCount]; terrain[cell] = type;
                if (HasSplitTerrain && SurfaceCutAt(cell).UpperTerrain is { } upper && upper != type)
                    (paintedCuts ??= []).Add(cell);
            }
        }
        if (_terrain is null && terrain is not null && !terrain.Any(t => t != 0)) terrain = null;
        uint[]? finalStop = stop.Any(t => t != 0) ? stop : null;
        bool sameCuts = paintedCuts is null ? current._paintedCuts is null :
            current._paintedCuts is not null && paintedCuts.SetEquals(current._paintedCuts);
        if (sameCuts && blocked.AsSpan().SequenceEqual(current._blocked) && Equal(terrain, current._terrain) && Equal(finalStop, current._noStop)) return current;
        return new(this, blocked, terrain, finalStop, paintedCuts, checked(current.Revision + 1));
        static bool Equal<T>(T[]? a, T[]? b) where T : IEquatable<T> => a is null ? b is null : b is not null && a.AsSpan().SequenceEqual(b);
    }
    public bool CanStop(int cell) => !IsBlocked(cell) && (_noStop is null || (_noStop[cell >> 5] & (1u << (cell & 31))) == 0);
    public SurfaceSample SurfaceAt(int cell) => _surface?[cell] ?? default;
    public SurfaceRise SurfaceRiseAt(int cell) => _rises?[cell] ?? default;
    public SurfaceCut SurfaceCutAt(int cell) => _cuts?.GetValueOrDefault(cell) ?? default;

    public int SurfaceElevationAt(WorldPosition position) => SurfaceElevationAt(CellAt(position), position.X, position.Y);

    /// <summary>Evaluate one cell's plane, including its edges where the adjacent plane can differ.</summary>
    public int SurfaceElevationAt(int cell, int x, int y)
    {
        int elevation = SurfaceAt(cell).Elevation;
        var cut = SurfaceCutAt(cell);
        if (cut != default)
            return cut.IsUpper(x - cell % Width * CellSize, y - cell / Width * CellSize, CellSize)
                ? cut.UpperElevation : elevation;
        var rise = SurfaceRiseAt(cell);
        if (rise == default) return elevation;
        int offsetX = x - cell % Width * CellSize, offsetY = y - cell / Width * CellSize;
        long scale = 2L * CellSize;
        long height = elevation * scale + (2L * offsetX - CellSize) * rise.X + (2L * offsetY - CellSize) * rise.Y;
        return (int)((height < 0 ? height - scale + 1 : height) / scale);
    }

    /// <summary>Evaluate an edge endpoint on the triangle that owns the open edge.</summary>
    public int SurfaceElevationAtEdge(int cell, int x, int y, int otherX, int otherY)
    {
        var cut = SurfaceCutAt(cell);
        if (cut == default) return SurfaceElevationAt(cell, x, y);
        int left = cell % Width * CellSize, top = cell / Width * CellSize;
        return cut.IsUpper(x + otherX - 2 * left, y + otherY - 2 * top, 2 * CellSize)
            ? cut.UpperElevation : SurfaceAt(cell).Elevation;
    }
    public ReadOnlySpan<int> ObstaclesAt(int cell) => _obstacleCells.TryGetValue(cell, out var values) ? values : [];
    public WorldBox Obstacle(int index) => _obstacles[index];

    public int Index(GridCell cell)
    {
        if ((uint)cell.X >= (uint)Width || (uint)cell.Y >= (uint)Height) throw new ArgumentOutOfRangeException(nameof(cell));
        return cell.Y * Width + cell.X;
    }

    public int CellAt(WorldPosition position)
    {
        if (position.X < 0 || position.X > (long)Width * CellSize || position.Y < 0 || position.Y > (long)Height * CellSize)
            throw new ArgumentOutOfRangeException(nameof(position));
        return Math.Min(position.Y / CellSize, Height - 1) * Width + Math.Min(position.X / CellSize, Width - 1);
    }

    public WorldPosition Center(int index, int elevation = 0)
    {
        if ((uint)index >= (uint)CellCount) throw new ArgumentOutOfRangeException(nameof(index));
        return new(index % Width * CellSize + CellSize / 2, index / Width * CellSize + CellSize / 2, elevation);
    }

    public bool IsBlocked(int index)
    {
        if ((uint)index >= (uint)CellCount) throw new ArgumentOutOfRangeException(nameof(index));
        return (_blocked[index >> 5] & (1u << (index & 31))) != 0;
    }

    /// <summary>Terrain of the whole cell or its lower triangle when split.</summary>
    public byte TerrainAt(int index)
    {
        if ((uint)index >= (uint)CellCount) throw new ArgumentOutOfRangeException(nameof(index));
        return _terrain?[index] ?? 0;
    }

    public byte TerrainAt(int index, bool upper) => upper && _paintedCuts?.Contains(index) != true &&
        SurfaceCutAt(index).UpperTerrain is { } terrain ? terrain : TerrainAt(index);

    public byte TerrainAt(WorldPosition point)
    {
        int cell = CellAt(point);
        return HasSplitTerrain && SurfaceCutAt(cell).IsUpper(point.X - cell % Width * CellSize, point.Y - cell / Width * CellSize, CellSize)
            ? TerrainAt(cell, upper: true) : TerrainAt(cell);
    }

    public NavigationGrid WithBlocked(int index, bool blocked)
    {
        if (IsBlocked(index) == blocked) return this;
        return new(this, index, blocked);
    }

    public bool IsRectangleClear(int a, int b)
    {
        int minX = Math.Min(a % Width, b % Width), maxX = Math.Max(a % Width, b % Width);
        int minY = Math.Min(a / Width, b / Width), maxY = Math.Max(a / Width, b / Width);
        for (int y = minY; y <= maxY; y++)
        {
            int first = y * Width + minX, last = y * Width + maxX;
            int left = first >> 5, right = last >> 5;
            uint leftMask = uint.MaxValue << (first & 31), rightMask = uint.MaxValue >> (31 - (last & 31));
            if (left == right) { if ((_blocked[left] & leftMask & rightMask) != 0) return false; }
            else
            {
                if ((_blocked[left] & leftMask) != 0 || (_blocked[right] & rightMask) != 0) return false;
                for (int word = left + 1; word < right; word++) if (_blocked[word] != 0) return false;
            }
        }
        return true;
    }

    public void WriteState(BinaryWriter writer)
    {
        writer.Write(Width); writer.Write(Height); writer.Write(CellSize); writer.Write(Revision);
        foreach (uint bits in _blocked) writer.Write(bits);
        writer.Write(_terrain is not null);
        if (_terrain is not null) writer.Write(_terrain);
        writer.Write(_noStop is not null); if (_noStop is not null) foreach (uint bits in _noStop) writer.Write(bits);
        int surfaceFormat = _surface is null ? 0 : _rises is null ? 1 : 2;
        writer.Write((byte)(surfaceFormat | (_cuts is null ? 0 : 4) | (HasSplitTerrain ? 8 : 0)));
        if (_surface is not null) foreach (var sample in _surface) { writer.Write(sample.Elevation); writer.Write(sample.Slope); writer.Write(sample.Depth); }
        if (_rises is not null) foreach (var rise in _rises) { writer.Write(rise.X); writer.Write(rise.Y); }
        if (_cuts is not null)
        {
            writer.Write(_cuts.Count);
            foreach (var (cell, cut) in _cuts.OrderBy(pair => pair.Key))
            {
                writer.Write(cell); writer.Write((byte)cut.UpperCorner); writer.Write(cut.UpperElevation);
                if (HasSplitTerrain) writer.Write(cut.UpperTerrain is null ? (short)-1 : TerrainAt(cell, upper: true));
            }
        }
        writer.Write(_obstacles.Length); foreach (var box in _obstacles) { writer.Write(box.MinX); writer.Write(box.MinY); writer.Write(box.MaxX); writer.Write(box.MaxY); }
    }
}
