namespace AudioEnhancer.Core;

/// <summary>
/// The "contract" between the UI and the future audio engine.
/// Five values, each 0-100. 0 = no effect, 100 = maximum.
/// It is an immutable record: once created it never changes. In Part 2 the audio
/// thread can safely read the latest instance without locks, because the UI never
/// edits an existing one - it always creates a new one.
/// </summary>
public sealed record EnhancerSettings(int Volume, int Bass, int Clarity, int Voice, int Detail);
