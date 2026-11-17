namespace AudioEnhancer.Audio;

/// <summary>
/// Part 4 Step 3. What the future "AudioEnhancer Virtual Output" driver has to be, in
/// terms this repository can check and test against, rather than only in prose in
/// PART4-STEP3.md. Every member here is a fact about the <em>boundary</em> between the
/// driver (Category B - not implemented here, requires the WDK) and this application
/// (Category A - implemented here); none of it is driver code, and none of it becomes
/// true by adding this file. It exists so that:
///
/// <list type="bullet">
/// <item>the requirements a driver author has to hit are written down as something a
/// test can hold constant, instead of only in a document that code changes could drift
/// away from silently;</item>
/// <item>the one piece of literal coupling between this app and the eventual driver -
/// the friendly-name fragment <see cref="VirtualEndpointDetector"/> matches on - has a
/// single source of truth that this class points at instead of duplicating.</item>
/// </list>
/// </summary>
public static class VirtualEndpointDriverContract
{
    /// <summary>
    /// The Windows audio role the driver's endpoint must register as. This is what
    /// makes it appear in Settings -&gt; System -&gt; Sound -&gt; Output and be selectable as
    /// the default playback device - the same role every physical speaker and
    /// headphone endpoint registers as.
    /// </summary>
    public const string RequiredDataFlow = "EDataFlow.Render (a playback endpoint), enumerated through IMMDeviceEnumerator like any real one.";

    /// <summary>
    /// The device-sharing mode this application will open the endpoint in. AudioEnhancer
    /// never requests exclusive mode anywhere in <see cref="WasapiAudioStream"/>, so the
    /// driver does not need to support it, only shared mode - the same mode every normal
    /// consumer audio device already supports.
    /// </summary>
    public const string RequiredShareMode = "AUDCLNT_SHAREMODE_SHARED. AudioEnhancer never requests exclusive mode.";

    /// <summary>
    /// AudioEnhancer reaches the virtual endpoint's audio the same way it already reaches
    /// today's default device: shared-mode loopback capture. This is a capability of the
    /// Windows audio engine/PortCls stack sitting on top of any standard render endpoint,
    /// not something the driver has to implement itself - which is exactly why Step 1
    /// found that <see cref="WasapiAudioStream"/> needs zero code changes.
    /// </summary>
    public const string CaptureMethod = "AUDCLNT_STREAMFLAGS_LOOPBACK capture on the endpoint's render pin, exactly as today's physical default device is captured.";

    /// <summary>
    /// What format the driver's mix format needs to be in. Deliberately unrestrictive:
    /// <see cref="ChannelMapper"/> maps any positive source channel count to any positive
    /// destination channel count, and <see cref="AdaptiveResampler"/> converts any positive
    /// source sample rate to any positive target sample rate. The one real requirement is
    /// that the format is well-formed enough for <see cref="AudioStreamFormat.FromWaveFormat"/>
    /// to parse - see <see cref="AudioStreamFormat.IsValid"/> for exactly what that means.
    /// </summary>
    public const string FormatRequirement =
        "Any channel count and sample rate the driver's IAudioClient.GetMixFormat reports; " +
        "the existing pipeline already resamples and remaps generically. PCM16/24/32 or Float32 sample " +
        "types are all already understood by SampleConverter.";

    /// <summary>
    /// The one piece of information the driver and this application must literally agree
    /// on: the substring of the endpoint's friendly name (its "DeviceFriendlyName" /
    /// "InterfaceFriendlyName" property, the same one Windows shows in Sound settings)
    /// that lets this application recognize the device as its own, out of every playback
    /// device on the machine.
    ///
    /// This does not live here as its own copy - it reads
    /// <see cref="VirtualEndpointDetector.ExpectedNameFragment"/> directly, specifically so
    /// the two can never drift apart. A test in this project checks that this property and
    /// the driver-facing name below describe the same string.
    /// </summary>
    public static string RequiredFriendlyNameFragment => VirtualEndpointDetector.ExpectedNameFragment;

    /// <summary>
    /// Where the driver boundary sits, end to end, for anyone (human or future code)
    /// trying to see the whole picture in one string:
    /// AudioEnhancer (this app, user mode) talks only to WASAPI/mmdevapi - it never talks
    /// to the driver directly, calls into kernel mode, or knows the driver exists as
    /// anything other than an <see cref="AudioDeviceInfo"/> in a device list. Windows'
    /// audio engine and the PortCls stack sit between the two. The driver's only job is to
    /// register a render endpoint with no physical hardware behind it and let the audio
    /// engine's normal mixing/loopback machinery do the rest - it does not need to know
    /// AudioEnhancer exists either.
    /// </summary>
    public const string BoundaryDescription =
        "AudioEnhancer (user mode) <-> WASAPI / mmdevapi.dll <-> Windows audio engine + PortCls " +
        "<-> the virtual driver (kernel mode, Category B). Neither end of that chain talks to the " +
        "other directly; both talk to the Windows audio stack in between, the same way every " +
        "physical audio device already does.";

    // -------------------------------------------------------------------------------
    // Part 4 Step 4 additions below. Nothing above this line was changed from Step 3 -
    // these are new members answering the specific contract points Step 4 was asked to
    // cover (buffer, timing/clock, latency, enumeration, default-device, startup/
    // shutdown, error/recovery) that Step 3's contract did not yet address. See
    // PART4-STEP4.md section A for which of these are confirmed Windows/WDK behavior,
    // which are this project's own design decisions, and which still need to be
    // checked against a real driver on real Windows.
    // -------------------------------------------------------------------------------

    /// <summary>
    /// How the driver's buffer negotiation needs to behave. There is no fixed size
    /// requirement: <see cref="AudioRingBuffer"/> already absorbs whatever period size
    /// shared-mode negotiation produces, the same way it already does for every
    /// physical device today - a virtual endpoint reporting an unusual default/minimum
    /// period is not expected to require any code change.
    /// </summary>
    public const string BufferBehavior =
        "Standard shared-mode buffer negotiation via IAudioClient.Initialize/GetBufferSize, the same " +
        "as any physical render endpoint. No specific period size is required - " + nameof(AudioRingBuffer) +
        " already adapts to whatever size shared-mode negotiation returns.";

    /// <summary>
    /// The driver must expose a coherent, monotonically advancing stream position (an
    /// <c>IAudioClock</c>/WaveRT position register), because <see cref="RingFillController"/>'s
    /// drift correction is built entirely around comparing that position's advance rate
    /// against wall-clock time - the same mechanism it already uses for every physical
    /// capture device. A virtual endpoint whose clock free-runs against a system timer
    /// (as WaveRT typically implements it, rather than a hardware crystal) is expected
    /// to drift less than physical hardware does, not more, so no change to
    /// <see cref="RingFillController"/>'s existing correction ceiling is anticipated -
    /// but this is a Category 3 item (needs Windows testing), not a confirmed fact yet.
    /// </summary>
    public const string TimingClockBehavior =
        "A coherent, monotonically advancing IAudioClock/WaveRT position, so " + nameof(RingFillController) +
        " can measure drift exactly as it already does for physical devices.";

    /// <summary>
    /// AudioEnhancer has no hard latency requirement of its own to place on the driver.
    /// <see cref="RingFillController"/> and <see cref="AudioRingBuffer"/> were built to
    /// absorb jitter and clock drift, not to require a specific round-trip time.
    /// Very high end-to-end latency (well beyond what any real playback device would
    /// report) would degrade the user's experience of hearing their own audio delayed,
    /// but would not by itself break correctness anywhere in the existing pipeline.
    /// </summary>
    public const string LatencyExpectation =
        "No fixed number. Existing jitter/drift absorption in RingFillController and AudioRingBuffer " +
        "is expected to cover normal WaveRT-class latency; extreme latency would be a UX problem, not " +
        "a correctness one.";

    /// <summary>
    /// The driver must enumerate exactly like a real device through the same
    /// <c>IMMDeviceEnumerator</c> path <see cref="IOutputDeviceManager"/> already uses -
    /// no separate or additional enumeration API. It should become visible without a
    /// reboot once installed (ordinary PnP/software-device install behavior), so that
    /// <see cref="VirtualEndpointStatusEvaluator"/> can find it on its next call, not
    /// only after the machine restarts.
    /// </summary>
    public const string EnumerationBehavior =
        "Enumerable via the same IMMDeviceEnumerator path IOutputDeviceManager already uses, visible " +
        "without a reboot once the driver package is installed.";

    /// <summary>
    /// A project design decision: installing the driver itself must not automatically make it
    /// the Windows default playback device. The driver installer remains side-effect free.
    /// During an active AudioEnhancer session, however, the application may temporarily route
    /// the Windows playback roles to the virtual endpoint and restore the user's previous
    /// endpoints when enhancement stops.
    /// </summary>
    public const string DefaultDeviceBehavior =
        "Installing the driver must NOT change the Windows default playback device. While " +
        "AudioEnhancer is actively running, the application may temporarily route playback " +
        "to the virtual endpoint and must restore the previous defaults when it stops.";

    /// <summary>
    /// Ordinary KS pin state transitions (<c>KSSTATE_STOP</c> -&gt; <c>ACQUIRE</c> -&gt;
    /// <c>PAUSE</c> -&gt; <c>RUN</c> and back), the same as any WDM audio render pin - no
    /// custom startup handshake. The endpoint must not hold any exclusive lock or
    /// resource when no client has an open stream, since <see cref="WasapiAudioEngine"/>
    /// may go a long time between <see cref="WasapiAudioEngine.Start"/> calls with no
    /// stream open at all.
    /// </summary>
    public const string StartupShutdownBehavior =
        "Standard KS pin state transitions (STOP/ACQUIRE/PAUSE/RUN); no resource should be held " +
        "exclusively while no client stream is open.";

    /// <summary>
    /// No special error handling is expected from the driver beyond standard AUDCLNT_E_*
    /// return codes on stream failure. <see cref="WasapiAudioEngine"/> already treats a
    /// stream fault as "reconnecting" while running and "faulted" once stopped (see the
    /// existing device-recovery tests) for every physical device today; a virtual
    /// endpoint disappearing (e.g. the driver being uninstalled while a stream is open)
    /// is expected to surface through that same path, not a new one.
    /// </summary>
    public const string ErrorRecoveryBehavior =
        "Standard AUDCLNT_E_* failure codes on stream faults; WasapiAudioEngine's existing " +
        "reconnect/faulted handling is expected to cover a virtual endpoint disappearing, with no new " +
        "recovery path required.";
}
