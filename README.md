# Tarkov Music Pause

Automatically pauses your music when you load into an Escape from Tarkov raid, and resumes it when you return to the menu.

## How it works

It watches your EFT log files for raid events (`GameStarted`, `GameStarting`, etc.) and sends the system media Play/Pause key — the same key as a multimedia keyboard. No game files are touched or modified.

## Ban safety

This tool is **read-only** — it only reads log files that EFT writes itself. It does not interact with the game process, inject code, or modify any game files. It should be safe to use, but use at your own risk.

## Download

👉 **[Download the latest release](../../releases/latest)**

No Python required — just download and run `TarkovMusicPause.exe`.

## Usage

1. Run `TarkovMusicPause.exe`
2. Set your EFT logs folder (default: `C:\Games\Logs`) — change this if EFT is installed elsewhere
3. Click **Start**
4. Minimise to tray with the **Minimise to tray** button — it keeps running in the background

## Manual build

Requires Python 3.10+:

```
pip install -r requirements-build.txt
powershell -File build_exe.ps1
```
