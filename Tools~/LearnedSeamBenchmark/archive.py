"""Archive explicitly listed local assets without parsing models or finding dependencies.

The manifest is a JSON list of asset_id/family_id/files records, where each file
record contains role and path. Relative input paths resolve against the manifest.
Only a brand-new output directory is accepted. No inputs are edited or removed.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import stat


_ID = re.compile(r"[A-Za-z0-9][A-Za-z0-9._-]{0,127}\Z")
_DEVICES = {"con", "prn", "aux", "nul", *(f"com{i}" for i in range(1, 10)),
            *(f"lpt{i}" for i in range(1, 10))}


def _safe_component(value):
    if (not value or value in (".", "..") or value[-1] in " ."
            or any(ord(c) < 32 or c in '<>:"/\\|?*' for c in value)
            or value.split(".")[0].casefold() in _DEVICES):
        raise ValueError(f"Unsafe Windows path component: {value!r}")


def _safe_id(value, label):
    if not isinstance(value, str) or not _ID.fullmatch(value):
        raise ValueError(f"Unsafe {label}; use 1-128 ASCII letters, digits, '.', '_' or '-'")
    _safe_component(value)
    return value


def _safe_output_path(path):
    path = Path(path)
    # Drive roots and UNC server/share anchors are not directory names.
    components = path.parts[1:] if path.anchor else path.parts
    for component in components:
        if component not in (".", ".."):
            _safe_component(component)


def _reject_links(path):
    # Check the lexical path before resolve(): resolving first hides junctions.
    for candidate in (path, *path.parents):
        try:
            info = candidate.lstat()
        except FileNotFoundError:
            continue
        if (stat.S_ISLNK(info.st_mode)
                or getattr(info, "st_file_attributes", 0) & getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 1024)):
            raise ValueError(f"Symlink/junction/reparse point is not supported: {candidate}")


def _absolute(path):
    path = Path(path)
    if not path.is_absolute() and (path.drive or path.root):
        raise ValueError("Use a complete absolute path or an ordinary relative path; drive-relative paths are ambiguous")
    lexical = path if path.is_absolute() else Path.cwd() / path
    _reject_links(lexical)
    canonical = Path(os.path.abspath(lexical)).resolve(strict=False)
    _reject_links(canonical)
    return canonical


def _fingerprint(info):
    return info.st_dev, info.st_ino, info.st_size, info.st_mtime_ns, info.st_mode


def _regular_file(path):
    _reject_links(path)
    info = path.lstat()
    if not stat.S_ISREG(info.st_mode):
        raise ValueError(f"Expected a regular file: {path}")
    return info


def _hash_input(path):
    before = _regular_file(path)
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        if _fingerprint(os.fstat(stream.fileno())) != _fingerprint(before):
            raise ValueError(f"Input changed while opening: {path}")
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    if _fingerprint(_regular_file(path)) != _fingerprint(before):
        raise ValueError(f"Input changed during preflight: {path}")
    return digest.hexdigest(), _fingerprint(before)


def _inside(path, parent):
    child_parts = tuple(p.casefold() for p in path.parts)
    parent_parts = tuple(p.casefold() for p in parent.parts)
    return child_parts[:len(parent_parts)] == parent_parts


def _enumerate(source):
    """Visit only an explicitly requested tree; never follow any links."""
    _reject_links(source)
    info = source.lstat()
    if stat.S_ISREG(info.st_mode):
        yield source, False
    elif stat.S_ISDIR(info.st_mode):
        yield source, True
        for child in sorted(source.iterdir(), key=lambda p: (p.name.casefold(), p.name)):
            yield from _enumerate(child)
    else:
        raise ValueError(f"Unsupported input type: {source}")


def _register_path(destination, directory, paths):
    for index in range(1, len(destination.parts) + 1):
        prefix = Path(*destination.parts[:index])
        kind = "directory" if index < len(destination.parts) or directory else "file"
        key = tuple(p.casefold() for p in prefix.parts)
        spelling = prefix.as_posix()
        previous = paths.get(key)
        if previous and (previous != (spelling, kind) or kind == "file"):
            raise ValueError(f"Windows path collision: {prefix} conflicts with {previous[0]}")
        paths[key] = (spelling, kind)


def _preflight(manifest, output):
    _safe_output_path(output)
    manifest, output = _absolute(manifest), _absolute(output)
    _safe_output_path(output)
    if output.exists():
        raise ValueError("Output must be a brand-new directory; existing inputs/results are never overwritten")
    manifest_sha, manifest_fingerprint = _hash_input(manifest)
    manifest_bytes = manifest.read_bytes()
    if hashlib.sha256(manifest_bytes).hexdigest() != manifest_sha:
        raise ValueError("Manifest changed during preflight")
    entries = json.loads(manifest_bytes)
    if not isinstance(entries, list) or not entries:
        raise ValueError("Manifest must be a nonempty JSON list")
    assets, families, mappings, files, directories, paths = {}, {}, [], [], set(), {}
    for entry_index, entry in enumerate(entries):
        if not isinstance(entry, dict):
            raise ValueError("Each asset entry must be an object")
        asset = _safe_id(entry.get("asset_id"), "asset_id")
        family = _safe_id(entry.get("family_id"), "family_id")
        if asset.casefold() in assets:
            raise ValueError(f"Duplicate/Windows case collision for asset_id: {asset}")
        if family.casefold() in families and families[family.casefold()] != family:
            raise ValueError(f"Windows case collision for family_id: {family}")
        families[family.casefold()] = family
        assets[asset.casefold()] = dict(asset_id=asset, family_id=family)
        requested = entry.get("files")
        if not isinstance(requested, list) or not requested:
            raise ValueError(f"Asset {asset} needs a nonempty files list")
        for file_index, item in enumerate(requested):
            if not isinstance(item, dict):
                raise ValueError("Each files entry must be an object")
            role = _safe_id(item.get("role"), "role")
            raw_path = item.get("path")
            if not isinstance(raw_path, str) or not raw_path.strip() or "\0" in raw_path:
                raise ValueError("Each files entry needs a nonempty path string")
            supplied = Path(raw_path)
            if not supplied.is_absolute() and (supplied.drive or supplied.root):
                raise ValueError("Input paths must be complete absolute paths or relative to the manifest")
            source = _absolute(supplied if supplied.is_absolute() else manifest.parent / supplied)
            if not source.exists():
                raise ValueError(f"Missing explicitly listed input: {source}")
            if _inside(output, source):
                raise ValueError(f"Output cannot be inside an input (self-copy): {source}")
            if not source.name:
                raise ValueError("Filesystem roots are not supported as input directories")
            destination_root = Path("assets") / asset / role / source.name
            mapping = dict(entry=entry_index, file_entry=file_index, asset_id=asset, family_id=family,
                           role=role, supplied_path=raw_path, source=str(source),
                           archived=destination_root.as_posix(), files=0)
            for path, is_directory in _enumerate(source):
                relative = path.relative_to(source) if path != source else Path()
                destination = destination_root / relative
                for component in destination.parts:
                    _safe_component(component)
                _register_path(destination, is_directory, paths)
                if is_directory:
                    directories.add(destination)
                else:
                    digest, fingerprint = _hash_input(path)
                    files.append(dict(asset_id=asset, family_id=family, role=role, source=str(path),
                                      archived=destination.as_posix(), input_sha256=digest,
                                      size_bytes=fingerprint[2], fingerprint=fingerprint))
                    mapping["files"] += 1
            mapping["type"] = "directory" if source.is_dir() else "file"
            mappings.append(mapping)
    return dict(manifest=manifest, output=output, manifest_bytes=manifest_bytes,
                manifest_sha=manifest_sha, manifest_fingerprint=manifest_fingerprint,
                assets=list(assets.values()), mappings=mappings, files=files, directories=directories)


def archive(manifest, output):
    plan = _preflight(manifest, output)
    root = plan["output"]
    # No directory creation takes place until every explicit input was checked.
    _reject_links(root)
    if _fingerprint(_regular_file(plan["manifest"])) != plan["manifest_fingerprint"]:
        raise ValueError("Manifest changed after preflight")
    for record in plan["files"]:
        if _fingerprint(_regular_file(Path(record["source"]))) != record["fingerprint"]:
            raise ValueError(f"Input changed after preflight: {record['source']}")
    root.mkdir(parents=True, exist_ok=False)
    for directory in sorted(plan["directories"], key=lambda p: (len(p.parts), p.as_posix())):
        target_directory = root / directory
        _reject_links(target_directory)
        target_directory.mkdir(parents=True, exist_ok=True)
    archived_records = []
    for record in plan["files"]:
        source, destination = Path(record["source"]), root / record["archived"]
        _reject_links(source)
        _reject_links(destination)
        destination.parent.mkdir(parents=True, exist_ok=True)
        _reject_links(destination)
        digest = hashlib.sha256()
        with source.open("rb") as incoming, destination.open("xb") as outgoing:
            if _fingerprint(os.fstat(incoming.fileno())) != record["fingerprint"]:
                raise ValueError(f"Input changed while opening: {source}")
            for block in iter(lambda: incoming.read(1024 * 1024), b""):
                outgoing.write(block)
                digest.update(block)
        if digest.hexdigest() != record["input_sha256"]:
            raise ValueError(f"Input bytes changed since preflight: {source}")
        copied_sha, _ = _hash_input(destination)
        if copied_sha != record["input_sha256"]:
            raise ValueError(f"Archived byte verification failed: {destination}")
        archived_records.append({key: value for key, value in record.items() if key != "fingerprint"}
                                | dict(output_sha256=copied_sha))
    _reject_links(root)
    with (root / "collection-manifest.json").open("xb") as stream:
        stream.write(plan["manifest_bytes"])
    manifest_output_sha, _ = _hash_input(root / "collection-manifest.json")
    report = dict(schema_version=1, archive_kind="explicit byte-for-byte local copy",
                  manifest_source=str(plan["manifest"]), manifest_sha256=plan["manifest_sha"],
                  manifest_archived="collection-manifest.json", manifest_output_sha256=manifest_output_sha,
                  dependency_discovery_performed=False, completeness_verified=False, fields_extracted=False,
                  assets=plan["assets"], entries=plan["mappings"], files=archived_records,
                  asset_count=len(plan["assets"]), file_count=len(archived_records),
                  total_bytes=sum(record["size_bytes"] for record in archived_records))
    with (root / "archive-report.json").open("x", encoding="utf-8") as stream:
        stream.write(json.dumps(report, indent=2, ensure_ascii=False))
    return report


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--manifest", required=True)
    parser.add_argument("--out", required=True)
    args = parser.parse_args()
    result = archive(args.manifest, args.out)
    print(json.dumps({key: result[key] for key in
                      ("asset_count", "file_count", "total_bytes", "completeness_verified")}))
