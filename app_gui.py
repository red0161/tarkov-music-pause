"""
Minimal Windows UI for the Tarkov log watcher (tkinter, stdlib only).
Run: python app_gui.py
"""
from __future__ import annotations

import os
import threading
import tkinter as tk
from pathlib import Path
from tkinter import ttk, scrolledtext

import pystray
from PIL import Image, ImageDraw

import pause_on_raid as core


def _make_tray_image() -> Image.Image:
    img = Image.new("RGBA", (64, 64), (0, 0, 0, 0))
    draw = ImageDraw.Draw(img)
    # Dark background circle
    draw.ellipse([0, 0, 63, 63], fill=(30, 30, 30, 255))
    # T top bar
    draw.rectangle([10, 12, 54, 24], fill=(255, 255, 255, 255))
    # Split stem = pause symbol (two bars)
    draw.rectangle([20, 26, 30, 52], fill=(255, 255, 255, 255))
    draw.rectangle([34, 26, 44, 52], fill=(255, 255, 255, 255))
    return img


def main() -> None:
    root = tk.Tk()
    root.title("Tarkov music pause")
    root.minsize(440, 300)

    var_logs = tk.StringVar(value=os.environ.get("TARKOV_LOGS", str(core.DEFAULT_LOG_ROOT)))
    var_resume = tk.BooleanVar(value=True)
    stop_event = threading.Event()
    worker: list[threading.Thread | None] = [None]
    tray_icon: list[pystray.Icon | None] = [None]

    # --- tray helpers ---

    def _show_window() -> None:
        root.after(0, lambda: (root.deiconify(), root.lift(), root.focus_force()))

    def _quit_app() -> None:
        stop_event.set()
        if tray_icon[0] is not None:
            tray_icon[0].stop()
        root.after(0, root.destroy)

    def _start_tray() -> None:
        if tray_icon[0] is not None:
            return
        menu = pystray.Menu(
            pystray.MenuItem("Show", lambda: _show_window(), default=True),
            pystray.MenuItem("Quit", lambda: _quit_app()),
        )
        icon = pystray.Icon("TarkovMusicPause", _make_tray_image(), "Tarkov Music Pause", menu)
        tray_icon[0] = icon
        threading.Thread(target=icon.run, daemon=True).start()

    def _stop_tray() -> None:
        if tray_icon[0] is not None:
            tray_icon[0].stop()
            tray_icon[0] = None

    def minimize_to_tray() -> None:
        root.withdraw()
        _start_tray()

    # --- log / state helpers ---

    def append_log(msg: str) -> None:
        def _do() -> None:
            log.insert(tk.END, msg + "\n")
            log.see(tk.END)

        root.after(0, _do)

    def set_running(running: bool) -> None:
        def _do() -> None:
            state.set("Running" if running else "Idle")
            btn_start.config(state=tk.DISABLED if running else tk.NORMAL)
            btn_stop.config(state=tk.NORMAL if running else tk.DISABLED)

        root.after(0, _do)

    # --- watcher thread ---

    def run_watcher() -> None:
        def send_media_main_thread() -> None:
            root.after(0, core.send_media_play_pause)

        try:
            core.tail_loop(
                Path(var_logs.get()).resolve(),
                0.5,
                False,
                var_resume.get(),
                stop_event=stop_event,
                line_sink=append_log,
                send_media=send_media_main_thread,
            )
        except Exception as e:
            append_log(f"Error: {e}")
        finally:
            worker[0] = None
            set_running(False)

    def start_clicked() -> None:
        if worker[0] is not None and worker[0].is_alive():
            return
        logs_dir = Path(var_logs.get()).resolve()
        if not logs_dir.is_dir():
            append_log(f"Not a folder: {logs_dir}")
            return
        stop_event.clear()
        set_running(True)
        t = threading.Thread(target=run_watcher, daemon=True)
        worker[0] = t
        t.start()

    def stop_clicked() -> None:
        stop_event.set()

    def on_minimize(event: tk.Event) -> None:  # type: ignore[type-arg]
        if root.state() == "iconic":
            minimize_to_tray()

    def on_close() -> None:
        minimize_to_tray()

    # --- layout ---

    frm = ttk.Frame(root, padding=10)
    frm.pack(fill=tk.BOTH, expand=True)

    ttk.Label(frm, text="EFT Logs folder").grid(row=0, column=0, sticky=tk.W)
    ttk.Entry(frm, textvariable=var_logs, width=56).grid(row=1, column=0, sticky=tk.EW)
    ttk.Label(
        frm,
        text="Use C:\\Games\\Logs (parent) or any log_* folder that contains application_*.log.",
        font=("Segoe UI", 8),
    ).grid(row=2, column=0, sticky=tk.W, pady=(2, 0))

    ttk.Checkbutton(frm, text="Resume after raid (media key)", variable=var_resume).grid(
        row=3, column=0, sticky=tk.W, pady=(8, 0)
    )

    def test_media() -> None:
        append_log("Test: sending media Play/Pause (should toggle your player).")
        try:
            core.send_media_play_pause()
        except Exception as e:
            append_log(f"Test failed: {e}")
            return
        append_log("Test: done.")

    btn_row = ttk.Frame(frm)
    btn_row.grid(row=4, column=0, sticky=tk.W, pady=(10, 0))
    btn_start = ttk.Button(btn_row, text="Start", command=start_clicked, width=12)
    btn_stop = ttk.Button(btn_row, text="Stop", command=stop_clicked, width=12, state=tk.DISABLED)
    btn_start.pack(side=tk.LEFT, padx=(0, 8))
    btn_stop.pack(side=tk.LEFT, padx=(0, 8))
    ttk.Button(btn_row, text="Test media key", command=test_media, width=16).pack(side=tk.LEFT, padx=(0, 8))
    ttk.Button(btn_row, text="Minimise to tray", command=minimize_to_tray, width=16).pack(side=tk.LEFT)

    state = tk.StringVar(value="Idle")
    ttk.Label(frm, textvariable=state).grid(row=5, column=0, sticky=tk.W, pady=(8, 0))

    log = scrolledtext.ScrolledText(frm, height=14, width=80, font=("Consolas", 9))
    log.grid(row=6, column=0, sticky=tk.NSEW, pady=(8, 0))
    frm.rowconfigure(6, weight=1)
    frm.columnconfigure(0, weight=1)

    root.bind("<Unmap>", on_minimize)
    root.protocol("WM_DELETE_WINDOW", on_close)
    root.mainloop()

    # mainloop exited (destroy called) — ensure tray is gone
    _stop_tray()


if __name__ == "__main__":
    main()
