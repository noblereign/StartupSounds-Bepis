# StartupSounds
[![Thunderstore Badge](https://modding.resonite.net/assets/available-on-thunderstore.svg)](https://thunderstore.io/c/resonite/)

A [Resonite](https://resonite.com/) mod that enhances your Resonite startup experience with custom sounds! Plays different sounds during launch, loading, and startup phases.

## Features

- **Launch Sound**: Plays when the game first starts
- **Loading Sound**: Smooth crossfade from launch to loading sound
- **Phase Sounds**: Random sounds during engine initialization phases (first 40 phases)
- **Done Sound**: Plays when loading is complete
- **Smart Sound Management**:
  - Smooth crossfading between sounds
  - Cooldown system to prevent sound spam
  - Automatic cleanup of sound resources

## Supported Audio Formats
- WAV
- FLAC
- OGG
- MP3

## Installation (Manual)
1. Install [BepisLoader](https://github.com/ResoniteModding/BepisLoader) for Resonite.
2. Download the latest release ZIP file (e.g., `Noble-StartupSounds-1.0.0.zip`) from the [Releases](https://github.com/Noble/StartupSounds/releases) page.
3. Extract the ZIP and copy the `patchers` folder to your BepInEx folder in your Resonite installation directory:
   - **Default location:** `C:\Program Files (x86)\Steam\steamapps\common\Resonite\BepInEx\`
4. The mod will create the following sound folders, where you can add your audio files:

```
Resonite/BepInEx/patchers/StartupSounds/StartupSounds
├── launch/   # First sound when starting Resonite
├── loading/  # Background music during the loading process
├── phase/    # Short sounds during initialization (for every phase until 40)
└── done/     # Final sound when Resonite is ready
```

5. Start the game. If you want to verify that the mod is working you can check your BepInEx logs.

## Credits
- [Original mod](https://github.com/dfgHiatus/StartupSounds) by dfgHiatus
- [Ported to MonkeyLoader](https://github.com/DexyThePuppy/StartupSounds-Monkey) by Dexy
- Ported to BepisLoader by Noble
- Uses [CSCore](https://github.com/filoe/cscore) for audio playback and crossfading