using System.Buffers;
using System.Text;

namespace FluxorViewer.Middleware;

/// <summary>
/// Receives serialised JSON, keeping it until more than <c>cap</c> bytes have been written and just counting the rest, so
/// a huge value is measured without being held in memory. A single write that asks for more room than the cap has left
/// (as one that writes a long string whole does) is still kept, so a value within the cap is always kept whole.
/// </summary>
internal sealed class CappedBuffer(int cap) : IBufferWriter<byte>
{
    private byte[] kept = [];
    private int keptLength;

    // What the writer writes into once more than the cap has been written; reused, and thrown away.
    private byte[] scratch = [];

    /// <summary>Every byte written, kept or not.</summary>
    public long Length { get; private set; }

    /// <summary>Whether more than the cap was written, so only part of it was kept.</summary>
    public bool IsOverCap => Length > cap;

    /// <summary>The JSON written, whole unless <see cref="IsOverCap"/>.</summary>
    public string Text => Encoding.UTF8.GetString(kept, 0, keptLength);

    /// <inheritdoc />
    public void Advance(int count)
    {
        if (!IsOverCap)
            keptLength += count;
        Length += count;
    }

    /// <inheritdoc />
    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        var size = Math.Max(sizeHint, 1);
        return IsOverCap ? Scratch(size) : Keep(size);
    }

    /// <inheritdoc />
    public Span<byte> GetSpan(int sizeHint = 0) => GetMemory(sizeHint).Span;

    /// <summary>
    /// Room for <paramref name="size"/> more kept bytes, growing the kept array by doubling up to the cap, and past it only
    /// as far as a single write asks.
    /// </summary>
    private Memory<byte> Keep(int size)
    {
        var needed = keptLength + size;
        if (kept.Length < needed)
            Array.Resize(ref kept, Math.Max(needed, Math.Min(cap, Math.Max(256, kept.Length * 2))));
        return kept.AsMemory(keptLength);
    }

    private byte[] Scratch(int size)
    {
        if (scratch.Length < size)
            scratch = new byte[Math.Max(size, 4096)];
        return scratch;
    }
}
