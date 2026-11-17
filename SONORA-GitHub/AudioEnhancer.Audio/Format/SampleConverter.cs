using System.Buffers.Binary;

namespace AudioEnhancer.Audio;

/// <summary>
/// Turns a device byte buffer into the interleaved float samples the DSP engine
/// works in, and back again.
///
/// The DSP engine speaks one language only: interleaved <c>float</c> in the range
/// -1..+1. Windows devices are usually already 32-bit float in shared mode, in which
/// case these methods are a straight copy, but a device can report 16- or 24-bit PCM
/// and the rest of the app must not have to care.
///
/// Every method is allocation-free and takes spans, so it is safe to call from the
/// audio thread in Part 2C.
/// </summary>
public static class SampleConverter
{
    private const float Int16Scale = 1f / 32768f;
    private const float Int24Scale = 1f / 8388608f;
    private const float Int32Scale = 1f / 2147483648f;

    /// <summary>How many whole samples a byte buffer holds in the given format.</summary>
    public static int SampleCount(int byteCount, AudioStreamFormat format)
    {
        int bytesPerSample = format.BytesPerSample;
        return bytesPerSample > 0 ? byteCount / bytesPerSample : 0;
    }

    /// <summary>
    /// Reads <paramref name="source"/> into <paramref name="destination"/> as floats.
    /// Returns the number of samples written: the smaller of what the source holds
    /// and what the destination can take, so a short buffer truncates instead of throwing.
    /// </summary>
    public static int ToFloat(ReadOnlySpan<byte> source, Span<float> destination, AudioStreamFormat format)
    {
        int bytesPerSample = format.BytesPerSample;
        if (bytesPerSample <= 0) return 0;

        int count = Math.Min(source.Length / bytesPerSample, destination.Length);
        if (count <= 0) return 0;

        switch (format.SampleType)
        {
            case SampleFormat.Float32:
                for (int i = 0; i < count; i++)
                {
                    int offset = i * 4;
                    uint bits = BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(offset, 4));
                    destination[i] = BitConverter.UInt32BitsToSingle(bits);
                }
                return count;

            case SampleFormat.Pcm16:
                for (int i = 0; i < count; i++)
                {
                    short value = BinaryPrimitives.ReadInt16LittleEndian(source.Slice(i * 2, 2));
                    destination[i] = value * Int16Scale;
                }
                return count;

            case SampleFormat.Pcm24:
                for (int i = 0; i < count; i++)
                {
                    int offset = i * 3;
                    int value = source[offset] | (source[offset + 1] << 8) | (source[offset + 2] << 16);
                    if ((value & 0x800000) != 0) value |= unchecked((int)0xFF000000);   // sign-extend
                    destination[i] = value * Int24Scale;
                }
                return count;

            case SampleFormat.Pcm32:
                for (int i = 0; i < count; i++)
                {
                    int value = BinaryPrimitives.ReadInt32LittleEndian(source.Slice(i * 4, 4));
                    destination[i] = value * Int32Scale;
                }
                return count;

            default:
                destination.Slice(0, count).Clear();   // unknown encoding: silence, never noise
                return count;
        }
    }

    /// <summary>
    /// Writes floats back into a device byte buffer, clamping to full scale so a hot
    /// signal wraps round to the opposite polarity under no circumstances.
    /// Returns the number of samples written.
    /// </summary>
    public static int FromFloat(ReadOnlySpan<float> source, Span<byte> destination, AudioStreamFormat format)
    {
        int bytesPerSample = format.BytesPerSample;
        if (bytesPerSample <= 0) return 0;

        int count = Math.Min(source.Length, destination.Length / bytesPerSample);
        if (count <= 0) return 0;

        switch (format.SampleType)
        {
            case SampleFormat.Float32:
                for (int i = 0; i < count; i++)
                {
                    float sample = Clamp(source[i]);
                    BinaryPrimitives.WriteUInt32LittleEndian(
                        destination.Slice(i * 4, 4), BitConverter.SingleToUInt32Bits(sample));
                }
                return count;

            case SampleFormat.Pcm16:
                for (int i = 0; i < count; i++)
                {
                    short value = (short)Math.Round(Clamp(source[i]) * 32767f);
                    BinaryPrimitives.WriteInt16LittleEndian(destination.Slice(i * 2, 2), value);
                }
                return count;

            case SampleFormat.Pcm24:
                for (int i = 0; i < count; i++)
                {
                    int value = (int)Math.Round(Clamp(source[i]) * 8388607f);
                    int offset = i * 3;
                    destination[offset] = (byte)(value & 0xFF);
                    destination[offset + 1] = (byte)((value >> 8) & 0xFF);
                    destination[offset + 2] = (byte)((value >> 16) & 0xFF);
                }
                return count;

            case SampleFormat.Pcm32:
                for (int i = 0; i < count; i++)
                {
                    int value = (int)Math.Round((double)Clamp(source[i]) * 2147483647.0);
                    BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(i * 4, 4), value);
                }
                return count;

            default:
                destination.Slice(0, count * bytesPerSample).Clear();
                return count;
        }
    }

    /// <summary>
    /// Keeps a sample inside full scale and turns a NaN or infinity - which a broken
    /// upstream stage could produce - into silence rather than a speaker-destroying spike.
    /// </summary>
    public static float Clamp(float sample)
    {
        if (float.IsNaN(sample) || float.IsInfinity(sample)) return 0f;
        if (sample > 1f) return 1f;
        if (sample < -1f) return -1f;
        return sample;
    }
}
