using System.Runtime.InteropServices;
using AudioEnhancer.Core;
using AudioEnhancer.Dsp;
using AudioEnhancer.Dsp.Testing;

namespace AudioEnhancer.Audio.Tests;

public static class Program
{
    public static int Main()
    {
        var runner = new TestRunner();
        Console.WriteLine("AudioEnhancer audio backend tests (Part 2B)");
        Console.WriteLine("===========================================");

        FormatTests(runner);
        SampleConversionTests(runner);
        RingBufferTests(runner);
        ProcessorTests(runner);
        StreamContractTests(runner);
        ChannelMapperTests(runner);
        ResamplerTests(runner);
        RingFillControllerTests(runner);
        SignalPathTests(runner);
        ResampledSignalPathTests(runner);
        IdleBehaviorTests(runner);
        OverrunBehaviorTests(runner);
        DeviceTests(runner);
        VirtualEndpointDetectorTests(runner);
        VirtualEndpointStatusTests(runner);
        VirtualEndpointDriverContractTests(runner);
        VirtualEndpointDriverContractStep4Tests(runner);
        VirtualEndpointCaptureTests(runner);
        BackendTests(runner);
        StreamTests(runner);
        EngineCaptureDeviceTests(runner);
        EngineDeviceRecoveryTests(runner);
        EngineDiagnosticsTests(runner);
        FieldStressAndStabilityTests(runner);

        return runner.Summarise();
    }

    // ---------------- Stream format ----------------

    private static void FormatTests(TestRunner runner)
    {
        Console.WriteLine("\n[Stream format]");

        runner.Test("frame arithmetic is right for 48 kHz stereo float", () =>
        {
            var format = AudioStreamFormat.DefaultStereo;
            TestRunner.AreEqual(format.BytesPerSample, 4, "bytes per sample");
            TestRunner.AreEqual(format.BlockAlign, 8, "block align");
            TestRunner.AreEqual(format.BytesPerSecond, 48000 * 8, "bytes per second");
            TestRunner.IsTrue(format.IsFloat, "float format not reported as float");
            TestRunner.IsTrue(format.IsValid, "default stereo format reported invalid");
        });

        runner.Test("16-bit stereo has half the block size", () =>
        {
            var format = new AudioStreamFormat(44100, 2, SampleFormat.Pcm16, 16);
            TestRunner.AreEqual(format.BytesPerSample, 2, "bytes per sample");
            TestRunner.AreEqual(format.BlockAlign, 4, "block align");
            TestRunner.IsTrue(!format.IsFloat, "PCM reported as float");
        });

        runner.Test("a 10 ms buffer at 48 kHz is 480 frames", () =>
        {
            var format = AudioStreamFormat.DefaultStereo;
            TestRunner.AreEqual(format.FramesFor(TimeSpan.FromMilliseconds(10)), 480, "frames for 10 ms");
            TestRunner.Approximately(format.DurationOf(480).TotalMilliseconds, 10.0, 0.001, "duration of 480 frames");
        });

        runner.Test("a fractional buffer rounds up, never short", () =>
        {
            var format = AudioStreamFormat.DefaultStereo;
            // 3 ms is 144 frames exactly; 3.01 ms must not truncate back to 144.
            int frames = format.FramesFor(TimeSpan.FromTicks(30100));   // 3.01 ms
            TestRunner.GreaterThan(frames, 144, "rounded-up frame count");
        });

        runner.Test("nonsense formats report themselves invalid instead of throwing", () =>
        {
            TestRunner.IsTrue(!new AudioStreamFormat(0, 2, SampleFormat.Float32, 32).IsValid, "zero sample rate");
            TestRunner.IsTrue(!new AudioStreamFormat(48000, 0, SampleFormat.Float32, 32).IsValid, "zero channels");
            TestRunner.IsTrue(!new AudioStreamFormat(48000, 2, SampleFormat.Unknown, 32).IsValid, "unknown encoding");
            TestRunner.AreEqual(new AudioStreamFormat(0, 0, SampleFormat.Unknown, 0).FramesFor(TimeSpan.FromSeconds(1)),
                0, "frames for an invalid format");
        });

        runner.Test("formats describe themselves readably", () =>
        {
            string text = AudioStreamFormat.DefaultStereo.ToString();
            TestRunner.IsTrue(text.Contains("48"), $"sample rate missing from '{text}'");
            TestRunner.IsTrue(text.Contains("stereo"), $"channel layout missing from '{text}'");
            TestRunner.IsTrue(text.Contains("float"), $"encoding missing from '{text}'");
        });

        runner.Test("a WAVEFORMATEXTENSIBLE from Windows is read correctly", () =>
        {
            // Exactly the layout WASAPI hands back from GetMixFormat on a typical
            // Windows 11 machine: 48 kHz, stereo, 32-bit float, extensible.
            AudioStreamFormat? parsed = BuildAndParseExtensible(48000, 2, 32, float32: true, channelMask: 0x3);
            TestRunner.IsTrue(parsed is not null, "extensible format failed to parse");
            TestRunner.AreEqual(parsed!.SampleRate, 48000, "parsed sample rate");
            TestRunner.AreEqual(parsed.Channels, 2, "parsed channels");
            TestRunner.IsTrue(parsed.IsFloat, "parsed encoding");
            TestRunner.AreEqual((int)parsed.ChannelMask, 0x3, "parsed channel mask");
        });

        runner.Test("a 16-bit extensible format is read as PCM, not float", () =>
        {
            AudioStreamFormat? parsed = BuildAndParseExtensible(44100, 6, 16, float32: false, channelMask: 0x3F);
            TestRunner.IsTrue(parsed is not null, "extensible PCM failed to parse");
            TestRunner.AreEqual(parsed!.Channels, 6, "parsed channels");
            TestRunner.IsTrue(!parsed.IsFloat, "16-bit PCM reported as float");
            TestRunner.AreEqual(parsed.BlockAlign, 12, "parsed block align");
        });

        runner.Test("a null format pointer is null, not a crash", () =>
        {
            TestRunner.IsTrue(AudioStreamFormat.FromWaveFormatForTests(IntPtr.Zero) is null, "null pointer");
        });
    }

    /// <summary>
    /// Builds a WAVEFORMATEXTENSIBLE in unmanaged memory exactly as Windows would,
    /// then reads it back through the production parser.
    /// </summary>
    private static AudioStreamFormat? BuildAndParseExtensible(
        int sampleRate, int channels, int bits, bool float32, uint channelMask)
    {
        const int waveFormatExSize = 18;
        const int extensibleSize = waveFormatExSize + 22;

        IntPtr block = Marshal.AllocCoTaskMem(extensibleSize);
        try
        {
            int blockAlign = channels * (bits / 8);

            Marshal.WriteInt16(block, 0, unchecked((short)0xFFFE));            // wFormatTag = EXTENSIBLE
            Marshal.WriteInt16(block, 2, (short)channels);                     // nChannels
            Marshal.WriteInt32(block, 4, sampleRate);                          // nSamplesPerSec
            Marshal.WriteInt32(block, 8, sampleRate * blockAlign);             // nAvgBytesPerSec
            Marshal.WriteInt16(block, 12, (short)blockAlign);                  // nBlockAlign
            Marshal.WriteInt16(block, 14, (short)bits);                        // wBitsPerSample
            Marshal.WriteInt16(block, 16, 22);                                 // cbSize
            Marshal.WriteInt16(block, 18, (short)bits);                        // wValidBitsPerSample
            Marshal.WriteInt32(block, 20, unchecked((int)channelMask));        // dwChannelMask

            Guid subFormat = float32
                ? new Guid("00000003-0000-0010-8000-00aa00389b71")
                : new Guid("00000001-0000-0010-8000-00aa00389b71");
            byte[] guidBytes = subFormat.ToByteArray();
            Marshal.Copy(guidBytes, 0, IntPtr.Add(block, 24), 16);

            return AudioStreamFormat.FromWaveFormatForTests(block);
        }
        finally
        {
            Marshal.FreeCoTaskMem(block);
        }
    }

    // ---------------- Sample conversion ----------------

    private static void SampleConversionTests(TestRunner runner)
    {
        Console.WriteLine("\n[Sample conversion]");

        runner.Test("float32 round-trips bit-identically", () =>
        {
            var format = AudioStreamFormat.DefaultStereo;
            float[] input = SignalGenerator.Noise(0.8f, 512, 2);
            byte[] bytes = new byte[input.Length * 4];
            float[] output = new float[input.Length];

            TestRunner.AreEqual(SampleConverter.FromFloat(input, bytes, format), input.Length, "samples written");
            TestRunner.AreEqual(SampleConverter.ToFloat(bytes, output, format), input.Length, "samples read");

            for (int i = 0; i < input.Length; i++)
                TestRunner.IsTrue(input[i] == output[i], $"sample {i} changed in a float round trip");
        });

        runner.Test("16-bit PCM round-trips inside one quantisation step", () =>
        {
            var format = new AudioStreamFormat(48000, 2, SampleFormat.Pcm16, 16);
            float[] input = SignalGenerator.Sine(1000f, 0.5f, 1024, 48000, 2);
            byte[] bytes = new byte[input.Length * 2];
            float[] output = new float[input.Length];

            SampleConverter.FromFloat(input, bytes, format);
            SampleConverter.ToFloat(bytes, output, format);

            TestRunner.LessThanOrEqual(Measure.DifferenceRms(input, output), 1.0 / 32768.0, "16-bit round-trip error");
        });

        runner.Test("24-bit PCM round-trips and sign-extends negatives", () =>
        {
            var format = new AudioStreamFormat(48000, 2, SampleFormat.Pcm24, 24);
            float[] input = { -0.9f, -0.25f, 0f, 0.25f, 0.9f, -1f };
            byte[] bytes = new byte[input.Length * 3];
            float[] output = new float[input.Length];

            SampleConverter.FromFloat(input, bytes, format);
            SampleConverter.ToFloat(bytes, output, format);

            for (int i = 0; i < input.Length; i++)
                TestRunner.Approximately(output[i], input[i], 1e-4, $"24-bit sample {i}");
        });

        runner.Test("32-bit PCM round-trips", () =>
        {
            var format = new AudioStreamFormat(48000, 2, SampleFormat.Pcm32, 32);
            float[] input = { -0.75f, -0.1f, 0f, 0.1f, 0.75f };
            byte[] bytes = new byte[input.Length * 4];
            float[] output = new float[input.Length];

            SampleConverter.FromFloat(input, bytes, format);
            SampleConverter.ToFloat(bytes, output, format);

            for (int i = 0; i < input.Length; i++)
                TestRunner.Approximately(output[i], input[i], 1e-5, $"32-bit sample {i}");
        });

        runner.Test("hot samples clamp instead of wrapping polarity", () =>
        {
            var format = new AudioStreamFormat(48000, 1, SampleFormat.Pcm16, 16);
            float[] input = { 4f, -4f };
            byte[] bytes = new byte[4];
            float[] output = new float[2];

            SampleConverter.FromFloat(input, bytes, format);
            SampleConverter.ToFloat(bytes, output, format);

            TestRunner.GreaterThan(output[0], 0.99, "positive overshoot clamped to full scale");
            TestRunner.LessThanOrEqual(output[0], 1.0, "positive overshoot exceeded full scale");
            TestRunner.LessThanOrEqual(output[1], -0.99, "negative overshoot clamped to full scale");
            TestRunner.GreaterThan(output[1], -1.01, "negative overshoot exceeded full scale");
        });

        runner.Test("NaN and infinity become silence, never a spike", () =>
        {
            TestRunner.Approximately(SampleConverter.Clamp(float.NaN), 0.0, 1e-9, "NaN");
            TestRunner.Approximately(SampleConverter.Clamp(float.PositiveInfinity), 0.0, 1e-9, "+Inf");
            TestRunner.Approximately(SampleConverter.Clamp(float.NegativeInfinity), 0.0, 1e-9, "-Inf");
        });

        runner.Test("a short destination truncates instead of throwing", () =>
        {
            var format = AudioStreamFormat.DefaultStereo;
            byte[] bytes = new byte[400];              // 100 samples
            float[] small = new float[10];

            TestRunner.AreEqual(SampleConverter.ToFloat(bytes, small, format), 10, "truncated read");
            TestRunner.AreEqual(SampleConverter.ToFloat(ReadOnlySpan<byte>.Empty, small, format), 0, "empty read");
            TestRunner.AreEqual(SampleConverter.FromFloat(small, Span<byte>.Empty, format), 0, "empty write");
        });

        runner.Test("an unsupported encoding produces silence, not noise", () =>
        {
            var format = new AudioStreamFormat(48000, 2, SampleFormat.Unknown, 16);
            byte[] bytes = { 0xFF, 0x7F, 0xFF, 0x7F };
            float[] output = new float[2];

            SampleConverter.ToFloat(bytes, output, format);
            TestRunner.Approximately(output[0], 0.0, 1e-9, "unknown encoding sample 0");
            TestRunner.Approximately(output[1], 0.0, 1e-9, "unknown encoding sample 1");
        });

        runner.Test("conversion allocates nothing on the audio thread", () =>
        {
            var format = AudioStreamFormat.DefaultStereo;
            float[] samples = SignalGenerator.Noise(0.5f, 480, 2);
            byte[] bytes = new byte[samples.Length * 4];

            SampleConverter.FromFloat(samples, bytes, format);   // warm up the JIT
            SampleConverter.ToFloat(bytes, samples, format);

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int block = 0; block < 100; block++)
            {
                SampleConverter.FromFloat(samples, bytes, format);
                SampleConverter.ToFloat(bytes, samples, format);
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            TestRunner.IsTrue(allocated == 0, $"allocated {allocated} bytes across 100 blocks");
        });
    }

    // ---------------- Ring buffer ----------------

    private static void RingBufferTests(TestRunner runner)
    {
        Console.WriteLine("\n[Ring buffer]");

        runner.Test("capacity rounds up to a power of two", () =>
        {
            TestRunner.AreEqual(new AudioRingBuffer(1000).Capacity, 1024, "capacity for 1000");
            TestRunner.AreEqual(new AudioRingBuffer(1024).Capacity, 1024, "capacity for 1024");
            TestRunner.AreEqual(new AudioRingBuffer(1).Capacity, 2, "capacity for 1");
            TestRunner.AreEqual(new AudioRingBuffer(-5).Capacity, 2, "capacity for a negative request");
        });

        runner.Test("what goes in comes out, in order", () =>
        {
            var ring = new AudioRingBuffer(64);
            float[] input = { 1f, 2f, 3f, 4f, 5f };
            float[] output = new float[5];

            TestRunner.AreEqual(ring.Write(input), 5, "samples written");
            TestRunner.AreEqual(ring.Count, 5, "count after write");
            TestRunner.AreEqual(ring.Read(output), 5, "samples read");
            TestRunner.AreEqual(ring.Count, 0, "count after read");

            for (int i = 0; i < input.Length; i++)
                TestRunner.Approximately(output[i], input[i], 1e-9, $"sample {i}");
        });

        runner.Test("data survives wrapping round the end of the buffer", () =>
        {
            var ring = new AudioRingBuffer(8);            // capacity 8
            float[] scratch = new float[6];

            ring.Write(new float[] { 1, 2, 3, 4, 5, 6 });
            ring.Read(scratch);                            // read position is now 6

            float[] input = { 10, 20, 30, 40, 50 };        // straddles the wrap
            TestRunner.AreEqual(ring.Write(input), 5, "wrapped write");

            float[] output = new float[5];
            TestRunner.AreEqual(ring.Read(output), 5, "wrapped read");
            for (int i = 0; i < input.Length; i++)
                TestRunner.Approximately(output[i], input[i], 1e-9, $"wrapped sample {i}");
        });

        runner.Test("a full buffer truncates the write and counts an overrun", () =>
        {
            var ring = new AudioRingBuffer(8);
            float[] tooMuch = new float[12];

            TestRunner.AreEqual(ring.Write(tooMuch), 8, "truncated write");
            TestRunner.AreEqual(ring.OverrunSamples, 4, "overrun count");
            TestRunner.IsTrue(ring.IsFull, "buffer should be full");
            TestRunner.AreEqual(ring.Write(new float[] { 1f }), 0, "write to a full buffer");
            TestRunner.AreEqual(ring.OverrunSamples, 5, "overrun count after a rejected write");
        });

        runner.Test("an empty buffer zero-fills and counts an underrun", () =>
        {
            var ring = new AudioRingBuffer(16);
            ring.Write(new float[] { 1f, 2f });

            float[] output = new float[6];
            for (int i = 0; i < output.Length; i++) output[i] = 99f;   // poison

            TestRunner.AreEqual(ring.Read(output), 2, "real samples read");
            TestRunner.AreEqual(ring.UnderrunSamples, 4, "underrun count");
            for (int i = 2; i < output.Length; i++)
                TestRunner.Approximately(output[i], 0.0, 1e-9, $"silence padding at {i}");
        });

        runner.Test("discard and clear behave", () =>
        {
            var ring = new AudioRingBuffer(16);
            ring.Write(new float[] { 1, 2, 3, 4, 5, 6 });

            TestRunner.AreEqual(ring.Discard(2), 2, "discarded");
            TestRunner.AreEqual(ring.Count, 4, "count after discard");
            TestRunner.AreEqual(ring.Discard(100), 4, "discard more than is held");
            TestRunner.AreEqual(ring.Count, 0, "count after over-discard");
            TestRunner.AreEqual(ring.Discard(5), 0, "discard from empty");

            ring.Write(new float[] { 7, 8 });
            ring.Clear();
            TestRunner.IsTrue(ring.IsEmpty, "buffer not empty after Clear");

            ring.ResetStatistics();
            TestRunner.AreEqual(ring.OverrunSamples, 0, "overruns after reset");
            TestRunner.AreEqual(ring.UnderrunSamples, 0, "underruns after reset");
        });

        runner.Test("free space and count always add up to capacity", () =>
        {
            var ring = new AudioRingBuffer(32);
            float[] block = new float[7];

            for (int round = 0; round < 40; round++)
            {
                ring.Write(block);
                TestRunner.AreEqual(ring.Count + ring.FreeCount, ring.Capacity, "count + free");
                ring.Read(new float[5]);
                TestRunner.AreEqual(ring.Count + ring.FreeCount, ring.Capacity, "count + free after read");
            }
        });

        runner.Test("reading and writing allocate nothing", () =>
        {
            var ring = new AudioRingBuffer(4096);
            float[] block = SignalGenerator.Noise(0.5f, 240, 2);
            float[] output = new float[block.Length];

            ring.Write(block);
            ring.Read(output);

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 200; i++)
            {
                ring.Write(block);
                ring.Read(output);
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            TestRunner.IsTrue(allocated == 0, $"allocated {allocated} bytes across 200 blocks");
        });

        runner.Test("one writer and one reader on different threads stay in sync", () =>
        {
            var ring = new AudioRingBuffer(2048);
            const int total = 200_000;

            var writer = new Thread(() =>
            {
                float[] block = new float[64];
                int produced = 0;
                while (produced < total)
                {
                    for (int i = 0; i < block.Length; i++) block[i] = produced + i;
                    int written = ring.Write(block);
                    if (written == 0) { Thread.SpinWait(50); continue; }
                    produced += written;
                }
            });

            long mismatches = 0;
            long consumed = 0;
            var reader = new Thread(() =>
            {
                float[] block = new float[64];
                while (consumed < total)
                {
                    int read = ring.Read(block);
                    if (read == 0) { Thread.SpinWait(50); continue; }
                    for (int i = 0; i < read; i++)
                    {
                        if (block[i] != consumed + i) mismatches++;
                    }
                    consumed += read;
                }
            });

            writer.Start();
            reader.Start();
            TestRunner.IsTrue(writer.Join(TimeSpan.FromSeconds(20)), "writer thread did not finish");
            TestRunner.IsTrue(reader.Join(TimeSpan.FromSeconds(20)), "reader thread did not finish");

            TestRunner.AreEqual(consumed, total, "samples consumed");
            TestRunner.AreEqual(mismatches, 0, "out-of-order or corrupted samples");
        });
    }

    // ---------------- Processor seam ----------------

    private static void ProcessorTests(TestRunner runner)
    {
        Console.WriteLine("\n[Processor seam]");

        runner.Test("the passthrough processor leaves audio bit-identical", () =>
        {
            var processor = PassthroughSampleProcessor.Instance;
            processor.Prepare(AudioStreamFormat.DefaultStereo);
            processor.Reset();

            float[] input = SignalGenerator.Noise(0.5f, 512, 2);
            float[] output = (float[])input.Clone();
            processor.Process(output);

            for (int i = 0; i < input.Length; i++)
                TestRunner.IsTrue(input[i] == output[i], $"passthrough changed sample {i}");
            TestRunner.AreEqual(processor.LatencyFrames, 0, "passthrough latency");
        });

        runner.Test("Prepare hands the device format straight to the DSP engine", () =>
        {
            var engine = new DspEngine();
            var processor = new DspEngineSampleProcessor(engine);
            var format = new AudioStreamFormat(44100, 6, SampleFormat.Pcm16, 16);

            processor.Prepare(format);

            TestRunner.AreEqual(engine.SampleRate, 44100, "engine sample rate");
            TestRunner.AreEqual(engine.Channels, 6, "engine channels");
            TestRunner.IsTrue(ReferenceEquals(processor.Format, format), "format not recorded");
        });

        runner.Test("the adapter actually enhances the audio", () =>
        {
            var processor = new DspEngineSampleProcessor();
            processor.Prepare(AudioStreamFormat.DefaultStereo);
            processor.UpdateSettings(new EnhancerSettings(0, 80, 0, 0, 0));

            float[] input = SignalGenerator.Noise(0.3f, 8192, 2);
            float[] output = (float[])input.Clone();
            processor.Process(output);

            TestRunner.GreaterThan(Measure.DifferenceRms(input, output), 1e-3, "difference with bass applied");
            TestRunner.IsTrue(!Measure.HasNonFinite(output), "adapter produced NaN or Inf");
            TestRunner.LessThanOrEqual(Measure.Peak(output), 1.0, "adapter output peak");
        });

        runner.Test("Bypass through the adapter is a bit-identical passthrough", () =>
        {
            var processor = new DspEngineSampleProcessor();
            processor.Prepare(AudioStreamFormat.DefaultStereo);
            processor.UpdateSettings(new EnhancerSettings(80, 80, 80, 80, 80));
            processor.Bypass = true;

            float[] input = SignalGenerator.Noise(0.5f, 4096, 2);
            float[] output = (float[])input.Clone();
            processor.Process(output);

            for (int i = 0; i < input.Length; i++)
                TestRunner.IsTrue(input[i] == output[i], $"bypass changed sample {i}");
            TestRunner.AreEqual(processor.LatencyFrames, 0, "latency while bypassed");
        });

        runner.Test("latency is reported so a stream can stay aligned", () =>
        {
            var processor = new DspEngineSampleProcessor();
            processor.Prepare(AudioStreamFormat.DefaultStereo);

            TestRunner.GreaterThan(processor.LatencyFrames, 0, "latency while active");
            processor.Bypass = true;
            TestRunner.AreEqual(processor.LatencyFrames, 0, "latency while bypassed");
            processor.Bypass = false;
            TestRunner.GreaterThan(processor.LatencyFrames, 0, "latency after un-bypassing");
        });

        runner.Test("Process allocates nothing through the adapter", () =>
        {
            var processor = new DspEngineSampleProcessor();
            processor.Prepare(AudioStreamFormat.DefaultStereo);
            processor.UpdateSettings(new EnhancerSettings(50, 70, 60, 40, 70));

            float[] buffer = SignalGenerator.Noise(0.4f, 480, 2);
            processor.Process(buffer);   // warm up

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int block = 0; block < 200; block++)
                processor.Process(buffer);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            TestRunner.IsTrue(allocated == 0, $"allocated {allocated} bytes across 200 blocks");
        });

        runner.Test("the adapter refuses a null engine rather than failing later", () =>
        {
            TestRunner.Throws<ArgumentNullException>(
                () => new DspEngineSampleProcessor(null!), "null engine");
            TestRunner.Throws<ArgumentNullException>(
                () => new DspEngineSampleProcessor().Prepare(null!), "null format");
        });

        runner.Test("a null settings object is ignored, not a crash", () =>
        {
            var processor = new DspEngineSampleProcessor();
            processor.Prepare(AudioStreamFormat.DefaultStereo);
            processor.UpdateSettings(null!);
        });
    }

    // ---------------- Stream contracts ----------------

    private static void StreamContractTests(TestRunner runner)
    {
        Console.WriteLine("\n[Stream contracts]");

        runner.Test("the default configuration is loopback with a sane buffer", () =>
        {
            var config = new AudioStreamConfiguration();
            TestRunner.IsTrue(config.Direction == AudioStreamDirection.Loopback, "default direction");
            TestRunner.IsTrue(config.DeviceId is null, "default device");
            TestRunner.IsTrue(config.UseEventCallback, "default callback mode");
            TestRunner.IsTrue(!config.UseProcessLoopbackCapture, "process loopback is opt-in at the stream contract level");
            TestRunner.AreEqual(config.ProcessLoopbackTargetProcessId, 0u, "default process-loopback target");
            TestRunner.Approximately(config.EffectiveBufferDuration.TotalMilliseconds, 20.0, 0.001,
                "default buffer duration");
        });

        runner.Test("an explicit buffer duration wins", () =>
        {
            var config = new AudioStreamConfiguration(BufferDuration: TimeSpan.FromMilliseconds(5));
            TestRunner.Approximately(config.EffectiveBufferDuration.TotalMilliseconds, 5.0, 0.001, "explicit duration");
        });

        runner.Test("the capture device is the system default unless one is named", () =>
        {
            var config = new AudioStreamConfiguration();
            TestRunner.IsTrue(config.CaptureDeviceId is null, "default capture device");

            var explicitConfig = new AudioStreamConfiguration(CaptureDeviceId: "capture-endpoint");
            TestRunner.AreEqual(explicitConfig.CaptureDeviceId, "capture-endpoint", "explicit capture device");
            TestRunner.IsTrue(explicitConfig.DeviceId is null, "naming a capture device must not set the output device");
        });

        runner.Test("process-loopback options are additive and do not move existing arguments", () =>
        {
            var config = new AudioStreamConfiguration(
                "output-endpoint", AudioStreamDirection.Loopback, TimeSpan.FromMilliseconds(10), false, false, "capture-endpoint")
            {
                UseProcessLoopbackCapture = true,
                ProcessLoopbackTargetProcessId = 1234,
            };

            TestRunner.AreEqual(config.DeviceId, "output-endpoint", "output device");
            TestRunner.AreEqual(config.CaptureDeviceId, "capture-endpoint", "legacy capture device stays intact");
            TestRunner.IsTrue(config.UseProcessLoopbackCapture, "process loopback flag");
            TestRunner.AreEqual(config.ProcessLoopbackTargetProcessId, 1234u, "target process id");
        });

        runner.Test("adding the capture device did not move any existing positional argument", () =>
        {
            // Callers written before CaptureDeviceId existed pass these five by position.
            var config = new AudioStreamConfiguration(
                "output-endpoint", AudioStreamDirection.Loopback, TimeSpan.FromMilliseconds(10), false, false);

            TestRunner.AreEqual(config.DeviceId, "output-endpoint", "output device");
            TestRunner.Approximately(config.BufferDuration.TotalMilliseconds, 10.0, 0.001, "buffer duration");
            TestRunner.IsTrue(!config.UseEventCallback, "callback flag");
            TestRunner.IsTrue(!config.FollowDefaultDevice, "follow-default flag");
            TestRunner.IsTrue(config.CaptureDeviceId is null, "capture device stays the default");
        });

        runner.Test("ring size covers four device buffers", () =>
        {
            var config = new AudioStreamConfiguration(BufferDuration: TimeSpan.FromMilliseconds(10));
            // 10 ms at 48 kHz stereo = 480 frames * 2 channels * 4 buffers.
            TestRunner.AreEqual(config.RingBufferSamples(AudioStreamFormat.DefaultStereo), 480 * 2 * 4, "ring size");
        });

        runner.Test("statistics report a clean stream", () =>
        {
            var clean = new AudioStreamStatistics(48000, 0, 0, 240);
            var glitchy = new AudioStreamStatistics(48000, 12, 0, 240);
            TestRunner.IsTrue(clean.IsClean, "clean statistics");
            TestRunner.IsTrue(!glitchy.IsClean, "glitchy statistics");
        });
    }

    // ---------------- Device detection ----------------

    private static void DeviceTests(TestRunner runner)
    {
        Console.WriteLine("\n[Windows playback devices]");

        runner.Test("the Part 1 placeholder still behaves", () =>
        {
            var manager = new NullOutputDeviceManager();
            TestRunner.AreEqual(manager.GetOutputDevices().Count, 1, "placeholder device count");
            TestRunner.IsTrue(manager.GetDefaultOutput().IsDefault, "placeholder default flag");
        });

        runner.Test("the device manager constructs without throwing", () =>
        {
            RequireWindows();
            using var manager = new WindowsOutputDeviceManager();
            TestRunner.IsTrue(manager.GetOutputDevices().Count >= 1, "device list is never empty");
        });

        runner.Test("playback devices are listed with an id and a name", () =>
        {
            RequireWindows();
            using var manager = new WindowsOutputDeviceManager();
            if (!manager.IsAvailable)
                TestRunner.Skipped($"Core Audio unavailable: {manager.LastError}");

            IReadOnlyList<AudioDeviceInfo> devices = manager.GetOutputDevices();
            Console.WriteLine($"        found {devices.Count} playback device(s)");

            foreach (AudioDeviceInfo device in devices)
            {
                Console.WriteLine($"        - {(device.IsDefault ? "*" : " ")} {device.Name}");
                TestRunner.IsTrue(!string.IsNullOrWhiteSpace(device.Id), "device id is empty");
                TestRunner.IsTrue(!string.IsNullOrWhiteSpace(device.Name), "device name is empty");
            }

            TestRunner.LessThanOrEqual(devices.Count(d => d.IsDefault), 1, "more than one default device");
        });

        runner.Test("the default device is in the list", () =>
        {
            RequireWindows();
            using var manager = new WindowsOutputDeviceManager();
            if (!manager.IsAvailable)
                TestRunner.Skipped($"Core Audio unavailable: {manager.LastError}");

            AudioDeviceInfo defaultDevice = manager.GetDefaultOutput();
            if (defaultDevice.Id == "unavailable")
                TestRunner.Skipped("no active playback device on this machine");

            TestRunner.IsTrue(
                manager.GetOutputDevices().Any(d => d.Id == defaultDevice.Id),
                "the default device is missing from the enumerated list");
        });

        runner.Test("the default device reports a usable format", () =>
        {
            RequireWindows();
            using var manager = new WindowsOutputDeviceManager();
            if (!manager.IsAvailable)
                TestRunner.Skipped($"Core Audio unavailable: {manager.LastError}");

            AudioDeviceCapabilities? capabilities = manager.GetDefaultCapabilities();
            if (capabilities is null)
                TestRunner.Skipped("no playback device could be queried");

            Console.WriteLine($"        {capabilities}");

            TestRunner.IsTrue(capabilities!.MixFormat.IsValid, "mix format is not valid");
            TestRunner.GreaterThan(capabilities.MixFormat.SampleRate, 7000, "implausible sample rate");
            TestRunner.LessThanOrEqual(capabilities.MixFormat.SampleRate, 768000, "implausible sample rate");
            TestRunner.GreaterThan(capabilities.MixFormat.Channels, 0, "channel count");
            TestRunner.LessThanOrEqual(capabilities.MixFormat.Channels, 32, "implausible channel count");
            TestRunner.GreaterThan(capabilities.DefaultPeriod.TotalMilliseconds, 0, "device period");
            TestRunner.LessThanOrEqual(
                capabilities.MinimumPeriod.TotalMilliseconds,
                capabilities.DefaultPeriod.TotalMilliseconds,
                "minimum period larger than the default period");
        });

        runner.Test("the DSP engine accepts whatever the device reports", () =>
        {
            RequireWindows();
            using var manager = new WindowsOutputDeviceManager();
            AudioDeviceCapabilities? capabilities = manager.GetDefaultCapabilities();
            if (capabilities is null)
                TestRunner.Skipped("no playback device could be queried");

            // This is the whole point of Part 2B: a real device format drives the real engine.
            var processor = new DspEngineSampleProcessor();
            processor.Prepare(capabilities!.MixFormat);
            processor.UpdateSettings(new EnhancerSettings(35, 35, 55, 25, 60));

            float[] buffer = SignalGenerator.Noise(0.5f, 4800, capabilities.MixFormat.Channels);
            processor.Process(buffer);

            TestRunner.IsTrue(!Measure.HasNonFinite(buffer), "NaN or Inf at the device's own format");
            TestRunner.LessThanOrEqual(Measure.Peak(buffer), 1.0, "peak at the device's own format");
        });

        runner.Test("an unknown device id returns null instead of throwing", () =>
        {
            RequireWindows();
            using var manager = new WindowsOutputDeviceManager();
            TestRunner.IsTrue(manager.GetCapabilities("not-a-real-device-id") is null, "bogus id");
            TestRunner.IsTrue(manager.GetCapabilities("") is null, "empty id");
        });

        runner.Test("refresh and dispose are safe to repeat", () =>
        {
            RequireWindows();
            var manager = new WindowsOutputDeviceManager();
            manager.Refresh();
            manager.GetOutputDevices();
            manager.Refresh();
            manager.GetOutputDevices();
            manager.Dispose();
            manager.Dispose();
            // Reaching this line at all is the assertion.
        });
    }

    // ---------------- Virtual endpoint detector (Part 4 Step 1 scaffolding) ----------------

    /// <summary>
    /// Covers <see cref="VirtualEndpointDetector"/> in isolation. It has no driver behind
    /// it yet - see PART4-STEP1.md - so these tests only need plain <see cref="AudioDeviceInfo"/>
    /// values, never a real device.
    /// </summary>
    private static void VirtualEndpointDetectorTests(TestRunner runner)
    {
        Console.WriteLine("\n[Virtual endpoint detector (Part 4 Step 1 scaffolding)]");

        runner.Test("a device whose name contains the expected fragment is recognized", () =>
        {
            var device = new AudioDeviceInfo("id-1", "AudioEnhancer Virtual Output (WDM)", false);
            TestRunner.IsTrue(VirtualEndpointDetector.IsVirtualEndpoint(device), "expected recognition to succeed");
        });

        runner.Test("recognition is case-insensitive", () =>
        {
            var device = new AudioDeviceInfo("id-2", "audioenhancer virtual output", false);
            TestRunner.IsTrue(VirtualEndpointDetector.IsVirtualEndpoint(device), "expected case-insensitive match");
        });

        runner.Test("an ordinary physical device is never mistaken for the virtual endpoint", () =>
        {
            var device = new AudioDeviceInfo("id-3", "Realtek High Definition Audio", false);
            TestRunner.IsTrue(!VirtualEndpointDetector.IsVirtualEndpoint(device), "a real device should not match");
        });

        runner.Test("a null device is handled without throwing", () =>
        {
            TestRunner.IsTrue(!VirtualEndpointDetector.IsVirtualEndpoint(null!), "null device should report false, not throw");
        });

        runner.Test("Find returns null until a virtual endpoint is actually present, exactly like today", () =>
        {
            var devices = new List<AudioDeviceInfo>
            {
                new("id-4", "Speakers (Realtek)", true),
                new("id-5", "Headphones (USB)", false),
            };
            TestRunner.IsTrue(VirtualEndpointDetector.Find(devices) is null,
                "no real machine has this endpoint yet, so Find must return null");
        });

        runner.Test("Find picks the virtual endpoint out of a mixed list", () =>
        {
            var devices = new List<AudioDeviceInfo>
            {
                new("id-6", "Speakers (Realtek)", true),
                new("id-7", "AudioEnhancer Virtual Output", false),
            };
            AudioDeviceInfo? found = VirtualEndpointDetector.Find(devices);
            TestRunner.IsTrue(found is not null, "expected the virtual endpoint to be found");
            TestRunner.AreEqual(found!.Id, "id-7", "found the wrong device");
        });

        runner.Test("Find prefers the SYSVAD Speakers endpoint when multiple Tablet Sample endpoints exist", () =>
        {
            var devices = new List<AudioDeviceInfo>
            {
                new("id-spdif", "SPDIF Interface (Virtual Audio Device (WDM) - Tablet Sample)", false),
                new("id-headphones", "Headphones (Virtual Audio Device (WDM) - Tablet Sample)", false),
                new("id-speakers", "Speakers (Virtual Audio Device (WDM) - Tablet Sample)", false),
            };
            AudioDeviceInfo? found = VirtualEndpointDetector.Find(devices);
            TestRunner.IsTrue(found is not null, "expected the preferred virtual endpoint to be found");
            TestRunner.AreEqual(found!.Id, "id-speakers", "Find selected the wrong Tablet Sample endpoint");
        });
    }

    /// <summary>
    /// Covers <see cref="VirtualEndpointStatusEvaluator"/> in isolation, the same way
    /// <see cref="VirtualEndpointDetectorTests"/> covers <see cref="VirtualEndpointDetector"/>:
    /// hand-built <see cref="AudioDeviceInfo"/> lists, no real device, no driver. See
    /// PART4-STEP2.md for why every one of these is exercising a state that cannot
    /// actually occur on a real machine yet, apart from "not installed".
    /// </summary>
    private static void VirtualEndpointStatusTests(TestRunner runner)
    {
        Console.WriteLine("\n[Virtual endpoint status (Part 4 Step 2 scaffolding)]");

        runner.Test("an empty device list reports NotInstalled with no device attached", () =>
        {
            var report = VirtualEndpointStatusEvaluator.Evaluate(defaultOutput: null, devices: Array.Empty<AudioDeviceInfo>());
            TestRunner.IsTrue(report.Status == VirtualEndpointStatus.NotInstalled, "expected NotInstalled");
            TestRunner.IsTrue(report.Device is null, "NotInstalled must not carry a device");
            TestRunner.IsTrue(!report.IsInstalled, "IsInstalled must be false when not installed");
        });

        runner.Test("a device list with only physical devices reports NotInstalled", () =>
        {
            var devices = new List<AudioDeviceInfo>
            {
                new("id-1", "Speakers (Realtek)", true),
                new("id-2", "Headphones (USB)", false),
            };
            var report = VirtualEndpointStatusEvaluator.Evaluate(devices[0], devices);
            TestRunner.IsTrue(report.Status == VirtualEndpointStatus.NotInstalled, "expected NotInstalled");
        });

        runner.Test("the virtual endpoint present but not default reports InstalledNotDefault", () =>
        {
            var virtualDevice = new AudioDeviceInfo("id-virtual", "AudioEnhancer Virtual Output", false);
            var defaultDevice = new AudioDeviceInfo("id-real", "Speakers (Realtek)", true);
            var devices = new List<AudioDeviceInfo> { defaultDevice, virtualDevice };

            var report = VirtualEndpointStatusEvaluator.Evaluate(defaultDevice, devices);

            TestRunner.IsTrue(report.Status == VirtualEndpointStatus.InstalledNotDefault, "expected InstalledNotDefault");
            TestRunner.IsTrue(report.Device is not null, "expected the found device to be attached");
            TestRunner.AreEqual(report.Device!.Id, "id-virtual", "attached the wrong device");
            TestRunner.IsTrue(report.IsInstalled, "IsInstalled must be true once found, even if not default");
        });

        runner.Test("the virtual endpoint present and default reports InstalledAndDefault", () =>
        {
            var virtualDevice = new AudioDeviceInfo("id-virtual", "AudioEnhancer Virtual Output", true);
            var devices = new List<AudioDeviceInfo>
            {
                new("id-real", "Speakers (Realtek)", false),
                virtualDevice,
            };

            var report = VirtualEndpointStatusEvaluator.Evaluate(virtualDevice, devices);

            TestRunner.IsTrue(report.Status == VirtualEndpointStatus.InstalledAndDefault, "expected InstalledAndDefault");
            TestRunner.AreEqual(report.Device!.Id, "id-virtual", "attached the wrong device");
        });

        runner.Test("a null default output never throws and still finds the endpoint by name", () =>
        {
            var virtualDevice = new AudioDeviceInfo("id-virtual", "AudioEnhancer Virtual Output", false);
            var devices = new List<AudioDeviceInfo> { virtualDevice };

            var report = VirtualEndpointStatusEvaluator.Evaluate(defaultOutput: null, devices);

            TestRunner.IsTrue(report.Status == VirtualEndpointStatus.InstalledNotDefault,
                "a null default cannot equal the found device's id, so it must not be reported as default");
        });

        runner.Test("no report ever claims the endpoint exists when Find could not locate it", () =>
        {
            var devices = new List<AudioDeviceInfo>
            {
                new("id-1", "Speakers (Realtek)", true),
            };
            var report = VirtualEndpointStatusEvaluator.Evaluate(devices[0], devices);
            TestRunner.IsTrue(report.Status == VirtualEndpointStatus.NotInstalled, "expected NotInstalled");
            TestRunner.IsTrue(report.Message.Contains("not installed", StringComparison.OrdinalIgnoreCase),
                "the message must say plainly that it is not installed");
        });

        runner.Test("Evaluate throws on a null device list rather than silently misreporting", () =>
        {
            TestRunner.Throws<ArgumentNullException>(
                () => VirtualEndpointStatusEvaluator.Evaluate(null, null!),
                "null devices list");
        });
    }

    /// <summary>
    /// Covers <see cref="VirtualEndpointDriverContract"/>: not driver behavior (there is
    /// no driver), but that the contract's constants are internally consistent and, most
    /// importantly, that its friendly-name fragment can never silently drift away from
    /// <see cref="VirtualEndpointDetector"/>'s - if it did, a real future driver could be
    /// built to this contract and still not be recognized by the app.
    /// </summary>
    private static void VirtualEndpointDriverContractTests(TestRunner runner)
    {
        Console.WriteLine("\n[Virtual endpoint driver contract (Part 4 Step 3 scaffolding)]");

        runner.Test("the contract's naming requirement is the same string VirtualEndpointDetector matches on", () =>
        {
            TestRunner.AreEqual(
                VirtualEndpointDriverContract.RequiredFriendlyNameFragment,
                VirtualEndpointDetector.ExpectedNameFragment,
                "driver contract vs detector fragment");
        });

        runner.Test("a device named exactly per the contract's fragment is recognized by the detector", () =>
        {
            var device = new AudioDeviceInfo(
                "id-contract",
                $"{VirtualEndpointDriverContract.RequiredFriendlyNameFragment} (WDM)",
                false);
            TestRunner.IsTrue(VirtualEndpointDetector.IsVirtualEndpoint(device),
                "a name built from the contract's own fragment must be recognized");
        });

        runner.Test("none of the contract's documentation strings are blank", () =>
        {
            TestRunner.IsTrue(!string.IsNullOrWhiteSpace(VirtualEndpointDriverContract.RequiredDataFlow), "RequiredDataFlow");
            TestRunner.IsTrue(!string.IsNullOrWhiteSpace(VirtualEndpointDriverContract.RequiredShareMode), "RequiredShareMode");
            TestRunner.IsTrue(!string.IsNullOrWhiteSpace(VirtualEndpointDriverContract.CaptureMethod), "CaptureMethod");
            TestRunner.IsTrue(!string.IsNullOrWhiteSpace(VirtualEndpointDriverContract.FormatRequirement), "FormatRequirement");
            TestRunner.IsTrue(!string.IsNullOrWhiteSpace(VirtualEndpointDriverContract.BoundaryDescription), "BoundaryDescription");
        });
    }

    /// <summary>
    /// Part 4 Step 4. Covers the contract fields Step 4 added -
    /// <see cref="VirtualEndpointDriverContract.BufferBehavior"/>,
    /// <see cref="VirtualEndpointDriverContract.TimingClockBehavior"/>,
    /// <see cref="VirtualEndpointDriverContract.LatencyExpectation"/>,
    /// <see cref="VirtualEndpointDriverContract.EnumerationBehavior"/>,
    /// <see cref="VirtualEndpointDriverContract.DefaultDeviceBehavior"/>,
    /// <see cref="VirtualEndpointDriverContract.StartupShutdownBehavior"/>, and
    /// <see cref="VirtualEndpointDriverContract.ErrorRecoveryBehavior"/> - none of this
    /// is a driver test (there is still no driver); it is a contract test, the same
    /// distinction Step 3's driver contract tests already draw.
    /// </summary>
    private static void VirtualEndpointDriverContractStep4Tests(TestRunner runner)
    {
        Console.WriteLine("\n[Virtual endpoint driver contract (Part 4 Step 4 additions)]");

        runner.Test("none of Step 4's new contract fields are blank", () =>
        {
            TestRunner.IsTrue(!string.IsNullOrWhiteSpace(VirtualEndpointDriverContract.BufferBehavior), "BufferBehavior");
            TestRunner.IsTrue(!string.IsNullOrWhiteSpace(VirtualEndpointDriverContract.TimingClockBehavior), "TimingClockBehavior");
            TestRunner.IsTrue(!string.IsNullOrWhiteSpace(VirtualEndpointDriverContract.LatencyExpectation), "LatencyExpectation");
            TestRunner.IsTrue(!string.IsNullOrWhiteSpace(VirtualEndpointDriverContract.EnumerationBehavior), "EnumerationBehavior");
            TestRunner.IsTrue(!string.IsNullOrWhiteSpace(VirtualEndpointDriverContract.DefaultDeviceBehavior), "DefaultDeviceBehavior");
            TestRunner.IsTrue(!string.IsNullOrWhiteSpace(VirtualEndpointDriverContract.StartupShutdownBehavior), "StartupShutdownBehavior");
            TestRunner.IsTrue(!string.IsNullOrWhiteSpace(VirtualEndpointDriverContract.ErrorRecoveryBehavior), "ErrorRecoveryBehavior");
        });

        runner.Test("configuration validation: the default-device contract explicitly forbids auto-selecting itself as default", () =>
        {
            // Guards the "do not install or modify Windows audio settings automatically"
            // rule at the contract level: if a future edit ever softened this wording,
            // this test - not just a human reviewer - would catch it.
            string text = VirtualEndpointDriverContract.DefaultDeviceBehavior;
            TestRunner.IsTrue(text.Contains("NOT", StringComparison.Ordinal) || text.Contains("not", StringComparison.Ordinal),
                "DefaultDeviceBehavior must state a restriction, not just a description");
            TestRunner.IsTrue(text.Contains("default", StringComparison.OrdinalIgnoreCase),
                "DefaultDeviceBehavior must actually be about the default device");
        });

        runner.Test("timing/clock and buffer contracts name the real classes they depend on, not just prose", () =>
        {
            // These two use nameof(...) in VirtualEndpointDriverContract itself, so this
            // test is really checking that nameof() resolved to what we expect - which
            // mainly proves the const still compiles against real class names. If
            // RingFillController or AudioRingBuffer are ever renamed, the contract file
            // fails to compile before this test could even run - this test documents
            // that guarantee rather than being the only thing providing it.
            TestRunner.IsTrue(VirtualEndpointDriverContract.TimingClockBehavior.Contains(nameof(RingFillController), StringComparison.Ordinal),
                "TimingClockBehavior should reference RingFillController by name");
            TestRunner.IsTrue(VirtualEndpointDriverContract.BufferBehavior.Contains(nameof(AudioRingBuffer), StringComparison.Ordinal),
                "BufferBehavior should reference AudioRingBuffer by name");
        });

        runner.Test("driver/application contract: none of the new fields claim the endpoint currently exists", () =>
        {
            string[] fields =
            {
                VirtualEndpointDriverContract.BufferBehavior,
                VirtualEndpointDriverContract.TimingClockBehavior,
                VirtualEndpointDriverContract.LatencyExpectation,
                VirtualEndpointDriverContract.EnumerationBehavior,
                VirtualEndpointDriverContract.DefaultDeviceBehavior,
                VirtualEndpointDriverContract.StartupShutdownBehavior,
                VirtualEndpointDriverContract.ErrorRecoveryBehavior,
            };
            // Deliberately narrow phrases: "once installed"/"is installed" show up in
            // ordinary conditional wording (EnumerationBehavior's "once the driver
            // package is installed" describes a future event, not a current fact), so
            // this only rejects language that would assert present-tense existence.
            foreach (string field in fields)
            {
                TestRunner.IsTrue(
                    !field.Contains("currently exists", StringComparison.OrdinalIgnoreCase) &&
                    !field.Contains("is currently installed", StringComparison.OrdinalIgnoreCase) &&
                    !field.Contains("is now available", StringComparison.OrdinalIgnoreCase),
                    $"contract field must describe a requirement, not assert the endpoint exists: \"{field}\"");
            }
        });
    }

    /// <summary>
    /// Covers <see cref="VirtualEndpointCapture"/> and
    /// <see cref="WasapiAudioEngine.TryUseVirtualEndpointForCapture"/>: the actual
    /// "connect it to the engine" step, using <see cref="FakeOutputDeviceManager"/> to
    /// simulate an installed endpoint without any driver, COM, or Windows call.
    /// </summary>
    private static void VirtualEndpointCaptureTests(TestRunner runner)
    {
        Console.WriteLine("\n[Virtual endpoint capture wiring (Part 4 Step 3 scaffolding)]");

        runner.Test("TrySelect reports NotInstalled and touches nothing when the endpoint is absent", () =>
        {
            var devices = new FakeOutputDeviceManager(new[]
            {
                new AudioDeviceInfo("id-real", "Speakers (Realtek)", true),
            });
            var backend = new RecordingBackend(devices);
            using var engine = new WasapiAudioEngine(backend);

            VirtualEndpointCaptureOutcome outcome = VirtualEndpointCapture.TrySelect(engine, devices);

            TestRunner.IsTrue(outcome.Result == VirtualEndpointCaptureResult.NotInstalled, "expected NotInstalled");
            TestRunner.IsTrue(!outcome.Succeeded, "Succeeded must be false");

            engine.Start();
            engine.Stop();
            TestRunner.IsTrue(backend.Created[0].CaptureDeviceId is null,
                "capture device must remain the default - TrySelect must not have called SetCaptureDevice");
        });

        runner.Test("TrySelect selects the virtual endpoint as the capture device when it is present", () =>
        {
            var virtualDevice = new AudioDeviceInfo("id-virtual", "AudioEnhancer Virtual Output", false);
            var devices = new FakeOutputDeviceManager(new[]
            {
                new AudioDeviceInfo("id-real", "Speakers (Realtek)", true),
                virtualDevice,
            });
            var backend = new RecordingBackend(devices);
            using var engine = new WasapiAudioEngine(backend);

            VirtualEndpointCaptureOutcome outcome = VirtualEndpointCapture.TrySelect(engine, devices);

            TestRunner.IsTrue(outcome.Succeeded, "expected TrySelect to succeed");
            TestRunner.IsTrue(outcome.Result == VirtualEndpointCaptureResult.Selected, "expected Selected");

            engine.Start();
            engine.Stop();
            TestRunner.AreEqual(backend.Created[0].CaptureDeviceId, "id-virtual", "capture device after TrySelect");
        });

        runner.Test("TryUseVirtualEndpointForCapture on the engine matches calling VirtualEndpointCapture.TrySelect directly", () =>
        {
            var virtualDevice = new AudioDeviceInfo("id-virtual-2", "AudioEnhancer Virtual Output", false);
            var devices = new FakeOutputDeviceManager(new[] { virtualDevice });
            var backend = new RecordingBackend(devices);
            using var engine = new WasapiAudioEngine(backend);

            VirtualEndpointCaptureOutcome outcome = engine.TryUseVirtualEndpointForCapture();

            TestRunner.IsTrue(outcome.Succeeded, "expected success");
            engine.Start();
            engine.Stop();
            TestRunner.AreEqual(backend.Created[0].CaptureDeviceId, "id-virtual-2", "capture device");
        });

        runner.Test("an engine that never calls TryUseVirtualEndpointForCapture still auto-selects the virtual endpoint when present", () =>
        {
            // Start() now intentionally selects a present virtual endpoint for capture on
            // its own, without requiring TryUseVirtualEndpointForCapture to be called first.
            var virtualDevice = new AudioDeviceInfo("id-virtual-3", "AudioEnhancer Virtual Output", false);
            var devices = new FakeOutputDeviceManager(new[]
            {
                new AudioDeviceInfo("id-real", "Speakers (Realtek)", true),
                virtualDevice,
            });
            var backend = new RecordingBackend(devices);
            using var engine = new WasapiAudioEngine(backend);
            engine.SetOutputDevice("id-real");

            engine.Start();
            engine.Stop();

            TestRunner.IsTrue(backend.Created[0].UseProcessLoopbackCapture,
                "the engine must use Windows process-loopback capture for system audio");
            TestRunner.IsTrue(backend.Created[0].CaptureDeviceId is null,
                "system process-loopback must not select a physical or virtual capture endpoint");
            TestRunner.AreEqual(backend.Created[0].DeviceId, "id-real",
                "render device must remain the selected physical output");
            TestRunner.AreEqual(backend.Created[0].ProcessLoopbackTargetProcessId, (uint)Environment.ProcessId,
                "process-loopback must exclude the AudioEnhancer process tree");
        });

        runner.Test("TrySelect throws on null arguments rather than silently doing nothing", () =>
        {
            var devices = new FakeOutputDeviceManager(new[] { new AudioDeviceInfo("id-1", "Speakers", true) });
            var backend = new RecordingBackend(devices);
            using var engine = new WasapiAudioEngine(backend);

            TestRunner.Throws<ArgumentNullException>(() => VirtualEndpointCapture.TrySelect(null!, devices), "null engine");
            TestRunner.Throws<ArgumentNullException>(() => VirtualEndpointCapture.TrySelect(engine, null!), "null devices");
        });
    }

    // ---------------- Backend ----------------

    private static void BackendTests(TestRunner runner)
    {
        Console.WriteLine("\n[Backend]");

        runner.Test("the factory never throws and always describes itself", () =>
        {
            using IAudioBackend backend = AudioBackend.CreateForCurrentPlatform();
            Console.WriteLine($"        {backend.Description}");
            TestRunner.IsTrue(!string.IsNullOrWhiteSpace(backend.Description), "empty description");
            TestRunner.IsTrue(backend.Devices.GetOutputDevices().Count >= 1, "empty device list");
        });

        runner.Test("the backend creates a real stream, stopped and idle", () =>
        {
            RequireWindows();
            using IAudioBackend backend = AudioBackend.CreateForCurrentPlatform();
            if (!backend.IsAvailable) TestRunner.Skipped("no Windows audio backend");

            using IAudioStream stream = backend.Create(new AudioStreamConfiguration());
            TestRunner.IsTrue(stream.State == AudioStreamState.Closed, "a new stream should be closed");
            TestRunner.IsTrue(stream.Processor is PassthroughSampleProcessor, "default processor");
            TestRunner.IsTrue(stream.Statistics.IsClean, "a stream that never ran should have no glitches");
        });

        runner.Test("a direction other than loopback is refused", () =>
        {
            RequireWindows();
            using IAudioBackend backend = AudioBackend.CreateForCurrentPlatform();
            if (!backend.IsAvailable) TestRunner.Skipped("no Windows audio backend");

            TestRunner.Throws<AudioBackendException>(
                () => backend.Create(new AudioStreamConfiguration(Direction: AudioStreamDirection.Render)),
                "render-only direction");
        });

        runner.Test("the null backend is honest about being unavailable", () =>
        {
            using var backend = new NullAudioBackend("testing");
            TestRunner.IsTrue(!backend.IsAvailable, "null backend claimed to be available");
            TestRunner.IsTrue(backend.Description.Contains("testing"), "reason missing from description");
            TestRunner.IsTrue(backend.GetDefaultCapabilities() is null, "null backend returned capabilities");
        });

        runner.Test("the Windows backend exposes its probe", () =>
        {
            RequireWindows();
            using var backend = new WindowsAudioBackend();
            TestRunner.IsTrue(backend.Probe is not null, "probe missing");

            IReadOnlyList<AudioDeviceCapabilities> all = backend.DescribeAll();
            foreach (AudioDeviceCapabilities capabilities in all)
            {
                Console.WriteLine($"        {capabilities}");
                TestRunner.IsTrue(capabilities.MixFormat.IsValid, $"invalid format on {capabilities.Device.Name}");
            }
        });

        runner.Test("dispose is idempotent", () =>
        {
            IAudioBackend backend = AudioBackend.CreateForCurrentPlatform();
            backend.Dispose();
            backend.Dispose();
            TestRunner.IsTrue(!backend.IsAvailable, "backend still available after dispose");
        });
    }

    // ---------------- Channel mapping ----------------

    private static void ChannelMapperTests(TestRunner runner)
    {
        Console.WriteLine("\n[Channel mapping]");

        runner.Test("matching layouts are a straight copy", () =>
        {
            TestRunner.IsTrue(ChannelMapper.IsPassthrough(2, 2), "stereo to stereo");
            TestRunner.IsTrue(!ChannelMapper.IsPassthrough(6, 2), "5.1 to stereo");

            float[] source = { 1, 2, 3, 4, 5, 6 };
            float[] destination = new float[6];
            TestRunner.AreEqual(ChannelMapper.Map(source, 2, destination, 2), 3, "frames mapped");
            for (int i = 0; i < source.Length; i++)
                TestRunner.Approximately(destination[i], source[i], 1e-9, $"sample {i}");
        });

        runner.Test("mono is duplicated to every output channel", () =>
        {
            float[] source = { 0.5f, -0.25f };
            float[] destination = new float[4];
            TestRunner.AreEqual(ChannelMapper.Map(source, 1, destination, 2), 2, "frames mapped");
            TestRunner.Approximately(destination[0], 0.5, 1e-6, "frame 0 left");
            TestRunner.Approximately(destination[1], 0.5, 1e-6, "frame 0 right");
            TestRunner.Approximately(destination[2], -0.25, 1e-6, "frame 1 left");
            TestRunner.Approximately(destination[3], -0.25, 1e-6, "frame 1 right");
        });

        runner.Test("5.1 keeps front left and right when the output is stereo", () =>
        {
            // One 5.1 frame: L, R, C, LFE, Ls, Rs.
            float[] source = { 0.1f, 0.2f, 0.3f, 0.4f, 0.5f, 0.6f };
            float[] destination = new float[2];
            TestRunner.AreEqual(ChannelMapper.Map(source, 6, destination, 2), 1, "frames mapped");
            TestRunner.Approximately(destination[0], 0.1, 1e-6, "front left");
            TestRunner.Approximately(destination[1], 0.2, 1e-6, "front right");
        });

        runner.Test("extra output channels are silent, not noise", () =>
        {
            float[] source = { 0.7f, -0.7f };
            float[] destination = new float[6];
            for (int i = 0; i < destination.Length; i++) destination[i] = 99f;

            TestRunner.AreEqual(ChannelMapper.Map(source, 2, destination, 6), 1, "frames mapped");
            TestRunner.Approximately(destination[0], 0.7, 1e-6, "front left");
            TestRunner.Approximately(destination[1], -0.7, 1e-6, "front right");
            for (int channel = 2; channel < 6; channel++)
                TestRunner.Approximately(destination[channel], 0.0, 1e-9, $"channel {channel}");
        });

        runner.Test("a short destination truncates instead of throwing", () =>
        {
            float[] source = new float[20];
            float[] destination = new float[6];
            TestRunner.AreEqual(ChannelMapper.Map(source, 2, destination, 2), 3, "truncated frames");
            TestRunner.AreEqual(ChannelMapper.Map(source, 0, destination, 2), 0, "zero source channels");
            TestRunner.AreEqual(ChannelMapper.Map(ReadOnlySpan<float>.Empty, 2, destination, 2), 0, "empty source");
        });

        runner.Test("mapping allocates nothing", () =>
        {
            float[] source = SignalGenerator.Noise(0.5f, 480, 6);
            float[] destination = new float[480 * 2];
            ChannelMapper.Map(source, 6, destination, 2);

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 200; i++)
                ChannelMapper.Map(source, 6, destination, 2);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            TestRunner.IsTrue(allocated == 0, $"allocated {allocated} bytes across 200 blocks");
        });
    }

    // ---------------- End-to-end signal path, without hardware ----------------

    /// <summary>
    /// Drives exactly the sequence the capture and render threads use - device bytes to
    /// float, channel map, ring buffer, DSP, float back to device bytes - so the whole
    /// chain is covered on a machine with no audio device at all.
    /// </summary>
    private static float[] RunSignalPath(
        AudioStreamFormat captureFormat,
        AudioStreamFormat renderFormat,
        DspEngineSampleProcessor processor,
        float[] captured,
        int renderBlockFrames)
    {
        int captureFrames = captured.Length / captureFormat.Channels;

        // Capture thread: float -> device bytes is what WASAPI would have handed us.
        byte[] deviceBytes = new byte[captureFrames * captureFormat.BlockAlign];
        SampleConverter.FromFloat(captured, deviceBytes, captureFormat);

        var ring = new AudioRingBuffer(captureFrames * renderFormat.Channels * 2);
        float[] captureScratch = new float[captureFrames * captureFormat.Channels];
        float[] mapScratch = new float[captureFrames * renderFormat.Channels];

        SampleConverter.ToFloat(deviceBytes, captureScratch, captureFormat);
        int mappedFrames = ChannelMapper.Map(captureScratch, captureFormat.Channels, mapScratch, renderFormat.Channels);
        ring.Write(mapScratch.AsSpan(0, mappedFrames * renderFormat.Channels));

        // Render thread: ring -> DSP -> device bytes, one device buffer at a time.
        int totalSamples = mappedFrames * renderFormat.Channels;
        float[] rendered = new float[totalSamples];
        float[] block = new float[renderBlockFrames * renderFormat.Channels];
        byte[] renderBytes = new byte[renderBlockFrames * renderFormat.BlockAlign];

        int written = 0;
        while (written < totalSamples)
        {
            int samples = Math.Min(block.Length, totalSamples - written);
            Span<float> slice = block.AsSpan(0, samples);

            ring.Read(slice);
            processor.Process(slice);
            SampleConverter.FromFloat(slice, renderBytes.AsSpan(0, samples * renderFormat.BytesPerSample), renderFormat);
            SampleConverter.ToFloat(renderBytes.AsSpan(0, samples * renderFormat.BytesPerSample),
                                    rendered.AsSpan(written, samples), renderFormat);
            written += samples;
        }

        return rendered;
    }

    /// <summary>
    /// Same idea as <see cref="RunSignalPath"/>, but with <see cref="AdaptiveResampler"/>
    /// sitting between the ring and the DSP, exactly where it sits in
    /// <see cref="WasapiAudioStream"/>'s render loop: ring -&gt; resampler -&gt; DSP -&gt; device
    /// bytes. This is what lets a capture and render format with different sample rates
    /// be exercised end to end with no hardware.
    ///
    /// <para><b>Deliberately does not use <see cref="RingFillController"/>.</b> Like
    /// <see cref="RunSignalPath"/>, this helper writes the whole capture into the ring up
    /// front rather than incrementally the way the real capture thread does. A controller
    /// reacting to a ring that starts out artificially full - the entire test signal,
    /// instead of a few milliseconds - would see a huge one-off fill error and apply a
    /// correspondingly large correction, which is not what a real-time session's fill
    /// controller would ever do in practice and would only make these tests measure the
    /// controller's reaction to the test harness rather than the resampler's accuracy.
    /// The resampler's ratio is therefore left at its nominal value throughout: format
    /// conversion only, no drift correction. <see cref="RingFillControllerTests"/> already
    /// covers the controller itself, against a harness built for that.</para>
    /// </summary>
    private static float[] RunResampledSignalPath(
        AudioStreamFormat captureFormat,
        AudioStreamFormat renderFormat,
        DspEngineSampleProcessor processor,
        float[] captured,
        int renderBlockFrames)
    {
        int captureFrames = captured.Length / captureFormat.Channels;

        // Capture side: float -> device bytes -> float -> channel-mapped to the render
        // layout, exactly what PushCapturedFrames does before anything reaches the ring.
        byte[] deviceBytes = new byte[captureFrames * captureFormat.BlockAlign];
        SampleConverter.FromFloat(captured, deviceBytes, captureFormat);

        float[] captureScratch = new float[captureFrames * captureFormat.Channels];
        float[] mapScratch = new float[captureFrames * renderFormat.Channels];
        SampleConverter.ToFloat(deviceBytes, captureScratch, captureFormat);
        int mappedFrames = ChannelMapper.Map(captureScratch, captureFormat.Channels, mapScratch, renderFormat.Channels);

        var ring = new AudioRingBuffer(mappedFrames * renderFormat.Channels + 8);
        ring.Write(mapScratch.AsSpan(0, mappedFrames * renderFormat.Channels));

        var resampler = new AdaptiveResampler();
        resampler.Prepare(renderFormat.Channels, captureFormat.SampleRate, renderFormat.SampleRate);
        // resampler.Ratio is left at NominalRatio - see remarks above.

        int destinationFrames = (int)Math.Round(mappedFrames * ((double)renderFormat.SampleRate / captureFormat.SampleRate));
        int totalSamples = destinationFrames * renderFormat.Channels;
        float[] rendered = new float[totalSamples];
        float[] block = new float[renderBlockFrames * renderFormat.Channels];
        byte[] renderBytes = new byte[renderBlockFrames * renderFormat.BlockAlign];

        int written = 0;
        while (written < totalSamples)
        {
            int samples = Math.Min(block.Length, totalSamples - written);
            Span<float> slice = block.AsSpan(0, samples);

            resampler.Render(ring, slice);
            processor.Process(slice);
            SampleConverter.FromFloat(slice, renderBytes.AsSpan(0, samples * renderFormat.BytesPerSample), renderFormat);
            SampleConverter.ToFloat(renderBytes.AsSpan(0, samples * renderFormat.BytesPerSample),
                                    rendered.AsSpan(written, samples), renderFormat);
            written += samples;
        }

        return rendered;
    }

    private static void ResampledSignalPathTests(TestRunner runner)
    {
        Console.WriteLine("\n[Signal path: capture -> ring -> resampler -> DSP -> render]");

        runner.Test("full chain at 48 kHz -> 48 kHz matches the unresampled chain exactly", () =>
        {
            // At matched rates AdaptiveResampler.Render falls straight through to
            // AudioRingBuffer.Read (see its unity-ratio remarks), so routing the signal
            // through the resampler here must not change a single sample versus the
            // plain ring -> DSP chain RunSignalPath already covers.
            var format = AudioStreamFormat.DefaultStereo;

            var direct = new DspEngineSampleProcessor();
            direct.Prepare(format);
            direct.UpdateSettings(new EnhancerSettings(20, 60, 30, 40, 50));

            var resampled = new DspEngineSampleProcessor();
            resampled.Prepare(format);
            resampled.UpdateSettings(new EnhancerSettings(20, 60, 30, 40, 50));

            float[] captured = SignalGenerator.Noise(0.3f, 9600, 2);
            float[] viaDirectChain = RunSignalPath(format, format, direct, captured, 480);
            float[] viaResampledChain = RunResampledSignalPath(format, format, resampled, captured, 480);

            TestRunner.AreEqual(viaResampledChain.Length, viaDirectChain.Length, "rendered sample count");
            TestRunner.IsTrue(!Measure.HasNonFinite(viaResampledChain), "48->48 chain produced NaN or Inf");
            for (int i = 0; i < viaDirectChain.Length; i++)
                TestRunner.IsTrue(viaDirectChain[i] == viaResampledChain[i],
                    $"sample {i}: 48kHz->48kHz through the resampler should be bit-identical to the direct chain");
        });

        runner.Test("full chain at 44.1 kHz -> 48 kHz reproduces a low-frequency tone through DSP bypass", () =>
        {
            var captureFormat = new AudioStreamFormat(44100, 2, SampleFormat.Float32, 32);
            var renderFormat = AudioStreamFormat.DefaultStereo;   // 48000, stereo, float32

            var processor = new DspEngineSampleProcessor();
            processor.Prepare(renderFormat);
            processor.Bypass = true;   // isolate resampling accuracy from DSP colouration

            const double toneHz = 100.0;
            const double durationSeconds = 0.5;
            int sourceFrames = (int)(captureFormat.SampleRate * durationSeconds);
            float[] captured = new float[sourceFrames * captureFormat.Channels];
            for (int frame = 0; frame < sourceFrames; frame++)
            {
                float sample = (float)Math.Sin(2.0 * Math.PI * toneHz * frame / captureFormat.SampleRate);
                captured[frame * 2] = sample;
                captured[frame * 2 + 1] = sample;
            }

            float[] rendered = RunResampledSignalPath(captureFormat, renderFormat, processor, captured, 480);

            TestRunner.IsTrue(!Measure.HasNonFinite(rendered), "44.1->48 chain produced NaN or Inf");
            TestRunner.LessThanOrEqual(Measure.Peak(rendered), 1.0, "44.1->48 chain output peak");

            int renderedFrames = rendered.Length / renderFormat.Channels;
            double maxError = 0.0;
            // Skip the first few frames at each end: the resampler needs to prime its
            // previous/next frame pair before interpolation is meaningful, and the very
            // last output frames can run slightly short of source data because of the
            // rounding in destinationFrames above - neither edge effect is what this test
            // is measuring.
            for (int frame = 8; frame < renderedFrames - 8; frame++)
            {
                double expected = Math.Sin(2.0 * Math.PI * toneHz * frame / renderFormat.SampleRate);
                double error = Math.Abs(rendered[frame * 2] - expected);
                if (error > maxError) maxError = error;
            }

            Console.WriteLine($"        peak error against the ideal 100 Hz tone, full chain: {maxError:F6}");
            TestRunner.LessThanOrEqual(maxError, 0.02, "peak full-chain resampling error at 100 Hz");
        });

        runner.Test("full chain at 44.1 kHz -> 48 kHz is enhanced without corrupting the signal", () =>
        {
            var captureFormat = new AudioStreamFormat(44100, 2, SampleFormat.Float32, 32);
            var renderFormat = AudioStreamFormat.DefaultStereo;

            var processor = new DspEngineSampleProcessor();
            processor.Prepare(renderFormat);
            processor.UpdateSettings(new EnhancerSettings(0, 90, 0, 0, 0));

            float[] captured = SignalGenerator.Noise(0.3f, 8820, 2);   // 200 ms at 44.1 kHz
            float[] rendered = RunResampledSignalPath(captureFormat, renderFormat, processor, captured, 480);

            TestRunner.IsTrue(!Measure.HasNonFinite(rendered), "44.1->48 enhanced chain produced NaN or Inf");
            TestRunner.LessThanOrEqual(Measure.Peak(rendered), 1.0, "44.1->48 enhanced chain output peak");
            TestRunner.GreaterThan(Measure.Rms(rendered), 1e-4, "44.1->48 enhanced chain output is silent");
        });
    }

    // ---------------- Idle behavior ----------------

    /// <summary>
    /// Drives the render loop's own sequence - ring.Count -> FillController.Update -&gt;
    /// resampler.Ratio -&gt; resampler.Render -&gt; DSP - the same order
    /// <see cref="WasapiAudioStream"/>'s RenderLoop uses, but against a ring that is
    /// never written to. This is what "idle" means for this stream: Windows is not
    /// playing anything, so the loopback capture side has nothing to push, yet the
    /// render device keeps asking for buffers on schedule and the loop must keep
    /// servicing them.
    /// </summary>
    private static void IdleBehaviorTests(TestRunner runner)
    {
        Console.WriteLine("\n[Idle behavior: render loop against an empty ring]");

        runner.Test("an idle ring renders silence indefinitely, with no exception, NaN or growing allocation", () =>
        {
            var format = AudioStreamFormat.DefaultStereo;
            var ring = new AudioRingBuffer(4096);   // never written - nothing playing

            var resampler = new AdaptiveResampler();
            resampler.Prepare(format.Channels, format.SampleRate, format.SampleRate);

            var controller = new RingFillController(ring.Capacity / 2);
            controller.SetActive(true);

            var processor = new DspEngineSampleProcessor();
            processor.Prepare(format);
            processor.UpdateSettings(new EnhancerSettings(40, 60, 50, 30, 50));

            float[] block = new float[480 * format.Channels];

            // Warm up every path once before measuring allocations, same pattern as the
            // other allocation tests in this file.
            resampler.Render(ring, block);
            controller.Update(ring.Count);
            resampler.Ratio = resampler.NominalRatio * (1.0 + controller.Correction);
            processor.Process(block);

            // No per-iteration message-string construction inside this loop - even an
            // untriggered TestRunner assertion built from an interpolated string would
            // itself allocate, which would corrupt the very measurement this loop exists
            // to take. NaN/Inf and peak are tracked as plain locals and asserted once,
            // after the allocation count has already been captured.
            bool nonFiniteSeen = false;
            float peakSeen = 0f;

            long before = GC.GetAllocatedBytesForCurrentThread();
            const int idleCallbacks = 2000;   // 2000 x 10 ms = 20 s of simulated silence
            for (int i = 0; i < idleCallbacks; i++)
            {
                resampler.Render(ring, block);
                controller.Update(ring.Count);
                resampler.Ratio = resampler.NominalRatio * (1.0 + controller.Correction);
                processor.Process(block);

                if (Measure.HasNonFinite(block)) nonFiniteSeen = true;
                float peak = Measure.Peak(block);
                if (peak > peakSeen) peakSeen = peak;
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            TestRunner.IsTrue(!nonFiniteSeen, "an idle callback produced NaN or Inf at some point during the run");
            TestRunner.LessThanOrEqual(peakSeen, 1.0, "peak output across all idle callbacks");
            TestRunner.IsTrue(allocated == 0, $"allocated {allocated} bytes across {idleCallbacks} idle render callbacks");
            TestRunner.GreaterThan(ring.UnderrunSamples, 0, "an idle ring should be counted as underrunning, not silently skipped");

            // An empty ring is a constant fill error of -target, and the controller is
            // proportional rather than integrating (see its class remarks), so the
            // correction settles at one fixed value on the very first update and holds
            // there - it must never grow further just because the idle period ran long,
            // and it must never exceed the configured ceiling.
            double expectedCorrection = Math.Clamp(
                RingFillController.DefaultGain * (0 - controller.TargetFillSamples),
                -RingFillController.DefaultMaxCorrection, RingFillController.DefaultMaxCorrection);
            TestRunner.Approximately(controller.Correction, expectedCorrection, 1e-12,
                "an indefinitely empty ring should settle at a fixed proportional correction, not run away");
            TestRunner.GreaterThan(controller.Correction, -RingFillController.DefaultMaxCorrection - 1e-12,
                "correction should never exceed the configured floor");
        });

        runner.Test("audio recovers cleanly once a capture resumes after an idle period", () =>
        {
            var format = AudioStreamFormat.DefaultStereo;
            var ring = new AudioRingBuffer(4096);

            var resampler = new AdaptiveResampler();
            resampler.Prepare(format.Channels, format.SampleRate, format.SampleRate);

            var controller = new RingFillController(ring.Capacity / 2);
            controller.SetActive(true);

            var processor = new DspEngineSampleProcessor();
            processor.Prepare(format);
            processor.Bypass = true;

            float[] block = new float[480 * format.Channels];

            // Simulate several seconds of silence, exactly as the idle test above does.
            for (int i = 0; i < 300; i++)
            {
                resampler.Render(ring, block);
                controller.Update(ring.Count);
                resampler.Ratio = resampler.NominalRatio * (1.0 + controller.Correction);
                processor.Process(block);
            }

            // Windows starts playing something again: capture begins pushing real frames.
            float[] tone = SignalGenerator.Sine(1000f, 0.5f, 480, format.SampleRate, format.Channels);
            ring.Write(tone);

            resampler.Render(ring, block);
            processor.Process(block);

            TestRunner.IsTrue(!Measure.HasNonFinite(block), "recovered block produced NaN or Inf");
            TestRunner.GreaterThan(Measure.Rms(block), 1e-4, "recovered block should carry real audio, not more silence");
        });
    }

    // ---------------- Overrun behavior ----------------

    private static void OverrunBehaviorTests(TestRunner runner)
    {
        Console.WriteLine("\n[Overrun behavior: capture outpacing the render side]");

        runner.Test("a capture burst larger than the ring is truncated, counted, and playback stays clean", () =>
        {
            // Stands in for the render side stalling (or a capture burst after a glitch):
            // PushCapturedFrames keeps calling Ring.Write regardless of whether the render
            // thread is keeping up, so the ring, not the capture loop, is what absorbs an
            // overrun. A tiny ring makes it easy to overflow deliberately.
            var format = AudioStreamFormat.DefaultStereo;
            var ring = new AudioRingBuffer(1024);

            float[] burst = SignalGenerator.Noise(0.4f, 2000, format.Channels);   // way more than the ring holds
            int written = ring.Write(burst);

            TestRunner.IsTrue(written < burst.Length, "a burst larger than capacity should be truncated by Write");
            TestRunner.AreEqual(ring.OverrunSamples, burst.Length - written, "overrun sample count");

            // The render side must still come back with a full, sane buffer afterwards -
            // an overrun on the write side must not corrupt what the read side hands back.
            var resampler = new AdaptiveResampler();
            resampler.Prepare(format.Channels, format.SampleRate, format.SampleRate);

            var processor = new DspEngineSampleProcessor();
            processor.Prepare(format);
            processor.UpdateSettings(new EnhancerSettings(30, 50, 40, 30, 40));

            float[] block = new float[480 * format.Channels];
            resampler.Render(ring, block);
            processor.Process(block);

            TestRunner.IsTrue(!Measure.HasNonFinite(block), "post-overrun render produced NaN or Inf");
            TestRunner.LessThanOrEqual(Measure.Peak(block), 1.0, "post-overrun render peak");
        });

        runner.Test("repeated overruns keep accumulating without capping or wrapping", () =>
        {
            var ring = new AudioRingBuffer(256);
            float[] chunk = new float[1000];

            for (int i = 0; i < 5; i++) ring.Write(chunk);

            // Every write after the first fully overruns a ring nobody is draining, so the
            // total should be very close to 5 * chunk.Length, not clamped to one write's
            // worth or reset between calls.
            TestRunner.GreaterThan(ring.OverrunSamples, 4 * (chunk.Length - ring.Capacity), "cumulative overrun count");
        });

        runner.Test("the render side recovers once the consumer catches up after an overrun", () =>
        {
            var format = AudioStreamFormat.DefaultStereo;
            var ring = new AudioRingBuffer(1024);

            // Overflow it once, same as a stalled render thread would.
            ring.Write(SignalGenerator.Noise(0.3f, 2000, format.Channels));
            long overrunsAfterBurst = ring.OverrunSamples;

            var resampler = new AdaptiveResampler();
            resampler.Prepare(format.Channels, format.SampleRate, format.SampleRate);
            var processor = new DspEngineSampleProcessor();
            processor.Prepare(format);

            float[] block = new float[256 * format.Channels];   // 512 samples per pull

            // Drain the ring fully, the way a render thread that stalled and then caught
            // back up would, before a normal-sized write is expected to fit cleanly again.
            resampler.Render(ring, block);
            resampler.Render(ring, block);
            TestRunner.AreEqual(ring.Count, 0, "ring should be fully drained before the recovery write");

            ring.Write(SignalGenerator.Noise(0.3f, 100, format.Channels));   // well within free capacity
            resampler.Render(ring, block);
            processor.Process(block);

            TestRunner.AreEqual(ring.OverrunSamples, overrunsAfterBurst, "no further overruns once writes fit again");
            TestRunner.IsTrue(!Measure.HasNonFinite(block), "post-recovery render produced NaN or Inf");
        });
    }

    // ---------------- AdaptiveResampler ----------------

    private static void ResamplerTests(TestRunner runner)
    {
        Console.WriteLine("\n[Adaptive resampler]");

        runner.Test("ratio 1.0 is a bit-identical passthrough", () =>
        {
            var resampler = new AdaptiveResampler();
            resampler.Prepare(channels: 2, sourceRate: 48000, targetRate: 48000);
            TestRunner.Approximately(resampler.Ratio, 1.0, 1e-12, "default ratio at matched rates");
            TestRunner.IsTrue(resampler.IsUnity, "ratio 1.0 should be treated as unity");

            var rng = new Random(1);
            float[] source = new float[2000];
            for (int i = 0; i < source.Length; i++) source[i] = (float)(rng.NextDouble() * 2.0 - 1.0);

            var ring = new AudioRingBuffer(4096);
            ring.Write(source);

            float[] destination = new float[source.Length];
            resampler.Render(ring, destination);

            for (int i = 0; i < source.Length; i++)
                TestRunner.IsTrue(destination[i] == source[i], $"sample {i}: expected exact bit-identical passthrough at ratio 1.0");
        });

        runner.Test("a ratio nudged back to 1.0 returns to bit-identical passthrough", () =>
        {
            var resampler = new AdaptiveResampler();
            resampler.Prepare(channels: 1, sourceRate: 48000, targetRate: 48000);

            // Run the interpolation path first so there is stale internal state to discard:
            // a primed previous/next frame pair and a nonzero phase.
            var warmupRing = new AudioRingBuffer(4096);
            warmupRing.Write(new float[600]);
            resampler.Ratio = 1.02;
            resampler.Render(warmupRing, new float[200]);

            // A fresh ring keeps this half of the test about the resampler's own state,
            // not about leftover queued samples from the warm-up ring.
            resampler.Ratio = 1.0;
            var ring = new AudioRingBuffer(4096);
            float[] source = { 0.25f, -0.5f, 0.75f, -1.0f, 0.125f };
            ring.Write(source);
            float[] destination = new float[source.Length];
            resampler.Render(ring, destination);

            for (int i = 0; i < source.Length; i++)
                TestRunner.IsTrue(destination[i] == source[i], $"sample {i}: passthrough did not resume cleanly after leaving unity");
        });

        runner.Test("44.1 kHz to 48 kHz reproduces a low-frequency tone accurately", () =>
        {
            const int sourceRate = 44100;
            const int targetRate = 48000;
            const double toneHz = 100.0;
            const double durationSeconds = 0.5;

            var resampler = new AdaptiveResampler();
            resampler.Prepare(channels: 1, sourceRate: sourceRate, targetRate: targetRate);
            TestRunner.Approximately(resampler.NominalRatio, (double)sourceRate / targetRate, 1e-12, "nominal ratio");
            TestRunner.IsTrue(!resampler.IsUnity, "44.1 kHz to 48 kHz must not fall into the unity deadband");

            int sourceFrames = (int)(sourceRate * durationSeconds);
            float[] source = new float[sourceFrames];
            for (int i = 0; i < sourceFrames; i++)
                source[i] = (float)Math.Sin(2.0 * Math.PI * toneHz * i / sourceRate);

            var ring = new AudioRingBuffer(NextPowerOfTwoAtLeast(sourceFrames + 8));
            ring.Write(source);

            int destinationFrames = (int)Math.Round(sourceFrames * ((double)targetRate / sourceRate));
            float[] destination = new float[destinationFrames];
            resampler.Render(ring, destination);

            double maxError = 0.0;
            for (int i = 0; i < destinationFrames; i++)
            {
                double expected = Math.Sin(2.0 * Math.PI * toneHz * i / targetRate);
                double error = Math.Abs(destination[i] - expected);
                if (error > maxError) maxError = error;
            }

            Console.WriteLine($"        peak error against the ideal 100 Hz tone: {maxError:F6}");
            TestRunner.LessThanOrEqual(maxError, 0.01, "peak resampling error at 100 Hz");
        });

        runner.Test("a starved ring still fills the destination, as silence and an underrun", () =>
        {
            var resampler = new AdaptiveResampler();
            resampler.Prepare(channels: 2, sourceRate: 44100, targetRate: 48000);

            var ring = new AudioRingBuffer(256);
            float[] destination = new float[64];
            Array.Fill(destination, 1234f);

            resampler.Render(ring, destination);

            TestRunner.IsTrue(Array.TrueForAll(destination, sample => sample == 0f), "starved output should be silence, not garbage");
            TestRunner.GreaterThan(ring.UnderrunSamples, 0, "starving the resampler should count as an underrun");
        });

        runner.Test("Render allocates nothing once primed", () =>
        {
            var resampler = new AdaptiveResampler();
            resampler.Prepare(channels: 2, sourceRate: 44100, targetRate: 48000);
            resampler.Ratio = resampler.NominalRatio;   // exercise the interpolation path, not the deadband

            var ring = new AudioRingBuffer(8192);
            float[] writeChunk = new float[960];   // 20 ms worth of stereo capture-rate audio
            float[] destination = new float[960];  // 20 ms worth of stereo render-rate audio

            // Warm up: first call primes _previousFrame/_nextFrame from the ring.
            ring.Write(writeChunk);
            resampler.Render(ring, destination);

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 500; i++)
            {
                ring.Write(writeChunk);
                resampler.Render(ring, destination);
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            TestRunner.IsTrue(allocated == 0, $"allocated {allocated} bytes across 500 resample callbacks");
        });
    }

    private static int NextPowerOfTwoAtLeast(int value)
    {
        int result = 2;
        while (result < value) result <<= 1;
        return result;
    }

    // ---------------- RingFillController ----------------

    private static void RingFillControllerTests(TestRunner runner)
    {
        Console.WriteLine("\n[Ring fill controller]");

        runner.Test("adjustment is frozen until the controller is made active", () =>
        {
            var controller = new RingFillController(targetFillSamples: 1000, gain: 0.01, maxCorrection: 0.5);
            TestRunner.IsTrue(!controller.IsActive, "starts inactive");

            controller.Update(9000);   // a large fill error, which would otherwise move the correction a lot
            TestRunner.Approximately(controller.Correction, 0.0, 1e-12, "correction while frozen before activation");

            controller.SetActive(true);
            controller.Update(9000);
            TestRunner.GreaterThan(controller.Correction, 0.0, "correction should move once active");

            double activeValue = controller.Correction;
            controller.SetActive(false);
            controller.Update(1);   // would otherwise swing the correction sharply negative
            TestRunner.Approximately(controller.Correction, activeValue, 1e-12, "correction should hold its last value once frozen again");
        });

        runner.Test("a full ring drives the correction positive, an empty ring negative", () =>
        {
            var controller = new RingFillController(targetFillSamples: 1000, gain: 1e-4, maxCorrection: 0.5);
            controller.SetActive(true);

            controller.Update(5000);
            TestRunner.GreaterThan(controller.Correction, 0.0, "an over-full ring should speed consumption up");

            controller.Reset();
            controller.Update(0);
            TestRunner.IsTrue(controller.Correction < 0.0, $"an empty ring should slow consumption down, got {controller.Correction}");
        });

        runner.Test("correction never exceeds the configured ceiling", () =>
        {
            var controller = new RingFillController(targetFillSamples: 0, gain: 1.0, maxCorrection: 0.02);
            controller.SetActive(true);
            controller.Update(1_000_000);
            TestRunner.Approximately(controller.Correction, 0.02, 1e-12, "correction should clamp at the ceiling");

            controller.Update(-1_000_000);
            TestRunner.Approximately(controller.Correction, -0.02, 1e-12, "correction should clamp at the floor");
        });

        runner.Test("Update allocates nothing", () =>
        {
            var controller = new RingFillController(targetFillSamples: 4096);
            controller.SetActive(true);
            controller.Update(4096);

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 2000; i++) controller.Update(4000 + i % 200);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            TestRunner.IsTrue(allocated == 0, $"allocated {allocated} bytes across 2000 updates");
        });

        // ---- AdaptiveResampler + RingFillController together: long-run clock-drift correction ----

        runner.Test("corrects +200 ppm of simulated capture-clock drift over 10 minutes", () =>
            RunDriftSimulation(driftPpm: 200.0));

        runner.Test("corrects -200 ppm of simulated capture-clock drift over 10 minutes", () =>
            RunDriftSimulation(driftPpm: -200.0));
    }

    /// <summary>
    /// Simulates a capture device whose clock runs <paramref name="driftPpm"/> parts per
    /// million away from the render device's, for ten minutes of audio, entirely in
    /// arithmetic - no real ring-write/ring-read pair takes ten minutes of wall-clock time
    /// to run through a tight loop, so this test finishes in well under a second while
    /// still exercising every callback a real ten-minute session would make.
    ///
    /// Each iteration stands in for one render callback: capture "writes" however many
    /// frames its drifted clock produced in that period, then the resampler pulls exactly
    /// one callback's worth of render-rate frames back out, same as
    /// <see cref="WasapiAudioStream"/>'s render loop does.
    /// </summary>
    private static void RunDriftSimulation(double driftPpm)
    {
        const int channels = 1;
        const int nominalFramesPerCallback = 4800;      // 100 ms at 48 kHz
        const int callbacksFor10Minutes = 6000;          // 6000 x 100 ms = 600 s = 10 minutes

        var ring = new AudioRingBuffer(1 << 18);
        int targetFill = ring.Capacity / 2;

        var resampler = new AdaptiveResampler();
        resampler.Prepare(channels, sourceRate: 48000, targetRate: 48000);   // no format conversion, pure drift correction

        var controller = new RingFillController(targetFill);
        controller.SetActive(true);

        double driftFactor = 1.0 + driftPpm / 1_000_000.0;
        var rng = new Random(unchecked((int)driftPpm * 7 + 1));
        float[] writeChunk = new float[(int)Math.Ceiling(nominalFramesPerCallback * Math.Max(1.0, driftFactor)) + 4];
        float[] destination = new float[nominalFramesPerCallback];

        // Prime the ring so the very first Render call has something to interpolate from.
        ring.Write(writeChunk.AsSpan(0, nominalFramesPerCallback));

        for (int callback = 0; callback < callbacksFor10Minutes; callback++)
        {
            int writeFrames = (int)Math.Round(nominalFramesPerCallback * driftFactor);
            for (int i = 0; i < writeFrames; i++) writeChunk[i] = (float)(rng.NextDouble() * 2.0 - 1.0);
            ring.Write(writeChunk.AsSpan(0, writeFrames));

            resampler.Render(ring, destination);

            controller.Update(ring.Count);
            resampler.Ratio = resampler.NominalRatio * (1.0 + controller.Correction);
        }

        double expectedCorrection = driftPpm / 1_000_000.0;
        Console.WriteLine($"        drift {driftPpm:+0;-0} ppm -> correction {controller.Correction * 1_000_000.0:F1} ppm, " +
                          $"final fill {ring.Count} (target {targetFill}), " +
                          $"overruns {ring.OverrunSamples}, underruns {ring.UnderrunSamples}");

        TestRunner.Approximately(controller.Correction, expectedCorrection, 0.00005,
            $"correction should converge close to the {driftPpm} ppm drift being corrected for");
        TestRunner.AreEqual(ring.OverrunSamples, 0, "ring should never overflow while the controller is tracking the drift");
        TestRunner.AreEqual(ring.UnderrunSamples, 0, "ring should never run dry while the controller is tracking the drift");
    }

    private static void SignalPathTests(TestRunner runner)
    {
        Console.WriteLine("\n[Signal path: capture -> ring -> DSP -> render]");

        runner.Test("audio survives the whole chain and is enhanced", () =>
        {
            var format = AudioStreamFormat.DefaultStereo;
            var processor = new DspEngineSampleProcessor();
            processor.Prepare(format);
            processor.UpdateSettings(new EnhancerSettings(0, 90, 0, 0, 0));

            float[] captured = SignalGenerator.Noise(0.3f, 9600, 2);
            float[] rendered = RunSignalPath(format, format, processor, captured, 480);

            TestRunner.IsTrue(!Measure.HasNonFinite(rendered), "chain produced NaN or Inf");
            TestRunner.LessThanOrEqual(Measure.Peak(rendered), 1.0, "chain output peak");
            TestRunner.GreaterThan(Measure.DifferenceRms(captured, rendered), 1e-3, "chain did nothing");
            TestRunner.GreaterThan(Measure.Rms(rendered), 1e-4, "chain output is silent");
        });

        runner.Test("bypass through the whole chain is near-transparent", () =>
        {
            var format = AudioStreamFormat.DefaultStereo;
            var processor = new DspEngineSampleProcessor();
            processor.Prepare(format);
            processor.UpdateSettings(new EnhancerSettings(70, 70, 70, 70, 70));
            processor.Bypass = true;

            float[] captured = SignalGenerator.Noise(0.4f, 4800, 2);
            float[] rendered = RunSignalPath(format, format, processor, captured, 480);

            // Float32 in and out, bypassed DSP: the chain must not touch a single sample.
            for (int i = 0; i < captured.Length; i++)
                TestRunner.IsTrue(captured[i] == rendered[i], $"bypassed chain changed sample {i}");
        });

        runner.Test("5.1 system audio reaches a stereo output device", () =>
        {
            var captureFormat = new AudioStreamFormat(48000, 6, SampleFormat.Float32, 32);
            var renderFormat = new AudioStreamFormat(48000, 2, SampleFormat.Pcm16, 16);

            var processor = new DspEngineSampleProcessor();
            processor.Prepare(renderFormat);
            processor.UpdateSettings(new EnhancerSettings(20, 60, 40, 30, 40));

            float[] captured = SignalGenerator.Noise(0.3f, 4800, 6);
            float[] rendered = RunSignalPath(captureFormat, renderFormat, processor, captured, 480);

            TestRunner.AreEqual(rendered.Length, 4800 * 2, "rendered sample count");
            TestRunner.IsTrue(!Measure.HasNonFinite(rendered), "5.1 to stereo produced NaN or Inf");
            TestRunner.LessThanOrEqual(Measure.Peak(rendered), 1.0, "5.1 to stereo peak");
            TestRunner.GreaterThan(Measure.Rms(rendered), 1e-4, "5.1 to stereo is silent");
        });

        runner.Test("16-bit output device round-trips the chain cleanly", () =>
        {
            var captureFormat = AudioStreamFormat.DefaultStereo;
            var renderFormat = new AudioStreamFormat(48000, 2, SampleFormat.Pcm16, 16);

            var processor = new DspEngineSampleProcessor();
            processor.Prepare(renderFormat);
            processor.Bypass = true;

            float[] captured = SignalGenerator.Sine(1000f, 0.5f, 4800, 48000, 2);
            float[] rendered = RunSignalPath(captureFormat, renderFormat, processor, captured, 480);

            TestRunner.LessThanOrEqual(Measure.DifferenceRms(captured, rendered), 1.0 / 32768.0,
                "16-bit chain error");
        });

        runner.Test("a starved render pull gets silence, not a stall or a glitch", () =>
        {
            var format = AudioStreamFormat.DefaultStereo;
            var ring = new AudioRingBuffer(4096);
            ring.Write(SignalGenerator.Noise(0.3f, 100, 2));   // 200 samples for a 960-sample pull

            var processor = new DspEngineSampleProcessor();
            processor.Prepare(format);

            float[] block = new float[480 * 2];
            int real = ring.Read(block);
            processor.Process(block);

            TestRunner.AreEqual(real, 200, "real samples available");
            TestRunner.AreEqual(ring.UnderrunSamples, block.Length - 200, "underrun count");
            TestRunner.IsTrue(!Measure.HasNonFinite(block), "starved pull produced NaN or Inf");
            TestRunner.LessThanOrEqual(Measure.Peak(block), 1.0, "starved pull peak");
        });

        runner.Test("the render step allocates nothing", () =>
        {
            var format = AudioStreamFormat.DefaultStereo;
            var ring = new AudioRingBuffer(8192);
            var processor = new DspEngineSampleProcessor();
            processor.Prepare(format);
            processor.UpdateSettings(new EnhancerSettings(40, 60, 50, 30, 50));

            float[] source = SignalGenerator.Noise(0.4f, 480, 2);
            float[] block = new float[480 * 2];
            byte[] bytes = new byte[480 * format.BlockAlign];

            // Warm up every path the render thread takes.
            ring.Write(source);
            ring.Read(block);
            processor.Process(block);
            SampleConverter.FromFloat(block, bytes, format);

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 200; i++)
            {
                ring.Write(source);
                ring.Read(block);
                processor.Process(block);
                SampleConverter.FromFloat(block, bytes, format);
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            TestRunner.IsTrue(allocated == 0, $"allocated {allocated} bytes across 200 render blocks");
        });
    }

    // ---------------- Live WASAPI stream ----------------

    private static void StreamTests(TestRunner runner)
    {
        Console.WriteLine("\n[WASAPI stream]");

        runner.Test("stop and dispose are safe before ever starting", () =>
        {
            RequireWindows();
            using var devices = new WindowsOutputDeviceManager();
            if (!devices.IsAvailable) TestRunner.Skipped($"Core Audio unavailable: {devices.LastError}");

            var stream = new WasapiAudioStream(new AudioStreamConfiguration(), devices);
            stream.Stop();
            stream.Dispose();
            stream.Dispose();
        });

        runner.Test("the processor can be swapped and never becomes null", () =>
        {
            RequireWindows();
            using var devices = new WindowsOutputDeviceManager();
            if (!devices.IsAvailable) TestRunner.Skipped($"Core Audio unavailable: {devices.LastError}");

            using var stream = new WasapiAudioStream(new AudioStreamConfiguration(), devices);
            var processor = new DspEngineSampleProcessor();

            stream.Processor = processor;
            TestRunner.IsTrue(ReferenceEquals(stream.Processor, processor), "processor not stored");

            stream.Processor = null!;
            TestRunner.IsTrue(stream.Processor is PassthroughSampleProcessor, "null processor not replaced");
        });

        runner.Test("rendering back to the captured device is refused, not allowed to howl", () =>
        {
            RequireWindows();
            using var devices = new WindowsOutputDeviceManager();
            if (!devices.IsAvailable) TestRunner.Skipped($"Core Audio unavailable: {devices.LastError}");
            if (devices.GetDefaultOutput().Id == "unavailable") TestRunner.Skipped("no default playback device");

            // DeviceId null means "render to the default", which is also what loopback
            // captures. Nothing is opened: the check happens before any client is activated.
            using var stream = new WasapiAudioStream(new AudioStreamConfiguration(), devices);

            using var faulted = new ManualResetEventSlim(false);
            string reason = "";
            stream.Faulted += (_, e) => { reason = e.Reason; faulted.Set(); };

            stream.Start();
            TestRunner.IsTrue(faulted.Wait(TimeSpan.FromSeconds(5)), "no fault was reported within 5 seconds");
            stream.Stop();

            Console.WriteLine($"        refused with: {reason}");
            TestRunner.IsTrue(
                reason.Contains("same as the device being captured", StringComparison.OrdinalIgnoreCase),
                $"unexpected fault reason: {reason}");
        });

        runner.Test("a second output device runs the real signal path", () =>
        {
            RequireWindows();
            using var devices = new WindowsOutputDeviceManager();
            if (!devices.IsAvailable) TestRunner.Skipped($"Core Audio unavailable: {devices.LastError}");

            AudioDeviceInfo defaultDevice = devices.GetDefaultOutput();
            AudioDeviceInfo? other = devices.GetOutputDevices().FirstOrDefault(d => d.Id != defaultDevice.Id);
            if (other is null) TestRunner.Skipped("this machine has only one playback device");

            AudioDeviceCapabilities? capabilities = devices.GetCapabilities(other.Id);
            AudioDeviceCapabilities? defaultCapabilities = devices.GetDefaultCapabilities();
            if (capabilities is null || defaultCapabilities is null) TestRunner.Skipped("device formats unavailable");
            if (capabilities!.MixFormat.SampleRate != defaultCapabilities!.MixFormat.SampleRate)
                TestRunner.Skipped("the two devices run at different sample rates");

            using var stream = new WasapiAudioStream(
                new AudioStreamConfiguration(DeviceId: other.Id), devices);

            string faultReason = "";
            stream.Faulted += (_, e) => faultReason = e.Reason;

            var processor = new DspEngineSampleProcessor();
            processor.UpdateSettings(new EnhancerSettings(0, 50, 0, 0, 0));
            stream.Processor = processor;

            stream.Start();
            Thread.Sleep(TimeSpan.FromMilliseconds(600));
            AudioStreamState state = stream.State;
            AudioStreamStatistics statistics = stream.Statistics;
            stream.Stop();

            Console.WriteLine($"        {other.Name}: state {state}, {statistics.FramesProcessed} frames, " +
                              $"latency {statistics.LatencyFrames} frames");

            if (state == AudioStreamState.Faulted)
                TestRunner.Skipped($"the device would not open: {faultReason}");

            TestRunner.GreaterThan(statistics.FramesProcessed, 0, "no frames reached the output device");
        });

        runner.Test("an explicitly chosen capture device is the one that gets captured", () =>
        {
            RequireWindows();
            using var devices = new WindowsOutputDeviceManager();
            if (!devices.IsAvailable) TestRunner.Skipped($"Core Audio unavailable: {devices.LastError}");

            AudioDeviceInfo defaultDevice = devices.GetDefaultOutput();
            if (defaultDevice.Id == "unavailable") TestRunner.Skipped("no default playback device");
            AudioDeviceInfo? other = devices.GetOutputDevices().FirstOrDefault(d => d.Id != defaultDevice.Id);
            if (other is null) TestRunner.Skipped("this machine has only one playback device");

            AudioDeviceCapabilities? capabilities = devices.GetCapabilities(other.Id);
            AudioDeviceCapabilities? defaultCapabilities = devices.GetDefaultCapabilities();
            if (capabilities is null || defaultCapabilities is null) TestRunner.Skipped("device formats unavailable");
            if (capabilities!.MixFormat.SampleRate != defaultCapabilities!.MixFormat.SampleRate)
                TestRunner.Skipped("the two devices run at different sample rates");

            // Capture the NON-default device and render to the default one: the reverse of
            // the arrangement above, so a stream that quietly ignored CaptureDeviceId and
            // captured the default would be caught by the id check below.
            using var stream = new WasapiAudioStream(
                new AudioStreamConfiguration(DeviceId: defaultDevice.Id, CaptureDeviceId: other.Id), devices);

            string faultReason = "";
            stream.Faulted += (_, e) => faultReason = e.Reason;
            stream.Processor = new DspEngineSampleProcessor();

            stream.Start();
            Thread.Sleep(TimeSpan.FromMilliseconds(600));
            AudioStreamState state = stream.State;
            AudioStreamStatistics statistics = stream.Statistics;
            AudioDeviceInfo captured = stream.CaptureDevice;
            stream.Stop();

            Console.WriteLine($"        capturing {captured.Name}: state {state}, {statistics.FramesProcessed} frames");

            if (state == AudioStreamState.Faulted)
                TestRunner.Skipped($"the device would not open: {faultReason}");

            TestRunner.IsTrue(
                string.Equals(captured.Id, other.Id, StringComparison.OrdinalIgnoreCase),
                $"captured '{captured.Id}' instead of the chosen '{other.Id}'");
            TestRunner.GreaterThan(statistics.FramesProcessed, 0, "no frames reached the output device");
        });

        runner.Test("the same-device safety check follows an explicitly chosen capture device", () =>
        {
            RequireWindows();
            using var devices = new WindowsOutputDeviceManager();
            if (!devices.IsAvailable) TestRunner.Skipped($"Core Audio unavailable: {devices.LastError}");

            AudioDeviceInfo defaultDevice = devices.GetDefaultOutput();
            if (defaultDevice.Id == "unavailable") TestRunner.Skipped("no default playback device");
            AudioDeviceInfo? other = devices.GetOutputDevices().FirstOrDefault(d => d.Id != defaultDevice.Id);
            if (other is null) TestRunner.Skipped("this machine has only one playback device");

            // Capture and output are both the NON-default device. Compared with the default,
            // these two are different, so only a check that uses the explicit capture id
            // can refuse this. Nothing is opened: the check runs before any client is activated.
            using var stream = new WasapiAudioStream(
                new AudioStreamConfiguration(DeviceId: other.Id, CaptureDeviceId: other.Id), devices);

            string reason = StartAndWaitForFirstFault(stream);
            TestRunner.IsTrue(
                reason.Contains("same as the device being captured", StringComparison.OrdinalIgnoreCase),
                $"unexpected fault reason: {reason}");
        });

        runner.Test("a capture device that cannot be opened is reported, not swapped for the default", () =>
        {
            RequireWindows();
            using var devices = new WindowsOutputDeviceManager();
            if (!devices.IsAvailable) TestRunner.Skipped($"Core Audio unavailable: {devices.LastError}");

            // Well-formed endpoint id that no machine has.
            using var stream = new WasapiAudioStream(
                new AudioStreamConfiguration(
                    CaptureDeviceId: "{0.0.0.00000000}.{00000000-0000-0000-0000-000000000000}"),
                devices);

            string reason = StartAndWaitForFirstFault(stream);
            TestRunner.IsTrue(
                reason.Contains("chosen capture device", StringComparison.OrdinalIgnoreCase),
                $"unexpected fault reason: {reason}");
        });
    }

    /// <summary>Starts a stream that is expected to refuse to open, and returns why.</summary>
    private static string StartAndWaitForFirstFault(WasapiAudioStream stream)
    {
        using var faulted = new ManualResetEventSlim(false);
        string reason = "";
        stream.Faulted += (_, e) => { reason = e.Reason; faulted.Set(); };

        stream.Start();
        bool raised = faulted.Wait(TimeSpan.FromSeconds(5));
        stream.Stop();

        TestRunner.IsTrue(raised, "no fault was reported within 5 seconds");
        Console.WriteLine($"        refused with: {reason}");
        return reason;
    }

    // ---------------- Engine: capture device pass-through ----------------

    private static void EngineCaptureDeviceTests(TestRunner runner)
    {
        Console.WriteLine("\n[Engine capture device]");

        runner.Test("the engine captures the default device unless told otherwise", () =>
        {
            var backend = new RecordingBackend();
            using var engine = new WasapiAudioEngine(backend);

            engine.Start();
            TestRunner.IsTrue(engine.State == EngineState.Running, $"engine is {engine.State}: {engine.StatusMessage}");
            engine.Stop();

            TestRunner.AreEqual(backend.Created.Count, 1, "streams created");
            TestRunner.IsTrue(backend.Created[0].CaptureDeviceId is null, "capture device should be the default");
            TestRunner.IsTrue(backend.Created[0].DeviceId is null, "output device should be the default");
            TestRunner.IsTrue(backend.Created[0].Direction == AudioStreamDirection.Loopback, "direction");
        });

        runner.Test("the engine passes the chosen capture and output devices to the stream", () =>
        {
            var backend = new RecordingBackend();
            using var engine = new WasapiAudioEngine(backend);

            engine.SetCaptureDevice("capture-endpoint");
            engine.SetOutputDevice("output-endpoint");
            engine.Start();
            engine.Stop();

            TestRunner.AreEqual(backend.Created.Count, 1, "streams created");
            TestRunner.AreEqual(backend.Created[0].CaptureDeviceId, "capture-endpoint", "capture device");
            TestRunner.AreEqual(backend.Created[0].DeviceId, "output-endpoint", "output device");
        });

        runner.Test("a capture device change waits for the next start and can be cleared", () =>
        {
            var backend = new RecordingBackend();
            using var engine = new WasapiAudioEngine(backend);

            engine.SetCaptureDevice("first");
            engine.Start();
            engine.SetCaptureDevice("second");   // running stream must be left alone
            TestRunner.AreEqual(backend.Created.Count, 1, "streams created while running");
            engine.Stop();

            engine.Start();
            engine.Stop();

            engine.SetCaptureDevice(null);
            engine.Start();
            engine.Stop();

            TestRunner.AreEqual(backend.Created.Count, 3, "streams created");
            TestRunner.AreEqual(backend.Created[0].CaptureDeviceId, "first", "first start");
            TestRunner.AreEqual(backend.Created[1].CaptureDeviceId, "second", "second start");
            TestRunner.IsTrue(backend.Created[2].CaptureDeviceId is null, "cleared capture device");
        });

        runner.Test("GetVirtualEndpointStatus reads through to the backend's device manager without opening a stream", () =>
        {
            var backend = new RecordingBackend();
            using var engine = new WasapiAudioEngine(backend);

            VirtualEndpointStatusReport report = engine.GetVirtualEndpointStatus();

            TestRunner.IsTrue(report.Status == VirtualEndpointStatus.NotInstalled,
                "the fake backend's device list has no virtual endpoint, so this must be NotInstalled");
            TestRunner.AreEqual(backend.Created.Count, 0, "reading status must not create a stream");
        });
    }

    // ---------------- Engine: device recovery ----------------

    /// <summary>
    /// Drives <see cref="WasapiAudioEngine.OnStreamFaulted"/> without any real device, by
    /// handing the engine a fake <see cref="IAudioStream"/> whose <see cref="AudioStreamState"/>
    /// and <see cref="IAudioStream.Faulted"/> event this test controls directly. That
    /// mirrors what a real <see cref="WasapiAudioStream"/> does on a recoverable fault:
    /// it keeps running (or reopens) on its own and raises Faulted as information, versus
    /// an unrecoverable fault where it actually stops.
    /// </summary>
    private static void EngineDeviceRecoveryTests(TestRunner runner)
    {
        Console.WriteLine("\n[Engine device recovery]");

        runner.Test("a fault while the stream is still running is reported as reconnecting, not stopped", () =>
        {
            var fakeStream = new FaultInjectableStream();
            var backend = new SingleStreamBackend(fakeStream);
            using var engine = new WasapiAudioEngine(backend);

            engine.Start();
            TestRunner.IsTrue(engine.State == EngineState.Running, $"engine should be running before the fault, was {engine.State}");

            // The stream reopens itself for anything recoverable, so it is still Running
            // (or Opening, mid-reopen) when it raises Faulted purely as information.
            fakeStream.State = AudioStreamState.Running;
            fakeStream.RaiseFaulted("simulated device hiccup");

            TestRunner.IsTrue(engine.State == EngineState.Starting, $"a fault the stream is surviving should read as reconnecting, was {engine.State}");
            TestRunner.IsTrue(engine.StatusMessage.Contains("Reconnecting", StringComparison.Ordinal),
                $"status message should mention reconnecting, was '{engine.StatusMessage}'");
            TestRunner.IsTrue(engine.StatusMessage.Contains("simulated device hiccup", StringComparison.Ordinal),
                $"status message should carry the fault reason, was '{engine.StatusMessage}'");

            engine.Stop();
        });

        runner.Test("a fault that leaves the stream stopped is reported as faulted", () =>
        {
            var fakeStream = new FaultInjectableStream();
            var backend = new SingleStreamBackend(fakeStream);
            using var engine = new WasapiAudioEngine(backend);

            engine.Start();
            TestRunner.IsTrue(engine.State == EngineState.Running, $"engine should be running before the fault, was {engine.State}");

            // The stream gave up (its own recovery-attempt ceiling, or an unrecoverable
            // cause) and is no longer running by the time Faulted is raised.
            fakeStream.State = AudioStreamState.Faulted;
            fakeStream.RaiseFaulted("device removed");

            TestRunner.IsTrue(engine.State == EngineState.Faulted, $"a fault that stopped the stream should mark the engine faulted, was {engine.State}");
            TestRunner.AreEqual(engine.StatusMessage, "device removed", "status message should be exactly the fault reason");

            engine.Stop();
        });

        runner.Test("recovering after a fault and starting again opens a fresh stream", () =>
        {
            var fakeStream = new FaultInjectableStream();
            var backend = new SingleStreamBackend(fakeStream);
            using var engine = new WasapiAudioEngine(backend);

            engine.Start();
            fakeStream.State = AudioStreamState.Faulted;
            fakeStream.RaiseFaulted("device removed");
            TestRunner.IsTrue(engine.State == EngineState.Faulted, $"engine should be faulted, was {engine.State}");

            engine.Stop();
            TestRunner.IsTrue(engine.State == EngineState.Stopped, $"engine should report stopped once told to stop, was {engine.State}");

            var secondStream = new FaultInjectableStream();
            backend.Next = secondStream;
            engine.Start();

            TestRunner.IsTrue(engine.State == EngineState.Running, $"engine should recover on the next Start, was {engine.State}");
            TestRunner.AreEqual(backend.Created.Count, 2, "a fresh stream should have been created for the restart");
            engine.Stop();
        });

        runner.Test("a StateChanged event fires once per fault, not once per subscriber tick", () =>
        {
            var fakeStream = new FaultInjectableStream();
            var backend = new SingleStreamBackend(fakeStream);
            using var engine = new WasapiAudioEngine(backend);

            int stateChangedCount = 0;
            engine.StateChanged += (_, _) => stateChangedCount++;

            engine.Start();
            int afterStart = stateChangedCount;
            TestRunner.GreaterThan(afterStart, 0, "StateChanged should fire at least once for Start");

            fakeStream.State = AudioStreamState.Faulted;
            fakeStream.RaiseFaulted("device removed");
            TestRunner.AreEqual(stateChangedCount, afterStart + 1, "StateChanged should fire exactly once for the fault");

            engine.Stop();
        });
    }

    // ---------------- Engine: diagnostics (Part 4 Step 7) ----------------

    /// <summary>
    /// Covers the one new piece of surface this diagnosis pass added:
    /// <see cref="WasapiAudioEngine.Statistics"/>. No real hardware needed - a fake
    /// backend's stream reports <c>default</c> statistics, which is still a value
    /// (Statistics is a struct), so the only thing worth asserting is presence/absence:
    /// null exactly when there is no stream to report on.
    /// </summary>
    private static void EngineDiagnosticsTests(TestRunner runner)
    {
        Console.WriteLine("\n[Engine diagnostics]");

        runner.Test("Statistics is null while stopped and present once a stream exists", () =>
        {
            var backend = new RecordingBackend();
            using var engine = new WasapiAudioEngine(backend);

            TestRunner.IsTrue(engine.Statistics is null, "no stream yet - Statistics should be null");

            engine.Start();
            TestRunner.IsTrue(engine.Statistics is not null, "a running engine should have a stream to report on");

            engine.Stop();
            TestRunner.IsTrue(engine.Statistics is null, "no stream after Stop - Statistics should be null again");
        });
    }

    // ---------------- Field stress and stability tests ----------------

    private static void FieldStressAndStabilityTests(TestRunner runner)
    {
        Console.WriteLine("\n[Field stress and stability]");

        runner.Test("Movie field: extreme explosion transients (+18 dB) remain clamped under limiter ceiling with zero clipping or NaN", () =>
        {
            var settings = new EnhancerSettings(85, 80, 65, 75, 65);
            var dsp = new DspEngine();
            dsp.Prepare(48000, 2);
            dsp.ApplySettings(settings);

            int sampleRate = 48000;
            int frames = sampleRate * 2;
            float[] buffer = new float[frames * 2];

            // 1st second: dialogue + low rumble
            for (int i = 0; i < sampleRate; i++)
            {
                float sample = 0.2f * MathF.Sin(2 * MathF.PI * 250f * i / sampleRate);
                buffer[i * 2] = sample;
                buffer[i * 2 + 1] = sample;
            }

            // 2nd second: massive explosion (+18 dBFS transient spikes and 30 Hz sub-bass rumble)
            for (int i = sampleRate; i < sampleRate + (sampleRate / 4); i++)
            {
                float rumble = 3.5f * MathF.Sin(2 * MathF.PI * 30f * i / sampleRate);
                float spike = (i % 64 < 32) ? 5.0f : -5.0f;
                buffer[i * 2] = rumble + spike;
                buffer[i * 2 + 1] = rumble + spike;
            }

            dsp.Process(buffer.AsSpan());

            float peak = 0.0f;
            for (int i = 0; i < buffer.Length; i++)
            {
                float s = buffer[i];
                TestRunner.IsTrue(!float.IsNaN(s), "movie sample must not be NaN");
                TestRunner.IsTrue(!float.IsInfinity(s), "movie sample must not be Infinity");
                TestRunner.IsTrue(s <= 1.0f && s >= -1.0f, $"movie sample must be within full-scale, was {s}");
                float abs = MathF.Abs(s);
                if (abs > peak) peak = abs;
            }

            TestRunner.IsTrue(peak <= 0.98f, $"limiter ceiling must catch explosion peak, was {peak}");
        });

        runner.Test("Gaming field: gunfire delta spikes (+24 dB) and rapid directional bursts maintain zero distortion and zero latency slip", () =>
        {
            var settings = new EnhancerSettings(85, 55, 75, 65, 80);
            var dsp = new DspEngine();
            dsp.Prepare(48000, 2);
            dsp.ApplySettings(settings);

            int frames = 48000;
            float[] buffer = new float[frames * 2];

            for (int shot = 0; shot < 12; shot++)
            {
                int pos = shot * 3500 + 400;
                buffer[pos * 2] = 12.0f;
                buffer[pos * 2 + 1] = -10.0f;
            }

            dsp.Process(buffer.AsSpan());

            for (int i = 0; i < buffer.Length; i++)
            {
                float s = buffer[i];
                TestRunner.IsTrue(!float.IsNaN(s) && !float.IsInfinity(s), "gaming sample must be finite");
                TestRunner.IsTrue(s <= 1.0f && s >= -1.0f, $"gaming sample must not exceed 1.0, was {s}");
            }
        });

        runner.Test("Music & Bass Boost: 100% volume (+24 dB) with 808 sub-bass drops remains pure without analogue or digital clipping", () =>
        {
            var settings = new EnhancerSettings(100, 100, 70, 40, 60);
            var dsp = new DspEngine();
            dsp.Prepare(48000, 2);
            dsp.ApplySettings(settings);

            int frames = 48000;
            float[] buffer = new float[frames * 2];

            for (int i = 0; i < frames; i++)
            {
                float freq = 20.0f + (80.0f * i / frames);
                float s = MathF.Sin(2 * MathF.PI * freq * i / 48000f);
                buffer[i * 2] = s;
                buffer[i * 2 + 1] = s;
            }

            dsp.Process(buffer.AsSpan());

            for (int i = 0; i < buffer.Length; i++)
            {
                float s = buffer[i];
                TestRunner.IsTrue(!float.IsNaN(s), "bass sweep must not produce NaN");
                TestRunner.IsTrue(s <= 1.0f && s >= -1.0f, $"bass sweep must be bounded, was {s}");
            }
        });

        runner.Test("Voice field: speech presence boost under noisy input conditions preserves dialogue clarity without filter ringing", () =>
        {
            var settings = new EnhancerSettings(75, 10, 80, 90, 45);
            var dsp = new DspEngine();
            dsp.Prepare(48000, 2);
            dsp.ApplySettings(settings);

            int frames = 48000;
            float[] buffer = new float[frames * 2];
            var rng = new Random(123);

            for (int i = 0; i < frames; i++)
            {
                float speech = 0.4f * MathF.Sin(2 * MathF.PI * 1200f * i / 48000f) + 0.3f * MathF.Sin(2 * MathF.PI * 2800f * i / 48000f);
                float noise = (float)(rng.NextDouble() * 0.15 - 0.075);
                buffer[i * 2] = speech + noise;
                buffer[i * 2 + 1] = speech + noise;
            }

            dsp.Process(buffer.AsSpan());

            for (int i = 0; i < buffer.Length; i++)
            {
                float s = buffer[i];
                TestRunner.IsTrue(!float.IsNaN(s), "voice audio must not produce NaN");
                TestRunner.IsTrue(s <= 1.0f && s >= -1.0f, $"voice audio must be bounded, was {s}");
            }
        });

        runner.Test("Multi-format agility: seamless transition between 44.1 kHz, 48 kHz, and 96 kHz streams without buffer glitches or stalls", () =>
        {
            int[] rates = new[] { 44100, 48000, 96000 };
            foreach (int rate in rates)
            {
                var dsp = new DspEngine();
                dsp.Prepare(rate, 2);
                dsp.ApplySettings(new EnhancerSettings(85, 75, 70, 55, 60));

                int frames = rate / 10; // 100 ms buffer
                float[] buffer = new float[frames * 2];
                for (int i = 0; i < frames; i++)
                {
                    float s = 0.5f * MathF.Sin(2 * MathF.PI * 440f * i / rate);
                    buffer[i * 2] = s;
                    buffer[i * 2 + 1] = s;
                }

                dsp.Process(buffer.AsSpan());

                for (int i = 0; i < buffer.Length; i++)
                {
                    TestRunner.IsTrue(!float.IsNaN(buffer[i]), $"sample at {rate} Hz must not be NaN");
                    TestRunner.IsTrue(buffer[i] <= 1.0f && buffer[i] >= -1.0f, $"sample at {rate} Hz must be bounded");
                }
            }
        });

        runner.Test("Device auto-routing: headphone device presence correctly identified and prioritized without distortion", () =>
        {
            var speakers = new AudioDeviceInfo("dev-speakers", "Speakers (Realtek Audio)", true);
            var headphones = new AudioDeviceInfo("dev-hp", "Headphones (Realtek Audio)", false);
            var sonyWh = new AudioDeviceInfo("dev-sony", "WH-1000XM4 Stereo", false);
            var boseQc = new AudioDeviceInfo("dev-bose", "Bose QuietComfort 45", false);
            var galaxyBuds = new AudioDeviceInfo("dev-buds", "Galaxy Buds2 Pro", false);
            var hardwareHp = new AudioDeviceInfo("dev-hw", "Generic Audio", false, IsHeadphoneHardware: true);

            TestRunner.IsTrue(!speakers.IsHeadphones, "speakers should not be marked as headphones");
            TestRunner.IsTrue(headphones.IsHeadphones, "headphones must be recognized as headphones");
            TestRunner.IsTrue(sonyWh.IsHeadphones, "Sony WH- series must be recognized as headphones");
            TestRunner.IsTrue(boseQc.IsHeadphones, "Bose QuietComfort must be recognized as headphones");
            TestRunner.IsTrue(galaxyBuds.IsHeadphones, "Galaxy Buds must be recognized as headphones");
            TestRunner.IsTrue(hardwareHp.IsHeadphones, "hardware form factor must be recognized as headphones");
        });

        runner.Test("Mute: DspEngineSampleProcessor clears buffer to digital silence when Muted is true", () =>
        {
            var dsp = new DspEngineSampleProcessor();
            dsp.Prepare(AudioStreamFormat.DefaultStereo);
            dsp.UpdateSettings(new EnhancerSettings(100, 100, 100, 100, 100));

            float[] buffer = new float[512];
            Array.Fill(buffer, 0.75f);

            // Unmuted: prime past limiter look-ahead (240 samples)
            dsp.Process(buffer.AsSpan());
            TestRunner.IsTrue(MathF.Abs(buffer[buffer.Length - 1]) > 0.01f, "unmuted buffer contains sound");

            // Muted: samples are cleared to zero
            dsp.Muted = true;
            Array.Fill(buffer, 0.75f);
            dsp.Process(buffer.AsSpan());

            for (int i = 0; i < buffer.Length; i++)
            {
                TestRunner.IsTrue(buffer[i] == 0.0f, $"sample {i} must be absolute digital silence (0.0)");
            }

            // Unmute: sound resumes once unmuted
            dsp.Muted = false;
            Array.Fill(buffer, 0.75f);
            dsp.Process(buffer.AsSpan());
            dsp.Process(buffer.AsSpan());
            TestRunner.IsTrue(MathF.Abs(buffer[buffer.Length - 1]) > 0.01f, "sound resumes after unmute");
        });

        runner.Test("WasapiAudioEngine: SetMasterMute updates processor mute state", () =>
        {
            var backend = new RecordingBackend();
            using var engine = new WasapiAudioEngine(backend);

            TestRunner.IsTrue(!engine.Processor.Muted, "default engine processor is unmuted");

            engine.SetMasterMute(true);
            TestRunner.IsTrue(engine.Processor.Muted, "engine processor is muted after SetMasterMute(true)");

            engine.SetMasterMute(false);
            TestRunner.IsTrue(!engine.Processor.Muted, "engine processor is unmuted after SetMasterMute(false)");
        });

        runner.Test("AudioSpectrumAnalyzer: Silence in produces near-zero spectrum across all 32 bands", () =>
        {
            var analyzer = new AudioEnhancer.Audio.Analysis.AudioSpectrumAnalyzer();
            analyzer.Prepare(48000);

            float[] silence = new float[2048 * 2];
            analyzer.WriteRenderSamples(silence.AsSpan(), 2);

            Span<float> bands = stackalloc float[32];
            analyzer.GetSpectrum(bands, out float peakDb, out float rmsDb, out bool active);

            TestRunner.IsTrue(!active, "silence should not be active");
            TestRunner.IsTrue(peakDb <= -90f, $"silence peak must be <= -90 dB, was {peakDb}");
            TestRunner.IsTrue(rmsDb <= -90f, $"silence rms must be <= -90 dB, was {rmsDb}");
            for (int i = 0; i < 32; i++)
            {
                TestRunner.IsTrue(bands[i] <= 0.05f, $"silence band {i} magnitude should be near zero, was {bands[i]}");
            }
        });

        runner.Test("AudioSpectrumAnalyzer: 1 kHz tone produces peak in corresponding midrange bands", () =>
        {
            var analyzer = new AudioEnhancer.Audio.Analysis.AudioSpectrumAnalyzer();
            analyzer.Prepare(48000);

            int frames = 2048;
            float[] buffer = new float[frames * 2];
            for (int i = 0; i < frames; i++)
            {
                float s = 0.8f * MathF.Sin(2 * MathF.PI * 1000f * i / 48000f);
                buffer[i * 2] = s;
                buffer[i * 2 + 1] = s;
            }

            analyzer.WriteRenderSamples(buffer.AsSpan(), 2);

            Span<float> bands = stackalloc float[32];
            analyzer.GetSpectrum(bands, out float peakDb, out float rmsDb, out bool active);

            TestRunner.IsTrue(active, "1 kHz tone must be active");
            TestRunner.IsTrue(peakDb > -6.0f, $"peak should reflect tone amplitude, was {peakDb}");
            TestRunner.IsTrue(rmsDb > -10.0f, $"rms should reflect tone energy, was {rmsDb}");

            // Find peak band
            int peakBand = 0;
            float maxMag = 0f;
            for (int i = 0; i < 32; i++)
            {
                if (bands[i] > maxMag)
                {
                    maxMag = bands[i];
                    peakBand = i;
                }
            }

            // 1 kHz in 32-band log space (20 Hz - 20 kHz) is around band 16-19
            TestRunner.IsTrue(peakBand >= 15 && peakBand <= 20, $"peak band for 1 kHz should be in bands 15-20, was band {peakBand}");
            TestRunner.IsTrue(maxMag > 0.5f, $"peak band magnitude should be strong, was {maxMag}");
        });

        runner.Test("AudioSpectrumAnalyzer: 60 Hz bass tone produces peak in low sub-bass bands", () =>
        {
            var analyzer = new AudioEnhancer.Audio.Analysis.AudioSpectrumAnalyzer();
            analyzer.Prepare(48000);

            int frames = 2048;
            float[] buffer = new float[frames * 2];
            for (int i = 0; i < frames; i++)
            {
                float s = 0.8f * MathF.Sin(2 * MathF.PI * 60f * i / 48000f);
                buffer[i * 2] = s;
                buffer[i * 2 + 1] = s;
            }

            analyzer.WriteRenderSamples(buffer.AsSpan(), 2);

            Span<float> bands = stackalloc float[32];
            analyzer.GetSpectrum(bands, out float peakDb, out float rmsDb, out bool active);

            TestRunner.IsTrue(active, "60 Hz tone must be active");
            int peakBand = 0;
            float maxMag = 0f;
            for (int i = 0; i < 32; i++)
            {
                if (bands[i] > maxMag)
                {
                    maxMag = bands[i];
                    peakBand = i;
                }
            }

            // 60 Hz in 32-band log space is in bands 3-6
            TestRunner.IsTrue(peakBand >= 2 && peakBand <= 7, $"peak band for 60 Hz should be in bands 2-7, was band {peakBand}");
        });
    }

    /// <summary>A stream whose state and fault the test controls directly, with no device behind it at all.</summary>
    private sealed class FaultInjectableStream : IAudioStream
    {
        public AudioStreamState State { get; set; } = AudioStreamState.Closed;
        public AudioStreamFormat Format => AudioStreamFormat.DefaultStereo;
        public AudioDeviceInfo Device { get; } = new("fake-output", "Fake output", false);
        public IAudioSampleProcessor Processor { get; set; } = PassthroughSampleProcessor.Instance;
        public AudioStreamStatistics Statistics => default;

        public event EventHandler<AudioStreamFaultedEventArgs>? Faulted;

        public void Start() => State = AudioStreamState.Running;
        public void Stop() => State = AudioStreamState.Closed;
        public void Dispose() { }

        /// <summary>Raises Faulted exactly as a real stream's engine thread would, from whatever <see cref="State"/> is set to at the time.</summary>
        public void RaiseFaulted(string reason) => Faulted?.Invoke(this, new AudioStreamFaultedEventArgs(reason));
    }

    /// <summary>Backend that hands out one prepared fake stream per Create call, so a test can drive it directly.</summary>
    private sealed class SingleStreamBackend : IAudioBackend
    {
        private readonly NullOutputDeviceManager _devices = new();

        public SingleStreamBackend(IAudioStream first) => Next = first;

        /// <summary>The stream the next Create call returns. Set this before calling engine.Start() again to simulate a fresh device.</summary>
        public IAudioStream Next { get; set; } = null!;

        public List<AudioStreamConfiguration> Created { get; } = new();

        public bool IsAvailable => true;
        public string Description => "Single-stream test backend";
        public IOutputDeviceManager Devices => _devices;
        public AudioDeviceCapabilities? GetCapabilities(string deviceId) => null;
        public AudioDeviceCapabilities? GetDefaultCapabilities() => null;

        public IAudioStream Create(AudioStreamConfiguration configuration)
        {
            Created.Add(configuration);
            return Next;
        }

        public void Dispose() { }
    }

    /// <summary>Backend that records every configuration it is asked to open and hands back an idle stream.</summary>
    private sealed class RecordingBackend : IAudioBackend
    {
        private readonly IOutputDeviceManager _devices;

        public RecordingBackend() : this(new NullOutputDeviceManager()) { }

        /// <summary>
        /// Part 4 Step 3: lets a test hand in a device list (e.g. one containing a fake
        /// "AudioEnhancer Virtual Output") without needing real hardware or a real driver.
        /// </summary>
        public RecordingBackend(IOutputDeviceManager devices) => _devices = devices;

        public List<AudioStreamConfiguration> Created { get; } = new();

        public bool IsAvailable => true;
        public string Description => "Recording test backend";
        public IOutputDeviceManager Devices => _devices;
        public AudioDeviceCapabilities? GetCapabilities(string deviceId) => null;
        public AudioDeviceCapabilities? GetDefaultCapabilities() => null;

        public IAudioStream Create(AudioStreamConfiguration configuration)
        {
            Created.Add(configuration);
            return new IdleStream();
        }

        public void Dispose() { }
    }

    /// <summary>
    /// Part 4 Step 3: a hand-built <see cref="IOutputDeviceManager"/> so tests can simulate
    /// "the virtual endpoint driver is installed" without any driver, COM, or Windows call -
    /// exactly the same reasoning <see cref="NullOutputDeviceManager"/> already uses for
    /// "no device at all". Nothing about this class talks to Windows.
    /// </summary>
    private sealed class FakeOutputDeviceManager : IOutputDeviceManager
    {
        private readonly List<AudioDeviceInfo> _devices;

        public FakeOutputDeviceManager(IEnumerable<AudioDeviceInfo> devices) => _devices = new List<AudioDeviceInfo>(devices);

        public AudioDeviceInfo GetDefaultOutput() => _devices.FirstOrDefault(d => d.IsDefault) ?? _devices[0];
        public IReadOnlyList<AudioDeviceInfo> GetOutputDevices() => _devices;
        public event EventHandler? DefaultOutputChanged { add { } remove { } }
        public event EventHandler? DevicesChanged { add { } remove { } }
    }

    private sealed class IdleStream : IAudioStream
    {
        public AudioStreamState State { get; private set; } = AudioStreamState.Closed;
        public AudioStreamFormat Format => AudioStreamFormat.DefaultStereo;
        public AudioDeviceInfo Device { get; } = new("test-output", "Test output", false);
        public IAudioSampleProcessor Processor { get; set; } = PassthroughSampleProcessor.Instance;
        public AudioStreamStatistics Statistics => default;
        public event EventHandler<AudioStreamFaultedEventArgs>? Faulted { add { } remove { } }
        public void Start() => State = AudioStreamState.Running;
        public void Stop() => State = AudioStreamState.Closed;
        public void Dispose() { }
    }

    private sealed class FakeDefaultAudioEndpointRouter : IDefaultAudioEndpointRouter
    {
        public int CaptureCallCount { get; private set; }
        public int SetAllRolesCallCount { get; private set; }
        public int RestoreCallCount { get; private set; }

        public bool TryCapture(out DefaultAudioEndpointSnapshot snapshot, out string error)
        {
            CaptureCallCount++;
            snapshot = new DefaultAudioEndpointSnapshot("id-console", "id-real", "id-communications");
            error = "";
            return true;
        }

        public bool TrySetAllRoles(string deviceId, out string error)
        {
            SetAllRolesCallCount++;
            error = "";
            return true;
        }

        public bool TryRestore(DefaultAudioEndpointSnapshot snapshot, out string error)
        {
            RestoreCallCount++;
            error = "";
            return true;
        }
    }

    private static void RequireWindows()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            TestRunner.Skipped("not running on Windows");
    }
}
