using System.Buffers;
namespace EventDeck;

// One producer-owned reference, plus short-lived reader references. Bytes are
// immutable after publication and are returned only after the last reader exits.
internal sealed class CaptureFrameBuffer : IDisposable
{
    private int _references = 1;
    public byte[] Bytes { get; }
    public int Width { get; }
    public int Height { get; }
    public int Stride => checked(Width * 4);
    public long Sequence { get; }
    public CaptureFrameBuffer(int width, int height, long sequence)
    {
        Width = width; Height = height; Sequence = sequence;
        Bytes = ArrayPool<byte>.Shared.Rent(checked(width * height * 4));
    }
    public CaptureFrameBuffer Retain() { Interlocked.Increment(ref _references); return this; }
    public void Dispose()
    {
        if (Interlocked.Decrement(ref _references) == 0) ArrayPool<byte>.Shared.Return(Bytes);
    }
}
