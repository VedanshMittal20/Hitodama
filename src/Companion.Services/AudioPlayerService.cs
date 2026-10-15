using NAudio.Wave;

namespace Companion.Services;

public sealed class AudioPlayerService : IDisposable
{
    private readonly object _sync = new();
    private readonly WaveOutEvent? _output;
    private readonly BufferedWaveProvider _buffer;
    private bool _isPlaying;
    private bool _disposed;

    public bool IsOutputAvailable => _output is not null;

    public AudioPlayerService()
    {
        var format = new WaveFormat(24000, 16, 1);
        _buffer = new BufferedWaveProvider(format)
        {
            BufferDuration = TimeSpan.FromSeconds(15),
            DiscardOnBufferOverflow = true,
            ReadFully = true
        };
        try
        {
            _output = new WaveOutEvent();
            _output.Init(_buffer);
        }
        catch (Exception exception)
        {
            _output = null;
            SafeDiagnostics.WriteFailure("audio", "output-init", exception);
        }
    }

    public void EnqueuePcm(byte[] pcmBytes)
    {
        ArgumentNullException.ThrowIfNull(pcmBytes);
        if (pcmBytes.Length == 0)
        {
            return;
        }

        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_sync)
        {
            _buffer.AddSamples(pcmBytes, 0, pcmBytes.Length);
            if (!_isPlaying && _output is not null)
            {
                try
                {
                    _output.Play();
                    _isPlaying = true;
                }
                catch
                {
                    // Headless audio driver fallback
                }
            }
        }
    }

    public void FlushImmediate()
    {
        if (_disposed)
        {
            return;
        }

        lock (_sync)
        {
            _buffer.ClearBuffer();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        lock (_sync)
        {
            if (_output is not null)
            {
                try { _output.Stop(); }
                catch (Exception exception)
                {
                    SafeDiagnostics.WriteFailure("audio-player", "stop-during-dispose", exception);
                }
            }
            _buffer.ClearBuffer();
            if (_output is not null)
            {
                try { _output.Dispose(); }
                catch (Exception exception)
                {
                    SafeDiagnostics.WriteFailure("audio-player", "dispose-output", exception);
                }
            }
            _isPlaying = false;
        }
    }
}
