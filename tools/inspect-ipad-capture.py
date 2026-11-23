"""Open a local iPad Bluetooth capture and save only the AirPods evidence."""

import os
import shutil
import subprocess
import sys
import tempfile
import tkinter as tk
from pathlib import Path
from tkinter import filedialog, messagebox, ttk


def save_report(report, destination: Path, source: Path):
    if destination.exists() and destination.samefile(source):
        raise ValueError("Choose a different file to keep your original capture.")
    pending = None
    try:
        with tempfile.NamedTemporaryFile(dir=destination.parent, delete=False) as out:
            pending = Path(out.name)
            report.seek(0)
            shutil.copyfileobj(report, out)
        os.replace(pending, destination)
    finally:
        if pending is not None:
            pending.unlink(missing_ok=True)


def main():
    window = tk.Tk()
    window.withdraw()
    window.title("Pairside · Inspect iPad capture")
    source = filedialog.askopenfilename(
        parent=window, title="Choose your iPad capture",
        filetypes=[("iPad diagnostics or PacketLogger", "*.tar.gz *.tgz *.pklg"), ("All files", "*.*")],
    )
    if not source:
        return

    window.deiconify()
    window.resizable(False, False)
    frame = ttk.Frame(window, padding=20)
    frame.grid()
    ttk.Label(frame, text="Reading the capture on this PC…").grid(sticky="w")
    progress = ttk.Progressbar(frame, length=300, mode="indeterminate")
    progress.grid(pady=(10, 0))
    progress.start()
    with tempfile.TemporaryFile() as report, tempfile.TemporaryFile() as errors:
        worker = subprocess.Popen(
            [sys.executable, str(Path(__file__).with_name("inspect-seal-trace.py")), "--features", "both", source],
            stdout=report, stderr=errors, creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
        )

        def close():
            if worker.poll() is None:
                worker.terminate()
                worker.wait()
            window.destroy()

        def finish():
            if worker.poll() is None:
                window.after(200, finish)
                return
            window.withdraw()
            errors.seek(0)
            detail = errors.read(4000).decode(errors="replace").strip()
            if worker.returncode or report.tell() == 0:
                messagebox.showinfo("Pairside · Capture result", detail or "No matching AirPods exchange was found.", parent=window)
            else:
                dest = filedialog.asksaveasfilename(
                    parent=window, title="Save the filtered AirPods report",
                    initialfile="AirPods capture report.txt", defaultextension=".txt", filetypes=[("Text report", "*.txt")],
                )
                if dest:
                    try:
                        save_report(report, Path(dest), Path(source))
                    except (OSError, ValueError) as error:
                        messagebox.showerror("Pairside", str(error), parent=window)
            close()

        window.protocol("WM_DELETE_WINDOW", close)
        window.after(200, finish)
        window.mainloop()


if __name__ == "__main__":
    main()
