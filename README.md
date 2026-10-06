# OpenShiina

An unofficial, open-source player for visual novels made with the ShiinaRio (椎名里緒) engine.
It reads the game's own files: its archives, its flow script `SRC_MAIN.SCN` and its scenario
files, and shows the story with the engine's effects, text, saves and settings.

**Status:** early. The first target is the eleven GRAND†CROSS "Plus" games; **Oreimo Plus** plays
from beginning to end with its title screen, choices, saves, OPTION page and backlog. The
others cannot be played yet.

You need your own installed copy of the game. OpenShiina contains no game data.

## Platforms

| Project | Platform |
|---|---|
| `src/OpenShiina.Core` | The engine, no user interface (`net10.0`): archives and decryption, image and sound decoders, the SCN interpreter, the story engine, the audio mixer |
| `src/OpenShiina.Windows` | Windows player (WPF) |
| Android, iOS, macOS | Planned with .NET MAUI |
| Linux | Later |

## Building

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```
dotnet build -c Release
```

The Windows player is written to `bin/Release/OpenShiina.Windows/`, with the scheme data
(`Formats.Json` and `ShiinaImage/`) next to `OpenShiina.exe`.

## Usage

Run `OpenShiina.exe` and choose the game's folder (the one with its `.exe` and `.WAR` files), or
pass the folder on the command line. The game is recognised by its `.exe`. The player runs the
game's own SCN scripts, so the game looks and behaves as it does in its own engine.

`OpenShiina.exe --story [folder]` starts the older player instead: it plays the story with the
story engine and its own copies of the game's screens (Oreimo Plus only).

Saves and settings are kept in `%AppData%\OpenShiina`.

## Documentation

- [`docs/engine-notes.md`](docs/engine-notes.md): what is known about the engine's archives,
  images, scenario commands, SCN bytecode and screens.
- `tools/ScnTools`: disassembler for SCN bytecode.
- `tests/OpenShiina.Harness`: runs the Windows player off screen and muted, clicks through the
  story and takes screenshots.

## Credits

- Archive formats and encryption schemes are based on [GARbro](https://github.com/morkt/GARbro) by morkt.
- Started from [GrandCrossExtractor](https://github.com/kagaminehaku/GrandCrossExtractor).

## License

MIT; see [LICENSE](LICENSE).

## Disclaimer

OpenShiina is not affiliated with, endorsed or sponsored by the developers of the ShiinaRio
engine or by any game publisher. All trademarks belong to their owners. Use it only with games
you own.
