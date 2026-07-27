using BepInEx;
using BepInEx.Logging;
using BepInEx.Preloader.Core.Patching;
using CSCore;
using CSCore.Codecs;
using CSCore.CoreAudioAPI;
using CSCore.DSP;
using CSCore.SoundOut;
using CSCore.Streams;
using FrooxEngine;
using System.Reflection;

namespace StartupSounds;

[PatcherPluginInfo(PluginMetadata.GUID, PluginMetadata.NAME, PluginMetadata.VERSION)]
[BepInDependency(BepInExResoniteShim.PluginMetadata.GUID, BepInDependency.DependencyFlags.HardDependency)]
public class Plugin : BasePatcher
{
    internal static new ManualLogSource Log = null!;

    private const int FADE_TIME = 1000;
    private const int PHASE_SOUND_COOLDOWN = 300;
    private const int POLLING_INTERVAL = 20;

    private static string baseSoundsPath = null!;
    private static readonly string[] soundFolders = ["done", "launch", "loading", "phase"];
    private static readonly string[] supportedExtensions = ["*.wav", "*.flac", "*.ogg", "*.mp3"];
    private static readonly Random random = new();

    private static ISoundOut? currentPlayer;
    private static ISoundOut? loadingPlayer;
    private static IWaveSource? currentSource;
    private static IWaveSource? loadingSource;
    private static readonly HashSet<ISoundOut> activeSounds = new();
    private static readonly MMDeviceEnumerator globalEnumerator = new();
    private static volatile bool shouldContinueMonitoring = true;
    private static volatile bool phaseWatcherStarted;
    private static DateTime lastPhaseSoundTime = DateTime.MinValue;
    private static int lastFixedPhaseIndex = -1;

    public override void Initialize()
    {
        Log = base.Log;
        baseSoundsPath = Path.Combine(Path.GetDirectoryName(typeof(Plugin).Assembly.Location)!, "StartupSounds");

        Log.LogInfo($"Plugin {PluginMetadata.GUID} is loaded!");

        try
        {
            Log.LogInfo("StartupSounds prepatcher loaded - starting audio and engine monitoring");
            CreateSoundFolders();
            _ = Task.Run(EngineStateSounds);
            StartEngineMonitoring();
        }
        catch (Exception e)
        {
            Log.LogError($"Error in StartupSounds Initialize: {e}");
        }
    }

    private static void CreateSoundFolders()
    {
        Log.LogInfo($"Creating sound folders at: {baseSoundsPath}");
        Directory.CreateDirectory(baseSoundsPath);
        foreach (var folder in soundFolders)
            Directory.CreateDirectory(Path.Combine(baseSoundsPath, folder));
    }

    private static void SetupEngineReadyHandler()
    {
        Log.LogInfo("Setting up engine ready handler...");
        try
        {
            Log.LogInfo("Waiting for Engine.Current to exist...");
            
            while (Engine.Current is null)
                Thread.Sleep(100);

            Log.LogInfo("Engine.Current found, starting AutoReadyAfterUpdates monitoring");

            _ = Task.Run(() =>
            {
                var progressProperty = typeof(Engine).GetProperty("InitProgress", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var progressField = typeof(Engine).GetField("<InitProgress>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance);

                while (shouldContinueMonitoring)
                {
                    try
                    {
                        if (Engine.Current is { AutoReadyAfterUpdates: <= 0 })
                        {
                            Log.LogInfo("AutoReadyAfterUpdates countdown finished - engine is truly ready!");
                            StopLoadingAndPlayDoneStatic();
                            shouldContinueMonitoring = false;
                            break;
                        }
                        
                        var rawProgress = progressProperty?.GetValue(Engine.Current) ?? progressField?.GetValue(Engine.Current);
                        if (rawProgress is IEngineInitProgress currentProgress)
                        {
                            var currentFixedPhaseIndex = currentProgress.FixedPhaseIndex;

                            if (currentFixedPhaseIndex != lastFixedPhaseIndex && 
                                currentFixedPhaseIndex >= 0 && 
                                currentFixedPhaseIndex <= 40)
                            {
                                var timeSinceLastSound = (DateTime.Now - lastPhaseSoundTime).TotalMilliseconds;
                                if (timeSinceLastSound >= PHASE_SOUND_COOLDOWN)
                                {
                                    try
                                    {
                                        string soundFile = GetRandomAudioFile(Path.Combine(baseSoundsPath, "phase"));
                                        Log.LogDebug($"Engine phase changed to {currentFixedPhaseIndex} - Playing phase sound.");
                                        _ = PlaySound(soundFile, 1.0f, false, false);
                                        lastPhaseSoundTime = DateTime.Now;
                                    }
                                    catch (FileNotFoundException) { }
                                }
                                lastFixedPhaseIndex = currentFixedPhaseIndex;
                            }
                        }
                        
                        Thread.Sleep(POLLING_INTERVAL);
                    }
                    catch (Exception ex)
                    {
                        Log.LogError($"Error monitoring AutoReadyAfterUpdates: {ex}");
                        Thread.Sleep(1000);
                    }
                }
            });
            
            Log.LogInfo("Successfully started AutoReadyAfterUpdates monitoring");
        }
        catch (Exception ex)
        {
            Log.LogError($"Error in engine ready handler: {ex}");
        }
    }

    public static void StartLoadingSoundsManually()
    {
        Log.LogInfo("Manually starting loading sounds with fade-in");
        _ = Task.Run(async () =>
        {
            try
            {
                await PlayLoadingSoundWithFadeIn();
            }
            catch (Exception ex)
            {
                Log.LogError($"Error manually starting loading sounds: {ex}");
            }
        });
    }
    
    public static bool IsWindowFocused()
    {
        try
        {
            return Engine.Current?.InputInterface?.IsWindowFocused ?? false;
        }
        catch (Exception ex)
        {
            Log.LogDebug($"Error checking window focus: {ex.Message}");
            return false;
        }
    }

    public static void StartEngineMonitoring()
    {
        if (phaseWatcherStarted)
        {
            Log.LogInfo("Engine monitoring already running - skipping start");
            return;
        }
        phaseWatcherStarted = true;

        Log.LogInfo("Starting engine monitoring in background...");
        _ = Task.Run(() =>
        {
            try
            {
                Log.LogInfo("About to call SetupEngineReadyHandler (bg)...");
                SetupEngineReadyHandler();
                Log.LogInfo("SetupEngineReadyHandler completed (bg)");
            }
            catch (Exception ex)
            {
                Log.LogError($"Engine monitoring failed: {ex}");
            }
        });
    }

    private static ISoundOut? PlaySound(string soundPath, float initialVolume = 1.0f, bool loop = false, bool track = true)
    {
        try
        {
            IWaveSource waveSource = CodecFactory.Instance.GetCodec(soundPath);

            if (loop)
                waveSource = new LoopStream(waveSource) { EnableLoop = true };

            int targetSampleRate = 48000;
            int targetChannels = 2;

            try
            {
                using var device = globalEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);
                targetSampleRate = device.DeviceFormat.SampleRate;
                targetChannels = device.DeviceFormat.Channels;
            }
            catch (Exception ex)
            {
                Log.LogWarning($"Could not fetch device format, falling back to 48kHz Stereo: {ex.Message}");
            }

            if (waveSource.WaveFormat.SampleRate != targetSampleRate)
            {
                waveSource = new DmoResampler(waveSource, new WaveFormat(targetSampleRate, waveSource.WaveFormat.BitsPerSample, waveSource.WaveFormat.Channels));
            }

            if (waveSource.WaveFormat.Channels == 1 && targetChannels >= 2)
            {
                waveSource = waveSource.ToSampleSource().ToStereo().ToWaveSource();
            }

            WasapiOut soundOut = new();
            soundOut.Initialize(waveSource);
            soundOut.Volume = initialVolume;

            lock (activeSounds) { activeSounds.Add(soundOut); }

            soundOut.Stopped += (s, e) =>
            {
                _ = Task.Run(async () =>
                {
                    await Task.Delay(100);

                    try { soundOut.Dispose(); } catch { }
                    try { waveSource.Dispose(); } catch { }

                    lock (activeSounds) { activeSounds.Remove(soundOut); }
                });
            };

            soundOut.Play();

            if (track)
            {
                currentSource = waveSource;
                currentPlayer = soundOut;
            }

            return soundOut;
        }
        catch (Exception ex)
        {
            Log.LogError($"Failed to play sound {soundPath}: {ex.Message}");
            return null;
        }
    }

    private static async Task EngineStateSounds()
    {
        try
        {
            Log.LogInfo("Waiting for renderer...");
            // wait until renderer is at least StartingUp
            
            while (Engine.Current?.RenderSystem?.State is null or RendererState.NotInitialized)
            {
                if (Userspace.Current is not null) return;
                await Task.Delay(50);
            }

            try
            {
                var launchFolder = Path.Combine(baseSoundsPath, "launch");
                var soundFile = GetRandomAudioFile(launchFolder);
                Log.LogInfo($"Playing launch sound: {soundFile}");
                _ = PlaySound(soundFile);
            }
            catch (FileNotFoundException)
            {
                Log.LogInfo("No launch sounds found, skipping to loading...");
            }
            catch (Exception ex)
            {
                Log.LogError($"Error playing launch sound: {ex}");
            }

            await Task.Delay(500);

            if (Userspace.Current is null)
            {
                Log.LogInfo("Starting loading sounds fade-in");
                await PlayLoadingSoundWithFadeIn();
            }
        }
        catch (Exception ex)
        {
            Log.LogError($"Error in master audio sequence: {ex}");
        }
    }

    private static async Task PlayLoadingSoundWithFadeIn()
    {
        try
        {
            var loadingFolder = Path.Combine(baseSoundsPath, "loading");
            var soundFile = GetRandomAudioFile(loadingFolder);
            
            Log.LogInfo($"Playing loading sound with fade-in: {soundFile}");
            
            var newLoadingPlayer = PlaySound(soundFile, 0.0f, true);
            if (newLoadingPlayer is null) return;
            
            loadingPlayer = newLoadingPlayer;
            loadingSource = currentSource;
            
            await FadeInLoadingSound(loadingPlayer);
        }
        catch (Exception ex)
        {
            Log.LogError($"Error playing loading sound with fade-in: {ex}");
        }
    }

    private static async Task FadeInLoadingSound(ISoundOut loadingPlayer)
    {
        try
        {
            const int fadeSteps = 20;
            const int stepDelay = FADE_TIME / fadeSteps;
            
            Log.LogInfo("Starting fade-in for loading sound");
            
            for (var i = 0; i <= fadeSteps; i++)
            {
                var progress = i / (float)fadeSteps;
                
                try
                {
                    if (loadingPlayer is { PlaybackState: not PlaybackState.Stopped })
                        loadingPlayer.Volume = progress;
                }
                catch (ObjectDisposedException) 
                { 
                    break;
                }
                
                await Task.Delay(stepDelay);
            }
            
            Log.LogInfo("Loading sound fade-in completed");
        }
        catch (Exception ex)
        {
            Log.LogError($"Error during loading sound fade-in: {ex}");
        }
    }

    private static void StopLoadingAndPlayDoneStatic()
    {
        try
        {
            Log.LogInfo("Engine ready - crossfading from loading to done sound");
            
            _ = Task.Run(async () =>
            {
                try
                {
                    await CrossfadeToNewSoundStatic("done");
                }
                catch (Exception ex)
                {
                    Log.LogError($"Error crossfading to done sound: {ex}");
                }
            });
        }
        catch (Exception ex)
        {
            Log.LogError($"Error in StopLoadingAndPlayDoneStatic: {ex}");
        }
    }

    private static async Task CrossfadeToNewSoundStatic(string folder)
    {
        try
        {
            var soundFile = GetRandomAudioFile(Path.Combine(baseSoundsPath, folder));
            if (soundFile is null)
            {
                Log.LogError($"No sound files found in folder: {folder}");
                return;
            }

            var oldPlayer = loadingPlayer ?? currentPlayer;
            var oldSource = loadingSource ?? currentSource;
            
            var newPlayer = PlaySound(soundFile, 0.0f, folder != "done");
            if (newPlayer is null) return;

            await PerformCrossfade(oldPlayer, newPlayer);

            currentPlayer = newPlayer;
            currentSource = newPlayer.WaveSource;
            
            loadingPlayer = null;
            loadingSource = null;
        }
        catch (Exception ex)
        {
            Log.LogError($"Error transitioning to new sound: {ex}");
        }
    }

    private static async Task PerformCrossfade(ISoundOut? oldPlayer, ISoundOut? newPlayer)
    {
        const int steps = 20;
        const int stepDelay = FADE_TIME / steps;

        for (var i = 0; i <= steps; i++)
        {
            var progress = i / (float)steps;
            
            try
            {
                if (oldPlayer is { PlaybackState: not PlaybackState.Stopped })
                    oldPlayer.Volume = Math.Max(0, 1.0f - progress);
            }
            catch (ObjectDisposedException) { }
            
            try
            {
                if (newPlayer is { PlaybackState: not PlaybackState.Stopped })
                    newPlayer.Volume = Math.Min(1.0f, progress);
            }
            catch (ObjectDisposedException) { }
            
            await Task.Delay(stepDelay);
        }

        try
        {
            if (oldPlayer is not null)
            {
                oldPlayer.Stop();
            }
        }
        catch (Exception) { }
    }

    private static string GetRandomAudioFile(string dir)
    {
        ArgumentException.ThrowIfNullOrEmpty(dir);

        var files = supportedExtensions
            .SelectMany(ext => Directory.GetFiles(dir, ext, SearchOption.AllDirectories))
            .ToArray();

        return files.Length == 0 
            ? throw new FileNotFoundException($"No audio files were found in the directory: {dir}")
            : files[random.Next(files.Length)];
    }
}
