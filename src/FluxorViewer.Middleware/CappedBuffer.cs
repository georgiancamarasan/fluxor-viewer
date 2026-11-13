using System.Buffers;
using System.Text;

namespace FluxorViewer.Middleware;

/// <summary>
/// Receives serialised JSON, keeping it only while it's within <c>cap</c> bytes and just counting the rest, so a huge
/// value is measured without being held in memory.
/// </summary>
internal sealed class CappedBuffer(int cap) : IBufferWriter<byte>
{
    private readonly ArrayBufferWriter<byte> kept = new();

    // What the writer writes into once past the cap; reused, and thrown away.
    private byte[] scratch = [];
    private bool discarding;

    /// <summary>Every byte written, kept or not.</summary>
    public long Length { get; private set; }

    /// <summary>Whether more than the cap was written, so <see cref="Text"/> is incomplete.</summary>
    public bool IsOverCap => Length > cap;

    /// <summary>The JSON written, as long as it's within the cap.</summary>
    public string Text => Encoding.UTF8.GetString(kept.WrittenSpan);

    /// <inheritdoc />
    public void Advance(int count)
    {
        if (!discarding)
            kept.Advance(count);
        Length += count;
    }

    /// <inheritdoc />
    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        discarding = IsOverCap;
        return discarding ? Scratch(sizeHint) : kept.GetMemory(sizeHint);
    }

    /// <inheritdoc />
    public Span<byte> GetSpan(int sizeHint = 0) => GetMemory(sizeHint).Span;

    private byte[] Scratch(int sizeHint)
    {
        var size = Math.Max(sizeHint, 4096);
        if (scratch.Length < size)
            scratch = new byte[size];
        return scratch;
    }
}
