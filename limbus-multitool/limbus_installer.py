from __future__ import annotations

import os
import subprocess
import sys
from pathlib import Path

try:
    from PySide6.QtCore import QSettings, Qt, QThread, Signal
    from PySide6.QtGui import QFont
    from PySide6.QtWidgets import (
        QApplication,
        QCheckBox,
        QFileDialog,
        QFrame,
        QGridLayout,
        QHBoxLayout,
        QLabel,
        QLineEdit,
        QMainWindow,
        QMessageBox,
        QPushButton,
        QPlainTextEdit,
        QProgressBar,
        QSizePolicy,
        QVBoxLayout,
        QWidget,
    )
except ModuleNotFoundError as exc:
    print("PySide6 is required. Install with: pip install -r requirements.txt", file=sys.stderr)
    raise SystemExit(1) from exc


APP_NAME = "Limbus Multi-tool"
ORG_NAME = "LimbusModTools"


def app_root() -> Path:
    if hasattr(sys, "_MEIPASS"):
        return Path(getattr(sys, "_MEIPASS"))
    return Path(__file__).resolve().parent


def backend_path() -> Path:
    return app_root() / "backend.ps1"


def payload_root() -> Path:
    return app_root() / "payload"


def default_game_dirs() -> list[Path]:
    candidates = [
        Path(r"C:\Program Files (x86)\Steam\steamapps\common\Limbus Company"),
        Path(r"C:\Program Files\Steam\steamapps\common\Limbus Company"),
    ]

    try:
        import winreg

        for hive, subkey in (
            (winreg.HKEY_CURRENT_USER, r"Software\Valve\Steam"),
            (winreg.HKEY_LOCAL_MACHINE, r"SOFTWARE\WOW6432Node\Valve\Steam"),
        ):
            try:
                with winreg.OpenKey(hive, subkey) as key:
                    steam_path, _ = winreg.QueryValueEx(key, "SteamPath")
                    candidates.append(Path(steam_path) / "steamapps" / "common" / "Limbus Company")
            except OSError:
                pass
    except Exception:
        pass

    seen: set[str] = set()
    result: list[Path] = []
    for candidate in candidates:
        key = str(candidate).lower()
        if key not in seen:
            seen.add(key)
            result.append(candidate)
    return result


def detect_game_dir() -> Path | None:
    for candidate in default_game_dirs():
        if (candidate / "LimbusCompany.exe").exists():
            return candidate
    return None


def is_game_dir(path: str) -> bool:
    root = Path(path)
    return (
        (root / "LimbusCompany.exe").exists()
        and (root / "GameAssembly.dll").exists()
        and (root / "UnityPlayer.dll").exists()
    )


def setting_bool(settings: QSettings, key: str, default: bool) -> bool:
    value = settings.value(key, None)
    if value is None:
        return default
    if isinstance(value, bool):
        return value
    return str(value).strip().lower() in {"1", "true", "yes", "on"}


class CommandWorker(QThread):
    line = Signal(str)
    finished_ok = Signal(bool)

    def __init__(self, action: str, game_dir: Path | None, plugins: list[str] | None = None):
        super().__init__()
        self.action = action
        self.game_dir = game_dir
        self.plugins = plugins or []

    def run(self) -> None:
        script = backend_path()
        payload = payload_root()
        if not script.exists():
            self.line.emit(f"[error] backend.ps1 not found: {script}")
            self.finished_ok.emit(False)
            return

        args = [
            "powershell.exe",
            "-NoProfile",
            "-ExecutionPolicy",
            "Bypass",
            "-File",
            str(script),
            "-Action",
            self.action,
            "-PayloadRoot",
            str(payload),
        ]
        if self.game_dir is not None:
            args.extend(["-GameDir", str(self.game_dir)])
        if self.plugins:
            args.extend(["-Plugins", ",".join(self.plugins)])

        self.line.emit(f"> {' '.join(args)}")
        try:
            process = subprocess.Popen(
                args,
                stdout=subprocess.PIPE,
                stderr=subprocess.STDOUT,
                text=True,
                encoding="utf-8",
                errors="replace",
                cwd=str(app_root()),
                creationflags=subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0,
            )
            assert process.stdout is not None
            for output_line in process.stdout:
                self.line.emit(output_line.rstrip())
            code = process.wait()
            self.finished_ok.emit(code == 0)
        except Exception as exc:
            self.line.emit(f"[error] {exc}")
            self.finished_ok.emit(False)


class StatusCard(QFrame):
    def __init__(self, title: str, value: str = "Not checked"):
        super().__init__()
        self.setObjectName("StatusCard")
        layout = QVBoxLayout(self)
        layout.setContentsMargins(16, 14, 16, 14)
        layout.setSpacing(4)

        self.title_label = QLabel(title)
        self.title_label.setObjectName("CardTitle")
        self.value_label = QLabel(value)
        self.value_label.setObjectName("CardValue")
        self.value_label.setWordWrap(True)

        layout.addWidget(self.title_label)
        layout.addWidget(self.value_label)

    def set_value(self, value: str, ok: bool | None = None) -> None:
        self.value_label.setText(value)
        if ok is None:
            self.setProperty("state", "")
        else:
            self.setProperty("state", "ok" if ok else "bad")
        self.style().unpolish(self)
        self.style().polish(self)


class MainWindow(QMainWindow):
    def __init__(self):
        super().__init__()
        self.settings = QSettings(ORG_NAME, APP_NAME)
        self.worker: CommandWorker | None = None

        self.setWindowTitle(APP_NAME)
        self.resize(980, 720)
        self.setMinimumSize(860, 620)

        root = QWidget()
        self.setCentralWidget(root)
        layout = QVBoxLayout(root)
        layout.setContentsMargins(24, 22, 24, 24)
        layout.setSpacing(18)

        header = QLabel(APP_NAME)
        header.setObjectName("Header")
        subtitle = QLabel("Installs BepInEx when needed, applies the compatibility patch, and deploys the selected fixes.")
        subtitle.setObjectName("Subtitle")
        layout.addWidget(header)
        layout.addWidget(subtitle)

        path_row = QHBoxLayout()
        self.path_edit = QLineEdit()
        self.path_edit.setPlaceholderText(r"C:\Program Files (x86)\Steam\steamapps\common\Limbus Company")
        saved = self.settings.value("gameDir", "", str)
        detected = detect_game_dir()
        self.path_edit.setText(saved or (str(detected) if detected else ""))
        self.path_edit.textChanged.connect(self.refresh_status)

        browse = QPushButton("Browse")
        browse.clicked.connect(self.browse)
        detect = QPushButton("Detect")
        detect.clicked.connect(self.detect)

        path_row.addWidget(QLabel("Game folder"))
        path_row.addWidget(self.path_edit, 1)
        path_row.addWidget(detect)
        path_row.addWidget(browse)
        layout.addLayout(path_row)

        grid = QGridLayout()
        grid.setSpacing(12)
        self.game_card = StatusCard("Game Folder")
        self.bepin_card = StatusCard("BepInEx")
        self.payload_card = StatusCard("Installer Payload")
        self.plugin_card = StatusCard("Plugins")
        grid.addWidget(self.game_card, 0, 0)
        grid.addWidget(self.bepin_card, 0, 1)
        grid.addWidget(self.payload_card, 1, 0)
        grid.addWidget(self.plugin_card, 1, 1)
        layout.addLayout(grid)

        plugin_box = QFrame()
        plugin_box.setObjectName("PluginBox")
        plugin_layout = QVBoxLayout(plugin_box)
        plugin_layout.setContentsMargins(16, 14, 16, 14)
        plugin_layout.setSpacing(6)
        plugin_title = QLabel("Plugins")
        plugin_title.setObjectName("SectionTitle")
        self.canvas_check = QCheckBox("Ultrawide UI fix")
        self.canvas_check.setToolTip("Fixes CanvasScaler behavior for ultrawide displays.")
        self.resize_check = QCheckBox("Window resize fix")
        self.resize_check.setToolTip("Restores the resizable window border after the Unity engine upgrade.")
        self.framepacing_check = QCheckBox("FPS / frame pacing fix")
        self.framepacing_check.setToolTip("Forces a 240 FPS cap, disables Unity v-sync, and keeps maximized window mode.")
        self.canvas_check.setChecked(setting_bool(self.settings, "pluginCanvas", True))
        self.resize_check.setChecked(setting_bool(self.settings, "pluginResize", True))
        self.framepacing_check.setChecked(setting_bool(self.settings, "pluginFramePacing", True))
        self.canvas_check.stateChanged.connect(self.save_plugin_selection)
        self.resize_check.stateChanged.connect(self.save_plugin_selection)
        self.framepacing_check.stateChanged.connect(self.save_plugin_selection)
        plugin_layout.addWidget(plugin_title)
        plugin_layout.addWidget(self.canvas_check)
        plugin_layout.addWidget(self.resize_check)
        plugin_layout.addWidget(self.framepacing_check)
        layout.addWidget(plugin_box)

        button_row = QHBoxLayout()
        self.install_btn = QPushButton("Install / Reapply Selected")
        self.launch_btn = QPushButton("Launch Game")

        self.install_btn.clicked.connect(lambda: self.run_action("install", needs_game=True))
        self.launch_btn.clicked.connect(lambda: self.run_action("launch", needs_game=True))

        for button in (self.install_btn, self.launch_btn):
            button.setMinimumHeight(36)
            button_row.addWidget(button)
        layout.addLayout(button_row)

        self.progress = QProgressBar()
        self.progress.setRange(0, 1)
        self.progress.setValue(0)
        self.progress.setTextVisible(False)
        layout.addWidget(self.progress)

        self.log = QPlainTextEdit()
        self.log.setReadOnly(True)
        self.log.setObjectName("Log")
        self.log.setSizePolicy(QSizePolicy.Policy.Expanding, QSizePolicy.Policy.Expanding)
        layout.addWidget(self.log, 1)

        self.refresh_status()

    def browse(self) -> None:
        start = self.path_edit.text() or str(Path.home())
        path = QFileDialog.getExistingDirectory(self, "Select Limbus Company folder", start)
        if path:
            self.path_edit.setText(path)

    def detect(self) -> None:
        path = detect_game_dir()
        if path:
            self.path_edit.setText(str(path))
            self.append_line(f"[info] Detected game folder: {path}")
        else:
            QMessageBox.warning(self, "Not found", "Could not auto-detect the Limbus Company install folder.")

    def selected_game_dir(self) -> Path | None:
        text = self.path_edit.text().strip()
        if not text:
            return None
        return Path(text)

    def selected_plugins(self) -> list[str]:
        selected: list[str] = []
        if self.canvas_check.isChecked():
            selected.append("canvas")
        if self.resize_check.isChecked():
            selected.append("resize")
        if self.framepacing_check.isChecked():
            selected.append("framepacing")
        return selected

    def save_plugin_selection(self) -> None:
        self.settings.setValue("pluginCanvas", self.canvas_check.isChecked())
        self.settings.setValue("pluginResize", self.resize_check.isChecked())
        self.settings.setValue("pluginFramePacing", self.framepacing_check.isChecked())

    def refresh_status(self) -> None:
        game_dir = self.selected_game_dir()
        if game_dir and is_game_dir(str(game_dir)):
            self.game_card.set_value(str(game_dir), True)
            self.settings.setValue("gameDir", str(game_dir))
        else:
            self.game_card.set_value("Select the folder containing LimbusCompany.exe", False)

        game_ok = game_dir is not None and is_game_dir(str(game_dir))
        if game_ok and (game_dir / "BepInEx" / "core" / "BepInEx.Unity.IL2CPP.dll").exists():
            self.bepin_card.set_value("BepInEx Unity IL2CPP present", True)
        elif game_ok:
            self.bepin_card.set_value("Will be downloaded during install", None)
        else:
            self.bepin_card.set_value("Select a game folder", None)

        payload = payload_root()
        payload_ok = (
            (payload / "scripts" / "reapply-limbus-fix.ps1").exists()
            and (payload / "scripts" / "rebuild-resources.ps1").exists()
            and (payload / "data" / "il2cpp-api-functions-unity6000-no-profiler.txt").exists()
            and (payload / "bin" / "Release" / "LimbusCanvasFix.dll").exists()
            and (payload / "bin" / "Release" / "LimbusWindowResizeFix.dll").exists()
            and (payload / "bin" / "Release" / "LimbusFramePacingFix.dll").exists()
        )
        self.payload_card.set_value("Ready" if payload_ok else f"Incomplete: {payload}", payload_ok)

        if game_dir:
            plugin_dir = game_dir / "BepInEx" / "plugins"
            installed = [
                (plugin_dir / "LimbusCanvasFix.dll").exists(),
                (plugin_dir / "LimbusWindowResizeFix.dll").exists(),
                (plugin_dir / "LimbusFramePacingFix.dll").exists(),
            ]
            installed_count = sum(1 for present in installed if present)
            if installed_count == len(installed):
                self.plugin_card.set_value("All plugins installed", True)
            elif installed_count:
                self.plugin_card.set_value(f"{installed_count} of {len(installed)} plugins installed", False)
            else:
                self.plugin_card.set_value("Plugins not installed", None)
        else:
            self.plugin_card.set_value("Select a game folder", None)

    def run_action(self, action: str, needs_game: bool) -> None:
        if self.worker is not None and self.worker.isRunning():
            QMessageBox.information(self, "Busy", "An operation is already running.")
            return

        game_dir = self.selected_game_dir()
        if needs_game and (game_dir is None or not is_game_dir(str(game_dir))):
            QMessageBox.warning(self, "Invalid folder", "Select the Limbus Company install folder first.")
            return

        plugins = self.selected_plugins()
        if action == "install" and not plugins:
            QMessageBox.warning(self, "No plugins selected", "Select at least one plugin to install.")
            return

        self.set_busy(True)
        self.append_line("")
        self.append_line(f"=== {action.upper()} ===")
        self.worker = CommandWorker(action, game_dir if needs_game else None, plugins if action == "install" else None)
        self.worker.line.connect(self.append_line)
        self.worker.finished_ok.connect(self.command_finished)
        self.worker.start()

    def set_busy(self, busy: bool) -> None:
        self.progress.setRange(0, 0 if busy else 1)
        self.progress.setValue(0 if busy else 1)
        for button in (self.install_btn, self.launch_btn, self.canvas_check, self.resize_check, self.framepacing_check):
            button.setEnabled(not busy)

    def append_line(self, text: str) -> None:
        self.log.appendPlainText(text)
        scrollbar = self.log.verticalScrollBar()
        scrollbar.setValue(scrollbar.maximum())

    def command_finished(self, ok: bool) -> None:
        self.set_busy(False)
        self.append_line("[ok] Operation completed." if ok else "[error] Operation failed.")
        self.refresh_status()


def apply_style(app: QApplication) -> None:
    app.setStyle("Fusion")
    font = QFont("Segoe UI", 10)
    app.setFont(font)
    checkmark = (app_root() / "assets" / "checkmark.svg").as_posix()
    app.setStyleSheet(
        """
        QWidget {
            background: #f5f7fb;
            color: #172033;
        }
        QLabel#Header {
            font-size: 26px;
            font-weight: 700;
        }
        QLabel#Subtitle {
            color: #536079;
            font-size: 13px;
        }
        QLineEdit {
            background: white;
            border: 1px solid #cfd7e6;
            border-radius: 6px;
            padding: 8px 10px;
        }
        QPushButton {
            background: #244c8f;
            color: white;
            border: 0;
            border-radius: 6px;
            padding: 8px 14px;
            font-weight: 600;
        }
        QPushButton:hover { background: #1c3f78; }
        QPushButton:disabled { background: #aab5c8; }
        QFrame#StatusCard {
            background: white;
            border: 1px solid #d9e1ee;
            border-radius: 8px;
        }
        QFrame#StatusCard[state="ok"] { border-color: #58a06a; }
        QFrame#StatusCard[state="bad"] { border-color: #c95d63; }
        QFrame#PluginBox {
            background: white;
            border: 1px solid #d9e1ee;
            border-radius: 8px;
        }
        QLabel#SectionTitle {
            color: #536079;
            font-size: 12px;
            font-weight: 700;
            text-transform: uppercase;
        }
        QCheckBox {
            background: transparent;
            spacing: 8px;
            font-size: 13px;
            padding: 3px 0;
            min-height: 22px;
        }
        QCheckBox::indicator {
            width: 14px;
            height: 14px;
            border: 1px solid #66758f;
            border-radius: 3px;
            background: #ffffff;
        }
        QCheckBox::indicator:hover {
            border-color: #244c8f;
            background: #eef4ff;
        }
        QCheckBox::indicator:checked {
            border-color: #244c8f;
            background: #244c8f;
            image: url(__CHECKMARK__);
        }
        QCheckBox::indicator:checked:hover {
            border-color: #1c3f78;
            background: #1c3f78;
        }
        QCheckBox::indicator:disabled {
            border-color: #aab5c8;
            background: #eef1f6;
        }
        QLabel#CardTitle {
            color: #536079;
            font-size: 12px;
            font-weight: 700;
            text-transform: uppercase;
        }
        QLabel#CardValue {
            font-size: 13px;
        }
        QPlainTextEdit#Log {
            background: #101827;
            color: #d9e4f2;
            border: 1px solid #202c42;
            border-radius: 8px;
            padding: 10px;
            font-family: Consolas, "Cascadia Mono", monospace;
            font-size: 12px;
        }
        QProgressBar {
            border: 1px solid #d4ddec;
            border-radius: 4px;
            background: white;
            height: 8px;
        }
        QProgressBar::chunk {
            background: #244c8f;
            border-radius: 4px;
        }
        """.replace("__CHECKMARK__", checkmark)
    )


def main() -> int:
    QApplication.setAttribute(Qt.ApplicationAttribute.AA_EnableHighDpiScaling, True)
    app = QApplication(sys.argv)
    app.setOrganizationName(ORG_NAME)
    app.setApplicationName(APP_NAME)
    apply_style(app)
    window = MainWindow()
    window.show()
    return app.exec()


if __name__ == "__main__":
    raise SystemExit(main())
