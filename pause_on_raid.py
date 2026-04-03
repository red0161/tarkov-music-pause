"""
Watch Escape from Tarkov logs and press the global media Play/Pause key when a
raid goes live (GameStarted / GameStarting / PlayerSpawnEvent in application_*.log;
Scav and PMC use the same kind of lines), and again when you return to menu after that raid.

Resume detection (either is enough; first line wins):
  - output_*.log: Reason:Stop game and/or network-connection "Disconnected" state
  - |Info|application|SelectProfile in application_*.log
  - HTTPS line containing client/game/profile/select in backend_*.log or
    network-connection*.log (EFT may duplicate the same gw-pvp lines there)

Cold boot runs SelectProfile and profile/select before any raid-live line; we only
resume when we had paused for the current raid. Reason:Stop game only appears
when leaving a server session, not at startup.

Default log root: C:\\Games\\Logs
Override:  set TARKOV_LOGS or pass --logs-dir
"""
from __future__ import annotations

import argparse
import os
import re
import sys
import threading
import time
from collections.abc import Callable
from pathlib import Path

# Windows virtual-key: media play/pause
VK_MEDIA_PLAY_PAUSE = 0xB3
KEYEVENTF_KEYUP = 0x0002
KEYEVENTF_EXTENDEDKEY = 0x0001
INPUT_KEYBOARD = 1

if sys.platform == "win32":
    import ctypes
    from ctypes import wintypes

    class _MOUSEINPUT(ctypes.Structure):
        _fields_ = (
            ("dx", wintypes.LONG),
            ("dy", wintypes.LONG),
            ("mouseData", wintypes.DWORD),
            ("dwFlags", wintypes.DWORD),
            ("time", wintypes.DWORD),
            ("dwExtraInfo", ctypes.c_void_p),
        )

    class _KEYBDINPUT(ctypes.Structure):
        _fields_ = (
            ("wVk", wintypes.WORD),
            ("wScan", wintypes.WORD),
            ("dwFlags", wintypes.DWORD),
            ("time", wintypes.DWORD),
            ("dwExtraInfo", ctypes.c_void_p),
        )

    class _HARDWAREINPUT(ctypes.Structure):
        _fields_ = (
            ("uMsg", wintypes.DWORD),
            ("wParamL", wintypes.WORD),
            ("wParamH", wintypes.WORD),
        )

    class _INPUTUNION(ctypes.Union):
        _fields_ = (("mi", _MOUSEINPUT), ("ki", _KEYBDINPUT), ("hi", _HARDWAREINPUT))

    class _INPUT(ctypes.Structure):
        _fields_ = (("type", wintypes.DWORD), ("unn", _INPUTUNION))

# In-raid world (PMC or Scav). Earlier lines = sooner pause; only first match sends pause.
RAID_WORLD_LIVE_RE = re.compile(
    r"\|Info\|application\|(?:Game(?:Started|Starting|Spawned|Runned)|PlayerSpawnEvent):"
)
# After a full raid, back-to-menu (log sample: after big GC, before next BE load).
SELECT_PROFILE_RE = re.compile(r"\|Info\|application\|SelectProfile\s")
# backend_*.log / network-connection*.log: HTTPS URL for profile re-select after raid.
BACKEND_PROFILE_SELECT_RE = re.compile(r"client/game/profile/select")
# output_*.log: leaving raid session (death / extract / disconnect)
OUTPUT_STOP_GAME_RE = re.compile(r"Reason:Stop game")
OUTPUT_DISCONNECTED_RE = re.compile(
    r"\|Info\|output\|network-connection\|Enter to the 'Disconnected' state"
)
DEFAULT_LOG_ROOT = Path(r"C:\Games\Logs")


def _send_input_key(vk: int, flags: int) -> int:
    """Return SendInput count (1 expected). Win32 only."""
    import ctypes

    inp = _INPUT()
    inp.type = INPUT_KEYBOARD
    inp.unn.ki = _KEYBDINPUT(vk, 0, flags, 0, ctypes.c_void_p(0))
    return int(ctypes.windll.user32.SendInput(1, ctypes.byref(inp), ctypes.sizeof(_INPUT)))


def send_media_play_pause() -> None:
    """Send system media Play/Pause (SendInput + extended key; fallback keybd_event)."""
    if sys.platform != "win32":
        raise SystemExit("This script only supports Windows (media keys).")
    import ctypes

    u32 = ctypes.windll.user32
    vk = VK_MEDIA_PLAY_PAUSE
    scan = u32.MapVirtualKeyW(vk, 0) & 0xFF
    if _send_input_key(vk, KEYEVENTF_EXTENDEDKEY) != 1:
        u32.keybd_event(vk, scan, KEYEVENTF_EXTENDEDKEY, 0)
    time.sleep(0.02)
    if _send_input_key(vk, KEYEVENTF_EXTENDEDKEY | KEYEVENTF_KEYUP) != 1:
        u32.keybd_event(vk, scan, KEYEVENTF_EXTENDEDKEY | KEYEVENTF_KEYUP, 0)
    time.sleep(0.02)


def _session_log_paths(session_dir: Path) -> tuple[Path, ...] | None:
    app = sorted(session_dir.glob("*application*.log"))
    if not app:
        return None
    out = sorted(session_dir.glob("*output*.log"))
    extra = sorted(session_dir.glob("*backend*.log"))
    net = sorted(session_dir.glob("*network-connection*.log"))
    return tuple(sorted(set(app + out + extra + net)))


def latest_session_watch_files(log_root: Path) -> tuple[Path, tuple[Path, ...]] | None:
    """Resolve log files: log_root may be C:\\Games\\Logs or a single log_* session folder.

    When log_root is the parent of many log_* dirs, pick the one whose application*.log
    was written most recently (folder mtime alone can point at the wrong session).
    """
    if not log_root.is_dir():
        return None
    direct = _session_log_paths(log_root)
    if direct is not None:
        return (log_root.resolve(), direct)
    session_dirs = [p for p in log_root.iterdir() if p.is_dir()]
    if not session_dirs:
        return None
    best: Path | None = None
    best_age = -1.0
    for session in session_dirs:
        paths = _session_log_paths(session)
        if paths is None:
            continue
        app_files = [x for x in paths if "application" in x.name.lower()]
        if not app_files:
            continue
        newest = max(x.stat().st_mtime for x in app_files)
        if newest > best_age:
            best_age = newest
            best = session
    if best is None:
        return None
    again = _session_log_paths(best)
    if again is None:
        return None
    return (best.resolve(), again)


def _process_line(
    line: str,
    *,
    resume_after_raid: bool,
    dry_run: bool,
    in_raid_audio_paused: list[bool],
    emit: Callable[[str], None],
    send_media: Callable[[], None],
) -> None:
    """Mutates in_raid_audio_paused[0] across calls."""
    flag = in_raid_audio_paused[0]
    if RAID_WORLD_LIVE_RE.search(line):
        if flag:
            return
        in_raid_audio_paused[0] = True
        msg = (
            f"Raid live -> pause (media key): {line[:80]}…"
            if len(line) > 80
            else f"Raid live -> pause (media key): {line}"
        )
        emit(msg)
        if not dry_run:
            send_media()
        return
    if not resume_after_raid or not in_raid_audio_paused[0]:
        return
    if OUTPUT_STOP_GAME_RE.search(line):
        label = "Raid session ended (Stop game)"
    elif OUTPUT_DISCONNECTED_RE.search(line):
        label = "Raid session ended (Disconnected)"
    elif SELECT_PROFILE_RE.search(line) or BACKEND_PROFILE_SELECT_RE.search(line):
        label = "Back to menu"
    else:
        return
    in_raid_audio_paused[0] = False
    msg = (
        f"{label} -> resume (media key): {line[:80]}…"
        if len(line) > 80
        else f"{label} -> resume (media key): {line}"
    )
    emit(msg)
    if not dry_run:
        send_media()


def _sleep_or_stop(poll_interval: float, stop_event: threading.Event | None) -> bool:
    """Sleep for poll_interval; return True if stop_event was set."""
    if stop_event is None:
        time.sleep(poll_interval)
        return False
    return stop_event.wait(timeout=poll_interval)


def tail_loop(
    log_root: Path,
    poll_interval: float,
    dry_run: bool,
    resume_after_raid: bool,
    *,
    stop_event: threading.Event | None = None,
    line_sink: Callable[[str], None] | None = None,
    send_media: Callable[[], None] | None = None,
) -> None:
    emit: Callable[[str], None] = line_sink or (lambda m: print(m, flush=True))
    send_media_fn: Callable[[], None] = send_media or send_media_play_pause
    active_session: Path | None = None
    # Per log file: byte offset already read from disk; reopen each poll so Windows sees new writes.
    file_pos: dict[Path, int] = {}
    partial_line: dict[Path, bytes] = {}
    in_raid_audio_paused = [False]
    waiting_logged = False

    emit(f"Log root: {log_root}")
    emit(f"  Exists: {log_root.is_dir()}")
    try:
        subdirs = sorted([p for p in log_root.iterdir() if p.is_dir()], key=lambda p: p.stat().st_mtime, reverse=True)
        emit(f"  Subdirs found: {len(subdirs)}, newest: {subdirs[0].name if subdirs else '(none)'}")
        if subdirs:
            try:
                files_in_newest = sorted(f.name for f in subdirs[0].iterdir() if f.is_file())
                emit(f"  Files in newest session: {files_in_newest or '(empty)'}")
            except Exception as exc2:
                emit(f"  Cannot list files in session: {exc2}")
    except Exception as exc:
        emit(f"  Cannot list subdirs: {exc}")

    def _tail_from_eof(p: Path, label: str) -> None:
        try:
            file_pos[p] = p.stat().st_size
        except OSError:
            file_pos[p] = 0
        partial_line[p] = b""
        emit(f"  {label} {p.name}")

    while True:
        if stop_event is not None and stop_event.is_set():
            break
        found = latest_session_watch_files(log_root)
        if found is None:
            if not waiting_logged:
                waiting_logged = True
                emit(f"Waiting for a Tarkov session in {log_root} ...")
            if _sleep_or_stop(poll_interval, stop_event):
                break
            continue
        waiting_logged = False
        session_dir, log_paths = found
        session_dir = session_dir.resolve()
        paths_resolved = tuple(p.resolve() for p in log_paths)
        desired = set(paths_resolved)

        if active_session != session_dir:
            active_session = session_dir
            file_pos.clear()
            partial_line.clear()
            in_raid_audio_paused[0] = False
            emit(f"Watching session: {session_dir}")
            for p in paths_resolved:
                _tail_from_eof(p, "tail")
        else:
            for p in list(file_pos.keys()):
                if p not in desired:
                    del file_pos[p]
                    del partial_line[p]
            for p in paths_resolved:
                if p not in file_pos:
                    _tail_from_eof(p, "+tail")

        any_data = False
        for p in sorted(file_pos.keys()):
            if stop_event is not None and stop_event.is_set():
                break
            try:
                size = p.stat().st_size
            except OSError:
                continue
            off = file_pos[p]
            if size < off:
                off = 0
                file_pos[p] = 0
                partial_line[p] = b""
                emit(f"  log truncated (reset): {p.name}")
            if size <= off:
                continue
            try:
                with p.open("rb") as f:
                    f.seek(off)
                    chunk = f.read()
            except OSError as exc:
                emit(f"  Read error {p.name}: {exc}")
                continue
            file_pos[p] = off + len(chunk)
            any_data = True
            data = partial_line.get(p, b"") + chunk
            lines = data.split(b"\n")
            partial_line[p] = lines[-1]
            for raw in lines[:-1]:
                try:
                    line = raw.decode("utf-8", errors="replace").rstrip("\r")
                except Exception:
                    continue
                _process_line(
                    line,
                    resume_after_raid=resume_after_raid,
                    dry_run=dry_run,
                    in_raid_audio_paused=in_raid_audio_paused,
                    emit=emit,
                    send_media=send_media_fn,
                )

        if not any_data:
            if _sleep_or_stop(poll_interval, stop_event):
                break


def main() -> None:
    parser = argparse.ArgumentParser(
        description="Pause media when an EFT raid starts; optionally resume when returning to menu (log watcher)."
    )
    parser.add_argument(
        "--logs-dir",
        type=Path,
        default=Path(os.environ.get("TARKOV_LOGS", DEFAULT_LOG_ROOT)),
        help=(
            f"Parent folder (e.g. {DEFAULT_LOG_ROOT}) or a log_* session folder with "
            "application*.log inside (default: %(default)s or %TARKOV_LOGS%)"
        ),
    )
    parser.add_argument(
        "--poll",
        type=float,
        default=0.5,
        help="Seconds between reads when file has no new data (default: 0.5)",
    )
    parser.add_argument(
        "--dry-run",
        action="store_true",
        help="Print matches but do not send media key",
    )
    parser.add_argument(
        "--no-resume",
        action="store_true",
        help="Only pause on raid start; do not send media key when returning to menu",
    )
    args = parser.parse_args()

    tail_loop(
        args.logs_dir.resolve(),
        args.poll,
        args.dry_run,
        resume_after_raid=not args.no_resume,
    )


if __name__ == "__main__":
    main()
