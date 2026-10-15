using System.Buffers.Binary;
using System.IO;
using NAudio;
using NAudio.Wave;

namespace Companion.Services;

public sealed class MicCaptureService : IDisposable
{
    private const int VoiceHangoverChunks = 18;
    private readonly object _sync = new();
    private readonly SynchronizationContext? _synchronizationContext;
    private WaveInEvent? _waveIn;
    private MemoryStream? _pcmBuffer;
    private TaskCompletionSource? _stopped;
    private int _voiceHangoverChunks;
    private int _voiceActive;
    private long _capturedChunkCount;
    private long _forwardedChunkCount;
    private long _speechSegmentCount;
    private bool _retainAudio;
    private bool _streamContinuously;
    private bool _bypassVadGate;
    private bool _disposed;

    public event Action<double>? AudioLevelChanged;
    public event Action<byte[]>? PcmChunkAvailable;
    public event Action? AudioStreamEnded;
    public bool IsRecording
    {
        get
        {
            lock (_sync)
            {
                return _waveIn is not null;
            }
        }
    }

    public MicCaptureService()
    {
        _synchronizationContext = SynchronizationContext.Current;
    }

    public void Start(bool bypassVadGate = false) =>
        StartRecording(retainAudio: true, streamContinuously: false, bypassVadGate: bypassVadGate);

    public void StartRecording(bool retainAudio = true, bool streamContinuously = false, bool bypassVadGate = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_sync)
        {
            if (_waveIn is not null)
            {
                throw new InvalidOperationException("Microphone capture is already active.");
            }

            _pcmBuffer = new MemoryStream();
            _voiceHangoverChunks = 0;
            _voiceActive = 0;
            _capturedChunkCount = 0;
            _forwardedChunkCount = 0;
            _speechSegmentCount = 0;
            _retainAudio = retainAudio;
            _streamContinuously = streamContinuously;
            _bypassVadGate = bypassVadGate;
            _stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _waveIn = new WaveInEvent
            {
                WaveFormat = new WaveFormat(16000, 16, 1),
                BufferMilliseconds = 40,
                NumberOfBuffers = 3
            };
            _waveIn.DataAvailable += OnDataAvailable;
            _waveIn.RecordingStopped += OnRecordingStopped;
            try
            {
                _waveIn.StartRecording();
            }
            catch (MmException mmException)
            {
                _waveIn.DataAvailable -= OnDataAvailable;
                _waveIn.RecordingStopped -= OnRecordingStopped;
                _waveIn.Dispose();
                _waveIn = null;
                _pcmBuffer.Dispose();
                _pcmBuffer = null;
                _stopped = null;
                SafeDiagnostics.LogError("mic", "MmException on capture start", mmException);
                throw;
            }
            catch (Exception exception)
            {
                _waveIn.DataAvailable -= OnDataAvailable;
                _waveIn.RecordingStopped -= OnRecordingStopped;
                _waveIn.Dispose();
                _waveIn = null;
                _pcmBuffer.Dispose();
                _pcmBuffer = null;
                _stopped = null;
                SafeDiagnostics.LogError("mic", "capture-start failure", exception);
                throw;
            }
        }

        SafeDiagnostics.WriteEvent(
            "mic",
            bypassVadGate ? "capture-started-bypass-vad" : streamContinuously ? "capture-started-continuous" : retainAudio ? "capture-started-retained" : "capture-started-gated");
    }

    public async Task<byte[]> StopRecordingAsWaveAsync(CancellationToken cancellationToken = default)
    {
        byte[] pcm = await StopRecordingAsPcmAsync(cancellationToken);
        using var waveStream = new MemoryStream();
        using (var writer = new WaveFileWriter(waveStream, new WaveFormat(16000, 16, 1)))
        {
            writer.Write(pcm, 0, pcm.Length);
        }

        return waveStream.ToArray();
    }

    public async Task<byte[]> StopRecordingAsPcmAsync(CancellationToken cancellationToken = default)
    {
        WaveInEvent waveIn;
        Task stopped;
        lock (_sync)
        {
            waveIn = _waveIn ?? throw new InvalidOperationException("Microphone capture is not active.");
            stopped = _stopped?.Task ?? throw new InvalidOperationException("Capture completion was not initialized.");
        }

        try
        {
            waveIn.StopRecording();
            await stopped.WaitAsync(cancellationToken);
        }
        catch
        {
            Cleanup(waveIn);
            throw;
        }

        byte[] pcm;
        lock (_sync)
        {
            pcm = _pcmBuffer?.ToArray() ?? [];
        }

        Cleanup(waveIn);
        return pcm;
    }

    public async Task CancelRecordingAsync()
    {
        WaveInEvent waveIn;
        Task stopped;
        lock (_sync)
        {
            waveIn = _waveIn ?? throw new InvalidOperationException("Microphone capture is not active.");
            stopped = _stopped?.Task ?? throw new InvalidOperationException("Capture completion was not initialized.");
        }

        try
        {
            waveIn.StopRecording();
            await stopped;
        }
        finally
        {
            Cleanup(waveIn);
        }
    }

    private void Cleanup(WaveInEvent waveIn)
    {
        lock (_sync)
        {
            if (ReferenceEquals(_waveIn, waveIn))
            {
                waveIn.DataAvailable -= OnDataAvailable;
                waveIn.RecordingStopped -= OnRecordingStopped;
                _waveIn = null;
                _pcmBuffer?.Dispose();
                _pcmBuffer = null;
                _streamContinuously = false;
                _bypassVadGate = false;
                _stopped = null;
                SafeDiagnostics.WriteMetric("mic", "captured-chunks", Interlocked.Read(ref _capturedChunkCount));
                SafeDiagnostics.WriteMetric("mic", "forwarded-chunks", Interlocked.Read(ref _forwardedChunkCount));
                SafeDiagnostics.WriteMetric("mic", "speech-segments", Interlocked.Read(ref _speechSegmentCount));
                SafeDiagnostics.WriteEvent("mic", "capture-closed");
            }
        }

        waveIn.Dispose();
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        Interlocked.Increment(ref _capturedChunkCount);
        lock (_sync)
        {
            if (_retainAudio)
            {
                _pcmBuffer?.Write(e.Buffer, 0, e.BytesRecorded);
            }
        }

        if (e.BytesRecorded < 2)
        {
            return;
        }

        double sumSquares = 0;
        int sampleCount = e.BytesRecorded / 2;
        for (int offset = 0; offset < sampleCount * 2; offset += 2)
        {
            double sample = BinaryPrimitives.ReadInt16LittleEndian(e.Buffer.AsSpan(offset, 2)) / 32768.0;
            sumSquares += sample * sample;
        }

        double rms = Math.Sqrt(sumSquares / sampleCount);
        double level = Math.Clamp(rms * 3.5, 0, 1);
        DispatchLevel(level);

        bool hasVoice = (rms * 32768.0) >= 220.0 || level >= 0.025;
        if (hasVoice)
        {
            _voiceHangoverChunks = VoiceHangoverChunks;
            if (Interlocked.Exchange(ref _voiceActive, 1) == 0)
            {
                Interlocked.Increment(ref _speechSegmentCount);
                SafeDiagnostics.WriteEvent("mic", "speech-started");
            }
        }

        if (_voiceHangoverChunks > 0)
        {
            _voiceHangoverChunks--;
            if (_voiceHangoverChunks == 0 && Interlocked.Exchange(ref _voiceActive, 0) == 1)
            {
                SafeDiagnostics.WriteEvent("mic", "speech-ended");
                if (!_streamContinuously && !_bypassVadGate)
                {
                    AudioStreamEnded?.Invoke();
                }
            }
        }

        Action<byte[]>? handler = PcmChunkAvailable;
        if (handler is not null && (_streamContinuously || _bypassVadGate || _voiceHangoverChunks > 0 || hasVoice))
        {
            handler(e.Buffer.AsSpan(0, e.BytesRecorded).ToArray());
            Interlocked.Increment(ref _forwardedChunkCount);
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is null)
        {
            _stopped?.TrySetResult();
        }
        else
        {
            SafeDiagnostics.WriteFailure("mic", "capture-stopped", e.Exception);
            _stopped?.TrySetException(e.Exception);
        }
    }

    private void DispatchLevel(double level)
    {
        Action<double>? handler = AudioLevelChanged;
        if (handler is null)
        {
            return;
        }

        if (_synchronizationContext is null)
        {
            handler(level);
        }
        else
        {
            _synchronizationContext.Post(
                static state =>
                {
                    var (callback, value) = ((Action<double>, double))state!;
                    callback(value);
                },
                (handler, level));
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        WaveInEvent? waveIn;
        lock (_sync)
        {
            waveIn = _waveIn;
            _waveIn = null;
            _pcmBuffer?.Dispose();
            _pcmBuffer = null;
        }

        if (waveIn is not null)
        {
            waveIn.DataAvailable -= OnDataAvailable;
            waveIn.RecordingStopped -= OnRecordingStopped;
            waveIn.Dispose();
        }
    }
}
