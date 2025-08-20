# AudioBridge

A Resonite mod that bridges audio from the Host process to the Renderer process, enabling audio capture for streaming and recording software like OBS / Discord.

## What it does

Resonite runs as two separate processes - the Host (main game) and the Renderer (graphics). By default, audio only plays through the Host process, which makes it invisible to most streaming/recording software that captures from specific application windows.

ResoniteAudioBridge solves this by:
- Sharing audio data from the Host to the Renderer process via shared memory
- Playing the same audio through both processes simultaneously
- Allowing you to mute either process to avoid hearing double audio
- Maintaining perfect synchronization between both audio streams

## Features

- **Real-time audio sharing** with minimal latency (20ms)
- **Configurable muting** - choose which process to mute (Host, Renderer, or neither)
- **Toggle on/off** without restarting the game
- **Automatic synchronization** - no audio delay when toggling
- Supports all common audio formats (16/24/32-bit, various sample rates)

## Installation

1. Download `AudioBridge.dll` and `AudioBridgeRenderer.dll` from the [Releases](https://github.com/knackrack615/AudioBridge/releases) page

2. Drop `AudioBridge.dll` in `Resonite\rml_mods`

3. Download & Extract [BepInEx](https://github.com/BepInEx/BepInEx/releases/download/v5.4.23.3/BepInEx_win_x64_5.4.23.3.zip) into `Resonite\Renderer`

4. Drop `AudioBridgeRenderer.dll` & `CSCore.dll` at `Resonite\Renderer\BepInEx\plugins`  
   (If the folder doesn't exist, create it)  
   (CSCore.dll can be found in the root of Resonite)

5. Done! Launch the game, audio should be coming out of both the Renderer & Host

## Configuration

You can configure the mod through Resonite's RML settings menu:

- **Enable audio sharing**: Toggle the audio bridge on/off
- **Mute target**: Choose which process to mute
  - `Host` - Mutes the Host process (game audio only plays through Renderer)
  - `Renderer` - Mutes the Renderer process (default, avoids double audio)
  - `None` - No muting (you'll hear audio from both processes)

## How it works

The mod uses:
- **Harmony** patches to intercept audio data from Resonite's audio driver
- **Memory-mapped files** for high-performance inter-process communication
- **Ring buffer** implementation for lock-free audio streaming
- **CSCore** audio library for playback in the Renderer process

## Troubleshooting

### No audio in Renderer
- Make sure BepInEx is properly installed in the Renderer folder
- Check that all DLLs are in the correct locations
- Verify the mod is enabled in RML settings

### Audio delay or stuttering
- The mod automatically syncs audio when toggling - if you experience delays, try toggling it off and on again
- Check your system's audio latency settings
- Ensure no other software is interfering with audio processing

### Double audio
- Change the "Mute target" setting in RML to either `Host` or `Renderer`
- The default is `Renderer` which should prevent double audio

## Requirements

- Resonite with ResoniteModLoader
- BepInEx 5.4.23.3 or compatible version
- Windows (uses Windows-specific memory-mapped file APIs)

### Performance
- Shared memory buffer: 2MB ring buffer
- Latency: 20ms (configurable in WasapiOut initialization)
- Memory-mapped file size: ~2MB + 64 bytes header

## Credits

- **Author**: Knackrack615
- **Version**: 1.0.0
- **License**: MIT

## Links

- [GitHub Repository](https://github.com/knackrack615/AudioBridge/)
- [Issues & Bug Reports](https://github.com/knackrack615/AudioBridge/issues)
