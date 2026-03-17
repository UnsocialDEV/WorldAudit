namespace WorldAudit.Domain;

public readonly record struct BlockPosition(int X, int Y, int Z)
{
    public ChunkPosition ToChunkPosition(int chunkSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(chunkSize, 0);
        return new ChunkPosition(
            FloorDivide(X, chunkSize),
            FloorDivide(Y, chunkSize),
            FloorDivide(Z, chunkSize));
    }

    public long DistanceSquaredTo(BlockPosition other)
    {
        var dx = (long)X - other.X;
        var dy = (long)Y - other.Y;
        var dz = (long)Z - other.Z;
        return (dx * dx) + (dy * dy) + (dz * dz);
    }

    private static int FloorDivide(int value, int divisor)
    {
        var quotient = value / divisor;
        var remainder = value % divisor;

        if (remainder != 0 && ((remainder > 0) != (divisor > 0)))
        {
            quotient--;
        }

        return quotient;
    }
}

public readonly record struct ChunkPosition(int X, int Y, int Z);
