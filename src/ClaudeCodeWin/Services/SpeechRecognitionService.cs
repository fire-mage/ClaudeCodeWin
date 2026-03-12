using System.IO;
using System.Runtime.InteropServices;
using Whisper.net;

namespace ClaudeCodeWin.Services;

/// <summary>
/// Offline speech-to-text using Whisper.net.
/// Records audio via Windows waveIn API (no NAudio dependency), transcribes with Whisper model.
/// </summary>
public sealed class SpeechRecognitionService : IDisposable
{
    private WhisperFactory? _factory;
    private WhisperProcessor? _processor;
    private string? _loadedModel;
    private bool _disposed;
    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private readonly SemaphoreSlim _transcribeLock = new(1, 1);

    // Recording state
    private IntPtr _hWaveIn;
    private volatile bool _isRecording;
    private readonly List<byte> _recordedPcm = [];
    private GCHandle _bufferHandle1, _bufferHandle2;
    private IntPtr _header1, _header2;
    private const int SampleRate = 16000;
    private const int BufferSize = 16000 * 2; // 1 second of 16-bit mono PCM
    private const int MaxRecordingSeconds = 120; // 2 minute cap
    private const int MaxPcmBytes = SampleRate * 2 * MaxRecordingSeconds;

    public bool IsModelLoaded => _processor is not null;
    public bool IsRecording => _isRecording;

    /// <summary>Fired from waveIn callback thread when recording hits max duration. Callers must dispatch to UI thread.</summary>
    public event Action? RecordingCapped;

    /// <summary>Load Whisper model. Call once after model is downloaded.</summary>
    public async Task LoadModelAsync(string modelName, CancellationToken ct = default)
    {
        ObjectDisposedThrow();
        await _loadLock.WaitAsync(ct);
        try
        {
            var modelPath = WhisperModelManager.GetModelPath(modelName);
            if (!File.Exists(modelPath))
                throw new FileNotFoundException($"Whisper model not found: {modelPath}");

            // Wait for any in-flight transcription to finish before disposing the processor
            await _transcribeLock.WaitAsync(ct);
            try
            {
                _processor?.Dispose();
                _processor = null;
                _factory?.Dispose();
                _factory = null;
                _loadedModel = null;
            }
            finally { _transcribeLock.Release(); }

            // CPU-bound model loading (~500MB) — run off UI thread
            await Task.Run(() =>
            {
                _factory = WhisperFactory.FromPath(modelPath);
                _processor = _factory.CreateBuilder()
                    .WithLanguage("auto")
                    .WithNoContext()
                    .Build();
            }, ct);
            _loadedModel = modelName;
        }
        finally { _loadLock.Release(); }
    }

    /// <summary>Start recording from default microphone.</summary>
    public void StartRecording()
    {
        ObjectDisposedThrow();
        if (_isRecording) return;

        lock (_recordedPcm) { _recordedPcm.Clear(); }

        var format = new WaveFormatEx
        {
            wFormatTag = 1, // PCM
            nChannels = 1,
            nSamplesPerSec = SampleRate,
            nAvgBytesPerSec = SampleRate * 2,
            nBlockAlign = 2,
            wBitsPerSample = 16,
            cbSize = 0
        };

        _waveInCallback = WaveInCallback;
        int result = waveInOpen(out _hWaveIn, WAVE_MAPPER, ref format, _waveInCallback, IntPtr.Zero, CALLBACK_FUNCTION);
        if (result != 0)
            throw new InvalidOperationException($"waveInOpen failed with code {result}. No microphone available?");

        // Prepare two rotating buffers — cleanup on any failure
        try
        {
            _header1 = PrepareBuffer(out _bufferHandle1);
            _header2 = PrepareBuffer(out _bufferHandle2);

            waveInAddBuffer(_hWaveIn, _header1, Marshal.SizeOf<WaveHeader>());
            waveInAddBuffer(_hWaveIn, _header2, Marshal.SizeOf<WaveHeader>());

            _isRecording = true; // BEFORE waveInStart — callbacks check this flag
            result = waveInStart(_hWaveIn);
            if (result != 0)
            {
                _isRecording = false;
                throw new InvalidOperationException($"waveInStart failed with code {result}");
            }
        }
        catch
        {
            _isRecording = false;
            CleanupRecording();
            throw;
        }
    }

    /// <summary>Stop recording and transcribe the audio. Returns recognized text.</summary>
    public async Task<string> StopAndTranscribeAsync(CancellationToken ct = default)
    {
        ObjectDisposedThrow();

        // Stop device if still running (may already be stopped by cap handler)
        if (_isRecording)
        {
            _isRecording = false;
            waveInStop(_hWaveIn);
        }

        // Always cleanup native handles (cap handler only calls waveInStop, not reset/cleanup)
        if (_hWaveIn != IntPtr.Zero)
            CleanupRecording();

        var processor = _processor;
        if (processor is null)
            return "[Model not loaded]";

        byte[] pcmBytes;
        lock (_recordedPcm)
        {
            if (_recordedPcm.Count < SampleRate * 2) // less than 1 second
                return string.Empty;
            pcmBytes = _recordedPcm.ToArray();
        }
        var samples = new float[pcmBytes.Length / 2];
        for (int i = 0; i < samples.Length; i++)
        {
            short sample = BitConverter.ToInt16(pcmBytes, i * 2);
            samples[i] = sample / 32768f;
        }

        // Run Whisper under transcribe lock to prevent LoadModelAsync from disposing processor mid-flight
        await _transcribeLock.WaitAsync(ct);
        try
        {
            // If LoadModelAsync disposed our processor while we waited for the lock, bail out
            if (_processor != processor)
                return "[Model reloaded during transcription]";

            var result = new System.Text.StringBuilder();
            await foreach (var segment in processor.ProcessAsync(samples, ct))
            {
                result.Append(segment.Text);
            }
            return result.ToString().Trim();
        }
        finally { _transcribeLock.Release(); }
    }

    /// <summary>Cancel recording without transcribing.</summary>
    public void CancelRecording()
    {
        if (!_isRecording && _hWaveIn == IntPtr.Zero) return;
        _isRecording = false;
        if (_hWaveIn != IntPtr.Zero)
        {
            waveInStop(_hWaveIn);
            CleanupRecording();
        }
        lock (_recordedPcm) { _recordedPcm.Clear(); }
    }

    // ── waveIn callback ──

    private WaveInProc? _waveInCallback; // prevent GC collection

    private void WaveInCallback(IntPtr hWaveIn, uint uMsg, IntPtr dwInstance, IntPtr dwParam1, IntPtr dwParam2)
    {
        if (uMsg != WIM_DATA || !_isRecording) return;

        var header = Marshal.PtrToStructure<WaveHeader>(dwParam1);
        if (header.dwBytesRecorded > 0)
        {
            var data = new byte[header.dwBytesRecorded];
            Marshal.Copy(header.lpData, data, 0, (int)header.dwBytesRecorded);
            bool capped;
            lock (_recordedPcm)
            {
                _recordedPcm.AddRange(data);
                capped = _recordedPcm.Count >= MaxPcmBytes;
            }
            if (capped)
            {
                _isRecording = false;
                // Signal cap without touching the device from callback context (MSDN restriction).
                // Consumer (StopAndTranscribeAsync) handles waveInStop + CleanupRecording.
                ThreadPool.QueueUserWorkItem(_ => RecordingCapped?.Invoke());
                return;
            }
        }

        // Re-queue buffer if still recording and device handle is valid
        // (guard against callback arriving after CleanupRecording freed the buffers)
        if (_isRecording && _hWaveIn != IntPtr.Zero)
        {
            waveInAddBuffer(hWaveIn, dwParam1, Marshal.SizeOf<WaveHeader>());
        }
    }

    // ── Buffer management ──

    private IntPtr PrepareBuffer(out GCHandle bufferHandle)
    {
        var buffer = new byte[BufferSize];
        bufferHandle = GCHandle.Alloc(buffer, GCHandleType.Pinned);

        var header = new WaveHeader
        {
            lpData = bufferHandle.AddrOfPinnedObject(),
            dwBufferLength = (uint)buffer.Length,
            dwFlags = 0,
            dwBytesRecorded = 0
        };

        var headerSize = Marshal.SizeOf<WaveHeader>();
        var headerPtr = Marshal.AllocHGlobal(headerSize);
        Marshal.StructureToPtr(header, headerPtr, false);

        waveInPrepareHeader(_hWaveIn, headerPtr, headerSize);
        return headerPtr;
    }

    /// <summary>
    /// Reset device (completes all pending callbacks synchronously on Windows),
    /// then free native buffer handles and close the device.
    /// </summary>
    private void CleanupRecording()
    {
        // waveInReset MUST be first — it synchronously completes all pending callbacks,
        // making it safe to free buffer memory afterwards.
        if (_hWaveIn != IntPtr.Zero)
            waveInReset(_hWaveIn);

        if (_header1 != IntPtr.Zero)
        {
            waveInUnprepareHeader(_hWaveIn, _header1, Marshal.SizeOf<WaveHeader>());
            Marshal.FreeHGlobal(_header1);
            _header1 = IntPtr.Zero;
        }
        if (_header2 != IntPtr.Zero)
        {
            waveInUnprepareHeader(_hWaveIn, _header2, Marshal.SizeOf<WaveHeader>());
            Marshal.FreeHGlobal(_header2);
            _header2 = IntPtr.Zero;
        }
        if (_bufferHandle1.IsAllocated) _bufferHandle1.Free();
        if (_bufferHandle2.IsAllocated) _bufferHandle2.Free();

        if (_hWaveIn != IntPtr.Zero)
        {
            waveInClose(_hWaveIn);
            _hWaveIn = IntPtr.Zero;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Always cleanup native handles — _isRecording may be false after cap but handles still allocated
        if (_isRecording || _hWaveIn != IntPtr.Zero)
        {
            _isRecording = false;
            if (_hWaveIn != IntPtr.Zero)
                waveInStop(_hWaveIn);
            CleanupRecording();
        }

        _processor?.Dispose();
        _processor = null;
        _factory?.Dispose();
        _factory = null;
        _loadLock.Dispose();
        _transcribeLock.Dispose();
    }

    private void ObjectDisposedThrow()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(SpeechRecognitionService));
    }

    // ── Win32 waveIn interop ──

    private const uint WAVE_MAPPER = unchecked((uint)-1);
    private const uint CALLBACK_FUNCTION = 0x00030000;
    private const uint WIM_DATA = 0x3C0;

    private delegate void WaveInProc(IntPtr hWaveIn, uint uMsg, IntPtr dwInstance, IntPtr dwParam1, IntPtr dwParam2);

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveFormatEx
    {
        public ushort wFormatTag;
        public ushort nChannels;
        public uint nSamplesPerSec;
        public uint nAvgBytesPerSec;
        public ushort nBlockAlign;
        public ushort wBitsPerSample;
        public ushort cbSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveHeader
    {
        public IntPtr lpData;
        public uint dwBufferLength;
        public uint dwBytesRecorded;
        public IntPtr dwUser;
        public uint dwFlags;
        public uint dwLoops;
        public IntPtr lpNext;
        public IntPtr reserved;
    }

    [DllImport("winmm.dll")]
    private static extern int waveInOpen(out IntPtr hWaveIn, uint deviceId, ref WaveFormatEx lpFormat, WaveInProc dwCallback, IntPtr dwInstance, uint dwFlags);

    [DllImport("winmm.dll")]
    private static extern int waveInClose(IntPtr hWaveIn);

    [DllImport("winmm.dll")]
    private static extern int waveInStart(IntPtr hWaveIn);

    [DllImport("winmm.dll")]
    private static extern int waveInStop(IntPtr hWaveIn);

    [DllImport("winmm.dll")]
    private static extern int waveInReset(IntPtr hWaveIn);

    [DllImport("winmm.dll")]
    private static extern int waveInPrepareHeader(IntPtr hWaveIn, IntPtr lpWaveInHdr, int uSize);

    [DllImport("winmm.dll")]
    private static extern int waveInUnprepareHeader(IntPtr hWaveIn, IntPtr lpWaveInHdr, int uSize);

    [DllImport("winmm.dll")]
    private static extern int waveInAddBuffer(IntPtr hWaveIn, IntPtr lpWaveInHdr, int uSize);

    [DllImport("winmm.dll")]
    private static extern int waveInGetNumDevs();

    /// <summary>Check if any recording device is available.</summary>
    public static bool HasMicrophone() => waveInGetNumDevs() > 0;
}
