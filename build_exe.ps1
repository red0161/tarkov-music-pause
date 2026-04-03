# Build a single-file GUI exe (no console). Requires Python on PATH.
Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

python -m pip install -r requirements-build.txt
python -m PyInstaller `
  --noconsole `
  --onefile `
  --name TarkovMusicPause `
  --clean `
  app_gui.py

Write-Host "Output: dist\TarkovMusicPause.exe" -ForegroundColor Green
