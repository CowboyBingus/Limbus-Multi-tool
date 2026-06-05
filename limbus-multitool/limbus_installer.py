from __future__ import annotations

import json
import os
import re
import subprocess
import sys
import urllib.error
import urllib.request
from pathlib import Path

try:
    from PySide6.QtCore import QSettings, Qt, QThread, QTimer, Signal
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
DEFAULT_VERSION = "0.0.0-dev"
RELEASES_URL = "https://github.com/CowboyBingus/Limbus-Multi-tool/releases"
LATEST_RELEASE_API = "https://api.github.com/repos/CowboyBingus/Limbus-Multi-tool/releases/latest"
UPDATE_ASSET_PATTERN = re.compile(r"^Limbus-Multi-tool-.+-win-x64\.zip$", re.IGNORECASE)
REQUIRED_BEPINEX_FILES = (
    ("BepInEx", "core", "BepInEx.Core.dll"),
    ("BepInEx", "core", "BepInEx.Unity.IL2CPP.dll"),
    ("BepInEx", "core", "Il2CppInterop.Runtime.dll"),
    ("BepInEx", "core", "LibCpp2IL.dll"),
    ("BepInEx", "core", "Cpp2IL.Core.dll"),
    ("BepInEx", "core", "Il2CppInterop.Generator.dll"),
    ("BepInEx", "core", "Mono.Cecil.dll"),
    ("BepInEx", "core", "Mono.Cecil.Rocks.dll"),
    ("doorstop_config.ini",),
    ("winhttp.dll",),
)
REQUIRED_PAYLOAD_FILES = (
    ("scripts", "reapply-limbus-fix.ps1"),
    ("scripts", "rebuild-resources.ps1"),
    ("data", "il2cpp-api-functions-unity6000-no-profiler.txt"),
    ("data", "System.JsonExtensions.dll-resources.dat.template"),
    ("native", "winhttp.dll"),
    ("native", "doorstop.dll"),
    ("bin", "Release", "LimbusCanvasFix.dll"),
    ("bin", "Release", "LimbusWindowResizeFix.dll"),
    ("bin", "Release", "LimbusFramePacingFix.dll"),
    ("tools", "patch-libcpp", "bin", "Release", "net6.0", "PatchLibCpp.exe"),
    ("tools", "patch-libcpp", "bin", "Release", "net6.0", "PatchLibCpp.dll"),
    ("tools", "patch-libcpp", "bin", "Release", "net6.0", "PatchLibCpp.runtimeconfig.json"),
    ("tools", "patch-libcpp", "bin", "Release", "net6.0", "PatchLibCpp.deps.json"),
    ("tools", "patch-libcpp", "bin", "Release", "net6.0", "Mono.Cecil.dll"),
    ("tools", "patch-libcpp", "bin", "Release", "net6.0", "Mono.Cecil.Rocks.dll"),
)


def app_root() -> Path:
    if hasattr(sys, "_MEIPASS"):
        return Path(getattr(sys, "_MEIPASS"))
    return Path(__file__).resolve().parent


def backend_path() -> Path:
    return app_root() / "backend.ps1"


def payload_root() -> Path:
    return app_root() / "payload"


def install_root() -> Path:
    root = app_root()
    if root.name.lower() == "_internal":
        return root.parent
    return root


def current_version() -> str:
    version_file = app_root() / "app_version.txt"
    if version_file.exists():
        value = version_file.read_text(encoding="utf-8", errors="replace").strip()
        if value:
            return value
    return DEFAULT_VERSION


def version_key(value: str) -> tuple[int, ...]:
    return tuple(int(part) for part in re.findall(r"\d+", value))


def is_newer_version(latest: str, current: str) -> bool:
    latest_clean = latest.strip().lstrip("vV")
    current_clean = current.strip().lstrip("vV")
    if latest_clean.lower() == current_clean.lower():
        return False
    latest_key = version_key(latest_clean)
    current_key = version_key(current_clean)
    if latest_key and current_key:
        return latest_key > current_key
    return latest_clean.lower() != current_clean.lower()


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


class UpdateCheckWorker(QThread):
    finished_result = Signal(object)

    def run(self) -> None:
        try:
            request = urllib.request.Request(
                LATEST_RELEASE_API,
                headers={
                    "Accept": "application/vnd.github+json",
                    "User-Agent": f"{APP_NAME}/{current_version()}",
                    "X-GitHub-Api-Version": "2022-11-28",
                },
            )
            with urllib.request.urlopen(request, timeout=15) as response:
                release = json.loads(response.read().decode("utf-8"))

            tag = str(release.get("tag_name") or release.get("name") or "").strip()
            if not tag:
                raise RuntimeError("Latest release did not include a tag name.")

            assets = release.get("assets") or []
            selected_asset: dict[str, object] | None = None
            for asset in assets:
                name = str(asset.get("name") or "")
                if UPDATE_ASSET_PATTERN.match(name):
                    selected_asset = asset
                    break
            if selected_asset is None:
                for asset in assets:
                    name = str(asset.get("name") or "")
                    if name.lower().endswith(".zip"):
                        selected_asset = asset
                        break
            if selected_asset is None:
                raise RuntimeError("Latest release does not have a downloadable zip asset.")

            asset_url = str(selected_asset.get("browser_download_url") or "")
            asset_name = str(selected_asset.get("name") or "release asset")
            if not asset_url:
                raise RuntimeError("Latest release asset is missing a download URL.")

            installed = current_version()
            self.finished_result.emit(
                {
                    "ok": True,
                    "available": is_newer_version(tag, installed),
                    "current": installed,
                    "tag": tag,
                    "name": str(release.get("name") or tag),
                    "body": str(release.get("body") or ""),
                    "html_url": str(release.get("html_url") or RELEASES_URL),
                    "asset_name": asset_name,
                    "asset_url": asset_url,
                    "published_at": str(release.get("published_at") or ""),
                }
            )
        except Exception as exc:
            self.finished_result.emit({"ok": False, "error": str(exc), "current": current_version()})


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
        self.update_worker: UpdateCheckWorker | None = None
        self.available_update: dict[str, object] | None = None
        self.update_check_manual = False

        self.setWindowTitle(f"{APP_NAME} {current_version()}")
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
        self.update_card = StatusCard("Updates", f"Current: {current_version()}")
        grid.addWidget(self.game_card, 0, 0)
        grid.addWidget(self.bepin_card, 0, 1)
        grid.addWidget(self.payload_card, 1, 0)
        grid.addWidget(self.plugin_card, 1, 1)
        grid.addWidget(self.update_card, 2, 0, 1, 2)
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
        self.update_btn = QPushButton("Check for Updates")

        self.install_btn.clicked.connect(lambda: self.run_action("install", needs_game=True))
        self.launch_btn.clicked.connect(lambda: self.run_action("launch", needs_game=True))
        self.update_btn.clicked.connect(self.update_button_clicked)

        for button in (self.install_btn, self.launch_btn, self.update_btn):
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
        QTimer.singleShot(1200, self.check_for_updates_auto)

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
        bepinex_missing = []
        if game_ok:
            bepinex_missing = [Path(*parts) for parts in REQUIRED_BEPINEX_FILES if not (game_dir / Path(*parts)).exists()]

        if game_ok and not bepinex_missing:
            self.bepin_card.set_value("BepInEx Unity IL2CPP present", True)
        elif game_ok:
            self.bepin_card.set_value("Incomplete; will be repaired during install", None)
        else:
            self.bepin_card.set_value("Select a game folder", None)

        payload = payload_root()
        missing_payload = [Path(*parts) for parts in REQUIRED_PAYLOAD_FILES if not (payload / Path(*parts)).exists()]
        payload_ok = not missing_payload
        payload_text = "Ready" if payload_ok else f"Missing {len(missing_payload)} file(s): {missing_payload[0]}"
        self.payload_card.set_value(payload_text, payload_ok)

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

    def check_for_updates_auto(self) -> None:
        if setting_bool(self.settings, "checkForUpdates", True):
            self.check_for_updates(manual=False)

    def update_button_clicked(self) -> None:
        if self.available_update is not None:
            self.confirm_and_start_update(self.available_update)
        else:
            self.check_for_updates(manual=True)

    def check_for_updates(self, manual: bool) -> None:
        if self.update_worker is not None and self.update_worker.isRunning():
            if manual:
                QMessageBox.information(self, "Update check", "An update check is already running.")
            return

        self.update_check_manual = manual
        self.update_btn.setEnabled(False)
        self.update_btn.setText("Checking...")
        self.update_card.set_value("Checking GitHub releases...", None)
        self.update_worker = UpdateCheckWorker()
        self.update_worker.finished_result.connect(self.update_check_finished)
        self.update_worker.start()

    def update_check_finished(self, result: object) -> None:
        self.update_btn.setEnabled(True)
        if not isinstance(result, dict) or not result.get("ok"):
            error = str(result.get("error") if isinstance(result, dict) else "Unknown update check error.")
            self.available_update = None
            self.update_btn.setText("Check for Updates")
            self.update_card.set_value(f"Update check failed: {error}", False)
            if self.update_check_manual:
                QMessageBox.warning(self, "Update check failed", error)
            return

        if result.get("available"):
            self.available_update = result
            tag = str(result["tag"])
            asset_name = str(result["asset_name"])
            self.update_btn.setText("Install Update")
            self.update_card.set_value(f"{tag} available: {asset_name}", False)
            notified_version = str(self.settings.value("notifiedUpdateVersion", "", str))
            if not self.update_check_manual and notified_version != tag:
                self.settings.setValue("notifiedUpdateVersion", tag)
                response = QMessageBox.question(
                    self,
                    "Update available",
                    f"{APP_NAME} {tag} is available.\n\nInstall it now?",
                    QMessageBox.StandardButton.Yes | QMessageBox.StandardButton.No,
                    QMessageBox.StandardButton.Yes,
                )
                if response == QMessageBox.StandardButton.Yes:
                    self.confirm_and_start_update(result, ask=False)
            elif self.update_check_manual:
                self.confirm_and_start_update(result)
        else:
            self.available_update = None
            self.update_btn.setText("Check for Updates")
            self.update_card.set_value(f"Up to date: {result['current']}", True)
            if self.update_check_manual:
                QMessageBox.information(self, "No update available", f"{APP_NAME} is up to date.")

    def confirm_and_start_update(self, update: dict[str, object], ask: bool = True) -> None:
        app_dir = install_root()
        exe_path = app_dir / f"{APP_NAME}.exe"
        if not exe_path.exists():
            QMessageBox.warning(
                self,
                "Updater unavailable",
                "Self-update is only available from the packaged app.",
            )
            return

        tag = str(update["tag"])
        asset_name = str(update["asset_name"])
        if ask:
            response = QMessageBox.question(
                self,
                "Install update",
                f"Install {APP_NAME} {tag}?\n\n{asset_name}",
                QMessageBox.StandardButton.Yes | QMessageBox.StandardButton.No,
                QMessageBox.StandardButton.Yes,
            )
            if response != QMessageBox.StandardButton.Yes:
                return

        self.append_line("")
        self.append_line(f"=== SELF UPDATE {tag} ===")
        self.append_line("[info] Downloading update in a detached updater. The app will close and reopen.")
        args = [
            "powershell.exe",
            "-NoProfile",
            "-ExecutionPolicy",
            "Bypass",
            "-File",
            str(backend_path()),
            "-Action",
            "selfupdate",
            "-PayloadRoot",
            str(payload_root()),
            "-AppDir",
            str(app_dir),
            "-UpdateUrl",
            str(update["asset_url"]),
            "-Version",
            tag,
            "-ParentPid",
            str(os.getpid()),
        ]
        try:
            subprocess.Popen(
                args,
                cwd=str(app_dir),
                creationflags=subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0,
            )
        except Exception as exc:
            QMessageBox.warning(self, "Update failed", str(exc))
            return

        QApplication.instance().quit()

    def set_busy(self, busy: bool) -> None:
        self.progress.setRange(0, 0 if busy else 1)
        self.progress.setValue(0 if busy else 1)
        for button in (self.install_btn, self.launch_btn, self.update_btn, self.canvas_check, self.resize_check, self.framepacing_check):
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
