namespace AudioEnhancer.Audio;

/// <summary>
/// Part 4 Step 2 scaffolding. Where the "AudioEnhancer Virtual Output" endpoint stands
/// relative to what <see cref="IOutputDeviceManager"/> can see right now.
///
/// <para><b>This enum has three values and, on every machine today, exactly one of them
/// is ever produced: <see cref="NotInstalled"/>.</b> No driver exists yet - see
/// PART4-STEP1.md and PART4-STEP2.md - so <see cref="VirtualEndpointDetector.Find"/>
/// never finds anything, and <see cref="VirtualEndpointStatusEvaluator.Evaluate"/> can
/// never honestly report anything else. The other two values exist so the evaluator,
/// its tests and the eventual UI text are all already correct for the day a Step 3+
/// driver makes them reachable, without anyone having to revisit this file then.</para>
/// </summary>
public enum VirtualEndpointStatus
{
    /// <summary>No playback device matching <see cref="VirtualEndpointDetector.ExpectedNameFragment"/>
    /// was found. The only possible status today.</summary>
    NotInstalled,

    /// <summary>The virtual endpoint was found, but Windows is not currently defaulting to it.</summary>
    InstalledNotDefault,

    /// <summary>The virtual endpoint was found and is the current Windows default playback device.</summary>
    InstalledAndDefault,
}

/// <summary>
/// The result of one <see cref="VirtualEndpointStatusEvaluator.Evaluate"/> call: a
/// status plus a message written for the person using the app, not a developer.
/// </summary>
/// <param name="Status">Which of the three states applied.</param>
/// <param name="Device">
/// The virtual endpoint's <see cref="AudioDeviceInfo"/> when <see cref="Status"/> is not
/// <see cref="VirtualEndpointStatus.NotInstalled"/>, otherwise null.
/// </param>
/// <param name="Message">
/// A complete sentence (or two) ready to show as-is. Never claims the endpoint exists
/// when it does not, and never tells the person AudioEnhancer can switch Windows'
/// default device for them - it can't; see <see cref="VirtualEndpointStatusEvaluator"/>'s
/// remarks for why.
/// </param>
public sealed record VirtualEndpointStatusReport(
    VirtualEndpointStatus Status,
    AudioDeviceInfo? Device,
    string Message)
{
    /// <summary>True only when <see cref="Device"/> is non-null - i.e. not <see cref="VirtualEndpointStatus.NotInstalled"/>.</summary>
    public bool IsInstalled => Status != VirtualEndpointStatus.NotInstalled;
}

/// <summary>
/// Turns a device list into a <see cref="VirtualEndpointStatusReport"/>. This is the
/// "wire up VirtualEndpointDetector" work PART4-STEP1.md's section 5 named as a Step 2+
/// task, split out so it can be written, tested and reviewed independently of whether a
/// driver exists yet - which is why every test for it in
/// <c>AudioEnhancer.Audio.Tests</c> builds its device lists by hand instead of asking
/// Windows for real ones.
///
/// <para><b>What this class deliberately does not do:</b> it does not open a stream or
/// change Windows settings. Runtime default-device routing belongs to
/// <see cref="WindowsDefaultAudioEndpointRouter"/> and is performed only while the
/// enhancement engine is actively running. This evaluator remains a pure status check.</para>
/// </summary>
public static class VirtualEndpointStatusEvaluator
{
    /// <summary>
    /// Evaluates status from an already-fetched default device and device list. Takes
    /// plain data rather than an <see cref="IOutputDeviceManager"/> so it can be unit
    /// tested with hand-built <see cref="AudioDeviceInfo"/> values and never has to
    /// touch Windows itself.
    /// </summary>
    public static VirtualEndpointStatusReport Evaluate(
        AudioDeviceInfo? defaultOutput,
        IReadOnlyList<AudioDeviceInfo> devices)
    {
        ArgumentNullException.ThrowIfNull(devices);

        AudioDeviceInfo? found = VirtualEndpointDetector.Find(devices);

        if (found is null)
        {
            return new VirtualEndpointStatusReport(
                VirtualEndpointStatus.NotInstalled,
                Device: null,
                Message: "The \"AudioEnhancer Virtual Output\" playback device is not installed on this " +
                         "computer. It requires a separate driver component that has not been built yet " +
                         "(see PART4-STEP2.md). Until it is, choose two physical outputs: let Windows play " +
                         "to one and have AudioEnhancer render to the other.");
        }

        bool isDefault = defaultOutput is not null &&
                          string.Equals(defaultOutput.Id, found.Id, StringComparison.OrdinalIgnoreCase);

        if (isDefault)
        {
            return new VirtualEndpointStatusReport(
                VirtualEndpointStatus.InstalledAndDefault,
                found,
                $"\"{found.Name}\" is installed and is Windows' current default playback device. " +
                "AudioEnhancer can capture it directly.");
        }

        return new VirtualEndpointStatusReport(
            VirtualEndpointStatus.InstalledNotDefault,
            found,
            $"\"{found.Name}\" is installed but Windows is not currently playing through it. " +
            "AudioEnhancer will route Windows audio to it automatically when enhancement starts.");
    }

    /// <summary>Convenience overload: reads the default device and device list from <paramref name="manager"/> itself.</summary>
    public static VirtualEndpointStatusReport Evaluate(IOutputDeviceManager manager)
    {
        ArgumentNullException.ThrowIfNull(manager);
        return Evaluate(manager.GetDefaultOutput(), manager.GetOutputDevices());
    }
}
