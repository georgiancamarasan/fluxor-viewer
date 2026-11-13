using System.Buffers;
using System.Text;

namespace FluxorViewer.Middleware;

/// <summary>
/// Receives serialised JSON, keeping it only while it's within <c>cap</c> bytes and just counting the rest, so a huge
/// value is measured without being held in memory. A single write that might cross the cap (the writer asks for room
/// for the worst case, e.g. a long string fully escaped) isn't kept either; <see cref="IsComplete"/> then says so.
/// </summary>
internal sealed class CappedBuffer(int cap) : IBufferWriter<byte>
{
    private byte[] kept = [];
    private int keptLength;

    // What the writer writes into once it's no longer kept; reused, and thrown away.
    private byte[] scratch = [];
    private bool discarding;

    /// <summary>Every byte written, kept or not.</summary>
    public long Length { get; private set; }

    /// <summary>Whether more than the cap was written.</summary>
    public bool IsOverCap => Length > cap;

    /// <summary>Whether everything written was kept, so <see cref="Text"/> is the whole JSON.</summary>
    public bool IsComplete => !discarding;

    /// <summary>The JSON kept: all of it when <see cref="IsComplete"/>.</summary>
    public string Text => Encoding.UTF8.GetString(kept, 0, keptLength);

    /// <inheritdoc />
    public void Advance(int count)
    {
        if (!discarding)
            keptLength += count;
        Length += count;
    }

    /// <inheritdoc />
    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        var size = Math.Max(sizeHint, 1);
        if (!discarding && keptLength + size <= cap)
            return Keep(size);
        discarding = true;
        return Scratch(size);
    }

    /// <inheritdoc />
    public Span<byte> GetSpan(int sizeHint = 0) => GetMemory(sizeHint).Span;

    /// <summary>Room for <paramref name="size"/> more kept bytes, growing the kept array by doubling but never past the cap.</summary>
    private Memory<byte> Keep(int size)
    {
        var needed = keptLength + size;
        if (kept.Length < needed)
            Array.Resize(ref kept, Math.Min(cap, Math.Max(needed, Math.Max(256, kept.Length * 2))));
        return kept.AsMemory(keptLength);
    }

    private byte[] Scratch(int size)
    {
        if (scratch.Length < size)
            scratch = new byte[Math.Max(size, 4096)];
        return scratch;
    }
}
