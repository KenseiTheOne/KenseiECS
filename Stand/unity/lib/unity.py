#!/usr/bin/env python3
"""KenseiECS test stand, module unity: finds the Unity editor and its build modules (no Unity started).

  unity.py find [--path P] [--version V] [--target auto|android|osx|win64|linux64]
      Prints one JSON object: the editor binary, its version and root folder, the build modules
      installed in it, the build target to use and whether that target (and IL2CPP for it) is there.

The editor is, in this order: --path (or the UNITY environment variable: the binary, Unity.app or the
editor folder); --version (or UNITY_VERSION) looked up in the Unity Hub install folders; otherwise
the newest editor of the Hub install folders that has the build module of the target (any editor if
none has it). Hub install folders: the default ones of macOS, Windows and Linux, the secondary
install path set in the Hub (secondaryInstallPath.json) and editors located by hand (editors*.json).
"""
import argparse
import glob
import json
import os
import platform
import re
import subprocess
import sys

TARGETS = {
    # target: (-buildTarget, build module folder, NamedBuildTarget group used in the report)
    "android": ("Android", "AndroidPlayer"),
    "osx": ("OSXUniversal", "MacStandaloneSupport"),
    "win64": ("Win64", "WindowsStandaloneSupport"),
    "linux64": ("Linux64", "LinuxStandaloneSupport"),
}


def host():
    s = platform.system()
    return "mac" if s == "Darwin" else "windows" if s == "Windows" or s.startswith(("MINGW", "MSYS", "CYGWIN")) else "linux"


def host_target():
    return {"mac": "osx", "windows": "win64", "linux": "linux64"}[host()]


def hub_config_dirs():
    h = host()
    home = os.path.expanduser("~")
    if h == "mac":
        return [os.path.join(home, "Library", "Application Support", "UnityHub")]
    if h == "windows":
        return [os.path.join(os.environ.get("APPDATA", os.path.join(home, "AppData", "Roaming")), "UnityHub")]
    return [os.path.join(os.environ.get("XDG_CONFIG_HOME", os.path.join(home, ".config")), "UnityHub")]


def default_install_dirs():
    h = host()
    home = os.path.expanduser("~")
    if h == "mac":
        return ["/Applications/Unity/Hub/Editor", os.path.join(home, "Applications", "Unity", "Hub", "Editor")]
    if h == "windows":
        dirs = []
        for var in ("ProgramFiles", "ProgramW6432", "ProgramFiles(x86)"):
            if os.environ.get(var):
                dirs.append(os.path.join(os.environ[var], "Unity", "Hub", "Editor"))
        dirs.append("C:\\Program Files\\Unity\\Hub\\Editor")
        return dirs
    return [os.path.join(home, "Unity", "Hub", "Editor"), "/opt/unity/hub/editor", "/opt/Unity/Hub/Editor"]


def read_json(path):
    try:
        with open(path, encoding="utf-8") as f:
            return json.load(f)
    except (OSError, ValueError):
        return None


def hub_install_dirs():
    dirs = list(default_install_dirs())
    located = []
    for cfg in hub_config_dirs():
        sec = read_json(os.path.join(cfg, "secondaryInstallPath.json"))
        if isinstance(sec, str) and sec.strip():
            dirs.append(sec.strip())
        for name in ("editors-v2.json", "editors.json"):
            data = read_json(os.path.join(cfg, name))
            # Editors located by hand: entries carry a "location" (the binary or the folder).
            for loc in re.findall(r'"location"\s*:\s*\[?\s*"([^"]+)"', json.dumps(data)) if data else []:
                located.append(loc.replace("\\\\", "\\"))
    seen, out = set(), []
    for d in dirs:
        if d and d not in seen:
            seen.add(d)
            out.append(d)
    return out, located


def binary_in(root):
    """The editor binary of an editor folder (Hub layout <install>/<version>/)."""
    h = host()
    for rel in (["Unity.app", "Contents", "MacOS", "Unity"], ["Editor", "Unity.exe"], ["Editor", "Unity"], ["Unity"]):
        p = os.path.join(root, *rel)
        if os.path.isfile(p):
            return p
    if h == "mac" and root.endswith(".app"):
        p = os.path.join(root, "Contents", "MacOS", "Unity")
        if os.path.isfile(p):
            return p
    return None


def editor_root(binary):
    """Editor folder of a binary: .../<version>/Unity.app/Contents/MacOS/Unity or .../<version>/Editor/Unity(.exe)."""
    b = os.path.abspath(binary)
    parts = b.replace("\\", "/").split("/")
    if "Unity.app" in parts:
        i = len(parts) - 1 - parts[::-1].index("Unity.app")
        return os.sep.join(parts[:i]) or "/"
    d = os.path.dirname(b)
    if os.path.basename(d).lower() == "editor":
        return os.path.dirname(d)
    return d


def normalize_binary(path):
    p = os.path.expanduser(path)
    if os.path.isdir(p):
        return binary_in(p)
    return p if os.path.isfile(p) else None


def version_of(binary, root):
    name = os.path.basename(root)
    if re.match(r"^\d+\.\d+\.\d+[abfpx]\d+", name):
        return name
    plist = os.path.join(root, "Unity.app", "Contents", "Info.plist")
    if os.path.isfile(plist):
        try:
            text = open(plist, encoding="utf-8", errors="replace").read()
            m = re.search(r"<key>CFBundleVersion</key>\s*<string>([^<]+)</string>", text)
            if m:
                return m.group(1).strip()
        except OSError:
            pass
    try:
        p = subprocess.run([binary, "-version"], stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True, timeout=90)
        m = re.search(r"\d+\.\d+\.\d+[abfpx]\d+", p.stdout or "")
        if m:
            return m.group(0)
    except (OSError, subprocess.SubprocessError):
        pass
    return "unknown"


def version_key(v):
    m = re.match(r"^(\d+)\.(\d+)\.(\d+)([abfpx]?)(\d*)", v or "")
    if not m:
        return (0, 0, 0, 0, 0)
    rank = {"a": 0, "b": 1, "f": 3, "p": 4, "x": 2, "": 3}[m.group(4)]
    return (int(m.group(1)), int(m.group(2)), int(m.group(3)), rank, int(m.group(5) or 0))


def engines_dirs(root):
    return [os.path.join(root, "PlaybackEngines"),
            os.path.join(root, "Unity.app", "Contents", "PlaybackEngines"),
            os.path.join(root, "Editor", "Data", "PlaybackEngines")]


def modules(root):
    """Build module folder name -> path (names as Unity writes them, compared without case)."""
    out = {}
    for d in engines_dirs(root):
        if not os.path.isdir(d):
            continue
        for name in os.listdir(d):
            p = os.path.join(d, name)
            if os.path.isdir(p) and not name.startswith("."):
                out.setdefault(name, p)
    return out


def module_path(mods, folder):
    for name, p in mods.items():
        if name.lower() == folder.lower():
            return p
    return None


def has_il2cpp(target, path):
    if not path:
        return False
    if target == "android":
        return os.path.isdir(os.path.join(path, "Variations", "il2cpp"))
    return bool(glob.glob(os.path.join(path, "Variations", "*il2cpp*")))


def candidates():
    dirs, located = hub_install_dirs()
    out = []
    for d in dirs:
        if not os.path.isdir(d):
            continue
        for name in sorted(os.listdir(d)):
            b = binary_in(os.path.join(d, name))
            if b:
                out.append(b)
    for loc in located:
        b = normalize_binary(loc)
        if b and b not in out:
            out.append(b)
    return out


def describe(binary, target, source):
    root = editor_root(binary)
    mods = modules(root)
    want = target
    if want == "auto":
        want = "android" if module_path(mods, TARGETS["android"][1]) else host_target()
    build_target, folder = TARGETS[want]
    path = module_path(mods, folder)
    return {
        "path": binary, "root": root, "version": version_of(binary, root), "source": source,
        "modules": sorted(mods), "target": want, "buildTarget": build_target, "targetModule": folder,
        "targetInstalled": bool(path), "il2cpp": has_il2cpp(want, path),
        "osxPlayer": bool(module_path(mods, "MacStandaloneSupport")),
        "osxIl2cpp": has_il2cpp("osx", module_path(mods, "MacStandaloneSupport")),
    }


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("cmd", choices=["find"])
    ap.add_argument("--path", default=os.environ.get("UNITY", ""))
    ap.add_argument("--version", default=os.environ.get("UNITY_VERSION", ""))
    ap.add_argument("--target", default="auto", choices=["auto"] + sorted(TARGETS))
    a = ap.parse_args()
    if a.path:
        b = normalize_binary(a.path)
        if not b:
            print(json.dumps({"error": "UNITY / --unity does not point to a Unity editor: %s" % a.path}))
            return 2
        print(json.dumps(describe(b, a.target, "UNITY / --unity")))
        return 0
    cands = candidates()
    infos = [describe(b, a.target, "Unity Hub") for b in cands]
    if a.version:
        hit = [i for i in infos if i["version"] == a.version]
        if not hit:
            print(json.dumps({"error": "Unity %s is not installed in the Unity Hub folders (%s); found: %s" % (
                a.version, ", ".join(hub_install_dirs()[0]), ", ".join(i["version"] for i in infos) or "none")}))
            return 2
        info = hit[0]
        info["source"] = "Unity Hub, UNITY_VERSION / --unity-version"
    else:
        if not infos:
            print(json.dumps({"error": "no Unity editor found in the Unity Hub folders (%s); set UNITY to the editor binary" % (
                ", ".join(hub_install_dirs()[0]))}))
            return 2
        ok = [i for i in infos if i["targetInstalled"]] if a.target != "auto" else \
            [i for i in infos if i["target"] == "android"] or infos
        pool = ok or infos
        info = sorted(pool, key=lambda i: version_key(i["version"]))[-1]
        info["source"] = "Unity Hub, newest editor" + (" with the %s module" % info["targetModule"] if info["targetInstalled"] else "")
    info["candidates"] = ["%s (%s)" % (i["version"], ", ".join(i["modules"])) for i in infos]
    print(json.dumps(info))
    return 0


if __name__ == "__main__":
    sys.exit(main())
