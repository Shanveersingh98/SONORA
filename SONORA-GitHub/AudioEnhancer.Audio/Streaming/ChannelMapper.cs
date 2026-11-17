namespace AudioEnhancer.Audio;

/// <summary>
/// Moves interleaved frames from one channel count to another.
///
/// The capture endpoint and the render endpoint are two different devices, so they do
/// not have to agree on channel count: system audio might be mixed at 5.1 while the
/// headphones you want it enhanced on are stereo.
///
/// The mapping is deliberately minimal and predictable rather than clever:
/// <list type="bullet">
/// <item>Same count: straight copy.</item>
/// <item>Mono to many: the one channel is duplicated to every output channel.</item>
/// <item>More in than out: the first N channels are kept. In the standard WAVE channel
/// order that is front-left and front-right, which is what a stereo device wants.
/// A proper downmix would fold centre and surrounds in with gain coefficients; that
/// is a mixing feature, not stream plumbing, so it is not done here.</item>
/// <item>Fewer in than out: the extra output channels are silent.</item>
/// </list>
///
/// Allocation-free, so it is safe on the audio thread.
/// </summary>
public static class ChannelMapper
{
    /// <summary>True when <see cref="Map"/> would be a plain copy.</summary>
    public static bool IsPassthrough(int sourceChannels, int destinationChannels) =>
        sourceChannels == destinationChannels;

    /// <summary>
    /// Maps whole frames from <paramref name="source"/> into <paramref name="destination"/>.
    /// Returns the number of frames written, which is the smaller of what each side holds.
    /// </summary>
    public static int Map(
        ReadOnlySpan<float> source, int sourceChannels,
        Span<float> destination, int destinationChannels)
    {
        if (sourceChannels <= 0 || destinationChannels <= 0) return 0;

        int frames = Math.Min(source.Length / sourceChannels, destination.Length / destinationChannels);
        if (frames <= 0) return 0;

        if (sourceChannels == destinationChannels)
        {
            source.Slice(0, frames * sourceChannels).CopyTo(destination);
            return frames;
        }

        if (sourceChannels == 1)
        {
            for (int frame = 0; frame < frames; frame++)
            {
                float sample = source[frame];
                int baseIndex = frame * destinationChannels;
                for (int channel = 0; channel < destinationChannels; channel++)
                    destination[baseIndex + channel] = sample;
            }
            return frames;
        }

        int shared = Math.Min(sourceChannels, destinationChannels);
        for (int frame = 0; frame < frames; frame++)
        {
            int sourceBase = frame * sourceChannels;
            int destinationBase = frame * destinationChannels;

            for (int channel = 0; channel < shared; channel++)
                destination[destinationBase + channel] = source[sourceBase + channel];

            for (int channel = shared; channel < destinationChannels; channel++)
                destination[destinationBase + channel] = 0f;
        }
        return frames;
    }
}
