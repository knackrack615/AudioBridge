using CSCore.CoreAudioAPI;
using CSCore.SoundOut;
using Elements.Core;
using FrooxEngine;
using HarmonyLib;
using ResoniteModLoader;
using System;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace AudioBridge;

// Enum for mute target selection
public enum MuteTarget
{
    None,
    Host,
    Renderer
}

// RML entry
public class AudioBridge : ResoniteMod
{
    internal const string VERSION_CONSTANT = "1.0.0";
    public override string Name => "AudioBridge";
    public override string Author => "Knackrack615";
    public override string Version => VERSION_CONSTANT;
    public override string Link => "https://github.com/knackrack615/AudioBridge/";

    [AutoRegisterConfigKey]
    private static readonly ModConfigurationKey<bool> ENABLED =
        new("enabled", "Enable audio sharing to renderer process?", () => true);
    
    [AutoRegisterConfigKey]
    private static readonly ModConfigurationKey<MuteTarget> MUTE_TARGET =
        new("muteTarget", "Which process to mute (prevents double audio)?", () => MuteTarget.Renderer);

    private static ModConfiguration _config;
    private static MuteTarget _currentMuteTarget = MuteTarget.Renderer;
    private static bool _isEnabled = false;

    public override void OnEngineInit()
    {
        try
        {
            _config = GetConfiguration();
            UniLog.Log("[AudioBridge] Initializing audio sharing module");
            
            // Set initial values and subscribe to configuration changes
            _currentMuteTarget = _config.GetValue(MUTE_TARGET);
            _isEnabled = _config.GetValue(ENABLED);
            _config.OnThisConfigurationChanged += OnConfigurationChanged;
            UniLog.Log($"[AudioBridge] Mute target set to: {_currentMuteTarget}");
            
            // Apply Harmony patches manually
            UniLog.Log("[AudioBridge] Applying audio driver patches");
            var harmony = new Harmony("net.knackrack615.AudioBridge");
            
            try
            {
                // Try to patch CSCoreAudioOutputDriver methods
                var driverType = typeof(CSCoreAudioOutputDriver);
                var baseType = typeof(AudioOutputDriver);
                
                // Get all methods including declared only (not inherited)
                var methods = driverType.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly);
                int patchedCount = 0;
                
                UniLog.Log($"[AudioBridge] Found {methods.Length} audio driver methods");
                
                foreach (var method in methods)
                {
                    // Log all Read-related methods
                    if (method.Name.Contains("Read") || method.Name.Contains("read"))
                    {
                        var parameters = method.GetParameters();
                        var paramInfo = string.Join(", ", parameters.Select(p => $"{p.ParameterType.Name} {p.Name}"));
                        UniLog.Log($"[AudioBridge] Discovered audio method: {method.Name}({paramInfo})");
                        
                        // Try to patch each Read method with the appropriate postfix
                        if (method.DeclaringType == driverType)
                        {
                            try
                            {
                                string postfixName = null;
                                
                                // Choose the right postfix based on method signature
                                if (method.Name == "Read" && parameters.Length == 3)
                                {
                                    if (parameters[0].ParameterType == typeof(float[]))
                                    {
                                        postfixName = "Read_Float_Postfix";
                                    }
                                    else if (parameters[0].ParameterType == typeof(byte[]))
                                    {
                                        postfixName = "Read_Byte_Postfix";
                                    }
                                }
                                else if (method.Name == "ReadAuto")
                                {
                                    postfixName = "ReadAuto_Span_Postfix";
                                }
                                
                                if (postfixName != null)
                                {
                                    var postfix = typeof(ShadowWriterPatch).GetMethod(postfixName,
                                        System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
                                    if (postfix != null)
                                    {
                                        harmony.Patch(method, postfix: new HarmonyMethod(postfix));
                                        UniLog.Log($"[AudioBridge] Successfully patched {method.Name}");
                                        patchedCount++;
                                    }
                                    else
                                    {
                                        UniLog.Log($"[AudioBridge] Patch method {postfixName} not found");
                                    }
                                }
                            }
                            catch (Exception patchEx)
                            {
                                UniLog.Log($"[AudioBridge] Failed to patch {method.Name}: {patchEx.Message}");
                            }
                        }
                    }
                }
                
                // Also check base class methods
                var baseMethods = baseType.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                UniLog.Log($"[AudioBridge] Base driver has {baseMethods.Length} methods");
                
                foreach (var method in baseMethods)
                {
                    if (method.Name.Contains("Read") || method.Name.Contains("read") || method.Name == "Start")
                    {
                        var parameters = method.GetParameters();
                        var paramInfo = string.Join(", ", parameters.Select(p => $"{p.ParameterType.Name} {p.Name}"));
                        UniLog.Log($"[AudioBridge] Found base method: {method.Name}({paramInfo})");
                        
                        // Patch Start method from base class
                        if (method.Name == "Start" && method.DeclaringType == baseType)
                        {
                            try
                            {
                                var startPostfix = typeof(ShadowWriterPatch).GetMethod("Start_Base_Postfix",
                                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
                                harmony.Patch(method, postfix: new HarmonyMethod(startPostfix));
                                UniLog.Log("[AudioBridge] Patched base Start method");
                            }
                            catch (Exception patchEx)
                            {
                                UniLog.Log($"[AudioBridge] Failed to patch base Start: {patchEx.Message}");
                            }
                        }
                    }
                }
                
                UniLog.Log($"[AudioBridge] Successfully patched {patchedCount} audio methods");
            }
            catch (Exception ex)
            {
                UniLog.Error($"[AudioBridge] Patching failed: {ex.Message}");
            }
            
            UniLog.Log("[AudioBridge] Audio driver patching completed");

            // Check if enabled in config
            UniLog.Log($"[AudioBridge] Audio sharing enabled: {_isEnabled}");
            
            if (_isEnabled)
            {
                UniLog.Log("[AudioBridge] Initializing audio writer for host process");
                
                // Initialize the bus as writer immediately
                UniLog.Log("[AudioBridge] Initializing shared memory audio buffer");
                if (ShadowBus.EnsureInit(writer: true))
                {
                    UniLog.Log("[AudioBridge] Shared memory audio buffer initialized");
                    // Reset buffer indices on initial start
                    ShadowBus.ResetBufferIndices();
                    // Explicitly publish that we're enabled
                    ShadowBus.PublishEnabled(true);
                    ShadowBus.PublishMuteTarget(_currentMuteTarget);
                }
                else
                {
                    UniLog.Error("[AudioBridge] Failed to initialize shared memory buffer");
                }
            }
            else
            {
                UniLog.Log("[AudioBridge] Audio sharing is disabled");
            }
        }
        catch (Exception ex)
        {
            UniLog.Error($"[AudioBridge] Initialization failed: {ex.Message}");
            UniLog.Error($"[AudioBridge] Stack trace: {ex.StackTrace}");
        }
    }


    internal static void Msg(string s) => UniLog.Log($"[AudioBridge] {s}");
    internal static void Err(string s) => UniLog.Error($"[AudioBridge] {s}", stackTrace: false);
    
    private void OnConfigurationChanged(ConfigurationChangedEvent e)
    {
        if (e.Key == ENABLED)
        {
            var previousEnabled = _isEnabled;
            _isEnabled = _config.GetValue(ENABLED);
            UniLog.Log($"[AudioBridge] Audio sharing enabled changed from {previousEnabled} to {_isEnabled}");
            
            if (_isEnabled && !previousEnabled)
            {
                // Enabling audio sharing
                UniLog.Log("[AudioBridge] Enabling audio sharing...");
                Task.Run(async () =>
                {
                    await Task.Delay(100); // Small delay
                    if (ShadowBus.EnsureInit(writer: true))
                    {
                        UniLog.Log("[AudioBridge] Audio sharing enabled successfully");
                        
                        // Reset buffer indices on re-enable for clean start
                        ShadowBus.ResetBufferIndices();
                        
                        ShadowBus.PublishEnabled(true);
                        
                        // Reset the writer state
                        ShadowWriterPatch.ResetState();
                        
                        // Apply mute configuration if needed
                        if (_currentMuteTarget == MuteTarget.Host)
                        {
                            // Try to apply mute configuration with a slight delay if audio device isn't ready
                            Task.Run(async () =>
                            {
                                for (int i = 0; i < 10; i++)
                                {
                                    if (ShadowWriterPatch.TryApplyMuteConfiguration(true))
                                    {
                                        break;
                                    }
                                    await Task.Delay(500);
                                }
                            });
                        }
                    }
                    else
                    {
                        UniLog.Error("[AudioBridge] Failed to enable audio sharing");
                    }
                });
            }
            else if (!_isEnabled && previousEnabled)
            {
                // Disabling audio sharing
                UniLog.Log("[AudioBridge] Disabling audio sharing...");
                
                // Unmute host if it was muted
                if (_currentMuteTarget == MuteTarget.Host)
                {
                    ShadowWriterPatch.ApplyMuteConfiguration(false);
                }
                
                // Publish disabled state before shutting down
                ShadowBus.PublishEnabled(false);
                
                // Wait a bit for renderer to see the change
                Task.Run(async () =>
                {
                    await Task.Delay(500);
                    ShadowBus.Shutdown();
                    ShadowWriterPatch.ResetState();
                    UniLog.Log("[AudioBridge] Audio sharing disabled");
                });
            }
        }
        else if (e.Key == MUTE_TARGET)
        {
            var previousTarget = _currentMuteTarget;
            _currentMuteTarget = _config.GetValue(MUTE_TARGET);
            UniLog.Log($"[AudioBridge] Mute target changed from {previousTarget} to {_currentMuteTarget}");
            
            // Only process if enabled
            if (_isEnabled)
            {
                // Publish the new mute target to shared memory
                ShadowBus.PublishMuteTarget(_currentMuteTarget);
                
                // Update host muting based on the new target
                // Mute host if target is Host, unmute for Renderer or None
                bool shouldMuteHost = (_currentMuteTarget == MuteTarget.Host);
                
                // Only apply if there's an actual change in host muting state
                if (previousTarget == MuteTarget.Host || _currentMuteTarget == MuteTarget.Host)
                {
                    ShadowWriterPatch.ApplyMuteConfiguration(shouldMuteHost);
                }
            }
        }
    }
    
    internal static MuteTarget GetCurrentMuteTarget() => _currentMuteTarget;
    internal static bool IsEnabled() => _isEnabled;
}



// ===== Shared bus (host<->renderer) =====
internal static class ShadowBus
{
    // Use Local namespace (no prefix) to avoid permission issues
    private const string MMF_NAME = "AudioBridge_SharedMemory";
    private const string MUTEX_NAME = "AudioBridge_SharedMemory_Mutex";

    // Header layout (bytes)
    //  0..3  : uint writeIdx
    //  4..7  : uint readIdx
    //  8..11 : int sampleRate
    // 12..15 : int channels
    // 16..19 : int muteTarget (0=None, 1=Host, 2=Renderer)
    // 20..23 : int enabled (0=disabled, 1=enabled)
    // 24..63 : reserved
    private const int HEADER_BYTES = 64;
    private const int RING_BYTES = 2 * 1024 * 1024; // 2MB ring buffer for stable audio
    private const int MMF_BYTES = HEADER_BYTES + RING_BYTES;

    private static MemoryMappedFile _mmf;
    private static MemoryMappedViewAccessor _view;
    private static Mutex _mtx;

    private static volatile bool _inited;

    public static bool EnsureInit(bool writer, int sampleRate = 48000, int channels = 2)
    {
        if (_inited)
        {
            // Already initialized, return silently
            return true;
        }
        
        UniLog.Log($"[AudioBridge] Initializing shared memory as {(writer ? "writer" : "reader")}");
        
        try
        {
            if (writer)
            {
                UniLog.Log($"[AudioBridge] Creating shared memory: {MMF_NAME}");
                _mmf = MemoryMappedFile.CreateOrOpen(MMF_NAME, MMF_BYTES, MemoryMappedFileAccess.ReadWrite);
                UniLog.Log("[AudioBridge] Shared memory created");
                
                UniLog.Log($"[AudioBridge] Creating synchronization mutex: {MUTEX_NAME}");
                _mtx = new Mutex(false, MUTEX_NAME);
                UniLog.Log("[AudioBridge] Mutex created");
                
                _view = _mmf.CreateViewAccessor(0, MMF_BYTES, MemoryMappedFileAccess.ReadWrite);
                UniLog.Log("[AudioBridge] Memory accessor created");
                
                _mtx.WaitOne();
                try
                {
                    // If first time, zero indices and write format
                    uint w = _view.ReadUInt32(0);
                    uint r = _view.ReadUInt32(4);
                    UniLog.Log($"[AudioBridge] Buffer indices: write={w}, read={r}");
                    
                    if (w > RING_BYTES || r > RING_BYTES)
                    {
                        UniLog.Log("[AudioBridge] Resetting buffer indices");
                        _view.Write(0, (uint)0);
                        _view.Write(4, (uint)0);
                    }
                    
                    _view.Write(8, sampleRate);
                    _view.Write(12, channels);
                    _view.Write(16, (int)AudioBridge.GetCurrentMuteTarget());
                    _view.Write(20, AudioBridge.IsEnabled() ? 1 : 0);
                    UniLog.Log($"[AudioBridge] Audio format: {sampleRate}Hz, {channels} channels");
                }
                finally { _mtx.ReleaseMutex(); }
            }
            else
            {
                UniLog.Log($"[AudioBridge] Opening shared memory: {MMF_NAME}");
                
                // For reader, try to wait for writer to create the MMF first
                bool mmfExists = false;
                Exception lastError = null;
                
                for (int attempt = 0; attempt < 10; attempt++)
                {
                    try
                    {
                        _mmf = MemoryMappedFile.OpenExisting(MMF_NAME, MemoryMappedFileRights.ReadWrite);
                        mmfExists = true;
                        UniLog.Log($"[AudioBridge] Shared memory opened on attempt {attempt + 1}");
                        break;
                    }
                    catch (Exception ex)
                    {
                        lastError = ex;
                        if (attempt < 9)
                        {
                            UniLog.Log($"[AudioBridge] Waiting for shared memory (attempt {attempt + 1}/10)");
                            Thread.Sleep(500);
                        }
                    }
                }
                
                if (!mmfExists)
                {
                    UniLog.Error($"[AudioBridge] Failed to open shared memory: {lastError?.Message}");
                    throw new InvalidOperationException($"MMF {MMF_NAME} not found", lastError);
                }
                
                UniLog.Log($"[AudioBridge] Opening synchronization mutex");
                _mtx = Mutex.OpenExisting(MUTEX_NAME);
                UniLog.Log("[AudioBridge] Mutex opened");
                
                _view = _mmf.CreateViewAccessor(0, MMF_BYTES, MemoryMappedFileAccess.ReadWrite);
                UniLog.Log("[AudioBridge] Memory accessor created");
            }
            
            _inited = true;
            UniLog.Log("[AudioBridge] Shared memory initialization complete");
            return true;
        }
        catch (Exception ex)
        {
            UniLog.Error($"[AudioBridge] Shared memory initialization failed: {ex.Message}");
            UniLog.Error($"[AudioBridge] Stack trace: {ex.StackTrace}");
            
            // Clean up on failure
            try { _view?.Dispose(); } catch { }
            try { _mtx?.Dispose(); } catch { }
            try { _mmf?.Dispose(); } catch { }
            _view = null;
            _mtx = null;
            _mmf = null;
            
            return false;
        }
    }

    public static void PublishFormat(int sampleRate, int channels)
    {
        if (!_inited) return;
        _mtx.WaitOne();
        try
        {
            UniLog.Log($"[AudioBridge] Publishing audio format: {sampleRate}Hz, {channels}ch");
            _view.Write(8, sampleRate);
            _view.Write(12, channels);
            _view.Write(16, (int)AudioBridge.GetCurrentMuteTarget());
        }
        finally { _mtx.ReleaseMutex(); }
    }
    
    public static void PublishMuteTarget(MuteTarget target)
    {
        if (!_inited) return;
        _mtx.WaitOne();
        try
        {
            _view.Write(16, (int)target);
            UniLog.Log($"[AudioBridge] Published mute target: {target}");
        }
        finally { _mtx.ReleaseMutex(); }
    }

    public static (int sampleRate, int channels) ReadFormat()
    {
        if (!_inited) return (48000, 2);
        _mtx.WaitOne();
        try
        {
            // Reading audio format
            int sr = _view.ReadInt32(8);
            int ch = _view.ReadInt32(12);
            if (sr <= 0 || ch <= 0) return (48000, 2);
            return (sr, ch);
        }
        finally { _mtx.ReleaseMutex(); }
    }
    
    public static MuteTarget ReadMuteTarget()
    {
        if (!_inited) return MuteTarget.Renderer;
        _mtx.WaitOne();
        try
        {
            int muteValue = _view.ReadInt32(16);
            if (muteValue < 0 || muteValue > 2) return MuteTarget.Renderer;
            return (MuteTarget)muteValue;
        }
        finally { _mtx.ReleaseMutex(); }
    }
    
    public static void PublishEnabled(bool enabled)
    {
        if (!_inited) return;
        _mtx.WaitOne();
        try
        {
            _view.Write(20, enabled ? 1 : 0);
            
            // Reset ring buffer indices when disabling to ensure clean restart
            if (!enabled)
            {
                _view.Write(0, (uint)0);  // Reset write index
                _view.Write(4, (uint)0);  // Reset read index
                UniLog.Log("[AudioBridge] Reset buffer indices on disable");
            }
            
            UniLog.Log($"[AudioBridge] Published enabled state: {enabled}");
        }
        finally { _mtx.ReleaseMutex(); }
    }
    
    public static bool ReadEnabled()
    {
        if (!_inited) return false;
        _mtx.WaitOne();
        try
        {
            return _view.ReadInt32(20) == 1;
        }
        finally { _mtx.ReleaseMutex(); }
    }
    
    public static void ResetBufferIndices()
    {
        if (!_inited) return;
        _mtx.WaitOne();
        try
        {
            _view.Write(0, (uint)0);  // Reset write index
            _view.Write(4, (uint)0);  // Reset read index
            UniLog.Log("[AudioBridge] Buffer indices reset to 0");
        }
        finally { _mtx.ReleaseMutex(); }
    }
    
    public static void Shutdown()
    {
        if (!_inited) return;
        
        UniLog.Log("[AudioBridge] Shutting down shared memory");
        _inited = false;
        
        try { _view?.Dispose(); } catch { }
        try { _mtx?.Dispose(); } catch { }
        try { _mmf?.Dispose(); } catch { }
        
        _view = null;
        _mtx = null;
        _mmf = null;
    }

    // Writer: float32 interleaved -> ring
    public static void WriteFloats(ReadOnlySpan<float> src)
    {
        if (!_inited || src.IsEmpty) return;

        var srcBytes = MemoryMarshal.AsBytes(src);

        _mtx.WaitOne();
        try
        {
            uint w = _view.ReadUInt32(0);
            uint r = _view.ReadUInt32(4);

            int free = (int)((RING_BYTES + r - w - 1) % RING_BYTES);
            int want = Math.Min(free, srcBytes.Length);
            if (want <= 0) return;

            int headOffset = HEADER_BYTES + (int)w;
            int tail = Math.Min(want, RING_BYTES - (int)w);

            _view.WriteArray(headOffset, srcBytes[..tail].ToArray(), 0, tail);
            if (want > tail)
            {
                _view.WriteArray(HEADER_BYTES, srcBytes[tail..want].ToArray(), 0, want - tail);
            }

            w = (uint)((w + want) % RING_BYTES);
            _view.Write(0, w);
        }
        finally { _mtx.ReleaseMutex(); }
    }

    // Reader: fill dst with float32 interleaved from ring; returns samples (floats) read
    public static int ReadFloats(Span<float> dst)
    {
        if (!_inited || dst.IsEmpty) return 0;

        var dstBytes = MemoryMarshal.AsBytes(dst);
        int gotBytes = 0;

        _mtx.WaitOne();
        try
        {
            uint w = _view.ReadUInt32(0);
            uint r = _view.ReadUInt32(4);

            int avail = (int)((RING_BYTES + w - r) % RING_BYTES);
            if (avail <= 0) return 0;

            int want = Math.Min(avail, dstBytes.Length);
            int headOffset = HEADER_BYTES + (int)r;
            int tail = Math.Min(want, RING_BYTES - (int)r);

            // First segment
            var tmp = new byte[tail];
            _view.ReadArray(headOffset, tmp, 0, tail);
            tmp.CopyTo(dstBytes);

            // Wrapped segment
            if (want > tail)
            {
                int rest = want - tail;
                var tmp2 = new byte[rest];
                _view.ReadArray(HEADER_BYTES, tmp2, 0, rest);
                tmp2.CopyTo(dstBytes[tail..]);
            }

            r = (uint)((r + want) % RING_BYTES);
            _view.Write(4, r);
            gotBytes = want;
        }
        finally { _mtx.ReleaseMutex(); }

        return gotBytes / sizeof(float); // floats read
    }
}

// ===== Writer patch (Host process) =====
internal static class ShadowWriterPatch
{
    private static readonly AccessTools.FieldRef<CSCoreAudioOutputDriver, WasapiOut>
        _fOut = AccessTools.FieldRefAccess<CSCoreAudioOutputDriver, WasapiOut>("_out");
    
    private static bool _busInitialized = false;
    private static WasapiOut _currentAudioOutput = null;
    private static bool _isMuted = false;
    
    // Postfix for Read(float[], int, int)
    private static void Read_Float_Postfix(CSCoreAudioOutputDriver __instance, float[] buffer, int offset, int count, ref int __result)
    {
        try
        {
            // Check if audio sharing is enabled
            if (!AudioBridge.IsEnabled()) return;
            
            var outp = _fOut(__instance);
            var fmt = outp?.ActualOutputFormat;
            
            // Log format info once
            if (!_loggedFormat && fmt != null)
            {
                UniLog.Log($"[AudioBridge] Audio output detected: {fmt.SampleRate}Hz, {fmt.Channels}ch, {fmt.BitsPerSample}bit");
                _loggedFormat = true;
            }
            
            if (fmt == null || fmt.BitsPerSample != 32) return;

            // __result is BYTES read; convert to float count
            int floatsRead = Math.Max(0, __result / sizeof(float));
            if (floatsRead == 0) return;

            // Initialize shared memory only once
            if (!_busInitialized)
            {
                if (ShadowBus.EnsureInit(writer: true, sampleRate: fmt.SampleRate, channels: fmt.Channels))
                {
                    ShadowBus.PublishFormat(fmt.SampleRate, fmt.Channels);
                    _busInitialized = true;
                    UniLog.Log("[AudioBridge] Shared memory initialized for audio streaming");
                }
                else
                {
                    return;
                }
            }
            
            // Write audio data
            ShadowBus.WriteFloats(buffer.AsSpan(offset, floatsRead));
            
            // Log periodically
            _writeCounter++;
            if (_writeCounter % 1000 == 0)
            {
                UniLog.Log($"[AudioBridge] Processed {_writeCounter} audio chunks");
            }
        }
        catch (Exception ex) { AudioBridge.Err($"Audio float processing error: {ex.Message}"); }
    }
    
    // Postfix for Read(byte[], int, int)
    private static void Read_Byte_Postfix(CSCoreAudioOutputDriver __instance, byte[] buffer, int offset, int count, ref int __result)
    {
        try
        {
            // Check if audio sharing is enabled
            if (!AudioBridge.IsEnabled()) return;
            if (__result <= 0) return;
            
            var outp = _fOut(__instance);
            var fmt = outp?.ActualOutputFormat;
            
            if (fmt == null) return;
            
            // Log format info once
            if (!_loggedByte)
            {
                UniLog.Log($"[AudioBridge] Audio output detected (byte mode): {fmt.SampleRate}Hz, {fmt.Channels}ch, {fmt.BitsPerSample}bit");
                _loggedByte = true;
            }
            
            // Convert byte data to float based on format
            int bytesRead = __result;
            int sampleRate = fmt.SampleRate;
            int channels = fmt.Channels;
            int bitsPerSample = fmt.BitsPerSample;
            
            // Initialize shared memory only once
            if (!_busInitialized)
            {
                if (ShadowBus.EnsureInit(writer: true, sampleRate: sampleRate, channels: channels))
                {
                    ShadowBus.PublishFormat(sampleRate, channels);
                    _busInitialized = true;
                    UniLog.Log("[AudioBridge] Shared memory initialized for audio streaming");
                }
                else
                {
                    return;
                }
            }
            
            // Convert bytes to float based on bit depth
            if (bitsPerSample == 32)
            {
                // It's already float data in byte form
                var floatBuffer = new float[bytesRead / sizeof(float)];
                Buffer.BlockCopy(buffer, offset, floatBuffer, 0, bytesRead);
                ShadowBus.WriteFloats(floatBuffer.AsSpan());
                
                _writeCounter++;
                if (_writeCounter % 1000 == 0)
                {
                    UniLog.Log($"[AudioBridge] Processed {_writeCounter} audio chunks (32-bit float)");
                }
            }
            else if (bitsPerSample == 16)
            {
                // Convert 16-bit PCM to float
                int sampleCount = bytesRead / 2; // 2 bytes per sample
                var floatBuffer = new float[sampleCount];
                
                for (int i = 0; i < sampleCount; i++)
                {
                    short sample = BitConverter.ToInt16(buffer, offset + i * 2);
                    floatBuffer[i] = sample / 32768.0f; // Convert to -1.0 to 1.0 range
                }
                
                ShadowBus.WriteFloats(floatBuffer.AsSpan());
                
                _writeCounter++;
                if (_writeCounter % 1000 == 0)
                {
                    UniLog.Log($"[AudioBridge] Processed {_writeCounter} audio chunks (16-bit PCM)");
                }
            }
            else if (bitsPerSample == 24)
            {
                // Convert 24-bit PCM to float
                int sampleCount = bytesRead / 3; // 3 bytes per sample
                var floatBuffer = new float[sampleCount];
                
                for (int i = 0; i < sampleCount; i++)
                {
                    int sample = (buffer[offset + i * 3] << 8) | 
                                 (buffer[offset + i * 3 + 1] << 16) | 
                                 (buffer[offset + i * 3 + 2] << 24);
                    sample >>= 8; // Sign extend
                    floatBuffer[i] = sample / 8388608.0f; // Convert to -1.0 to 1.0 range
                }
                
                ShadowBus.WriteFloats(floatBuffer.AsSpan());
                
                _writeCounter++;
                if (_writeCounter % 1000 == 0)
                {
                    UniLog.Log($"[AudioBridge] Processed {_writeCounter} audio chunks (24-bit PCM)");
                }
            }
            else
            {
                // Unsupported format
                if (!_loggedUnsupported)
                {
                    UniLog.Log($"[AudioBridge] Unsupported audio format: {bitsPerSample}-bit");
                    _loggedUnsupported = true;
                }
            }
        }
        catch (Exception ex) { AudioBridge.Err($"Audio byte processing error: {ex.Message}"); }
    }
    
    private static bool _loggedUnsupported = false;
    
    // Postfix for ReadAuto(Span<byte>, WaveFormat)
    private static void ReadAuto_Span_Postfix(CSCoreAudioOutputDriver __instance, ref int __result)
    {
        try
        {
            
            // Log that ReadAuto was called
            if (!_loggedAuto)
            {
                UniLog.Log("[AudioBridge] Auto-read method detected");
                _loggedAuto = true;
            }
        }
        catch (Exception ex) { AudioBridge.Err($"Auto-read processing error: {ex.Message}"); }
    }
    
    private static bool _loggedByte = false;
    private static bool _loggedAuto = false;
    
    // Postfix for base class Start method
    private static void Start_Base_Postfix(AudioOutputDriver __instance, string context)
    {
        try
        {
            
            UniLog.Log($"[AudioBridge] Audio driver started with context: {context}");
            
            // Check if it's actually a CSCoreAudioOutputDriver
            if (__instance is CSCoreAudioOutputDriver csDriver)
            {
                var outp = _fOut(csDriver);
                if (outp != null)
                {
                    var fmt = outp.ActualOutputFormat;
                    if (fmt != null)
                    {
                        UniLog.Log($"[AudioBridge] Audio device format: {fmt.SampleRate}Hz, {fmt.Channels}ch, {fmt.BitsPerSample}bit");
                    }
                    
                    var device = outp.Device;
                    if (device != null)
                    {
                        UniLog.Log($"[AudioBridge] Audio device: {device.FriendlyName}");
                        
                        // Store reference and apply muting if needed
                        _currentAudioOutput = outp;
                        if (AudioBridge.IsEnabled() && AudioBridge.GetCurrentMuteTarget() == MuteTarget.Host)
                        {
                            UniLog.Log("[AudioBridge] Applying Host mute configuration on audio start");
                            ApplyMuteConfiguration(true);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            AudioBridge.Err($"Audio start error: {ex.Message}");
        }
    }

    private static bool _loggedFormat = false;
    private static int _writeCounter = 0;
    
    internal static bool TryApplyMuteConfiguration(bool shouldMute)
    {
        if (_currentAudioOutput == null || _currentAudioOutput.Device == null)
        {
            UniLog.Log("[AudioBridge] No audio device available to mute/unmute yet");
            return false;
        }
        
        ApplyMuteConfiguration(shouldMute);
        return true;
    }
    
    internal static void ApplyMuteConfiguration(bool shouldMute)
    {
        if (_currentAudioOutput == null || _currentAudioOutput.Device == null)
        {
            UniLog.Log("[AudioBridge] No audio device available to mute/unmute");
            return;
        }
        
        try
        {
            using var sessionManager = AudioSessionManager2.FromMMDevice(_currentAudioOutput.Device);
            using var sessionEnumerator = sessionManager.GetSessionEnumerator();
            var currentProcessId = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
            
            foreach (var session in sessionEnumerator)
            {
                using var sessionControl = session.QueryInterface<AudioSessionControl2>();
                if (sessionControl.ProcessID == currentProcessId)
                {
                    using var simpleVolume = session.QueryInterface<SimpleAudioVolume>();
                    simpleVolume.MasterVolume = shouldMute ? 0.0f : 1.0f;
                    _isMuted = shouldMute;
                    UniLog.Log($"[AudioBridge] Host audio {(shouldMute ? "muted" : "unmuted")} (audio still available for sharing)");
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            AudioBridge.Err($"Failed to {(shouldMute ? "mute" : "unmute")} host audio: {ex.Message}");
        }
    }
    
    internal static void ResetState()
    {
        UniLog.Log("[AudioBridge] Resetting audio writer state");
        _busInitialized = false;
        // Don't reset _currentAudioOutput - keep the reference to the audio device
        _isMuted = false;
        _loggedFormat = false;
        _loggedByte = false;
        _loggedAuto = false;
        _writeCounter = 0;
    }
}

