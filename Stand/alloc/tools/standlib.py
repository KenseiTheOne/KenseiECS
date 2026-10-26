"""Shared helpers of the alloc module: paths, the two runtimes, builds and probe processes.

Every dotnet command is its own subprocess with an explicit environment (never a shell line),
so the .NET 8 and .NET 10 environments never leak into each other.

Nothing here depends on where the repository is checked out or on the machine:
  * paths are derived from this file (Stand/alloc/tools);
  * the local .NET SDK is found through DOTNET_ROOT, the `dotnet` on PATH or the standard
    install folders of macOS, Linux and Windows;
  * the official .NET 8 runtime (CI runs the tests on it) is downloaded from the release
    metadata of dotnet/core into Stand/out/tools/dotnet8, checked by SHA-512.
"""
import hashlib
import json
import os
import platform
import re
import shutil
import subprocess
import sys
import tarfile
import tempfile
import time
import urllib.request
import zipfile

MODULE = "alloc"
TOOLS = os.path.dirname(os.path.abspath(__file__))
MOD = os.path.dirname(TOOLS)                      # Stand/alloc
STAND = os.path.dirname(MOD)                      # Stand
WT = os.path.dirname(STAND)                       # the checkout the stand lives in (repository root)
OUT = os.path.join(STAND, "out")
WORK = os.path.join(OUT, MODULE)                  # this module's artifacts besides reports
REPORTS = os.path.join(OUT, "reports", MODULE)
TOOLS_OUT = os.path.join(OUT, "tools")
DOTNET8 = os.path.join(TOOLS_OUT, "dotnet8")      # the official .NET 8 runtime, downloaded
MIXED_ROOT = os.path.join(TOOLS_OUT, MODULE, "dnroot")   # local SDK + both runtimes, like CI
RELEASES_8 = "https://builds.dotnet.microsoft.com/dotnet/release-metadata/8.0/releases.json"
WINDOWS = os.name == "nt"


def log(msg):
    sys.stderr.write("[alloc] " + msg + "\n")
    sys.stderr.flush()


class StandError(Exception):
    pass


def exe(name):
    return name + ".exe" if WINDOWS else name


def rel(path):
    """A path for reports: relative to the repository root when it lies inside, else as given."""
    if not path:
        return path
    p = os.path.abspath(path)
    root = os.path.abspath(WT)
    if p == root:
        return "."
    if p.startswith(root + os.sep):
        return os.path.relpath(p, root).replace(os.sep, "/")
    return p


# ---------------------------------------------------------------------------
# the local SDK
# ---------------------------------------------------------------------------

_SDK_ROOT = None


def _has_sdk(root):
    return bool(root) and os.path.isfile(os.path.join(root, exe("dotnet"))) and os.path.isdir(os.path.join(root, "sdk")) \
        and any(os.path.isdir(os.path.join(root, "sdk", d)) for d in os.listdir(os.path.join(root, "sdk")))


def _standard_roots():
    home = os.path.expanduser("~")
    if sys.platform == "darwin":
        return ["/usr/local/share/dotnet", "/opt/homebrew/opt/dotnet/libexec", "/usr/local/opt/dotnet/libexec",
                os.path.join(home, ".dotnet")]
    if WINDOWS:
        pf = os.environ.get("ProgramFiles", r"C:\Program Files")
        return [os.path.join(pf, "dotnet"), os.path.join(home, ".dotnet")]
    return ["/usr/share/dotnet", "/usr/lib/dotnet", "/usr/lib64/dotnet", "/usr/local/share/dotnet", "/snap/dotnet-sdk/current",
            os.path.join(home, ".dotnet")]


def sdk_root():
    """The .NET SDK the stand builds with: DOTNET_ROOT, else the `dotnet` on PATH (symlinks resolved),
    else a standard install folder. It must hold an SDK that builds net8.0 projects."""
    global _SDK_ROOT
    if _SDK_ROOT:
        return _SDK_ROOT
    candidates = []
    if os.environ.get("DOTNET_ROOT"):
        candidates.append(os.environ["DOTNET_ROOT"])
    found = shutil.which("dotnet")
    if found:
        candidates.append(os.path.dirname(os.path.realpath(found)))
    candidates += _standard_roots()
    for c in candidates:
        if _has_sdk(c):
            _SDK_ROOT = os.path.abspath(c)
            return _SDK_ROOT
    raise StandError("no .NET SDK found: set DOTNET_ROOT or put `dotnet` on PATH (tried %s)" % ", ".join(candidates))


def sdk_host():
    return os.path.join(sdk_root(), exe("dotnet"))


# ---------------------------------------------------------------------------
# environment
# ---------------------------------------------------------------------------

def clean_env(extra=None):
    """The current environment without any DOTNET_/COMPlus_/MSBUILD knob, plus `extra`."""
    env = {k: v for k, v in os.environ.items()
           if not (k.startswith("DOTNET_") or k.startswith("COMPlus_") or k.startswith("MSBUILD"))}
    env["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1"
    env["DOTNET_NOLOGO"] = "1"
    env["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1"
    env["DOTNET_gcServer"] = "0"
    if extra:
        env.update(extra)
    return env


def sdk_env():
    """Environment for builds: the local SDK (net8.0 targets roll forward to its runtime locally)."""
    return clean_env({"DOTNET_ROOT": sdk_root(), "DOTNET_ROLL_FORWARD": "Major"})


def runtime_env(name, extra=None):
    """(host, env) running a net8.0 app on the named runtime: net10 = the newest local runtime via
    roll-forward (it must be .NET 10), net8 = the official 8.0 runtime in the mixed root, latest patch only."""
    if name == "net10":
        env = clean_env({"DOTNET_ROOT": sdk_root(), "DOTNET_ROLL_FORWARD": "Major"})
        host = sdk_host()
    elif name == "net8":
        env = clean_env({"DOTNET_ROOT": MIXED_ROOT, "DOTNET_ROLL_FORWARD": "LatestPatch",
                         "DOTNET_MULTILEVEL_LOOKUP": "0"})
        host = os.path.join(MIXED_ROOT, exe("dotnet"))
    else:
        raise StandError("unknown runtime " + name)
    if extra:
        env.update(extra)
    return host, env


def expected_runtime_prefix(name):
    return {"net10": ".NET 10.", "net8": ".NET 8."}[name]


def run(cmd, env, cwd=None, timeout=1800, check=True, capture=True):
    """Run one command; returns (returncode, stdout, stderr)."""
    p = subprocess.run(cmd, env=env, cwd=cwd, timeout=timeout,
                       stdout=subprocess.PIPE if capture else None,
                       stderr=subprocess.PIPE if capture else None, text=True, encoding="utf-8", errors="replace")
    if check and p.returncode != 0:
        tail = ((p.stdout or "")[-4000:] + "\n" + (p.stderr or "")[-4000:]).strip()
        raise StandError("command failed (%d): %s\n%s" % (p.returncode, " ".join(cmd), tail))
    return p.returncode, p.stdout or "", p.stderr or ""


# ---------------------------------------------------------------------------
# .NET 8 runtime and the mixed DOTNET_ROOT
# ---------------------------------------------------------------------------

def _runtime_versions(root):
    d = os.path.join(root, "shared", "Microsoft.NETCore.App")
    if not os.path.isdir(d):
        return []
    return sorted((v for v in os.listdir(d) if os.path.isdir(os.path.join(d, v))), key=_version_key)


def _version_key(v):
    out = []
    for part in v.split("-")[0].split("."):
        out.append(int(part) if part.isdigit() else 0)
    return out


def _dotnet8_version(root):
    vs = [v for v in _runtime_versions(root) if v.startswith("8.0.")]
    return vs[-1] if vs else None


def runtime_rid():
    """The runtime identifier of this machine for the official runtime archives."""
    machine = platform.machine().lower()
    arch = {"x86_64": "x64", "amd64": "x64", "x64": "x64", "arm64": "arm64", "aarch64": "arm64",
            "armv7l": "arm", "i386": "x86", "i686": "x86", "x86": "x86"}.get(machine)
    if arch is None:
        raise StandError("unsupported processor architecture for the .NET 8 download: " + machine)
    if sys.platform == "darwin":
        return "osx-" + arch
    if WINDOWS:
        return "win-" + arch
    if sys.platform.startswith("linux"):
        musl = os.path.exists("/etc/alpine-release") or "musl" in (platform.libc_ver()[0] or "").lower()
        return ("linux-musl-" if musl else "linux-") + arch
    raise StandError("unsupported operating system for the .NET 8 download: " + sys.platform)


def _safe_member(name):
    p = os.path.normpath(name)
    return not (p.startswith("..") or os.path.isabs(p) or p.startswith(os.sep))


def _download_dotnet8(dest):
    """Latest 8.0 runtime for this machine from the official release metadata, checked by SHA-512."""
    rid = runtime_rid()
    log("downloading the release metadata " + RELEASES_8)
    with urllib.request.urlopen(RELEASES_8, timeout=120) as r:
        meta = json.loads(r.read().decode("utf-8"))
    version = meta["latest-runtime"]
    rel_ = next(x for x in meta["releases"] if (x.get("runtime") or {}).get("version") == version)
    want = ".zip" if WINDOWS else ".tar.gz"
    files = [x for x in rel_["runtime"]["files"] if x.get("rid") == rid and x["name"].endswith(want)]
    if not files:
        raise StandError("the .NET %s release has no %s runtime archive for %s" % (version, want, rid))
    f = files[0]
    parent = os.path.dirname(dest)
    dl = tempfile.mkdtemp(prefix=".dl-", dir=parent)
    try:
        archive = os.path.join(dl, os.path.basename(f["url"]))
        log("downloading the .NET %s runtime (%s) %s" % (version, rid, f["url"]))
        with urllib.request.urlopen(f["url"], timeout=600) as r, open(archive, "wb") as o:
            shutil.copyfileobj(r, o)
        h = hashlib.sha512()
        with open(archive, "rb") as i:
            for chunk in iter(lambda: i.read(1 << 20), b""):
                h.update(chunk)
        if h.hexdigest().lower() != f["hash"].lower():
            raise StandError("SHA-512 mismatch for " + f["url"])
        tmp = tempfile.mkdtemp(prefix=".dotnet8-", dir=parent)
        if want == ".zip":
            with zipfile.ZipFile(archive) as z:
                for name in z.namelist():
                    if not _safe_member(name):
                        raise StandError("unexpected path in the runtime archive: " + name)
                z.extractall(tmp)
        else:
            with tarfile.open(archive, "r:gz") as t:
                for m in t.getmembers():
                    if not _safe_member(m.name) or ((m.issym() or m.islnk()) and not _safe_member(os.path.join(os.path.dirname(m.name), m.linkname))):
                        raise StandError("unexpected path in the runtime archive: " + m.name)
                if hasattr(tarfile, "data_filter"):
                    t.extractall(tmp, filter="data")
                else:
                    t.extractall(tmp)
        return tmp
    finally:
        shutil.rmtree(dl, ignore_errors=True)


def ensure_dotnet8():
    """Stand/out/tools/dotnet8: the official .NET 8 runtime for this machine, downloaded on first use.
    Created atomically (two runs may race for it) and reused when another process created it first."""
    if _dotnet8_version(DOTNET8):
        return _dotnet8_version(DOTNET8)
    os.makedirs(os.path.dirname(DOTNET8), exist_ok=True)
    tmp = _download_dotnet8(DOTNET8)
    try:
        os.rename(tmp, DOTNET8)
    except OSError:
        shutil.rmtree(tmp, ignore_errors=True)
        if not _dotnet8_version(DOTNET8):
            raise
    v = _dotnet8_version(DOTNET8)
    if not v:
        raise StandError("the downloaded runtime has no Microsoft.NETCore.App 8.0.x: " + DOTNET8)
    return v


def _link_dir(src, dst):
    """A directory link: a symlink, or on Windows without the privilege a junction."""
    try:
        os.symlink(src, dst, target_is_directory=True)
    except OSError:
        if not WINDOWS:
            raise
        subprocess.run(["cmd", "/c", "mklink", "/J", dst, src], check=True, stdout=subprocess.PIPE, stderr=subprocess.PIPE)


def ensure_mixed_root():
    """Stand/out/tools/alloc/dnroot: the local SDK and both runtimes in one DOTNET_ROOT, as CI has
    them (setup-dotnet 8.0.x and 10.0.x). The muxer itself is a copy (it finds the root from its own
    real path); host/fxr, sdk, packs and the frameworks are links into the local SDK and into
    Stand/out/tools/dotnet8."""
    v8 = ensure_dotnet8()
    if os.path.exists(os.path.join(MIXED_ROOT, exe("dotnet"))) and _check_mixed_root(v8):
        return v8
    root = sdk_root()
    parent = os.path.dirname(MIXED_ROOT)
    os.makedirs(parent, exist_ok=True)
    tmp = tempfile.mkdtemp(prefix=".dnroot-", dir=parent)
    shutil.copy2(os.path.join(root, exe("dotnet")), os.path.join(tmp, exe("dotnet")))
    for name in ("host", "sdk", "packs", "sdk-manifests", "templates", "library-packs", "metadata"):
        src = os.path.join(root, name)
        if os.path.exists(src):
            _link_dir(src, os.path.join(tmp, name))
    fw = os.path.join(tmp, "shared", "Microsoft.NETCore.App")
    os.makedirs(fw)
    for v in _runtime_versions(root):
        _link_dir(os.path.join(root, "shared", "Microsoft.NETCore.App", v), os.path.join(fw, v))
    if not os.path.exists(os.path.join(fw, v8)):
        _link_dir(os.path.join(DOTNET8, "shared", "Microsoft.NETCore.App", v8), os.path.join(fw, v8))
    asp = os.path.join(root, "shared", "Microsoft.AspNetCore.App")
    if os.path.isdir(asp):
        _link_dir(asp, os.path.join(tmp, "shared", "Microsoft.AspNetCore.App"))
    if os.path.lexists(MIXED_ROOT):
        shutil.rmtree(MIXED_ROOT)
    os.rename(tmp, MIXED_ROOT)
    if not _check_mixed_root(v8):
        raise StandError("the mixed DOTNET_ROOT does not list both runtimes and an SDK: " + MIXED_ROOT)
    return v8


def _check_mixed_root(v8):
    host, env = runtime_env("net8")
    try:
        _, out, _ = run([host, "--list-runtimes"], env, timeout=60)
        _, sdks, _ = run([host, "--list-sdks"], env, timeout=60)
    except Exception:
        return False
    return ("Microsoft.NETCore.App " + v8) in out and "Microsoft.NETCore.App 10." in out and bool(sdks.strip())


# ---------------------------------------------------------------------------
# source trees, flavors, builds
# ---------------------------------------------------------------------------

def tree_of(src):
    """The folder holding KenseiECS/, KenseiECS.Tests/ ... for a KenseiECS package folder."""
    return os.path.dirname(os.path.abspath(src))


def _same(a, b):
    try:
        return os.path.samefile(a, b)
    except OSError:
        return False


def default_flavor(src):
    src = os.path.abspath(src)
    if _same(src, os.path.join(WT, "KenseiECS")):
        return "wt"
    srcroot = os.path.join(OUT, "src")
    r = os.path.relpath(src, srcroot)
    if not r.startswith("..") and r.count(os.sep) == 1 and r.endswith(os.sep + "KenseiECS"):
        return r.split(os.sep)[0]
    return "custom-" + hashlib.sha1(src.encode("utf-8")).hexdigest()[:8]


def package_version(src):
    try:
        with open(os.path.join(src, "package.json"), encoding="utf-8") as f:
            return json.load(f).get("version")
    except Exception:
        return None


def _vparse(v):
    core = re.split(r"[-+]", v or "")[0].split(".")
    out = []
    for i in range(3):
        try:
            out.append(int(core[i]))
        except (IndexError, ValueError):
            out.append(0)
    return tuple(out)


def contract_version(src):
    """The version whose documents the package folder has: package.json, or the next patch when CHANGELOG.md starts
    with a non-empty [Unreleased] section and package.json is not above the CHANGELOG's last release (a working copy
    between releases); a package.json above that release is the release in the making itself."""
    version = package_version(src)
    try:
        with open(os.path.join(src, "CHANGELOG.md"), encoding="utf-8", errors="replace") as f:
            text = f.read()
    except OSError:
        return version
    heads = re.findall(r"^## \[([^\]]+)\]", text, re.M)
    if version and heads and heads[0].strip().lower() == "unreleased":
        body = re.search(r"^## \[Unreleased\][^\n]*\n(.*?)(?=^## \[|\Z)", text, re.M | re.S | re.I)
        last = next((h for h in heads[1:] if re.match(r"\d", h)), None)
        if body and body.group(1).strip() and (not last or _vparse(version) <= _vparse(last)):
            x = _vparse(version)
            return "%d.%d.%d" % (x[0], x[1], x[2] + 1)
    return version


def version_skip(meta, version):
    """None when a mutant (its "since"/"until" header fields) is meant for trees of this contract version, else why not."""
    if not version:
        return None
    v = _vparse(version)
    why = ""
    if meta.get("since") and v < _vparse(meta["since"]):
        why = "contract version %s: the mutant is for %s and newer (it changes code of those versions)" % (version, meta["since"])
    elif meta.get("until") and v > _vparse(meta["until"]):
        why = "contract version %s: the mutant is for %s and older (it changes code that later versions rewrote)" % (version, meta["until"])
    if why and meta.get("version-note"):
        why += "; " + meta["version-note"]
    return why or None


def stand_ref(src):
    """{"ref", "commit", ...} of a tree that Stand/run.sh extracted with git archive, or None."""
    try:
        with open(os.path.join(tree_of(src), ".stand-ref"), encoding="utf-8") as f:
            return json.load(f)
    except Exception:
        return None


def git(args, timeout=120, binary=False):
    """Read-only git in the repository the stand lives in (log, show, archive only). None on failure."""
    try:
        p = subprocess.run(["git", "-C", WT] + args, stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=timeout)
    except Exception:
        return None
    if p.returncode != 0:
        return None
    return p.stdout if binary else p.stdout.decode("utf-8", "replace").strip()


def tree_commit(src, flavor):
    """The commit of a tree extracted by Stand/run.sh (.stand-ref), else of the tag named like the flavor
    or like the tree's folder under Stand/out/src/ (trees extracted before .stand-ref existed)."""
    ref = stand_ref(src)
    if ref and ref.get("commit"):
        return ref["commit"]
    for name in (flavor, default_flavor(src)):
        if name and name != "wt" and not name.startswith("custom-"):
            commit = git(["log", "-1", "--format=%H", "refs/tags/" + name, "--"], timeout=30)
            if commit:
                return commit
    return None


def git_extract(commit, paths, dest):
    """`git archive <commit> <paths>` unpacked into dest (read-only use of the repository). Returns the
    paths that were extracted; paths the commit does not have are skipped."""
    listing = git(["show", commit + ":"], timeout=60) or ""
    present = {x.strip().rstrip("/") for x in listing.splitlines() if x.strip()}
    want = [p for p in paths if p in present]
    if not want:
        return []
    data = git(["archive", "--format=tar", commit] + want, binary=True, timeout=300)
    if data is None:
        return []
    os.makedirs(dest, exist_ok=True)
    import io
    with tarfile.open(fileobj=io.BytesIO(data)) as t:
        for m in t.getmembers():
            if not _safe_member(m.name):
                raise StandError("unexpected path in git archive: " + m.name)
        if hasattr(tarfile, "data_filter"):
            t.extractall(dest, filter="data")
        else:
            t.extractall(dest)
    return want


def build(project, src, flavor, debug, configuration="Release", extra_props=None):
    """dotnet build of one stand project against one source tree. Returns the output dll."""
    gensrc = os.path.join(tree_of(src), "KenseiECS.Generators")
    tag = "dbg" if debug else "rel"
    proj = os.path.join(MOD, project)
    csproj = next(os.path.join(proj, f) for f in os.listdir(proj) if f.endswith(".csproj"))
    name = os.path.splitext(os.path.basename(csproj))[0]
    cmd = [sdk_host(), "build", csproj, "-c", configuration,
           "-p:KenseiSrc=" + os.path.abspath(src), "-p:KenseiGenSrc=" + gensrc,
           "-p:StandFlavor=" + flavor, "-p:KenseiDebug=" + ("true" if debug else "false"),
           "-p:StandPython=" + sys.executable,
           "--disable-build-servers", "-nologo", "-v:q", "-clp:ErrorsOnly"]
    for k, v in (extra_props or {}).items():
        cmd.append("-p:%s=%s" % (k, v))
    t0 = time.time()
    run(cmd, sdk_env(), timeout=1800)
    dll = os.path.join(OUT, "bin", "%s-%s" % (flavor, tag), name, configuration, "net8.0", name + ".dll")
    if not os.path.exists(dll):
        raise StandError("build produced no " + dll)
    log("built %s for %s-%s in %.1fs" % (name, flavor, tag, time.time() - t0))
    return dll


# ---------------------------------------------------------------------------
# probe processes
# ---------------------------------------------------------------------------

def parse_lines(text, prefix):
    out = []
    for line in text.splitlines():
        if line.startswith(prefix + " "):
            out.append(json.loads(line[len(prefix) + 1:]))
    return out


def probe(dll, runtime, args, extra_env=None, timeout=900):
    """Run a probe dll on a runtime; returns (info, results, stderr). Checks the runtime it got."""
    host, env = runtime_env(runtime, extra_env)
    code, out, err = run([host, dll] + args, env, timeout=timeout, check=False)
    infos = parse_lines(out, "INFO")
    info = infos[0] if infos else None
    if info is not None and not info["runtime"].startswith(expected_runtime_prefix(runtime)):
        raise StandError("asked for %s, the probe ran on %s (install the .NET 10 SDK, or pass --runtimes net8)"
                         % (runtime, info["runtime"]))
    if code not in (0, 1):
        raise StandError("probe %s %s failed (%d):\n%s\n%s" % (os.path.basename(dll), " ".join(args), code,
                                                             out[-3000:], err[-3000:]))
    return info, parse_lines(out, "RESULT"), err


def mem_probe(dll, runtime, types, entities, layout, filters):
    host, env = runtime_env(runtime)
    _, out, err = run([host, dll, "--types", str(types), "--entities", str(entities),
                       "--layout", layout, "--filters", str(filters)], env, timeout=900)
    rows = parse_lines(out, "MEM")
    if not rows:
        raise StandError("memory probe printed nothing:\n" + out[-2000:] + err[-2000:])
    row = rows[0]
    if not row["runtime"].startswith(expected_runtime_prefix(runtime)):
        raise StandError("asked for %s, the memory probe ran on %s" % (runtime, row["runtime"]))
    return row


class Lock:
    """mkdir-based lock, so two runs of this module never build into the same folder at once.
    A lock whose owner process is gone is taken over."""

    def __init__(self, name):
        self.path = os.path.join(WORK, "locks", name + ".lock")

    def _stale(self):
        try:
            with open(os.path.join(self.path, "pid")) as f:
                pid = int(f.read().strip())
        except (OSError, ValueError):
            return os.path.exists(self.path) and time.time() - os.path.getmtime(self.path) > 60
        if WINDOWS:
            return False
        try:
            os.kill(pid, 0)
            return False
        except ProcessLookupError:
            return True
        except PermissionError:
            return False

    def __enter__(self):
        os.makedirs(os.path.dirname(self.path), exist_ok=True)
        t0 = time.time()
        while True:
            try:
                os.mkdir(self.path)
                with open(os.path.join(self.path, "pid"), "w") as f:
                    f.write(str(os.getpid()))
                return self
            except FileExistsError:
                if self._stale():
                    shutil.rmtree(self.path, ignore_errors=True)
                    continue
                if time.time() - t0 > 3600:
                    raise StandError("lock held for an hour: " + self.path)
                time.sleep(1)

    def __exit__(self, *exc):
        shutil.rmtree(self.path, ignore_errors=True)
