"""Rebuild the pinned x64 codec bundle without changing the system MSYS2 installation.

Requires Windows, Python 3.14+, and an existing MSYS2 UCRT64 GCC/CMake/Ninja toolchain.
Default: stage and verify. --apply backs up and replaces the two runtime directories.
"""
import argparse
import concurrent.futures
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import struct
import sys
import tarfile
import tempfile
import urllib.request
import uuid


def digest(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def run(*args, **kwargs):
    subprocess.run([str(a) for a in args], check=True, **kwargs)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--apply", action="store_true")
    parser.add_argument("--msys", default="C:/msys64")
    parser.add_argument("--jobs", type=int, default=12)
    args = parser.parse_args()
    if os.name != "nt" or sys.version_info < (3, 14):
        raise RuntimeError("Windows and Python 3.14+ are required (tar.zst support).")
    repo = Path(__file__).resolve().parent.parent
    compiler = Path(args.msys).resolve() / "ucrt64/bin"
    for name in ("gcc.exe", "g++.exe", "cmake.exe", "ninja.exe", "objdump.exe"):
        if not (compiler / name).is_file():
            raise FileNotFoundError(compiler / name)

    # GNU ld does not reliably handle non-ASCII absolute library paths on Windows.
    alias = Path(tempfile.gettempdir()) / ("hdriv-codecs-" + hashlib.sha256(str(repo).encode()).hexdigest()[:12])
    if not alias.exists():
        env = dict(os.environ, HDRIV_ALIAS=str(alias), HDRIV_REPO=str(repo))
        run("pwsh", "-NoProfile", "-Command",
            "New-Item -ItemType Junction -Path $env:HDRIV_ALIAS -Target $env:HDRIV_REPO | Out-Null", env=env)
    if alias.resolve() != repo or not str(alias).isascii():
        raise RuntimeError("Build alias must point at this checkout and have an ASCII path.")
    root = alias / "external/_deps/codec-upgrade"
    downloads = root / "downloads"
    downloads.mkdir(parents=True, exist_ok=True)
    lock = json.loads((repo / "eng/codecs.lock.json").read_text())
    ultra_spec = next(item for item in lock["sources"] if item["name"] == "libultrahdr")
    ultra_source = root / "sources" / ultra_spec["sourceDirectory"]

    def download(item):
        path = downloads / item.get("archive", item["url"].rsplit("/", 1)[1])
        if not path.exists():
            partial = path.with_suffix(path.suffix + ".part")
            with urllib.request.urlopen(item["url"], timeout=120) as response, partial.open("wb") as output:
                shutil.copyfileobj(response, output)
            if digest(partial) != item["sha256"]:
                raise RuntimeError("Download checksum mismatch: " + item["name"])
            partial.replace(path)
        if digest(path) != item["sha256"]:
            raise RuntimeError("Cached checksum mismatch: " + item["name"])
        return item, path

    with concurrent.futures.ThreadPoolExecutor(max_workers=6) as pool:
        archives = list(pool.map(download, lock["packages"] + lock["sources"]))
    prefix = root / "prefix/ucrt64"
    for item, path in archives:
        stamp = root / "extracted" / item["sha256"]
        if stamp.exists():
            continue
        with tarfile.open(path, "r:*") as archive:
            if item in lock["packages"]:
                members = [m for m in archive if m.isfile() and m.name.startswith("ucrt64/")]
                archive.extractall(root / "prefix", members=members, filter="data")
            else:
                archive.extractall(root / "sources", members=[m for m in archive if m.isfile()], filter="data")
        stamp.parent.mkdir(exist_ok=True)
        stamp.touch()

    os.environ["PATH"] = str(prefix / "bin") + os.pathsep + str(compiler) + os.pathsep + os.environ["PATH"]
    cmake = compiler / "cmake.exe"
    common = ["-G", "Ninja", "-DCMAKE_BUILD_TYPE=Release", f"-DCMAKE_PREFIX_PATH={prefix.as_posix()}",
              f"-DCMAKE_CXX_COMPILER={(compiler / 'g++.exe').as_posix()}"]

    def build(name, source, options, install=False):
        output = root / ("build-" + name)
        run(cmake, "-S", source, "-B", output, *common, *options)
        run(cmake, "--build", output, "--parallel", args.jobs)
        if install:
            run(cmake, "--install", output)
        return output

    # The upstream Ultra HDR backend requires its pinned PR1503 API. Keep this
    # private library separate from the current libheif used for normal decoding.
    heif_source = root / "sources/libheif-4a3f74bc593ebfc29becc1ed5dd0a61cc66d40e1"
    heif_prefix = root / "heif-ultrahdr"
    patch = ultra_source / "cmake/patches/libheif_pr1503.patch"
    patch_args = ["git", "apply", "--directory=" + heif_source.resolve().relative_to(repo).as_posix()]
    if subprocess.run([*patch_args, "--reverse", "--check", str(patch)], cwd=repo,
                      capture_output=True).returncode != 0:
        run(*patch_args, str(patch), cwd=repo)
    heif_cmake = heif_source / "libheif/CMakeLists.txt"
    naming = '\nset_target_properties(heif PROPERTIES OUTPUT_NAME "heif-uhdr")\n'
    if naming not in heif_cmake.read_text():
        with heif_cmake.open("a") as output:
            output.write(naming)
    build("heif-ultrahdr", heif_source, [
        f"-DCMAKE_C_COMPILER={(compiler / 'gcc.exe').as_posix()}", "-DBUILD_SHARED_LIBS=ON",
        f"-DCMAKE_INSTALL_PREFIX={heif_prefix.as_posix()}", "-DWITH_EXPERIMENTAL_GAIN_MAP=ON",
        "-DWITH_EXAMPLES=OFF", "-DBUILD_TESTING=OFF", "-DWITH_DOCS=OFF", "-DWITH_GDK_PIXBUF=OFF",
        "-DENABLE_PLUGIN_LOADING=OFF", "-DWITH_LIBSHARPYUV=OFF", "-DWITH_OpenH264_DECODER=OFF",
        "-DWITH_LIBDE265=ON", "-DWITH_X265=ON", "-DWITH_AOM_DECODER=ON", "-DWITH_AOM_ENCODER=ON"], install=True)

    # Keep private HEIF headers ahead of the general dependency prefix.
    # Definition-list handling and sRGB NCLX signaling are now fixed upstream.
    ultra_cmake = ultra_source / "CMakeLists.txt"
    private_headers = '\nif(TARGET libheif::heif)\n  target_include_directories(core BEFORE PRIVATE $<TARGET_PROPERTY:libheif::heif,INTERFACE_INCLUDE_DIRECTORIES>)\nendif()\n'
    if private_headers not in ultra_cmake.read_text():
        with ultra_cmake.open("a") as output:
            output.write(private_headers)
    ultra = build("ultrahdr-" + ultra_spec["revision"][:12], ultra_source, [
        f"-DCMAKE_C_COMPILER={(compiler / 'gcc.exe').as_posix()}", "-DBUILD_SHARED_LIBS=ON",
        "-DUHDR_BUILD_TESTS=OFF", "-DUHDR_BUILD_BENCHMARK=OFF", "-DUHDR_BUILD_DEPS=OFF",
        "-DCMAKE_NO_SYSTEM_FROM_IMPORTED=ON", "-DUHDR_ENABLE_GLES=OFF", "-DUHDR_ENABLE_HEIF=ON", "-ULIBHEIF_HAS_GAIN_MAP",
        f"-Dlibheif_DIR={heif_prefix.as_posix()}/lib/cmake/libheif", "-DUHDR_WRITE_XMP=ON", "-DUHDR_WRITE_ISO=ON"])
    if "LIBHEIF_HAS_GAIN_MAP:INTERNAL=1" not in (ultra / "CMakeCache.txt").read_text():
        raise RuntimeError("Ultra HDR HEIF/AVIF backend was silently disabled")
    build("openexr-pinned", root / "sources/openexr-3.5.1", [
        f"-DCMAKE_C_COMPILER={(compiler / 'gcc.exe').as_posix()}", "-DBUILD_SHARED_LIBS=ON",
        "-DBUILD_TESTING=OFF", "-DOPENEXR_BUILD_TOOLS=OFF", "-DOPENEXR_INSTALL_TOOLS=OFF",
        "-DOPENEXR_BUILD_EXAMPLES=OFF", "-DOPENEXR_BUILD_PYTHON=OFF",
        f"-DCMAKE_INSTALL_PREFIX={prefix.as_posix()}"], install=True)
    bridge = build("bridge-pinned", alias / "native/HdrImageViewer.Native",
                   ["-DHDRIMAGEVIEWER_REQUIRE_OPENEXR=ON"]) / "Release"

    staging = root / "staging" / uuid.uuid4().hex
    tools = staging / "encoders"
    native = staging / "native"
    available = {p.name.lower(): p for folder in (bridge, ultra, heif_prefix / "bin", prefix / "bin")
                 for p in folder.iterdir() if p.suffix.lower() in (".dll", ".exe")}
    system = Path(os.environ["SystemRoot"]) / "System32"

    def copy_closure(names, destination):
        destination.mkdir(parents=True)
        pending = list(names)
        copied = set()
        while pending:
            name = pending.pop().lower()
            if name in copied:
                continue
            source = available[name]
            shutil.copy2(source, destination / source.name)
            copied.add(name)
            output = subprocess.check_output([str(compiler / "objdump.exe"), "-p", str(source)], text=True)
            for dependency in re.findall(r"DLL Name:\s*(\S+)", output):
                key = dependency.lower()
                if key in available:
                    pending.append(key)
                elif not key.startswith(("api-ms-", "ext-ms-")) and not (system / dependency).is_file():
                    raise RuntimeError(f"Unresolved dependency: {source.name} -> {dependency}")

    copy_closure(["cjxl.exe", "djxl.exe", "jxlinfo.exe", "avifenc.exe", "avifdec.exe",
                  "avifgainmaputil.exe", "heif-enc.exe", "heif-dec.exe", "heif-info.exe",
                  "libheif.dll", "ultrahdr_app.exe"], tools)
    copy_closure(["HdrImageViewer.Native.dll"], native)
    shutil.copytree(prefix / "share/licenses", tools / "licenses")
    for name in (ultra_source.name, "openexr-3.5.1", heif_source.name):
        for file in list((root / "sources" / name).glob("LICENSE*")) + list((root / "sources" / name).glob("COPYING*")):
            target = tools / "licenses" / name / file.name
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(file, target)
    shutil.copy2(repo / "eng/codecs.lock.json", tools / "codecs.lock.json")
    manifest = {"schema": 1, "files": {}}
    for directory, label in ((tools, "encoders"), (native, "native")):
        manifest["files"][label] = {p.name: digest(p) for p in sorted(directory.iterdir()) if p.is_file()}
    (tools / "codec-manifest.json").write_text(json.dumps(manifest, indent=2) + "\n")

    # Remove MSYS2 from PATH: a missing bundled dependency must fail here.
    clean_env = dict(os.environ, PATH=str(system) + os.pathsep + os.environ["SystemRoot"])
    for executable, argument, expected in [("cjxl.exe", "--version", "0.12.0"),
                                           ("avifenc.exe", "--version", "1.4.2"),
                                           ("heif-enc.exe", "--version", "1.23.5"),
                                           ("ultrahdr_app.exe", "--help", "v" + ultra_spec["version"])]:
        result = subprocess.run([str(tools / executable), argument], capture_output=True, text=True, env=clean_env)
        if expected not in result.stdout + result.stderr:
            raise RuntimeError(f"{executable} version/load check failed: {result.stdout} {result.stderr}")
    smoke = staging / "smoke"
    smoke.mkdir()
    raw = smoke / "hdr.raw"
    # Exercise odd dimensions as well as all three gain-map containers.
    width, height = 33, 17
    raw.write_bytes(struct.pack("<eeee", 2.0, 1.0, 0.5, 1.0) * width * height)
    for extension in ("jpg", "heic", "avif"):
        encoded = smoke / ("gainmap." + extension)
        decoded = smoke / (extension + ".raw")
        run(tools / "ultrahdr_app.exe", "-m", "0", "-p", raw, "-w", str(width), "-h", str(height),
            "-a", "4", "-C", "2", "-t", "0", "-s", "1", "-z", encoded, env=clean_env)
        run(tools / "ultrahdr_app.exe", "-m", "1", "-j", encoded, "-o", "0", "-O", "4",
            "-z", decoded, env=clean_env)
        if ((extension != "jpg" and b"tmap" not in encoded.read_bytes())
                or decoded.stat().st_size != width * height * 8):
            raise RuntimeError("Gain Map encode/decode smoke test failed: " + extension)
    print("Verified staged bundle:", staging, flush=True)
    if args.apply:
        backup = root / "backups" / uuid.uuid4().hex
        backup.mkdir(parents=True)
        moves = []
        try:
            for source, target, label in [(tools, repo / "external/encoders/x64", "encoders"),
                                           (native, repo / "native/HdrImageViewer.Native/build/x64/Release", "native")]:
                # Check all resolved paths before moving a directory, including existing junctions.
                if not target.resolve().is_relative_to(repo) or not source.resolve().is_relative_to(repo):
                    raise RuntimeError("Runtime directory escapes checkout")
                previous = backup / label
                target.parent.mkdir(parents=True, exist_ok=True)
                had_previous = target.exists()
                if had_previous:
                    target.rename(previous)
                moves.append((source, target, previous, had_previous))
                source.rename(target)
        except Exception:
            for source, target, previous, had_previous in reversed(moves):
                if target.exists():
                    target.rename(source)
                if had_previous:
                    previous.rename(target)
            raise
        print("Applied. Previous bundle preserved in", backup, flush=True)


if __name__ == "__main__":
    main()
