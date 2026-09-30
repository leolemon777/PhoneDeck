"""Validate the bytes users download, without extracting or executing archive contents."""
import hashlib
import json
from pathlib import Path, PurePosixPath
import re
import sys
import tarfile
import zipfile


def digest(stream):
    value = hashlib.sha256()
    while block := stream.read(1024 * 1024):
        value.update(block)
    return value.hexdigest()


def safe_name(name):
    path = PurePosixPath(name)
    if path.is_absolute() or ".." in path.parts or "\\" in name:
        raise RuntimeError("Unsafe archive/manifest path: " + name)
    return name


def verify_archive(archive, manifest):
    prefix = manifest["packageRoot"] + "/"
    expected = {safe_name(item["path"]): item for item in manifest["files"]}
    if len(expected) != len(manifest["files"]):
        raise RuntimeError("Duplicate manifest paths")
    actual = {}
    if archive.suffix == ".zip":
        with zipfile.ZipFile(archive) as content:
            for item in content.infolist():
                if item.is_dir():
                    continue
                name = safe_name(item.filename)
                if not name.startswith(prefix) or name[len(prefix):] in actual:
                    raise RuntimeError("Unexpected/duplicate ZIP member: " + name)
                with content.open(item) as stream:
                    actual[name[len(prefix):]] = (item.file_size, digest(stream), None)
    else:
        with tarfile.open(archive, "r:gz") as content:
            for item in content:
                if item.isdir():
                    continue
                name = safe_name(item.name)
                if not item.isfile() or not name.startswith(prefix) or name[len(prefix):] in actual:
                    raise RuntimeError("Unexpected/duplicate tar member: " + name)
                with content.extractfile(item) as stream:
                    actual[name[len(prefix):]] = (item.size, digest(stream), item.mode)
    if actual.keys() != expected.keys():
        raise RuntimeError("Archive file set differs from final manifest: missing=" + repr(sorted(expected.keys() - actual.keys())) + "; extra=" + repr(sorted(actual.keys() - expected.keys())))
    for name, item in expected.items():
        size, sha, mode = actual[name]
        if (size, sha) != (item["bytes"], item["sha256"]):
            raise RuntimeError("Archive byte/hash mismatch: " + name)
        if mode is not None and (name.endswith("PhoneDeck.Desktop") or "/whisper-cli" in name or name.endswith("phonedeck-hotkeys")) and not mode & 0o111:
            raise RuntimeError("Runtime lost executable permissions: " + name)
    if manifest["rid"].startswith("osx-") and "Contents/MacOS/hotkey-runtime/phonedeck-hotkeys" not in expected:
        raise RuntimeError("Missing Mac hotkey helper")
    if manifest["modelBundled"] != any(name.endswith("models/ggml-small-q5_1.bin") for name in expected):
        raise RuntimeError("Model bundle declaration differs from archive")
    print("Verified archive files and executable permissions:", archive.name, len(actual))


root = Path(sys.argv[1])
source = sys.argv[2]
checked = set()
for line in (root / "checksums.sha256").read_text(encoding="utf-8-sig").splitlines():
    match = re.fullmatch(r"([0-9a-f]{64})  ([^/\\]+)", line)
    if not match or match[2] in checked:
        raise RuntimeError("Invalid/duplicate checksum entry")
    with (root / match[2]).open("rb") as stream:
        if digest(stream) != match[1]:
            raise RuntimeError("Download checksum mismatch: " + match[2])
    checked.add(match[2])
artifacts = {item.name for item in root.iterdir() if item.is_file() and (item.suffix in (".zip", ".gz", ".exe", ".deb") or item.name.endswith(".build.json"))}
if checked != artifacts:
    raise RuntimeError("Download file set differs from checksums")
manifests = list(root.glob("*.build.json"))
if len(manifests) != 1:
    raise RuntimeError("Expected one final build manifest")
manifest = json.loads(manifests[0].read_text(encoding="utf-8-sig"))
if manifest["sourceDirty"] or manifest["sourceCommit"] != source or manifest["fileHashStage"] != "distributed-payload":
    raise RuntimeError("Build provenance or final hash stage mismatch")
archives = [item for item in root.iterdir() if item.suffix in (".zip", ".gz")]
if len(archives) != 1:
    raise RuntimeError("Expected one portable desktop archive")
verify_archive(archives[0], manifest)
print("Verified download checksums and source:", manifest["rid"], source)
