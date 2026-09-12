using System.Runtime.Versioning;
using NAudio.Wave;

namespace OpenEmu.Core.Audio;

/// <summary>Windows audio output via WASAPI (shared mode) with a small resilient ring buffer.</summary>
[SupportedOSPlatform("windows")]
public sealed class WasapiAudioSink : IAudioSink
{
    private WasapiOut? _out;
    private BufferedWaveProvider? _provider;
    private VolumeSampleProviderShim? _volume;
    private int _sampleRate = 48000, _channels = 2;
    private float _vol = 1f;
    private bool _muted;

    public float Volume { get => _vol; set { _vol = Math.Clamp(value, 0, 1); Apply(); } }
    public bool Muted { get => _muted; set { _muted = value; Apply(); } }
    public int BufferedMilliseconds => _provider == null ? 0 : (int)_provider.BufferedDuration.TotalMilliseconds;

    public void Configure(int sampleRate, int channels = 2)
    {
        if (_out != null && _sampleRate == sampleRate && _channels == channels) return;
        Dispose();
        _sampleRate = sampleRate; _channels = channels;
        _provider = new BufferedWaveProvider(new WaveFormat(sampleRate, 16, channels))
        {
            BufferDuration = TimeSpan.FromMilliseconds(500),
            DiscardOnBufferOverflow = true,
            ReadFully = true,
        };
        _volume = new VolumeSampleProviderShim(_provider.ToSampleProvider());
        Apply();
        _out = new WasapiOut(NAudio.CoreAudioApi.AudioClientShareMode.Shared, 40);
        _out.Init(_volume);
        _out.Play();
    }

    private void Apply() { if (_volume != null) _volume.Gain = _muted ? 0 : _vol; }

    public void Write(ReadOnlySpan<short> interleaved)
    {
        if (_provider == null) return;
        var bytes = new byte[interleaved.Length * 2];
        Buffer.BlockCopy(interleaved.ToArray(), 0, bytes, 0, bytes.Length);
        _provider.AddSamples(bytes, 0, bytes.Length);
    }

    public void Clear() => _provider?.ClearBuffer();

    public void Dispose()
    {
        _out?.Stop(); _out?.Dispose(); _out = null; _provider = null; _volume = null;
    }

    private sealed class VolumeSampleProviderShim : ISampleProvider
    {
        private readonly ISampleProvider _src;
        public float Gain = 1f;
        public VolumeSampleProviderShim(ISampleProvider src) => _src = src;
        public WaveFormat WaveFormat => _src.WaveFormat;
        public int Read(float[] buffer, int offset, int count)
        {
            var n = _src.Read(buffer, offset, count);
            if (Gain != 1f) for (var i = 0; i < n; i++) buffer[offset + i] *= Gain;
            return n;
        }
    }
}
