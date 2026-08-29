#!/usr/bin/env python3
"""Safely merge normalized Arcaea wiki entries into a JSON collection.

Typical workflow:
1. Open a WikiWiki song page and paste tools/wikiwiki_console_extract.js into
   the browser console. The normalized JSON is copied to the clipboard.
2. Run:
       python tools/merge_wiki_entry.py database.json --clipboard

The merger:
- skips an entry when all substantive source/song/chart data is unchanged;
- replaces an existing entry when the same page/song has changed;
- appends new entries;
- refuses to overwrite corrupt JSON silently;
- can ask, skip, or abort when an input JSON file is corrupt;
- writes atomically and makes a .bak copy before changing an existing target.

Supported input/target shapes:
- one normalized entry: {"source": ..., "song": ..., "charts": ...}
- a plain list of normalized entries
- {"entries": [...]} collections
- {"songs": [...]} collections where every item is a normalized entry

New database files use:
    {"format": "arcaea_wiki_entries", "schema_version": 1, "entries": [...]}
"""

from __future__ import annotations

import argparse
import copy
import json
import os
import shutil
import sys
import tempfile
import unicodedata
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Iterable
from urllib.parse import urlsplit, urlunsplit

VOLATILE_META_KEYS = {
    "fetched_at",
    "source_updated_at",
    "parser_version",
    "validation",
    "missing_constants",
    "missing_notes",
}


def utc_now() -> str:
    return datetime.now(timezone.utc).replace(microsecond=0).isoformat().replace("+00:00", "Z")


def normalized_text(value: Any) -> str:
    return " ".join(unicodedata.normalize("NFKC", str(value or "")).casefold().split())


def normalized_url(value: Any) -> str:
    raw = str(value or "").strip()
    if not raw:
        return ""
    try:
        p = urlsplit(raw)
        # Fragment is presentation/navigation state, not page identity.
        return urlunsplit((p.scheme.casefold(), p.netloc.casefold(), p.path, p.query, ""))
    except Exception:
        return raw


def is_entry(value: Any) -> bool:
    return (
        isinstance(value, dict)
        and isinstance(value.get("source"), str)
        and isinstance(value.get("song"), dict)
        and isinstance(value.get("charts"), dict)
        and bool(value["song"].get("title"))
    )


def validate_entry(entry: dict[str, Any]) -> list[str]:
    errors: list[str] = []
    if not is_entry(entry):
        return ["object is not a normalized wiki entry"]
    if not entry.get("charts"):
        errors.append("charts object is empty")
    for diff, chart in entry.get("charts", {}).items():
        if not isinstance(chart, dict):
            errors.append(f"{diff}: chart must be an object")
            continue
        notes = chart.get("notes")
        constant = chart.get("constant")
        if notes is not None and (not isinstance(notes, int) or notes <= 0):
            errors.append(f"{diff}: invalid notes value {notes!r}")
        if constant is not None and (
            not isinstance(constant, (int, float))
            or isinstance(constant, bool)
            or not 0 <= float(constant) <= 15
        ):
            errors.append(f"{diff}: invalid constant value {constant!r}")
    return errors


def entry_identity(entry: dict[str, Any]) -> tuple[str, ...]:
    source = normalized_text(entry.get("source"))
    meta = entry.get("_meta") if isinstance(entry.get("_meta"), dict) else {}
    source_url = normalized_url(meta.get("source_url"))
    if source_url:
        return ("url", source, source_url)

    song = entry.get("song", {})
    title = normalized_text(song.get("title"))
    artist = normalized_text(song.get("artist"))
    # Title alone is not safe in Arcaea (e.g. collaboration duplicate names).
    return ("song", source, title, artist)


def semantic_entry(entry: dict[str, Any], compare_meta: bool = False) -> Any:
    """Return the data used to decide whether an existing entry is unchanged.

    By default we compare source + song + charts. Fetch timestamps and parser
    bookkeeping do not make an otherwise identical song entry "changed".
    --compare-meta includes stable _meta fields as well, while still ignoring
    inherently volatile fetch/validation fields.
    """
    base: dict[str, Any] = {
        "source": entry.get("source"),
        "song": copy.deepcopy(entry.get("song")),
        "charts": copy.deepcopy(entry.get("charts")),
    }
    if compare_meta:
        meta = copy.deepcopy(entry.get("_meta", {})) if isinstance(entry.get("_meta"), dict) else {}
        for key in VOLATILE_META_KEYS:
            meta.pop(key, None)
        base["_meta"] = meta
    return base


def canonical_json(value: Any) -> str:
    return json.dumps(value, ensure_ascii=False, sort_keys=True, separators=(",", ":"))


def changed_paths(old: Any, new: Any, prefix: str = "") -> list[str]:
    """Return a compact list of changed JSON paths for console reporting."""
    if type(old) is not type(new):
        return [prefix or "$"]
    if isinstance(old, dict):
        out: list[str] = []
        for key in sorted(set(old) | set(new)):
            path = f"{prefix}.{key}" if prefix else str(key)
            if key not in old or key not in new:
                out.append(path)
            else:
                out.extend(changed_paths(old[key], new[key], path))
        return out
    if isinstance(old, list):
        if old == new:
            return []
        return [prefix or "$"]
    return [] if old == new else [prefix or "$"]


def extract_entries(value: Any) -> list[dict[str, Any]]:
    if is_entry(value):
        return [value]

    if isinstance(value, list):
        entries = [item for item in value if is_entry(item)]
        if len(entries) != len(value):
            raise ValueError("list contains one or more objects that are not normalized wiki entries")
        return entries

    if isinstance(value, dict):
        for key in ("entries", "songs"):
            candidate = value.get(key)
            if isinstance(candidate, list):
                entries = [item for item in candidate if is_entry(item)]
                if entries and len(entries) == len(candidate):
                    return entries
        if isinstance(value.get("sources"), list):
            # Useful for the earlier multi-source extractor output. Flatten any
            # normalized entries without accepting arbitrary unrelated JSON.
            out: list[dict[str, Any]] = []
            for item in value["sources"]:
                if is_entry(item):
                    out.append(item)
            if out:
                return out

    raise ValueError("JSON does not contain normalized wiki entry data")


def detect_target_shape(value: Any) -> tuple[str, list[dict[str, Any]]]:
    if is_entry(value):
        return "single", [value]
    if isinstance(value, list):
        return "list", extract_entries(value)
    if isinstance(value, dict):
        if isinstance(value.get("entries"), list):
            return "entries", extract_entries(value)
        if isinstance(value.get("songs"), list):
            entries = [item for item in value["songs"] if is_entry(item)]
            if len(entries) == len(value["songs"]) and entries:
                return "songs", entries
    raise ValueError("target JSON is valid but is not a supported wiki-entry collection")


def rebuild_target(original: Any, shape: str, entries: list[dict[str, Any]]) -> Any:
    if shape == "list":
        return entries
    if shape == "entries":
        out = copy.deepcopy(original)
        out["entries"] = entries
        out["updated_at"] = utc_now()
        return out
    if shape == "songs":
        out = copy.deepcopy(original)
        out["songs"] = entries
        out["updated_at"] = utc_now()
        return out
    if shape == "single":
        # A single-entry file cannot hold two records. Promote it into the
        # standard collection format rather than discarding the old record.
        return {
            "format": "arcaea_wiki_entries",
            "schema_version": 1,
            "updated_at": utc_now(),
            "entries": entries,
        }
    raise ValueError(f"unsupported target shape: {shape}")


def new_collection(entries: list[dict[str, Any]]) -> dict[str, Any]:
    return {
        "format": "arcaea_wiki_entries",
        "schema_version": 1,
        "updated_at": utc_now(),
        "entries": entries,
    }


def ask_corrupt(path_label: str, error: Exception, gui: bool) -> str:
    message = f"Corrupt/invalid JSON: {path_label}\n\n{error}\n\nSkip this file?"
    if gui:
        try:
            import tkinter as tk
            from tkinter import messagebox

            root = tk.Tk()
            root.withdraw()
            answer = messagebox.askyesnocancel(
                "Arcaea DB merger",
                message + "\n\nYes = skip, No = abort, Cancel = abort",
                parent=root,
            )
            root.destroy()
            return "skip" if answer is True else "abort"
        except Exception:
            pass

    if sys.stdin.isatty():
        print(f"\n{message}", file=sys.stderr)
        while True:
            answer = input("[S]kip / [A]bort: ").strip().casefold()
            if answer in {"s", "skip"}:
                return "skip"
            if answer in {"a", "abort", "q", "quit"}:
                return "abort"

    # Non-interactive automation must never guess that corrupt data is safe.
    return "abort"


def read_json_text(text: str, label: str) -> Any:
    try:
        return json.loads(text)
    except json.JSONDecodeError as exc:
        raise ValueError(f"JSON parse error at line {exc.lineno}, column {exc.colno}: {exc.msg}") from exc


def load_json_file(path: Path) -> Any:
    return read_json_text(path.read_text(encoding="utf-8-sig"), str(path))


def clipboard_text() -> str:
    try:
        import tkinter as tk

        root = tk.Tk()
        root.withdraw()
        try:
            text = root.clipboard_get()
        finally:
            root.destroy()
        return text
    except Exception as exc:
        raise RuntimeError(f"could not read clipboard: {exc}") from exc


def atomic_write_json(path: Path, value: Any, backup: bool) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    if backup and path.exists():
        backup_path = path.with_suffix(path.suffix + ".bak")
        shutil.copy2(path, backup_path)
        print(f"backup: {backup_path}")

    payload = json.dumps(value, ensure_ascii=False, indent=2) + "\n"
    fd, temp_name = tempfile.mkstemp(prefix=path.name + ".", suffix=".tmp", dir=path.parent)
    temp_path = Path(temp_name)
    try:
        with os.fdopen(fd, "w", encoding="utf-8", newline="\n") as fh:
            fh.write(payload)
            fh.flush()
            os.fsync(fh.fileno())
        os.replace(temp_path, path)
    finally:
        if temp_path.exists():
            temp_path.unlink(missing_ok=True)


def merge_entries(
    existing: list[dict[str, Any]],
    incoming: Iterable[dict[str, Any]],
    compare_meta: bool,
) -> tuple[list[dict[str, Any]], dict[str, int]]:
    output = copy.deepcopy(existing)
    index = {entry_identity(entry): i for i, entry in enumerate(output)}
    stats = {"new": 0, "updated": 0, "identical": 0}

    for entry in incoming:
        errors = validate_entry(entry)
        if errors:
            raise ValueError(f"invalid entry {entry.get('song', {}).get('title')!r}: {'; '.join(errors)}")

        identity = entry_identity(entry)
        previous_index = index.get(identity)
        if previous_index is None:
            output.append(copy.deepcopy(entry))
            index[identity] = len(output) - 1
            stats["new"] += 1
            print(f"NEW      {entry['song']['title']}")
            continue

        previous = output[previous_index]
        old_semantic = semantic_entry(previous, compare_meta=compare_meta)
        new_semantic = semantic_entry(entry, compare_meta=compare_meta)
        if canonical_json(old_semantic) == canonical_json(new_semantic):
            stats["identical"] += 1
            print(f"SAME     {entry['song']['title']}  (skipped)")
            continue

        paths = changed_paths(old_semantic, new_semantic)
        preview = ", ".join(paths[:8])
        if len(paths) > 8:
            preview += f", +{len(paths) - 8} more"
        print(f"UPDATE   {entry['song']['title']}  [{preview}]")
        output[previous_index] = copy.deepcopy(entry)
        stats["updated"] += 1

    return output, stats


def choose_paths_gui() -> tuple[Path | None, list[Path]]:
    import tkinter as tk
    from tkinter import filedialog

    root = tk.Tk()
    root.withdraw()
    target = filedialog.askopenfilename(
        title="Choose existing database JSON, or Cancel to create one",
        filetypes=[("JSON files", "*.json"), ("All files", "*.*")],
        parent=root,
    )
    if not target:
        target = filedialog.asksaveasfilename(
            title="Create database JSON",
            defaultextension=".json",
            filetypes=[("JSON files", "*.json")],
            parent=root,
        )
    inputs = filedialog.askopenfilenames(
        title="Choose extracted entry JSON file(s)",
        filetypes=[("JSON files", "*.json"), ("All files", "*.*")],
        parent=root,
    )
    root.destroy()
    return (Path(target) if target else None, [Path(p) for p in inputs])


def self_test() -> int:
    base = {
        "source": "arcaea_wikiwiki_jp",
        "song": {"title": "TEST", "artist": "A"},
        "charts": {"FTR": {"level": "10", "constant": 10.4, "notes": 1000, "chart_designer": "X"}},
        "_meta": {"source_url": "https://wikiwiki.jp/arcaea/TEST", "fetched_at": "old"},
    }
    same = copy.deepcopy(base)
    same["_meta"]["fetched_at"] = "new"
    merged, stats = merge_entries([base], [same], compare_meta=False)
    assert stats == {"new": 0, "updated": 0, "identical": 1}
    assert len(merged) == 1

    changed = copy.deepcopy(base)
    changed["charts"]["FTR"]["constant"] = 10.5
    merged, stats = merge_entries([base], [changed], compare_meta=False)
    assert stats["updated"] == 1
    assert merged[0]["charts"]["FTR"]["constant"] == 10.5

    new = copy.deepcopy(base)
    new["song"]["title"] = "OTHER"
    new["_meta"]["source_url"] = "https://wikiwiki.jp/arcaea/OTHER"
    merged, stats = merge_entries([base], [new], compare_meta=False)
    assert stats["new"] == 1 and len(merged) == 2
    print("merge_wiki_entry self-test passed")
    return 0


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description="Merge normalized Arcaea wiki entries safely")
    parser.add_argument("target", nargs="?", help="JSON database to create/update")
    parser.add_argument("inputs", nargs="*", help="JSON entry/bundle files to merge")
    parser.add_argument("--clipboard", action="store_true", help="also read one entry/bundle from the clipboard")
    parser.add_argument("--stdin", action="store_true", help="also read JSON from stdin")
    parser.add_argument(
        "--corrupt",
        choices=("ask", "skip", "abort"),
        default="ask",
        help="what to do with corrupt input JSON (default: ask)",
    )
    parser.add_argument("--gui", action="store_true", help="use file dialogs; also use a GUI dialog for corrupt JSON")
    parser.add_argument("--compare-meta", action="store_true", help="include stable _meta fields when deciding if an entry changed")
    parser.add_argument("--no-backup", action="store_true", help="do not create target.json.bak before replacing an existing target")
    parser.add_argument("--dry-run", action="store_true", help="show merge decisions without writing the target")
    parser.add_argument("--self-test", action="store_true", help="run built-in merger tests and exit")
    return parser


def main() -> int:
    args = build_parser().parse_args()
    if args.self_test:
        return self_test()

    target_path: Path | None = Path(args.target) if args.target else None
    input_paths = [Path(p) for p in args.inputs]

    if args.gui or target_path is None:
        gui_target, gui_inputs = choose_paths_gui()
        target_path = target_path or gui_target
        input_paths.extend(gui_inputs)

    if target_path is None:
        print("No target selected.", file=sys.stderr)
        return 2

    original: Any = None
    shape = "new"
    existing_entries: list[dict[str, Any]] = []

    if target_path.exists():
        try:
            original = load_json_file(target_path)
            shape, existing_entries = detect_target_shape(original)
        except Exception as exc:
            decision = args.corrupt
            if decision == "ask":
                decision = ask_corrupt(str(target_path), exc, gui=args.gui)
            if decision == "skip":
                print(f"SKIP target: {target_path} ({exc})", file=sys.stderr)
                return 0
            print(f"ABORT target: {target_path} ({exc})", file=sys.stderr)
            return 2

    incoming_entries: list[dict[str, Any]] = []

    def accept_payload(payload: Any, label: str) -> None:
        try:
            entries = extract_entries(payload)
            incoming_entries.extend(entries)
            print(f"input: {label}: {len(entries)} entr{'y' if len(entries) == 1 else 'ies'}")
        except Exception as exc:
            decision = args.corrupt
            if decision == "ask":
                decision = ask_corrupt(label, exc, gui=args.gui)
            if decision == "skip":
                print(f"SKIP input: {label} ({exc})", file=sys.stderr)
                return
            raise RuntimeError(f"ABORT input: {label}: {exc}") from exc

    try:
        for path in input_paths:
            try:
                accept_payload(load_json_file(path), str(path))
            except Exception as exc:
                if isinstance(exc, RuntimeError):
                    raise
                decision = args.corrupt
                if decision == "ask":
                    decision = ask_corrupt(str(path), exc, gui=args.gui)
                if decision == "skip":
                    print(f"SKIP input: {path} ({exc})", file=sys.stderr)
                    continue
                raise RuntimeError(f"ABORT input: {path}: {exc}") from exc

        if args.clipboard:
            try:
                accept_payload(read_json_text(clipboard_text(), "clipboard"), "clipboard")
            except Exception as exc:
                if isinstance(exc, RuntimeError) and str(exc).startswith("ABORT input"):
                    raise
                decision = args.corrupt
                if decision == "ask":
                    decision = ask_corrupt("clipboard", exc, gui=args.gui)
                if decision != "skip":
                    raise RuntimeError(f"ABORT input: clipboard: {exc}") from exc

        if args.stdin:
            text = sys.stdin.read()
            accept_payload(read_json_text(text, "stdin"), "stdin")
    except RuntimeError as exc:
        print(str(exc), file=sys.stderr)
        return 2

    if not incoming_entries:
        print("No valid incoming entries. Nothing to do.")
        return 0

    try:
        merged_entries, stats = merge_entries(existing_entries, incoming_entries, args.compare_meta)
    except Exception as exc:
        print(f"Merge failed: {exc}", file=sys.stderr)
        return 2

    if stats["new"] == 0 and stats["updated"] == 0:
        print(f"No changes. Identical entries skipped: {stats['identical']}")
        return 0

    if shape == "new":
        output = new_collection(merged_entries)
    else:
        output = rebuild_target(original, shape, merged_entries)

    print(
        f"summary: new={stats['new']} updated={stats['updated']} "
        f"identical={stats['identical']} total={len(merged_entries)}"
    )

    if args.dry_run:
        print("dry-run: target not written")
        return 0

    atomic_write_json(target_path, output, backup=not args.no_backup)
    print(f"written: {target_path}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
