namespace OpenEmu.Core.Audio;

public interface IAudioSink : IDisposable
{
    void Configure(int sampleRate, int channels = 2);
    void Write(ReadOnlySpan<short> interleaved);
    float Volume { get; set; }
    bool Muted { get; set; }
    /// <summary>Approximate buffered audio in milliseconds (used for pacing).</summary>
    int BufferedMilliseconds { get; }
    void Clear();
}

public sealed class NullAudioSink : IAudioSink
{
    public float Volume { get; set; } = 1f;
    public bool Muted { get; set; }
    public int BufferedMilliseconds => 0;
    public long SamplesWritten { get; private set; }
    public void Configure(int sampleRate, int channels = 2) { }
    public void Write(ReadOnlySpan<short> interleaved) => SamplesWritten += interleaved.Length;
    public void Clear() { }
    public void Dispose() { }
}

public static class AudioSinkFactory
{
    public static IAudioSink Create()
    {
        if (OperatingSystem.IsWindows())
        {
            try { return new WasapiAudioSink(); } catch { }
        }
        return new NullAudioSink();
    }
}
