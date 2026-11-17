using System;
using System.Diagnostics;
using System.Threading;

namespace AudioEnhancer.Audio.Analysis;

/// <summary>
/// High-performance, lock-free real-time audio spectrum analyzer.
///
/// Observes post-DSP audio on the render stream without ever blocking or allocating
/// on the real-time audio thread. Computes a 2048-point windowed FFT at the actual
/// sample rate, mapping frequencies logarithmically into 32 RTA bands with configurable
/// ballistics smoothing, plus true peak and RMS metrics.
/// </summary>
public sealed class AudioSpectrumAnalyzer
{
    public const int FftSize = 2048;
    public const int BandCount = 32;
    private const int RingBufferSize = 8192;
    private const int RingBufferMask = RingBufferSize - 1;

    // Minimum and maximum frequency bounds for the 32 RTA bands
    private const float MinFreqHz = 20f;
    private const float MaxFreqHz = 20000f;

    // Dynamic range for normalized 0.0 to 1.0 spectrum output
    private const float MinDbFloor = -60f;
    private const float MaxDbCeiling = 0f;

    // Configurable ballistics smoothing
    private float _attack = 0.65f;
    private float _decay = 0.82f;

    // Lock-free circular ring buffer written by audio thread, read by UI thread
    private readonly float[] _ringBuffer = new float[RingBufferSize];
    private long _writeIndex;

    // Working buffers pre-allocated once (used strictly on the analysis thread)
    private readonly float[] _fftInput = new float[FftSize];
    private readonly float[] _fftReal = new float[FftSize];
    private readonly float[] _fftImag = new float[FftSize];
    private readonly float[] _hannWindow = new float[FftSize];
    private readonly int[] _bitReversal = new int[FftSize];
    private readonly float[] _cosTable = new float[FftSize / 2];
    private readonly float[] _sinTable = new float[FftSize / 2];

    // Logarithmic frequency bin mapping per band (startBin and endBin inclusive)
    private readonly int[] _bandStartBin = new int[BandCount];
    private readonly int[] _bandEndBin = new int[BandCount];

    // Smoothed band magnitudes (preserved across frames for decay)
    private readonly float[] _smoothedBands = new float[BandCount];

    private volatile int _sampleRate = 48000;
    private volatile bool _isPrepared;

    public AudioSpectrumAnalyzer()
    {
        InitializeFftTables();
        Prepare(48000);
    }

    /// <summary>Configurable attack smoothing coefficient (0.0 to 1.0, default 0.65).</summary>
    public float Attack
    {
        get => _attack;
        set => _attack = Math.Clamp(value, 0.01f, 1.0f);
    }

    /// <summary>Configurable decay smoothing coefficient (0.0 to 1.0, default 0.82).</summary>
    public float Decay
    {
        get => _decay;
        set => _decay = Math.Clamp(value, 0.01f, 1.0f);
    }

    /// <summary>
    /// Configures the analyzer for the stream's sample rate and re-computes
    /// the 32 logarithmic frequency bin boundaries. Safe to call on stream start/change.
    /// </summary>
    public void Prepare(int sampleRate)
    {
        int rate = sampleRate > 0 ? sampleRate : 48000;
        _sampleRate = rate;

        float deltaF = (float)rate / FftSize;
        float logMin = MathF.Log10(MinFreqHz);
        float logMax = MathF.Log10(MathF.Min(MaxFreqHz, rate * 0.495f));
        float step = (logMax - logMin) / BandCount;

        for (int i = 0; i < BandCount; i++)
        {
            float fStart = MathF.Pow(10.0f, logMin + i * step);
            float fEnd = MathF.Pow(10.0f, logMin + (i + 1) * step);

            int startBin = Math.Max(1, (int)MathF.Floor(fStart / deltaF));
            int endBin = Math.Min(FftSize / 2 - 1, (int)MathF.Ceiling(fEnd / deltaF));
            if (endBin < startBin) endBin = startBin;

            _bandStartBin[i] = startBin;
            _bandEndBin[i] = endBin;
        }

        Reset();
        _isPrepared = true;
    }

    /// <summary>Clears the ring buffer and smoothed bands.</summary>
    public void Reset()
    {
        Array.Clear(_ringBuffer, 0, _ringBuffer.Length);
        Array.Clear(_smoothedBands, 0, _smoothedBands.Length);
        Volatile.Write(ref _writeIndex, 0L);
    }

    /// <summary>
    /// OBSERVATION TAP: Called by the audio/render thread after DSP processing.
    /// REAL-TIME SAFE: 0 allocations, 0 locks, 0 waiting. Takes under 1 microsecond.
    /// </summary>
    public void WriteRenderSamples(ReadOnlySpan<float> interleaved, int channels)
    {
        if (!_isPrepared || interleaved.IsEmpty || channels <= 0) return;

        try
        {
            int frames = interleaved.Length / channels;
            long writePos = Volatile.Read(ref _writeIndex);
            var buffer = _ringBuffer;

            if (channels == 2)
            {
                // Fast-path stereo downmix to mono
                for (int frame = 0; frame < frames; frame++)
                {
                    int i = frame * 2;
                    buffer[(writePos + frame) & RingBufferMask] = 0.5f * (interleaved[i] + interleaved[i + 1]);
                }
            }
            else if (channels == 1)
            {
                for (int frame = 0; frame < frames; frame++)
                {
                    buffer[(writePos + frame) & RingBufferMask] = interleaved[frame];
                }
            }
            else
            {
                float invCh = 1.0f / channels;
                for (int frame = 0; frame < frames; frame++)
                {
                    int baseIdx = frame * channels;
                    float sum = 0f;
                    for (int ch = 0; ch < channels; ch++) sum += interleaved[baseIdx + ch];
                    buffer[(writePos + frame) & RingBufferMask] = sum * invCh;
                }
            }

            Volatile.Write(ref _writeIndex, writePos + frames);
        }
        catch
        {
            // Defensive guarantee: analysis tap failure MUST NEVER disrupt the audio thread
        }
    }

    /// <summary>
    /// PULL ANALYSIS: Called periodically (e.g., at 30-60 FPS) by the UI/ViewModel timer.
    /// Thread-safe, non-blocking, isolated with try-catch.
    /// </summary>
    public void GetSpectrum(Span<float> outBands, out float outPeakDb, out float outRmsDb, out bool isAudioActive)
    {
        outPeakDb = -120f;
        outRmsDb = -120f;
        isAudioActive = false;

        if (outBands.Length < BandCount) return;

        try
        {
            long writePos1 = Volatile.Read(ref _writeIndex);
            if (writePos1 < FftSize)
            {
                // Not enough samples accumulated yet; decay existing output gracefully
                for (int i = 0; i < BandCount; i++)
                {
                    _smoothedBands[i] *= _decay;
                    outBands[i] = _smoothedBands[i];
                }
                return;
            }

            // Snapshot latest 2048 mono samples from the ring buffer
            long startPos = writePos1 - FftSize;
            var ring = _ringBuffer;
            var input = _fftInput;
            float maxAbs = 0f;
            float sumSq = 0f;

            for (int i = 0; i < FftSize; i++)
            {
                float s = ring[(startPos + i) & RingBufferMask];
                input[i] = s;

                float abs = MathF.Abs(s);
                if (abs > maxAbs) maxAbs = abs;
                sumSq += s * s;
            }

            // Validate that audio thread didn't lap the reader during copy
            long writePos2 = Volatile.Read(ref _writeIndex);
            if (writePos2 - writePos1 >= (RingBufferSize - FftSize))
            {
                // Inconsistent snapshot due to extreme UI stutter; retain previous smoothed values
                for (int i = 0; i < BandCount; i++) outBands[i] = _smoothedBands[i];
                return;
            }

            // Calculate Peak and RMS dBFS
            outPeakDb = maxAbs > 1e-6f ? 20f * MathF.Log10(maxAbs) : -120f;
            float rmsLinear = MathF.Sqrt(sumSq / FftSize);
            outRmsDb = rmsLinear > 1e-6f ? 20f * MathF.Log10(rmsLinear) : -120f;
            isAudioActive = outRmsDb > -65f;

            // Apply Hann Window to reduce spectral leakage
            var real = _fftReal;
            var imag = _fftImag;
            var window = _hannWindow;
            for (int i = 0; i < FftSize; i++)
            {
                real[i] = input[i] * window[i];
                imag[i] = 0f;
            }

            // Compute In-Place Cooley-Tukey Radix-2 FFT
            ComputeFftInPlace(real, imag);

            // Compute bin magnitudes and map to 32 logarithmic RTA bands
            float scale = 2.0f / FftSize;
            float attack = _attack;
            float decay = _decay;
            var smoothed = _smoothedBands;

            for (int b = 0; b < BandCount; b++)
            {
                int kStart = _bandStartBin[b];
                int kEnd = _bandEndBin[b];
                float bandPower = 0f;
                int count = 0;

                for (int k = kStart; k <= kEnd; k++)
                {
                    float r = real[k];
                    float im = imag[k];
                    float mag = MathF.Sqrt(r * r + im * im) * scale;
                    if (mag > bandPower) bandPower = mag; // Peak bin in this band
                    count++;
                }

                // Convert linear magnitude to normalized 0.0 - 1.0 range over [-60 dB, 0 dB]
                float db = bandPower > 1e-5f ? 20f * MathF.Log10(bandPower) : MinDbFloor;
                float normalized = Math.Clamp((db - MinDbFloor) / (MaxDbCeiling - MinDbFloor), 0.0f, 1.0f);

                // Asymmetric ballistics: fast attack on transients, smooth decay
                if (normalized > smoothed[b])
                    smoothed[b] = smoothed[b] + (normalized - smoothed[b]) * attack;
                else
                    smoothed[b] = smoothed[b] * decay;

                outBands[b] = smoothed[b];
            }
        }
        catch
        {
            // Guaranteed error isolation: any analysis glitch will never affect audio
            for (int i = 0; i < BandCount; i++) outBands[i] = 0f;
        }
    }

    private void InitializeFftTables()
    {
        // 1. Hann Window
        for (int i = 0; i < FftSize; i++)
        {
            _hannWindow[i] = 0.5f * (1.0f - MathF.Cos(2.0f * MathF.PI * i / (FftSize - 1)));
        }

        // 2. Bit-Reversal Table
        int log2N = (int)Math.Round(Math.Log2(FftSize));
        for (int i = 0; i < FftSize; i++)
        {
            int reversed = 0;
            for (int bit = 0; bit < log2N; bit++)
            {
                if ((i & (1 << bit)) != 0)
                    reversed |= 1 << (log2N - 1 - bit);
            }
            _bitReversal[i] = reversed;
        }

        // 3. Twiddle Factors
        for (int i = 0; i < FftSize / 2; i++)
        {
            float theta = -2.0f * MathF.PI * i / FftSize;
            _cosTable[i] = MathF.Cos(theta);
            _sinTable[i] = MathF.Sin(theta);
        }
    }

    private void ComputeFftInPlace(float[] real, float[] imag)
    {
        // Bit-reversal permutation
        var bitRev = _bitReversal;
        for (int i = 0; i < FftSize; i++)
        {
            int j = bitRev[i];
            if (j > i)
            {
                float tr = real[i]; real[i] = real[j]; real[j] = tr;
                float ti = imag[i]; imag[i] = imag[j]; imag[j] = ti;
            }
        }

        // Cooley-Tukey Radix-2 decimation-in-time
        int cosStep = FftSize / 2;
        for (int halfSize = 1; halfSize < FftSize; halfSize <<= 1)
        {
            int size = halfSize << 1;
            int step = cosStep / halfSize;

            for (int k = 0; k < FftSize; k += size)
            {
                for (int j = 0; j < halfSize; j++)
                {
                    int tableIdx = j * step;
                    float wr = _cosTable[tableIdx];
                    float wi = _sinTable[tableIdx];

                    int u = k + j;
                    int v = k + j + halfSize;

                    float vr = real[v];
                    float vi = imag[v];

                    float tr = wr * vr - wi * vi;
                    float ti = wr * vi + wi * vr;

                    real[v] = real[u] - tr;
                    imag[v] = imag[u] - ti;
                    real[u] += tr;
                    imag[u] += ti;
                }
            }
        }
    }
}
