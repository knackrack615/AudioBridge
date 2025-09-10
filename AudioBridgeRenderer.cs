using System;
using System.IO.MemoryMappedFiles;
using System.Threading;
using BepInEx;
using UnityEngine;
using CSCore;
using CSCore.CoreAudioAPI;
using CSCore.SoundOut;
using Process = System.Diagnostics.Process;

namespace AudioBridge.Renderer
{
    // Mirror of the MuteTarget enum from main mod
    public enum MuteTarget
    {
        None = 0,
        Host = 1,
        Renderer = 2
    }
    
    [BepInPlugin("com.knackrack615.AudioBridgeRenderer", "AudioBridge Renderer", "1.0.0")]
    public class AudioBridgeRendererPlugin : BaseUnityPlugin
    {
        private ShadowAudioPlayer _audioPlayer;
        private bool _initialized = false;
        
        void Awake()
        {
            Logger.LogInfo("[AudioBridge.Renderer] Initializing audio renderer plugin");
            
            try
            {
                // Start immediately, no delay
                InitializeAudio();
            }
            catch (Exception ex)
            {
                Logger.LogError($"Failed in Awake: {ex}");
            }
        }
        
        void InitializeAudio()
        {
            if (_initialized) return;
            _initialized = true;
            
            Logger.LogInfo("[AudioBridge.Renderer] Starting shared memory audio reader");
            
            try
            {
                _audioPlayer = new ShadowAudioPlayer();
                _audioPlayer.Start();
                Logger.LogInfo("[AudioBridge.Renderer] Audio reader started successfully");
            }
            catch (Exception ex)
            {
                Logger.LogError($"[AudioBridge.Renderer] Failed to start audio reader: {ex}");
            }
        }
        
        void OnDestroy()
        {
            _audioPlayer?.Stop();
        }
    }
    
    public class ShadowAudioPlayer
    {
        private WasapiOut _audioOut;
        private ShadowBusReader _busReader;
        private Thread _audioThread;
        private bool _running;
        
        public void Start()
        {
            _audioThread = new Thread(AudioThreadMain) { IsBackground = true };
            _audioThread.Start();
        }
        
        private void AudioThreadMain()
        {
            Debug.Log("[AudioBridge.Renderer] Audio thread starting - will monitor for audio sharing");
            _running = true;
            bool wasDisabled = false;
            
            while (_running)  // Main loop - keeps trying to connect
            {
                try
                {
                    // Initialize the shadow bus reader
                    _busReader = new ShadowBusReader();
                    
                    // Try to connect to shared memory with better retry logic
                    bool connected = false;
                    for (int i = 0; i < 10 && !connected; i++)
                    {
                        if (_busReader.TryConnect())
                        {
                            connected = true;
                            break;
                        }
                        Thread.Sleep(500);
                    }
                    
                    if (!connected)
                    {
                        Debug.Log("[AudioBridge.Renderer] Shared memory not available, will retry...");
                        _busReader.Dispose();
                        _busReader = null;
                        Thread.Sleep(2000);
                        continue;
                    }
                    
                    // Check if audio sharing is enabled
                    if (!_busReader.IsEnabled())
                    {
                        Debug.Log("[AudioBridge.Renderer] Audio sharing is disabled, waiting for it to be enabled...");
                        _busReader.Dispose();
                        _busReader = null;
                        Thread.Sleep(2000);
                        continue;
                    }
                    
                    Debug.Log("[AudioBridge.Renderer] Connected and audio sharing is enabled!");
                    
                    // Sync with the writer's current position by resetting read index to match write index
                    _busReader.SyncWithWriter();
                    
                    var (sampleRate, channels) = _busReader.GetFormat();
                    Debug.Log($"[AudioBridge.Renderer] Audio format: {sampleRate}Hz, {channels} channels");
                    
                    // Read SessionID from shared memory
                    string sessionId = _busReader.GetSessionId();
                    Debug.Log($"[AudioBridge.Renderer] Using SessionID: {sessionId ?? "none"}");
                    
                    // Create audio source
                    var audioSource = new ShadowAudioSource(_busReader, sampleRate, channels);
                    
                    // Initialize audio output with SessionID if available
                    if (!string.IsNullOrEmpty(sessionId) && Guid.TryParse(sessionId, out Guid sessionGuid))
                    {
                        // Use the same SessionID but with crossProcessSession: false to maintain separate control
                        // This groups them in Windows but keeps them as separate audio sessions for muting
                        _audioOut = new WasapiOut(false, AudioClientShareMode.Shared, 20, sessionGuid, false);
                        Debug.Log($"[AudioBridge.Renderer] Created WasapiOut with SessionID: {sessionGuid} (crossProcess: false for independent muting)");
                    }
                    else
                    {
                        // Fallback to default if no SessionID
                        _audioOut = new WasapiOut(false, AudioClientShareMode.Shared, 20);
                        Debug.Log("[AudioBridge.Renderer] Created WasapiOut without SessionID (legacy mode)");
                    }
                    
                    _audioOut.Initialize(audioSource.ToWaveSource());
                    _audioOut.Play();
                
                Debug.Log("[AudioBridge.Renderer] Audio playback started");
                
                // Check if we should mute based on configuration
                var muteTarget = _busReader.GetMuteTarget();
                Debug.Log($"[AudioBridge.Renderer] Mute target from host: {muteTarget}");
                
                if (muteTarget == MuteTarget.Renderer)
                {
                    // Mute this process's audio session so we don't hear it locally
                    // but it will still be available for recording/streaming
                    MuteCurrentProcessAudio(_audioOut.Device);
                }
                _running = true;
                
                // Keep thread alive and monitor
                MuteTarget lastMuteTarget = muteTarget;
                
                while (_running)
                {
                    Thread.Sleep(5000);
                    
                    // Check if audio sharing has been disabled
                    if (!_busReader.IsEnabled())
                    {
                        Debug.Log("[AudioBridge.Renderer] Audio sharing disabled by host, stopping playback");
                        wasDisabled = true;
                        break;  // Break inner loop to clean up, but stay in outer loop to monitor for re-enable
                    }
                    
                    // Check for mute configuration changes
                    var currentMuteTarget = _busReader.GetMuteTarget();
                    if (currentMuteTarget != lastMuteTarget)
                    {
                        Debug.Log($"[AudioBridge.Renderer] Mute target changed to: {currentMuteTarget}");
                        if (currentMuteTarget == MuteTarget.Renderer)
                        {
                            MuteCurrentProcessAudio(_audioOut.Device);
                        }
                        else if (lastMuteTarget == MuteTarget.Renderer)
                        {
                            UnmuteCurrentProcessAudio(_audioOut.Device);
                        }
                        lastMuteTarget = currentMuteTarget;
                    }
                    
                    var playbackState = _audioOut?.PlaybackState ?? PlaybackState.Stopped;
                    if (playbackState == PlaybackState.Stopped && _running)
                    {
                        Debug.LogWarning("[AudioBridge.Renderer] Audio playback stopped, attempting restart");
                        try
                        {
                            _audioOut?.Play();
                        }
                        catch (Exception ex)
                        {
                            Debug.LogError($"[AudioBridge.Renderer] Failed to restart playback: {ex.Message}");
                        }
                    }
                }
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[AudioBridge.Renderer] Audio thread error: {ex}");
                }
                finally
                {
                    // Clean up before potentially restarting
                    if (_audioOut != null)
                    {
                        Debug.Log("[AudioBridge.Renderer] Cleaning up audio output...");
                        try { _audioOut.Stop(); } catch { }
                        try { _audioOut.Dispose(); } catch { }
                        _audioOut = null;
                    }
                    
                    if (_busReader != null)
                    {
                        try { _busReader.Dispose(); } catch { }
                        _busReader = null;
                    }
                }
                
                // Check if we should exit or retry
                if (!_running) 
                {
                    Debug.Log("[AudioBridge.Renderer] Audio thread exiting (Stop() was called)");
                    break;  // Exit only if Stop() was called
                }
                
                // If audio was disabled, log that we're waiting for re-enable
                if (wasDisabled)
                {
                    Debug.Log("[AudioBridge.Renderer] Audio sharing was disabled, waiting for re-enable...");
                    wasDisabled = false;
                }
                else
                {
                    Debug.Log("[AudioBridge.Renderer] Will retry connection in 2 seconds...");
                }
                Thread.Sleep(2000);
            }
        }
        
        public void Stop()
        {
            _running = false;
            _audioOut?.Stop();
            _audioOut?.Dispose();
            _busReader?.Dispose();
            _audioThread?.Join(1000);
        }
        
        private void MuteCurrentProcessAudio(MMDevice device)
        {
            try
            {
                Debug.Log("[AudioBridge.Renderer] Muting local audio playback (audio still available for recording)");
                
                using var sessionManager = AudioSessionManager2.FromMMDevice(device);
                using var sessionEnumerator = sessionManager.GetSessionEnumerator();
                var currentProcessId = (uint)Process.GetCurrentProcess().Id;
                
                foreach (var session in sessionEnumerator)
                {
                    using var sessionControl = session.QueryInterface<AudioSessionControl2>();
                    if (sessionControl.ProcessID == currentProcessId)
                    {
                        using var simpleVolume = session.QueryInterface<SimpleAudioVolume>();
                        simpleVolume.MasterVolume = 0.0f;
                        Debug.Log($"[AudioBridge.Renderer] Successfully muted audio session for process {currentProcessId}");
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[AudioBridge.Renderer] Failed to mute audio session: {ex.Message}");
                Debug.LogWarning("[AudioBridge.Renderer] Audio will play normally (not muted)");
            }
        }
        
        private void UnmuteCurrentProcessAudio(MMDevice device)
        {
            try
            {
                Debug.Log("[AudioBridge.Renderer] Unmuting local audio playback");
                
                using var sessionManager = AudioSessionManager2.FromMMDevice(device);
                using var sessionEnumerator = sessionManager.GetSessionEnumerator();
                var currentProcessId = (uint)Process.GetCurrentProcess().Id;
                
                foreach (var session in sessionEnumerator)
                {
                    using var sessionControl = session.QueryInterface<AudioSessionControl2>();
                    if (sessionControl.ProcessID == currentProcessId)
                    {
                        using var simpleVolume = session.QueryInterface<SimpleAudioVolume>();
                        simpleVolume.MasterVolume = 1.0f;
                        Debug.Log($"[AudioBridge.Renderer] Successfully unmuted audio session for process {currentProcessId}");
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[AudioBridge.Renderer] Failed to unmute audio session: {ex.Message}");
            }
        }
    }
    
    public class ShadowBusReader : IDisposable
    {
        private const string MMF_NAME = "AudioBridge_SharedMemory";
        private const string MUTEX_NAME = "AudioBridge_SharedMemory_Mutex";
        private const int HEADER_BYTES = 64;
        private const int RING_BYTES = 2 * 1024 * 1024; // 2MB ring buffer for stable audio
        
        private MemoryMappedFile _mmf;
        private MemoryMappedViewAccessor _view;
        private Mutex _mutex;
        private long _totalSamplesRead;
        
        public bool TryConnect()
        {
            try
            {
                _mmf = MemoryMappedFile.OpenExisting(MMF_NAME, MemoryMappedFileRights.ReadWrite);
                _mutex = Mutex.OpenExisting(MUTEX_NAME);
                _view = _mmf.CreateViewAccessor(0, HEADER_BYTES + RING_BYTES, MemoryMappedFileAccess.ReadWrite);
                return true;
            }
            catch
            {
                Dispose();
                return false;
            }
        }
        
        public (int sampleRate, int channels) GetFormat()
        {
            if (_mutex == null || _view == null) return (48000, 2);
            
            _mutex.WaitOne();
            try
            {
                int sr = _view.ReadInt32(8);
                int ch = _view.ReadInt32(12);
                return (sr > 0 ? sr : 48000, ch > 0 ? ch : 2);
            }
            finally { _mutex.ReleaseMutex(); }
        }
        
        public string GetSessionId()
        {
            if (_mutex == null || _view == null) return null;
            
            _mutex.WaitOne();
            try
            {
                byte[] sessionBytes = new byte[36];
                _view.ReadArray(24, sessionBytes, 0, 36);
                
                // Find null terminator
                int length = Array.IndexOf(sessionBytes, (byte)0);
                if (length == -1) length = 36;
                if (length == 0) return null;
                
                return System.Text.Encoding.ASCII.GetString(sessionBytes, 0, length);
            }
            finally { _mutex.ReleaseMutex(); }
        }
        
        public MuteTarget GetMuteTarget()
        {
            if (_mutex == null || _view == null) return MuteTarget.Renderer;
            
            _mutex.WaitOne();
            try
            {
                int muteValue = _view.ReadInt32(16);
                if (muteValue < 0 || muteValue > 2) return MuteTarget.Renderer;
                return (MuteTarget)muteValue;
            }
            finally { _mutex.ReleaseMutex(); }
        }
        
        public bool IsEnabled()
        {
            if (_mutex == null || _view == null) return false;
            
            _mutex.WaitOne();
            try
            {
                return _view.ReadInt32(20) == 1;
            }
            finally { _mutex.ReleaseMutex(); }
        }
        
        public void SyncWithWriter()
        {
            if (_mutex == null || _view == null) return;
            
            _mutex.WaitOne();
            try
            {
                // Set read index to match write index (start reading from current position)
                uint w = _view.ReadUInt32(0);
                _view.Write(4, w);
                Debug.Log($"[AudioBridge.Renderer] Synced read index to write index: {w}");
            }
            finally { _mutex.ReleaseMutex(); }
        }
        
        public int ReadFloats(float[] buffer, int offset, int count)
        {
            if (_view == null) return 0;
            
            int bytesWanted = count * sizeof(float);
            byte[] tempBuffer = new byte[bytesWanted];
            int gotBytes = 0;
            
            _mutex.WaitOne();
            try
            {
                uint w = _view.ReadUInt32(0);
                uint r = _view.ReadUInt32(4);
                
                int avail = (int)((RING_BYTES + w - r) % RING_BYTES);
                if (avail <= 0) return 0;
                
                int want = Math.Min(avail, bytesWanted);
                int headOffset = HEADER_BYTES + (int)r;
                int tail = Math.Min(want, RING_BYTES - (int)r);
                
                // Read first segment
                _view.ReadArray(headOffset, tempBuffer, 0, tail);
                
                // Read wrapped segment if needed
                if (want > tail)
                {
                    int rest = want - tail;
                    _view.ReadArray(HEADER_BYTES, tempBuffer, tail, rest);
                }
                
                // Update read index
                r = (uint)((r + want) % RING_BYTES);
                _view.Write(4, r);
                gotBytes = want;
            }
            finally { _mutex.ReleaseMutex(); }
            
            // Convert bytes to floats
            int floatsRead = gotBytes / sizeof(float);
            Buffer.BlockCopy(tempBuffer, 0, buffer, offset, gotBytes);
            
            _totalSamplesRead += floatsRead;
            return floatsRead;
        }
        
        public void Dispose()
        {
            _view?.Dispose();
            _mutex?.Dispose();
            _mmf?.Dispose();
            _view = null;
            _mutex = null;
            _mmf = null;
        }
    }
    
    public class ShadowAudioSource : ISampleSource
    {
        private readonly ShadowBusReader _reader;
        private readonly WaveFormat _format;
        
        public ShadowAudioSource(ShadowBusReader reader, int sampleRate, int channels)
        {
            _reader = reader;
            _format = new WaveFormat(sampleRate, 32, channels, AudioEncoding.IeeeFloat);
        }
        
        public WaveFormat WaveFormat => _format;
        public bool CanSeek => false;
        public long Position { get => 0; set { } }
        public long Length => 0;
        
        public int Read(float[] buffer, int offset, int count)
        {
            try
            {
                int got = _reader.ReadFloats(buffer, offset, count);
                
                // Fill silence if needed
                if (got < count)
                {
                    for (int i = offset + got; i < offset + count; i++)
                    {
                        buffer[i] = 0f;
                    }
                }
                
                return count;
            }
            catch
            {
                Array.Clear(buffer, offset, count);
                return count;
            }
        }
        
        public void Dispose() { }
    }
}