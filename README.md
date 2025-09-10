# ResoniteAudioBridge
[![Thunderstore Badge](https://gist.githubusercontent.com/art0007i/c4871bbdb30d31e7899328754916bb81/raw/076910e4939e624f17c88bd879770d3bd2fe3f1e/available-on-thunderstore.svg)](https://thunderstore.io/c/resonite/)

A Resonite mod that bridges audio from the Host process to the Renderer process, enabling audio capture for streaming and recording software like OBS/Discord.

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

1. Download `HGCommunity-AudioBridge-2.0.0.zip` from the [Releases](https://github.com/knackrack615/ResoniteAudioBridge/releases) page

2. Download & Extract [BepInEx](https://github.com/BepInEx/BepInEx/releases/download/v5.4.23.3/BepInEx_win_x64_5.4.23.3.zip) into `Resonite\Renderer`

3. Extract the contents of `HGCommunity-AudioBridge-2.0.0.zip` to their respective directories.

4. Done! Launch the game, audio should be coming out of the Renderer process (by default, configurable)

## Configuration

You can configure the mod through the BepisModSettings menu:

- **Enable audio sharing**: Toggle the audio bridge on/off
- **Mute target**: Choose which process to mute
  - `None` - No muting (you'll hear audio from both processes)
  - `Host` - Mutes the Host process (game audio only plays through Renderer)
  - `Renderer` - Mutes the Renderer process (default, avoids double audio)
**Debug Logging**: Prints debugging information to the Resonite logs to help diagnose issues.

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
- Verify the mod is enabled in BepisModSettings

### Audio delay or stuttering
- The mod automatically syncs audio when toggling - if you experience delays, try toggling it off and on again
- Check your system's audio latency settings
- Ensure no other software is interfering with audio processing

### Double audio
- Change the "Mute target" setting in BepisModSettings to either `Host` or `Renderer`
- The default is `Renderer` which should prevent double audio

## Requirements

- Resonite with ResoniteModLoader
- BepInEx 5.4.23.3 or compatible version
- Windows (uses Windows-specific memory-mapped file APIs)
- .NET Framework 4.7.2 (for Renderer plugin)
- .NET 9.0 (for Host mod)

## Technical Details

### Audio Formats Supported
- 16-bit PCM
- 24-bit PCM  
- 32-bit float (IeeeFloat)
- All standard sample rates (44.1kHz, 48kHz, etc.)
- Mono, stereo, and multi-channel configurations

### Performance
- Shared memory buffer: 2MB ring buffer
- Latency: 20ms (configurable in WasapiOut initialization)
- Memory-mapped file size: ~2MB + 64 bytes header

## License

SPDX-License-Identifier: AGPL-3.0-or-later WITH LICENSE-ATTRIBUTION

This project is licensed under the [GNU Affero General Public License v3.0](LICENSE) with [attribution term](LICENSE-ATTRIBUTION).

## Credits

- **Author**: Knackrack615
- **Version**: 2.0.0
- **License**: MIT

## Links

- [GitHub Repository](https://github.com/knackrack615/ResoniteAudioBridge/)
- [Issues & Bug Reports](https://github.com/knackrack615/ResoniteAudioBridge/issues)