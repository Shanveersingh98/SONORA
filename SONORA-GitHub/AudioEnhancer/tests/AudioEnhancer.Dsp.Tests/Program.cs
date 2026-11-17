using AudioEnhancer.Core;
using AudioEnhancer.Dsp;
using AudioEnhancer.Dsp.Testing;

namespace AudioEnhancer.Dsp.Tests;

public static class Program
{
    private const int SampleRate = 48000;
    private const int Channels = 2;
    private const int OneSecond = 48000;

    public static int Main()
    {
        var runner = new TestRunner();
        Console.WriteLine("AudioEnhancer DSP tests");
        Console.WriteLine("=======================");

        GainTests(runner);
        BassTests(runner);
        TrebleTests(runner);
        VoiceAndDetailTests(runner);
        LimiterTests(runner);
        ClippingTests(runner);
        OnOffTests(runner);
        ParameterLimitTests(runner);
        SliderMappingTests(runner);
        SilenceTests(runner);
        StabilityTests(runner);
        ConsumerLoudnessAndLimiterTests(runner);

        return runner.Summarise();
    }

    private static float[] Run(IDspProcessor processor, float[] input)
    {
        var buffer = (float[])input.Clone();
        processor.Prepare(SampleRate, Channels);
        processor.Reset();
        processor.Process(buffer);
        return buffer;
    }

    private static DspEngine Engine(EnhancerSettings settings)
    {
        var engine = new DspEngine();
        engine.Prepare(SampleRate, Channels);
        engine.ApplySettings(settings);
        engine.Reset();
        return engine;
    }

    // ---------------- Gain ----------------

    private static void GainTests(TestRunner runner)
    {
        Console.WriteLine("\n[Volume / master gain]");

        runner.Test("+6.02 dB doubles the amplitude", () =>
        {
            var gain = new GainProcessor();
            gain.Prepare(SampleRate, Channels);
            gain.SetGainDb(6.0206f);
            gain.Reset();   // snap to target, no ramp

            var input = SignalGenerator.Sine(1000f, 0.25f, OneSecond, SampleRate, Channels);
            var output = (float[])input.Clone();
            gain.Process(output);

            TestRunner.Approximately(Measure.Rms(output) / Measure.Rms(input), 2.0f, 0.01f, "gain ratio");
        });

        runner.Test("0 dB is unity, bit-identical", () =>
        {
            var gain = new GainProcessor();
            gain.SetGainDb(0f);
            var input = SignalGenerator.Noise(0.4f, 4096, Channels);
            var output = Run(gain, input);
            for (int i = 0; i < input.Length; i++)
                TestRunner.IsTrue(input[i] == output[i], $"sample {i} changed at 0 dB");
        });

        runner.Test("negative gain attenuates", () =>
        {
            var gain = new GainProcessor();
            gain.Prepare(SampleRate, Channels);
            gain.SetGainDb(-12f);
            gain.Reset();

            var input = SignalGenerator.Sine(1000f, 0.5f, OneSecond, SampleRate, Channels);
            var output = (float[])input.Clone();
            gain.Process(output);

            TestRunner.Approximately(Measure.Rms(output) / Measure.Rms(input),
                DspMath.DbToLinear(-12f), 0.01f, "attenuation ratio");
        });

        runner.Test("gain changes ramp instead of stepping", () =>
        {
            var gain = new GainProcessor();
            gain.Prepare(SampleRate, Channels);
            gain.SetGainDb(0f);
            gain.Reset();
            gain.SetGainDb(12f);   // slam the slider

            gain.Process(SignalGenerator.Sine(1000f, 0.5f, 256, SampleRate, Channels));

            // ~5 ms into a 20 ms ramp: moving, but nowhere near the 4x target yet.
            TestRunner.GreaterThan(gain.CurrentLinearGain, 1.0f, "smoothed gain moved");
            TestRunner.LessThanOrEqual(gain.CurrentLinearGain, 3.9f, "smoothed gain after 256 frames");
        });
    }

    // ---------------- Bass ----------------

    private static void BassTests(TestRunner runner)
    {
        Console.WriteLine("\n[Bass]");

        runner.Test("boosts low frequencies", () =>
        {
            var bass = new BassProcessor();
            bass.SetAmount(1.0f);
            var input = SignalGenerator.Sine(50f, 0.2f, OneSecond, SampleRate, Channels);
            var output = Run(bass, input);
            TestRunner.GreaterThan(Measure.RmsTail(output) / Measure.RmsTail(input), 2.5f, "50 Hz boost ratio");
        });

        runner.Test("leaves high frequencies alone", () =>
        {
            var bass = new BassProcessor();
            bass.SetAmount(1.0f);
            var input = SignalGenerator.Sine(8000f, 0.2f, OneSecond, SampleRate, Channels);
            var output = Run(bass, input);
            TestRunner.Approximately(Measure.RmsTail(output) / Measure.RmsTail(input), 1.0f, 0.05f,
                "8 kHz ratio under full bass");
        });

        runner.Test("amount 0 is transparent", () =>
        {
            var bass = new BassProcessor();
            bass.SetAmount(0f);
            var input = SignalGenerator.Noise(0.3f, 4096, Channels);
            var output = Run(bass, input);
            TestRunner.Approximately(Measure.DifferenceRms(input, output), 0f, 1e-7f, "difference at amount 0");
        });

        runner.Test("boost scales with the slider", () =>
        {
            var input = SignalGenerator.Sine(50f, 0.2f, OneSecond, SampleRate, Channels);

            var half = new BassProcessor(); half.SetAmount(0.5f);
            var full = new BassProcessor(); full.SetAmount(1.0f);

            float halfRatio = Measure.RmsTail(Run(half, input)) / Measure.RmsTail(input);
            float fullRatio = Measure.RmsTail(Run(full, input)) / Measure.RmsTail(input);

            TestRunner.GreaterThan(fullRatio, halfRatio, "full bass louder than half");
            TestRunner.GreaterThan(halfRatio, 1.2f, "half bass still audible");
        });
    }

    // ---------------- Treble (Clarity EQ) ----------------

    private static void TrebleTests(TestRunner runner)
    {
        Console.WriteLine("\n[Clarity / treble EQ]");

        runner.Test("shelf band boosts high frequencies", () =>
        {
            var eq = new EqProcessor();
            eq.SetBandGain(SliderMap.ClarityShelfBand, 10f);
            var input = SignalGenerator.Sine(9000f, 0.2f, OneSecond, SampleRate, Channels);
            var output = Run(eq, input);
            TestRunner.GreaterThan(Measure.RmsTail(output) / Measure.RmsTail(input), 2.2f, "9 kHz boost ratio");
        });

        runner.Test("shelf band leaves low frequencies alone", () =>
        {
            var eq = new EqProcessor();
            eq.SetBandGain(SliderMap.ClarityShelfBand, 10f);
            var input = SignalGenerator.Sine(100f, 0.2f, OneSecond, SampleRate, Channels);
            var output = Run(eq, input);
            TestRunner.Approximately(Measure.RmsTail(output) / Measure.RmsTail(input), 1.0f, 0.05f,
                "100 Hz ratio under treble boost");
        });

        runner.Test("air band boosts the top octave", () =>
        {
            var eq = new EqProcessor();
            eq.SetBandGain(SliderMap.ClarityAirBand, 6f);
            var input = SignalGenerator.Sine(SliderMap.ClarityAirHz, 0.2f, OneSecond, SampleRate, Channels);
            var output = Run(eq, input);
            TestRunner.GreaterThan(Measure.RmsTail(output) / Measure.RmsTail(input), 1.6f, "12 kHz boost ratio");
        });

        runner.Test("flat bands are transparent", () =>
        {
            var eq = new EqProcessor();
            var input = SignalGenerator.Noise(0.3f, 4096, Channels);
            var output = Run(eq, input);
            TestRunner.Approximately(Measure.DifferenceRms(input, output), 0f, 1e-7f, "difference when flat");
        });

        runner.Test("an out-of-range band index is ignored, not a crash", () =>
        {
            var eq = new EqProcessor();
            eq.SetBandGain(-1, 10f);
            eq.SetBandGain(99, 10f);
            var input = SignalGenerator.Noise(0.3f, 1024, Channels);
            var output = Run(eq, input);
            TestRunner.Approximately(Measure.DifferenceRms(input, output), 0f, 1e-7f, "difference after bad band");
        });
    }

    // ---------------- Voice + Detail ----------------

    private static void VoiceAndDetailTests(TestRunner runner)
    {
        Console.WriteLine("\n[Voice / Detail]");

        runner.Test("voice bell boosts the presence band", () =>
        {
            var voice = new VoiceProcessor();
            voice.SetAmount(1.0f);
            var input = SignalGenerator.Sine(SliderMap.VoiceFrequencyHz, 0.2f, OneSecond, SampleRate, Channels);
            var output = Run(voice, input);
            TestRunner.GreaterThan(Measure.RmsTail(output) / Measure.RmsTail(input), 2.0f, "2.6 kHz boost ratio");
        });

        runner.Test("voice bell leaves the bass alone", () =>
        {
            var voice = new VoiceProcessor();
            voice.SetAmount(1.0f);
            var input = SignalGenerator.Sine(60f, 0.2f, OneSecond, SampleRate, Channels);
            var output = Run(voice, input);
            TestRunner.Approximately(Measure.RmsTail(output) / Measure.RmsTail(input), 1.0f, 0.05f,
                "60 Hz ratio under voice boost");
        });

        runner.Test("detail exciter adds energy that was not there", () =>
        {
            var detail = new DetailProcessor();
            detail.SetAmount(1.0f);
            var input = SignalGenerator.Noise(0.3f, OneSecond, Channels);
            var output = Run(detail, input);

            TestRunner.GreaterThan(Measure.DifferenceRms(input, output), 1e-4f, "exciter difference energy");
            TestRunner.GreaterThan(Measure.Rms(output), Measure.Rms(input), "exciter added energy");
            TestRunner.IsTrue(!Measure.HasNonFinite(output), "exciter produced NaN or Inf");
        });

        runner.Test("detail at amount 0 is transparent", () =>
        {
            var detail = new DetailProcessor();
            detail.SetAmount(0f);
            var input = SignalGenerator.Noise(0.3f, 4096, Channels);
            var output = Run(detail, input);
            TestRunner.Approximately(Measure.DifferenceRms(input, output), 0f, 1e-7f, "difference at amount 0");
        });
    }

    // ---------------- Limiter ----------------

    private static void LimiterTests(TestRunner runner)
    {
        Console.WriteLine("\n[Limiter]");

        runner.Test("holds a hot signal at the ceiling", () =>
        {
            var limiter = new Limiter();
            limiter.SetCeilingDb(-1.0f);
            var input = SignalGenerator.Sine(440f, 3.0f, OneSecond, SampleRate, Channels);   // far over full scale
            var output = Run(limiter, input);

            float ceiling = DspMath.DbToLinear(-1.0f);
            float tailPeak = Measure.Peak(output.AsSpan(output.Length / 2));
            TestRunner.LessThanOrEqual(tailPeak, ceiling * 1.02f, "limited tail peak");
            TestRunner.GreaterThan(tailPeak, ceiling * 0.8f, "limiter over-reducing");
        });

        runner.Test("quiet signals pass through, allowing for look-ahead delay", () =>
        {
            var limiter = new Limiter();
            limiter.SetCeilingDb(-1.0f);
            var input = SignalGenerator.Sine(440f, 0.2f, OneSecond, SampleRate, Channels);
            var output = Run(limiter, input);

            int shift = limiter.LatencyFrames * Channels;
            TestRunner.GreaterThan(shift, 0f, "look-ahead latency");
            // output[n] should equal input[n - latency] once the delay line has filled.
            TestRunner.Approximately(
                Measure.DifferenceRms(input.AsSpan(0, input.Length - shift), output.AsSpan(shift)),
                0f, 1e-6f, "quiet-signal difference");
        });

        runner.Test("look-ahead latency is reported", () =>
        {
            var limiter = new Limiter { AttackMs = 1.0f };
            limiter.Prepare(SampleRate, Channels);
            float latencyMs = limiter.LatencyFrames * 1000f / SampleRate;
            TestRunner.Approximately(latencyMs, 5.0f, 0.2f, "latency for a 1 ms attack");
        });

        runner.Test("reports gain reduction for metering", () =>
        {
            var limiter = new Limiter();
            limiter.SetCeilingDb(-1.0f);
            Run(limiter, SignalGenerator.Sine(440f, 2.0f, OneSecond, SampleRate, Channels));
            TestRunner.LessThanOrEqual(limiter.ReadMaxGainReduction(), 0.9f, "reported gain reduction");
        });
    }

    // ---------------- Clipping protection ----------------

    private static void ClippingTests(TestRunner runner)
    {
        Console.WriteLine("\n[Clipping protection]");

        runner.Test("nothing exceeds full scale with every slider at 100", () =>
        {
            var engine = Engine(new EnhancerSettings(100, 100, 100, 100, 100));
            var buffer = SignalGenerator.Noise(0.99f, OneSecond, Channels);
            engine.Process(buffer);

            TestRunner.LessThanOrEqual(Measure.Peak(buffer), 1.0f, "engine output peak");
            TestRunner.IsTrue(!Measure.HasNonFinite(buffer), "engine produced NaN or Inf");
        });

        runner.Test("hard clip catches a signal already over full scale", () =>
        {
            var limiter = new Limiter { ClippingProtection = true };
            limiter.SetCeilingDb(0f);
            var output = Run(limiter, SignalGenerator.Sine(440f, 5.0f, 4096, SampleRate, Channels));
            TestRunner.LessThanOrEqual(Measure.Peak(output), 1.0f, "hard-clipped peak");
        });

        runner.Test("a full-scale square-ish burst stays inside full scale", () =>
        {
            var engine = Engine(new EnhancerSettings(100, 80, 80, 60, 80));
            var buffer = SignalGenerator.Impulse(1.0f, OneSecond, Channels);
            engine.Process(buffer);
            TestRunner.LessThanOrEqual(Measure.Peak(buffer), 1.0f, "impulse peak");
        });
    }

    // ---------------- Bypass (ON / OFF) ----------------

    private static void OnOffTests(TestRunner runner)
    {
        Console.WriteLine("\n[Enhancement ON/OFF (Bypass)]");

        runner.Test("Bypass is a bit-identical passthrough", () =>
        {
            var engine = Engine(new EnhancerSettings(80, 80, 80, 80, 80));
            engine.Bypass = true;

            var input = SignalGenerator.Noise(0.5f, 8192, Channels);
            var output = (float[])input.Clone();
            engine.Process(output);

            for (int i = 0; i < input.Length; i++)
                TestRunner.IsTrue(input[i] == output[i], $"sample {i} changed while bypassed");
        });

        runner.Test("ON actually changes the audio", () =>
        {
            var engine = Engine(new EnhancerSettings(0, 80, 0, 0, 0));
            var input = SignalGenerator.Noise(0.3f, 8192, Channels);
            var output = (float[])input.Clone();
            engine.Process(output);
            TestRunner.GreaterThan(Measure.DifferenceRms(input, output), 1e-3f, "difference when enabled");
        });

        runner.Test("all sliders at 0 is very close to a passthrough", () =>
        {
            var engine = Engine(new EnhancerSettings(0, 0, 0, 0, 0));
            var input = SignalGenerator.Noise(0.3f, 8192, Channels);
            var output = (float[])input.Clone();
            engine.Process(output);

            // Not bit-identical: the limiter's look-ahead still delays the signal.
            int shift = engine.LatencyFrames * Channels;
            TestRunner.Approximately(
                Measure.DifferenceRms(input.AsSpan(0, input.Length - shift), output.AsSpan(shift)),
                0f, 1e-6f, "difference at zero settings");
        });

        runner.Test("toggling Bypass repeatedly does not blow up", () =>
        {
            var engine = Engine(new EnhancerSettings(60, 90, 70, 50, 80));
            var buffer = SignalGenerator.Noise(0.4f, 2048, Channels);

            for (int block = 0; block < 20; block++)
            {
                engine.Bypass = block % 2 == 0;
                engine.Process(buffer);
            }

            TestRunner.IsTrue(!Measure.HasNonFinite(buffer), "NaN or Inf after toggling");
            TestRunner.LessThanOrEqual(Measure.Peak(buffer), 1.0f, "peak after toggling");
        });

        runner.Test("Bypass reports zero latency", () =>
        {
            var engine = Engine(new EnhancerSettings(50, 50, 50, 50, 50));
            TestRunner.GreaterThan(engine.LatencyFrames, 0f, "latency while active");
            engine.Bypass = true;
            TestRunner.IsTrue(engine.LatencyFrames == 0, "latency while bypassed");
        });
    }

    // ---------------- Parameter limits ----------------

    private static void ParameterLimitTests(TestRunner runner)
    {
        Console.WriteLine("\n[Parameter limits]");

        runner.Test("out-of-range values clamp to the safe range", () =>
        {
            var gain = new GainProcessor();
            gain.SetGainDb(1000f);
            TestRunner.Approximately(gain.GainDb, DspLimits.GainMaxDb, 1e-4f, "gain max");
            gain.SetGainDb(-1000f);
            TestRunner.Approximately(gain.GainDb, DspLimits.GainMinDb, 1e-4f, "gain min");

            var bass = new BassProcessor();
            bass.SetAmount(9f);
            TestRunner.Approximately(bass.Amount, DspLimits.AmountMax, 1e-4f, "bass amount max");
            bass.SetAmount(-9f);
            TestRunner.Approximately(bass.Amount, DspLimits.AmountMin, 1e-4f, "bass amount min");

            var eq = new EqProcessor();
            eq.SetBandGain(0, 500f);
            TestRunner.Approximately(eq.GetBandGain(0), DspLimits.BandGainMaxDb, 1e-4f, "band gain max");

            var limiter = new Limiter();
            limiter.SetCeilingDb(20f);
            TestRunner.Approximately(limiter.CeilingDb, DspLimits.CeilingMaxDb, 1e-4f, "ceiling max");
            limiter.SetCeilingDb(-200f);
            TestRunner.Approximately(limiter.CeilingDb, DspLimits.CeilingMinDb, 1e-4f, "ceiling min");
        });

        runner.Test("NaN parameters fall back to the minimum, never reaching a filter", () =>
        {
            var gain = new GainProcessor();
            gain.SetGainDb(float.NaN);
            TestRunner.IsTrue(!float.IsNaN(gain.GainDb), "gain became NaN");

            var bass = new BassProcessor();
            bass.SetAmount(float.NaN);
            TestRunner.IsTrue(!float.IsNaN(bass.Amount), "bass amount became NaN");

            var output = Run(bass, SignalGenerator.Noise(0.3f, 1024, Channels));
            TestRunner.IsTrue(!Measure.HasNonFinite(output), "NaN parameter produced NaN audio");
        });

        runner.Test("sliders outside 0-100 are clamped", () =>
        {
            var engine = Engine(new EnhancerSettings(500, -400, 10_000, -1, 250));
            TestRunner.Approximately(engine.Gain.GainDb, SliderMap.VolumeDb(100), 1e-4f, "volume over 100");
            TestRunner.Approximately(engine.Bass.Amount, 0f, 1e-4f, "bass under 0");
            TestRunner.Approximately(engine.Detail.Amount, 1f, 1e-4f, "detail over 100");

            var buffer = SignalGenerator.Noise(0.8f, 4096, Channels);
            engine.Process(buffer);
            TestRunner.IsTrue(!Measure.HasNonFinite(buffer), "NaN or Inf from clamped sliders");
            TestRunner.LessThanOrEqual(Measure.Peak(buffer), 1.0f, "peak from clamped sliders");
        });

        runner.Test("stays stable at 44.1 kHz with everything maxed", () =>
        {
            var engine = new DspEngine();
            engine.Prepare(44100, 2);
            engine.ApplySettings(new EnhancerSettings(100, 100, 100, 100, 100));
            engine.Reset();

            var buffer = SignalGenerator.Noise(0.9f, 44100, 2);
            engine.Process(buffer);
            TestRunner.IsTrue(!Measure.HasNonFinite(buffer), "NaN or Inf at 44.1 kHz");
            TestRunner.LessThanOrEqual(Measure.Peak(buffer), 1.0f, "peak at 44.1 kHz");
        });
    }

    // ---------------- Slider mapping ----------------

    private static void SliderMappingTests(TestRunner runner)
    {
        Console.WriteLine("\n[Slider mapping]");

        runner.Test("slider 0 means no effect on every control", () =>
        {
            var engine = Engine(new EnhancerSettings(0, 0, 0, 0, 0));
            TestRunner.Approximately(engine.Gain.GainDb, 0f, 1e-4f, "volume at 0");
            TestRunner.Approximately(engine.Bass.Amount, 0f, 1e-4f, "bass at 0");
            TestRunner.Approximately(engine.Voice.Amount, 0f, 1e-4f, "voice at 0");
            TestRunner.Approximately(engine.Eq.GetBandGain(SliderMap.ClarityShelfBand), 0f, 1e-4f, "clarity at 0");
            TestRunner.Approximately(engine.Detail.Amount, 0f, 1e-4f, "detail at 0");
        });

        runner.Test("each slider drives only its own stage", () =>
        {
            var engine = Engine(new EnhancerSettings(0, 100, 0, 0, 0));
            TestRunner.Approximately(engine.Bass.Amount, 1f, 1e-4f, "bass driven");
            TestRunner.Approximately(engine.Voice.Amount, 0f, 1e-4f, "voice untouched by bass slider");
            TestRunner.Approximately(engine.Detail.Amount, 0f, 1e-4f, "detail untouched by bass slider");
            TestRunner.Approximately(engine.Gain.GainDb, 0f, 1e-4f, "volume untouched by bass slider");
        });

        runner.Test("a built-in preset produces a sane, audible change", () =>
        {
            // The "Gaming" preset from BuiltInPresetManager.
            var engine = Engine(new EnhancerSettings(35, 35, 55, 25, 60));
            var input = SignalGenerator.Noise(0.3f, OneSecond, Channels);
            var output = (float[])input.Clone();
            engine.Process(output);

            TestRunner.GreaterThan(Measure.DifferenceRms(input, output), 1e-3f, "preset difference energy");
            TestRunner.LessThanOrEqual(Measure.Peak(output), 1.0f, "preset peak");
            TestRunner.IsTrue(!Measure.HasNonFinite(output), "preset produced NaN or Inf");
        });
    }

    // ---------------- Silence ----------------

    private static void SilenceTests(TestRunner runner)
    {
        Console.WriteLine("\n[Silence]");

        runner.Test("silence in, silence out with everything maxed", () =>
        {
            var engine = Engine(new EnhancerSettings(100, 100, 100, 100, 100));
            var buffer = SignalGenerator.Silence(OneSecond, Channels);
            engine.Process(buffer);

            TestRunner.Approximately(Measure.Peak(buffer), 0f, 1e-9f, "silence peak");
            TestRunner.IsTrue(!Measure.HasNonFinite(buffer), "silence produced NaN or Inf");
        });

        runner.Test("filter tails decay to silence after a burst", () =>
        {
            var engine = Engine(new EnhancerSettings(0, 100, 0, 0, 0));
            engine.Process(SignalGenerator.Impulse(1.0f, 1024, Channels));

            var tail = SignalGenerator.Silence(OneSecond, Channels);
            engine.Process(tail);

            TestRunner.LessThanOrEqual(Measure.Peak(tail.AsSpan(tail.Length - 4096)), 1e-5f, "decayed tail peak");
        });
    }

    // ---------------- Stability and real-time safety ----------------

    private static void StabilityTests(TestRunner runner)
    {
        Console.WriteLine("\n[Stability and real-time safety]");

        runner.Test("Process allocates nothing on the audio thread", () =>
        {
            var engine = Engine(new EnhancerSettings(50, 70, 60, 40, 70));
            var buffer = SignalGenerator.Noise(0.4f, 480, Channels);

            engine.Process(buffer);   // warm up the JIT and the coefficient design
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int block = 0; block < 200; block++)
                engine.Process(buffer);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            TestRunner.IsTrue(allocated == 0, $"allocated {allocated} bytes across 200 blocks");
        });

        runner.Test("handles mono, stereo and 5.1", () =>
        {
            foreach (int channels in new[] { 1, 2, 6 })
            {
                var engine = new DspEngine();
                engine.Prepare(SampleRate, channels);
                engine.ApplySettings(new EnhancerSettings(60, 80, 60, 40, 60));
                engine.Reset();

                var buffer = SignalGenerator.Noise(0.5f, 4096, channels);
                engine.Process(buffer);
                TestRunner.IsTrue(!Measure.HasNonFinite(buffer), $"NaN or Inf with {channels} channels");
                TestRunner.LessThanOrEqual(Measure.Peak(buffer), 1.0f, $"peak with {channels} channels");
            }
        });

        runner.Test("buffer size does not change the result", () =>
        {
            var settings = new EnhancerSettings(0, 70, 50, 0, 0);
            var reference = SignalGenerator.Noise(0.3f, 9600, Channels);

            var wholeBuffer = (float[])reference.Clone();
            Engine(settings).Process(wholeBuffer);

            var chunked = (float[])reference.Clone();
            var engine = Engine(settings);
            int offset = 0;
            while (offset < 9600)
            {
                int frames = Math.Min(133, 9600 - offset);
                engine.Process(chunked.AsSpan(offset * Channels, frames * Channels));
                offset += frames;
            }

            TestRunner.Approximately(Measure.DifferenceRms(wholeBuffer, chunked), 0f, 1e-6f, "buffer-size difference");
        });

        runner.Test("empty and ragged buffers are safe no-ops", () =>
        {
            var engine = Engine(new EnhancerSettings(0, 100, 0, 0, 0));

            engine.Process(Span<float>.Empty);

            // An odd sample count cannot form a whole stereo frame.
            var ragged = new float[1];
            engine.Process(ragged);
            TestRunner.Approximately(ragged[0], 0f, 1e-9f, "ragged buffer touched");
        });

        runner.Test("settings can change while audio is flowing", () =>
        {
            var engine = Engine(new EnhancerSettings(0, 0, 0, 0, 0));
            var buffer = SignalGenerator.Noise(0.4f, 480, Channels);

            for (int block = 0; block < 100; block++)
            {
                engine.ApplySettings(new EnhancerSettings(block, 100 - block, block, block / 2, block));
                engine.Process(buffer);
            }

            TestRunner.IsTrue(!Measure.HasNonFinite(buffer), "NaN or Inf while sweeping settings");
            TestRunner.LessThanOrEqual(Measure.Peak(buffer), 1.0f, "peak while sweeping settings");
        });
    }

    // ---------------- Consumer Loudness & Soft-Knee Limiter ----------------

    private static void ConsumerLoudnessAndLimiterTests(TestRunner runner)
    {
        Console.WriteLine("\n[Consumer Loudness & Soft-Knee Limiter]");

        runner.Test("volume at 100 maps to +36 dB clean loudness range", () =>
        {
            TestRunner.Approximately(SliderMap.VolumeDb(100), 36.0f, 1e-4f, "volume at 100");
        });

        runner.Test("volume at 50 maps to +18 dB clean loudness range", () =>
        {
            TestRunner.Approximately(SliderMap.VolumeDb(50), 18.0f, 1e-4f, "volume at 50");
        });

        runner.Test("volume at 0 maps to 0 dB unity gain", () =>
        {
            TestRunner.Approximately(SliderMap.VolumeDb(0), 0.0f, 1e-4f, "volume at 0");
        });

        runner.Test("limiter soft-knee prevents digital clipping with all sliders at 100", () =>
        {
            var engine = Engine(new EnhancerSettings(100, 100, 100, 100, 100));
            var input = SignalGenerator.Noise(0.9f, OneSecond, Channels);
            engine.Process(input);

            TestRunner.LessThanOrEqual(Measure.Peak(input), 1.0f, "peak with max loudness");
            TestRunner.IsTrue(!Measure.HasNonFinite(input), "no NaN or Inf at max loudness");
        });

        runner.Test("limiter lookahead protects loud sine burst with no digital clipping", () =>
        {
            var engine = Engine(new EnhancerSettings(100, 100, 100, 100, 100));
            var input = SignalGenerator.Sine(100f, 1.0f, OneSecond, SampleRate, Channels);
            engine.Process(input);

            TestRunner.LessThanOrEqual(Measure.Peak(input), 1.0f, "sine transient peak");
            TestRunner.IsTrue(!Measure.HasNonFinite(input), "no NaN or Inf on sine burst");
        });
    }
}
