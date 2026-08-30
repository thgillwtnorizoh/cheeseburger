#!/usr/bin/env python3
"""Prepend explicit WikiWiki page URLs to an existing retry artifact.

Only URLs that are absent from both successful records and the existing failure queue
are added. URL fragments are stripped, so browser highlight fragments never create a
second fetch target. The script updates validation.requested/failed consistently and
leaves the fetched song records untouched.
"""

from __future__ import annotations

import argparse
import copy
import json
from pathlib import Path
from typing import Any
from urllib.parse import unquote, urlsplit, urlunsplit


def canonical_url(value: str) -> str:
    value = value.strip()
    parts = urlsplit(value)
    return urlunsplit((parts.scheme, parts.netloc, parts.path, parts.query, ""))


def title_from_url(url: str) -> str:
    path = urlsplit(url).path.rstrip("/")
    return unquote(path.rsplit("/", 1)[-1]) if path else url


def source_url(record: dict[str, Any]) -> str:
    meta = record.get("_meta")
    if not isinstance(meta, dict):
        return ""
    value = meta.get("source_url")
    return canonical_url(str(value)) if value else ""


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--input", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--url", action="append", default=[], help="Explicit WikiWiki song-page URL")
    args = parser.parse_args()

    data = json.loads(args.input.read_text(encoding="utf-8"))
    if not isinstance(data, dict):
        parser.error("input must be a WikiWiki crawl object")
    validation = data.get("validation")
    songs = data.get("songs")
    if not isinstance(validation, dict) or not isinstance(songs, list):
        parser.error("input is missing validation/songs")
    failures = validation.get("failures")
    if not isinstance(failures, list):
        parser.error("input is missing validation.failures")

    successful_urls = {
        url for record in songs if isinstance(record, dict) for url in [source_url(record)] if url
    }
    queued_urls = {
        canonical_url(str(item.get("url")))
        for item in failures
        if isinstance(item, dict) and item.get("url")
    }

    manual: list[dict[str, str]] = []
    added_urls: set[str] = set()
    skipped_success: list[str] = []
    skipped_queued: list[str] = []
    skipped_duplicate: list[str] = []

    for raw in args.url:
        url = canonical_url(raw)
        if not url:
            continue
        if url in successful_urls:
            skipped_success.append(url)
            continue
        if url in queued_urls:
            skipped_queued.append(url)
            continue
        if url in added_urls:
            skipped_duplicate.append(url)
            continue
        added_urls.add(url)
        manual.append(
            {
                "title": title_from_url(url),
                "url": url,
                "error": "Explicit manual target queued for WikiWiki fetch",
            }
        )

    result = copy.deepcopy(data)
    out_validation = result["validation"]
    old_requested = int(out_validation.get("requested", len(songs) + len(failures)))
    new_failures = manual + copy.deepcopy(failures)
    out_validation["requested"] = old_requested + len(manual)
    out_validation["failed"] = len(new_failures)
    out_validation["failures"] = new_failures
    out_validation["ok"] = False if new_failures else bool(songs) and not out_validation.get("invalid")

    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")

    print(f"Existing successes: {len(songs)} record(s)")
    print(f"Existing failure queue: {len(failures)} record(s), {len(queued_urls)} unique URL(s)")
    print(f"Manual targets supplied: {len(args.url)}")
    print(f"Manual targets newly added at front: {len(manual)}")
    for item in manual:
        print(f"  + {item['title']} -> {item['url']}")
    print(f"Already successful, not fetched again: {len(skipped_success)}")
    for url in skipped_success:
        print(f"  = success {url}")
    print(f"Already queued, not duplicated: {len(skipped_queued)}")
    for url in skipped_queued:
        print(f"  = queued {url}")
    if skipped_duplicate:
        print(f"Duplicate manual URLs ignored: {len(skipped_duplicate)}")
    print(f"New target total: {out_validation['requested']}")
    print(f"New failure queue: {out_validation['failed']} record(s)")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
