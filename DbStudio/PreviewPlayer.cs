using NAudio.Vorbis;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Cheeseburger.DbStudio;

internal sealed class PreviewPlayer : IDisposable
{
    private readonly System.Windows.Forms.Timer _fadeTimer;
    private IWavePlayer? _output;
    private WaveStream? _reader;
    private VolumeSampleProvider? _volume;
    private float _fadeStep;
    private bool _stopping;

    public event EventHandler? PlaybackStopped;

    public bool IsPlaying => _output?.PlaybackState == PlaybackState.Playing && !_stopping;
    public bool IsActive => _output is not null;
    public string? CurrentPath { get; private set; }

    public PreviewPlayer()
    {
        _fadeTimer = new System.Windows.Forms.Timer { Interval = 20 };
        _fadeTimer.Tick += FadeTick;
    }

    public void Play(string path)
    {
        StopImmediately();

        WaveStream reader = string.Equals(Path.GetExtension(path), ".ogg", StringComparison.OrdinalIgnoreCase)
            || string.Equals(Path.GetExtension(path), ".oga", StringComparison.OrdinalIgnoreCase)
            ? new VorbisWaveReader(path)
            : new AudioFileReader(path);

        try
        {
            var volume = new VolumeSampleProvider(reader.ToSampleProvider()) { Volume = 1f };
            var output = new WaveOutEvent
            {
                DesiredLatency = 120,
                NumberOfBuffers = 3,
            };

            output.PlaybackStopped += OutputPlaybackStopped;
            output.Init(volume);

            _reader = reader;
            _volume = volume;
            _output = output;
            _stopping = false;
            CurrentPath = path;
            output.Play();
        }
        catch
        {
            reader.Dispose();
            Cleanup();
            throw;
        }
    }

    public void FadeOutAndStop(int durationMs = 300)
    {
        if (_output is null)
            return;

        if (_output.PlaybackState != PlaybackState.Playing || _volume is null)
        {
            StopImmediately();
            return;
        }

        if (_stopping) return;
        _stopping = true;

        var ticks = Math.Max(1, durationMs / _fadeTimer.Interval);
        _fadeStep = Math.Max(0.01f, _volume.Volume / ticks);
        _fadeTimer.Start();
    }

    private void FadeTick(object? sender, EventArgs e)
    {
        if (_volume is null || _output is null)
        {
            _fadeTimer.Stop();
            return;
        }

        _volume.Volume = Math.Max(0f, _volume.Volume - _fadeStep);
        if (_volume.Volume > 0f) return;

        _fadeTimer.Stop();
        _output.Stop();
    }

    public void StopImmediately()
    {
        _fadeTimer.Stop();
        if (_output is not null)
        {
            try
            {
                _output.PlaybackStopped -= OutputPlaybackStopped;
                _output.Stop();
            }
            catch
            {
                // Cleanup below still releases whatever was created.
            }
        }
        Cleanup();
    }

    private void OutputPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        _fadeTimer.Stop();
        Cleanup();
        PlaybackStopped?.Invoke(this, EventArgs.Empty);
    }

    private void Cleanup()
    {
        var output = _output;
        _output = null;
        if (output is not null)
        {
            try { output.PlaybackStopped -= OutputPlaybackStopped; } catch { }
            output.Dispose();
        }

        _reader?.Dispose();
        _reader = null;
        _volume = null;
        CurrentPath = null;
        _stopping = false;
    }

    public void Dispose()
    {
        StopImmediately();
        _fadeTimer.Dispose();
        GC.SuppressFinalize(this);
    }
}
