using System.Threading;

namespace AudioEnhancer.Audio;

/// <summary>
/// A fixed-size float ring buffer for exactly one writer thread and one reader thread.
///
/// This is the piece that lets a capture callback and a render callback run on
/// different threads at different buffer sizes without either of them waiting for a
/// lock. Every operation is allocation-free and lock-free, which is what makes it
/// legal to call from a real-time audio callback.
///
/// The rules it follows, and the reason for each:
/// <list type="bullet">
/// <item>Capacity is rounded up to a power of two so the wrap is a mask, not a divide.</item>
/// <item>Positions are 64-bit and only ever increase, so a wrap of the index cannot
/// be mistaken for an empty or full buffer.</item>
/// <item>A write that does not fit is truncated and counted as an overrun. A read that
/// cannot be filled is zero-padded and counted as an underrun. Dropping or padding
/// audio is bad; blocking the audio thread is worse.</item>
/// </list>
/// </summary>
public sealed class AudioRingBuffer
{
    private readonly float[] _buffer;
    private readonly int _mask;

    private long _writePosition;
    private long _readPosition;
    private long _overrunSamples;
    private long _underrunSamples;

    /// <param name="capacitySamples">
    /// Wanted capacity in samples, not frames: for stereo, multiply the frame count by two.
    /// Rounded up to the next power of two.
    /// </param>
    public AudioRingBuffer(int capacitySamples)
    {
        if (capacitySamples < 2) capacitySamples = 2;
        int capacity = NextPowerOfTwo(capacitySamples);
        _buffer = new float[capacity];
        _mask = capacity - 1;
    }

    /// <summary>Total capacity in samples, after rounding up.</summary>
    public int Capacity => _buffer.Length;

    /// <summary>Samples currently waiting to be read.</summary>
    public int Count
    {
        get
        {
            long available = Volatile.Read(ref _writePosition) - Volatile.Read(ref _readPosition);
            if (available < 0) return 0;
            return available > _buffer.Length ? _buffer.Length : (int)available;
        }
    }

    /// <summary>Room left for writing, in samples.</summary>
    public int FreeCount => _buffer.Length - Count;

    public bool IsEmpty => Count == 0;

    public bool IsFull => FreeCount == 0;

    /// <summary>Samples the writer had to throw away because the buffer was full.</summary>
    public long OverrunSamples => Volatile.Read(ref _overrunSamples);

    /// <summary>Samples the reader had to invent as silence because the buffer was empty.</summary>
    public long UnderrunSamples => Volatile.Read(ref _underrunSamples);

    /// <summary>
    /// Copies as much of <paramref name="source"/> in as fits and returns how many
    /// samples were taken. Anything left over is counted as an overrun.
    /// Call from the producer thread only.
    /// </summary>
    public int Write(ReadOnlySpan<float> source)
    {
        if (source.Length == 0) return 0;

        long write = Volatile.Read(ref _writePosition);
        long read = Volatile.Read(ref _readPosition);
        int free = _buffer.Length - (int)(write - read);

        int toWrite = source.Length <= free ? source.Length : free;
        if (toWrite < source.Length)
            Volatile.Write(ref _overrunSamples, Volatile.Read(ref _overrunSamples) + (source.Length - toWrite));
        if (toWrite <= 0) return 0;

        int start = (int)(write & _mask);
        int firstChunk = _buffer.Length - start;
        if (firstChunk > toWrite) firstChunk = toWrite;

        source.Slice(0, firstChunk).CopyTo(_buffer.AsSpan(start, firstChunk));
        int remainder = toWrite - firstChunk;
        if (remainder > 0)
            source.Slice(firstChunk, remainder).CopyTo(_buffer.AsSpan(0, remainder));

        // Published last: the reader must never see a position that outruns the data.
        Volatile.Write(ref _writePosition, write + toWrite);
        return toWrite;
    }

    /// <summary>
    /// Fills <paramref name="destination"/> and returns how many samples were real.
    /// Anything the buffer could not supply is written as silence and counted as an
    /// underrun, so the caller always gets a completely filled buffer.
    /// Call from the consumer thread only.
    /// </summary>
    public int Read(Span<float> destination)
    {
        if (destination.Length == 0) return 0;

        long read = Volatile.Read(ref _readPosition);
        long write = Volatile.Read(ref _writePosition);
        int available = (int)(write - read);
        if (available < 0) available = 0;

        int toRead = destination.Length <= available ? destination.Length : available;

        if (toRead > 0)
        {
            int start = (int)(read & _mask);
            int firstChunk = _buffer.Length - start;
            if (firstChunk > toRead) firstChunk = toRead;

            _buffer.AsSpan(start, firstChunk).CopyTo(destination.Slice(0, firstChunk));
            int remainder = toRead - firstChunk;
            if (remainder > 0)
                _buffer.AsSpan(0, remainder).CopyTo(destination.Slice(firstChunk, remainder));

            Volatile.Write(ref _readPosition, read + toRead);
        }

        int shortfall = destination.Length - toRead;
        if (shortfall > 0)
        {
            destination.Slice(toRead, shortfall).Clear();
            Volatile.Write(ref _underrunSamples, Volatile.Read(ref _underrunSamples) + shortfall);
        }

        return toRead;
    }

    /// <summary>Throws away up to <paramref name="samples"/> of waiting audio. Consumer thread only.</summary>
    public int Discard(int samples)
    {
        if (samples <= 0) return 0;

        long read = Volatile.Read(ref _readPosition);
        long write = Volatile.Read(ref _writePosition);
        int available = (int)(write - read);
        if (available <= 0) return 0;

        int toDiscard = samples <= available ? samples : available;
        Volatile.Write(ref _readPosition, read + toDiscard);
        return toDiscard;
    }

    /// <summary>
    /// Empties the buffer. Call while the stream is stopped - it moves both positions,
    /// so it is not safe to run against a live producer or consumer.
    /// </summary>
    public void Clear()
    {
        Volatile.Write(ref _readPosition, 0);
        Volatile.Write(ref _writePosition, 0);
        Array.Clear(_buffer, 0, _buffer.Length);
    }

    /// <summary>Zeroes the overrun and underrun counters without touching the audio.</summary>
    public void ResetStatistics()
    {
        Volatile.Write(ref _overrunSamples, 0);
        Volatile.Write(ref _underrunSamples, 0);
    }

    internal static int NextPowerOfTwo(int value)
    {
        int result = 2;
        while (result < value && result < 0x40000000)
            result <<= 1;
        return result;
    }
}
