namespace AudioEnhancer.Audio;

/// <summary>
/// What happened when something asked the engine to capture the virtual endpoint.
/// </summary>
public enum VirtualEndpointCaptureResult
{
    /// <summary>No matching endpoint was found. The only possible result today - see PART4-STEP3.md.</summary>
    NotInstalled,

    /// <summary>The endpoint was found and the engine's capture device was set to it.</summary>
    Selected,
}

/// <param name="Result">Which of the two outcomes happened.</param>
/// <param name="Message">A complete sentence ready to show as-is.</param>
public sealed record VirtualEndpointCaptureOutcome(VirtualEndpointCaptureResult Result, string Message)
{
    public bool Succeeded => Result == VirtualEndpointCaptureResult.Selected;
}

/// <summary>
/// Part 4 Step 3. The one place that actually connects the future virtual endpoint to
/// <see cref="WasapiAudioEngine"/> - everything Step 1 and Step 2 built
/// (<see cref="VirtualEndpointDetector"/>, <see cref="VirtualEndpointStatusEvaluator"/>)
/// only *found* the endpoint or *described* its status; nothing called
/// <see cref="WasapiAudioEngine.SetCaptureDevice"/> with it until this file.
///
/// <para><b>This is opt-in, not automatic.</b> Nothing in <see cref="WasapiAudioEngine"/>'s
/// constructor, <see cref="WasapiAudioEngine.Start"/>, or anywhere else calls this on its
/// own. A caller (the eventual UI, or a test) has to invoke
/// <see cref="WasapiAudioEngine.TryUseVirtualEndpointForCapture"/> explicitly. That is
/// what "keep the virtual endpoint optional" means in code: an app that never calls it
/// behaves exactly as it did before Step 3 existed - default-device capture, unchanged -
/// and there is no code path anywhere that silently switches capture out from under a
/// running configuration.</para>
///
/// <para><b>What this does not do:</b> it does not start or stop the stream (the existing
/// <see cref="WasapiAudioEngine.Start"/>/<see cref="WasapiAudioEngine.Stop"/> calls still
/// own that), it does not touch the render/output device, and it does not create,
/// install, or detect a driver - it only acts on whatever
/// <see cref="VirtualEndpointStatusEvaluator"/> already found through the existing,
/// unmodified <see cref="IOutputDeviceManager"/> enumeration path.</para>
/// </summary>
public static class VirtualEndpointCapture
{
    /// <summary>
    /// Looks for the virtual endpoint via <paramref name="devices"/> and, if found, sets
    /// it as <paramref name="engine"/>'s capture device (see
    /// <see cref="WasapiAudioEngine.SetCaptureDevice"/> for exactly when that takes
    /// effect - same rule as calling it directly).
    /// </summary>
    public static VirtualEndpointCaptureOutcome TrySelect(WasapiAudioEngine engine, IOutputDeviceManager devices)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(devices);

        VirtualEndpointStatusReport report = VirtualEndpointStatusEvaluator.Evaluate(devices);

        if (!report.IsInstalled)
            return new VirtualEndpointCaptureOutcome(VirtualEndpointCaptureResult.NotInstalled, report.Message);

        engine.SetCaptureDevice(report.Device!.Id);

        return new VirtualEndpointCaptureOutcome(
            VirtualEndpointCaptureResult.Selected,
            $"Capture device set to \"{report.Device.Name}\". " +
            "If the engine is already running, stop and start it again for the change to take effect - " +
            "the same rule that already applies to SetCaptureDevice.");
    }
}
