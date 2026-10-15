namespace Pome.Core;

public sealed record PciBar(int Function, int Index, ulong Size, bool Is64Bit);
public sealed record BarAllocation(PciBar Bar, ulong Address);
public sealed record MmioLayout(IReadOnlyList<BarAllocation> Allocations, ulong LowBytes, ulong HighBytes);

/// <summary>Pure planning only. It cannot establish host or guest BAR mapping support.</summary>
public static class MmioAllocator
{
    public static MmioLayout Plan(IEnumerable<PciBar> bars, ulong lowStart, ulong lowLength, ulong highStart, ulong highLength)
    {
        checked
        {
            var lowEnd = lowStart + lowLength;
            var highEnd = highStart + highLength;
            if (lowEnd > 0x1_0000_0000UL || highStart < 0x1_0000_0000UL) throw new ArgumentOutOfRangeException(nameof(lowLength));
            var low = lowStart; var high = highStart;
            var result = new List<BarAllocation>();
            var occupied = new HashSet<(int Function, int Index)>();
            foreach (var bar in bars.OrderByDescending(b => b.Size))
            {
                if (bar.Function is < 0 or > 7 || bar.Index is < 0 or > 5 || (bar.Is64Bit && bar.Index == 5)) throw new InvalidDataException("Invalid PCI function or BAR index.");
                if (!occupied.Add((bar.Function, bar.Index)) || (bar.Is64Bit && !occupied.Add((bar.Function, bar.Index + 1)))) throw new InvalidDataException("Overlapping PCI BAR registers.");
                if (bar.Size == 0 || (bar.Size & (bar.Size - 1)) != 0) throw new InvalidDataException("BAR size must be a nonzero power of two obtained from trusted resource discovery.");
                var cursor = bar.Is64Bit ? high : low;
                var aligned = (cursor + bar.Size - 1) & ~(bar.Size - 1);
                var end = aligned + bar.Size;
                if (end > (bar.Is64Bit ? highEnd : lowEnd)) throw new InvalidDataException("Insufficient MMIO aperture.");
                result.Add(new(bar, aligned));
                if (bar.Is64Bit) high = end; else low = end;
            }
            return new(result, low - lowStart, high - highStart);
        }
    }
}
