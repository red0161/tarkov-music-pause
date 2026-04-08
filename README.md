# Tarkov Music Pause

Watches EFT log files for raid events (`GameStarted`, `GameStarting`, etc.) and sends the system media Play/Pause key to pause your music. Sends it again when you return to the menu.

Read-only — only reads logs that EFT writes itself, does not touch the game.

👉 **[Download latest release](../../releases/latest)** (~30KB)

## Run on Windows startup

1. Press `Win + R`, type `shell:startup`, press Enter
2. Copy a shortcut to `TarkovMusicPause.exe` into that folder

It will launch minimised to the tray automatically when you log in.

## Preferences

Settings are saved in `%AppData%\TarkovMusicPause\` (e.g. `C:\Users\<you>\AppData\Roaming\TarkovMusicPause\`). Delete this folder to reset everything to defaults.

## Building from source

Requires [.NET SDK 6+](https://dotnet.microsoft.com/download) and .NET Framework 4.8 (pre-installed on Windows 10/11).

```
dotnet build -c Release
```

Output is at `bin\Release\net48\TarkovMusicPause.exe`.
