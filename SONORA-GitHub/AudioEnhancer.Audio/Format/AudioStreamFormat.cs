using System.Runtime.InteropServices;
using AudioEnhancer.Audio.Interop;

namespace AudioEnhancer.Audio;

/// <summary>How one sample is stored in the device's byte buffer.</summary>
public enum SampleFormat
{
    Unknown = 0,
    Pcm16,
    Pcm24,
    Pcm32,
    Float32,
}

/// <summary>
/// A stream format in terms the rest of the app understands: no WAVEFORMATEX,
/// no COM. This is the handshake between a Windows device and the DSP engine -
/// <c>DspEngine.Prepare(format.SampleRate, format.Channels)</c>.
/// </summary>
public sealed record AudioStreamFormat(
    int SampleRate,
    int Channels,
    SampleFormat SampleType,
    int BitsPerSample,
    uint ChannelMask = 0)
{
    /// <summary>The format WASAPI shared mode almost always reports on Windows 10/11.</summary>
    public static AudioStreamFormat DefaultStereo { get; } =
        new(48000, 2, SampleFormat.Float32, 32, 0x3);

    public int BytesPerSample => BitsPerSample / 8;

    /// <summary>Bytes in one frame: one sample for every channel.</summary>
    public int BlockAlign => BytesPerSample * Channels;

    public int BytesPerSecond => BlockAlign * SampleRate;

    /// <summary>True when the device hands us float samples, so no conversion is needed at all.</summary>
    public bool IsFloat => SampleType == SampleFormat.Float32;

    public bool IsValid =>
        SampleRate > 0 &&
        Channels > 0 &&
        BitsPerSample > 0 &&
        BitsPerSample % 8 == 0 &&
        SampleType != SampleFormat.Unknown;

    /// <summary>Frames in a span of time, rounded up so a buffer is never short.</summary>
    public int FramesFor(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero || SampleRate <= 0) return 0;
        double frames = duration.TotalSeconds * SampleRate;
        return (int)Math.Ceiling(frames);
    }

    public TimeSpan DurationOf(int frames) =>
        SampleRate > 0
            ? TimeSpan.FromSeconds((double)frames / SampleRate)
            : TimeSpan.Zero;

    public override string ToString()
    {
        string kind = SampleType switch
        {
            SampleFormat.Float32 => "32-bit float",
            SampleFormat.Pcm16 => "16-bit PCM",
            SampleFormat.Pcm24 => "24-bit PCM",
            SampleFormat.Pcm32 => "32-bit PCM",
            _ => $"{BitsPerSample}-bit unknown",
        };
        string layout = Channels switch
        {
            1 => "mono",
            2 => "stereo",
            6 => "5.1",
            8 => "7.1",
            _ => $"{Channels} ch",
        };
        return $"{SampleRate / 1000.0:0.#} kHz, {layout}, {kind}";
    }

    // -----------------------------------------------------------------------
    //  WAVEFORMATEX <-> AudioStreamFormat
    // -----------------------------------------------------------------------

    /// <summary>
    /// Reads a WAVEFORMATEX / WAVEFORMATEXTENSIBLE that Windows allocated.
    /// Returns null rather than throwing on anything malformed - a strange device
    /// should show up as "unsupported", never as a crash in the device list.
    /// </summary>
    /// <summary>
    /// Public entry point to the WAVEFORMATEX parser, so the test suite can check it
    /// against a hand-built structure without the backend having to open a device.
    /// Nothing in the app calls this - the device layer uses the internal overload.
    /// </summary>
    public static AudioStreamFormat? FromWaveFormatForTests(IntPtr waveFormatPointer) =>
        FromWaveFormat(waveFormatPointer);

    internal static AudioStreamFormat? FromWaveFormat(IntPtr pointer)
    {
        if (pointer == IntPtr.Zero) return null;

        WaveFormatEx basic;
        try
        {
            basic = Marshal.PtrToStructure<WaveFormatEx>(pointer);
        }
        catch
        {
            return null;
        }

        if (basic.Channels == 0 || basic.SamplesPerSec == 0 || basic.BitsPerSample == 0)
            return null;

        ushort tag = basic.FormatTag;
        uint channelMask = 0;

        if (tag == WaveFormatTag.Extensible && basic.ExtraSize >= 22)
        {
            WaveFormatExtensible extensible;
            try
            {
                extensible = Marshal.PtrToStructure<WaveFormatExtensible>(pointer);
            }
            catch
            {
                return null;
            }

            channelMask = extensible.ChannelMask;

            if (extensible.SubFormat == KsDataFormat.SubTypeIeeeFloat)
                tag = WaveFormatTag.IeeeFloat;
            else if (extensible.SubFormat == KsDataFormat.SubTypePcm)
                tag = WaveFormatTag.Pcm;
            else
                tag = 0; // a compressed or vendor subtype: not something the DSP can touch
        }

        SampleFormat sampleFormat = (tag, basic.BitsPerSample) switch
        {
            (WaveFormatTag.IeeeFloat, 32) => SampleFormat.Float32,
            (WaveFormatTag.Pcm, 16) => SampleFormat.Pcm16,
            (WaveFormatTag.Pcm, 24) => SampleFormat.Pcm24,
            (WaveFormatTag.Pcm, 32) => SampleFormat.Pcm32,
            _ => SampleFormat.Unknown,
        };

        return new AudioStreamFormat(
            (int)basic.SamplesPerSec,
            basic.Channels,
            sampleFormat,
            basic.BitsPerSample,
            channelMask);
    }
}
