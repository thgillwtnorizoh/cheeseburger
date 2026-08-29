#!/usr/bin/env python3
"""Fetch and normalize Arcaea wiki data.

Current milestone: discover a validated song list from the Japanese WikiWiki
"title order" page. The script intentionally keeps source provenance so later
Fandom/Miraheze/CN adapters can feed the same merger.
"""

from __future__ import annotations

import argparse
import html
import json
import re
import sys
import time
import unicodedata
from dataclasses import asdict, dataclass
from datetime import datetime, timezone
from pathlib import Path
from typing import Any
from urllib.parse import unquote, urljoin, urlparse

import requests
from bs4 import BeautifulSoup

WIKIWIKI_ROOT = "https://wikiwiki.jp/arcaea/"
WIKIWIKI_TITLE_URL = urljoin(
    WIKIWIKI_ROOT,
    "%E3%82%BF%E3%82%A4%E3%83%88%E3%83%AB%E9%A0%86",
)
DIFFICULTIES = ("PST", "PRS", "FTR", "ETR", "BYD")
USER_AGENT = (
    "RenderMyMind-ArcaeaDB/0.2 "
    "(+https://github.com/thgillwtnorizoh/cheeseburger; respectful read-only fetcher)"
)


def utc_now() -> str:
    return datetime.now(timezone.utc).replace(microsecond=0).isoformat().replace("+00:00", "Z")


def clean_text(value: str | None) -> str:
    if not value:
        return ""
    value = html.unescape(value).replace("\xa0", " ")
    return re.sub(r"\s+", " ", value).strip()


def normalize_key(value: str) -> str:
    value = unicodedata.normalize("NFKC", value)
    return re.sub(r"\s+", " ", value).strip().casefold()


def parse_version(text: str) -> str | None:
    m = re.search(r"\b(\d+\.\d+(?:\.\d+)?)\b", text)
    return m.group(1) if m else None


def parse_declared_counts(text: str) -> dict[str, int | None]:
    def grab(pattern: str) -> int | None:
        m = re.search(pattern, text, re.I)
        return int(m.group(1)) if m else None

    return {
        "normal": grab(r"収録曲数\s*(\d+)\s*曲"),
        "tutorial": 1 if "Tutorial" in text else None,
        "beyond_exclusive": grab(r"Beyond[^0-9]{0,30}(\d+)\s*曲"),
        "april_fools": grab(r"エイプリルフール[^0-9]{0,20}(\d+)\s*曲"),
        "deleted": grab(r"削除済み?\s*(\d+)\s*曲"),
    }


def parse_index_game_version(text: str) -> str | None:
    m = re.search(r"iOS/Android\s*[:：]\s*ver\.\s*(\d+\.\d+(?:\.\d+)?)", text, re.I)
    return m.group(1) if m else None


def classify_row(title: str, row_text: str) -> str:
    joined = f"{title} {row_text}"
    if "AF譜面限定楽曲" in joined or "エイプリルフール" in joined:
        return "april_fools"
    if "Beyond譜面限定楽曲" in joined or "Beyond 譜面限定楽曲" in joined:
        return "beyond_exclusive"
    if "削除済" in joined or re.search(r"\bRemoved\b", joined, re.I):
        return "deleted"
    if normalize_key(title) == "tutorial":
        return "tutorial"
    return "normal"


def parse_level(text: str) -> str | None:
    text = clean_text(text)
    if not text:
        return None
    m = re.search(r"(?:^|\s)(\?|\d{1,2}\+?)(?:\s|$)", text)
    if m:
        return m.group(1)
    return text if len(text) <= 8 else None


class FetchError(RuntimeError):
    pass


class HttpClient:
    def __init__(self, timeout: float = 25.0, retries: int = 3, delay: float = 1.0) -> None:
        self.timeout = timeout
        self.retries = retries
        self.delay = delay
        self.session = requests.Session()
        self.session.headers.update(
            {
                "User-Agent": USER_AGENT,
                "Accept-Language": "en,ja;q=0.9",
            }
        )

    def get_text(self, url: str) -> str:
        last: Exception | None = None
        for attempt in range(1, self.retries + 1):
            try:
                r = self.session.get(url, timeout=self.timeout)
                r.raise_for_status()
                if not r.encoding or r.encoding.lower() == "iso-8859-1":
                    r.encoding = r.apparent_encoding or "utf-8"
                return r.text
            except Exception as exc:
                last = exc
                if attempt < self.retries:
                    time.sleep(self.delay * attempt)
        raise FetchError(f"GET failed after {self.retries} attempts: {url}: {last}")


@dataclass
class WikiWikiSong:
    title: str
    page: str
    url: str
    artist: str | None
    pack: str | None
    added_version: str | None
    added_version_raw: str | None
    charts: dict[str, dict[str, str | None]]
    kind: str


def same_wiki_song_link(href: str) -> bool:
    absolute = urljoin(WIKIWIKI_ROOT, href)
    p = urlparse(absolute)
    if p.netloc != "wikiwiki.jp" or not p.path.startswith("/arcaea/"):
        return False
    page = unquote(p.path[len("/arcaea/") :])
    if not page or page.startswith("#"):
        return False
    return page not in {"タイトル順", "RecentChanges", "FrontPage", "MenuBar"}


def choose_song_anchor(cells: list[Any]) -> Any | None:
    for cell in cells[:2]:
        for anchor in cell.find_all("a", href=True):
            if same_wiki_song_link(anchor.get("href", "")):
                text = clean_text(anchor.get_text(" ", strip=True))
                # A real Arcaea title can begin with '#': #1f1e33. Anchor
                # filtering must therefore be based on the URL, not title text.
                if text:
                    return anchor
    return None


def parse_wikiwiki_title_index(page_html: str) -> dict[str, Any]:
    soup = BeautifulSoup(page_html, "html.parser")
    page_text = clean_text(soup.get_text(" ", strip=True))
    declared = parse_declared_counts(page_text)
    index_version = parse_index_game_version(page_text)

    songs: list[WikiWikiSong] = []
    seen: set[tuple[str, str]] = set()

    for tr in soup.find_all("tr"):
        cells = tr.find_all(["td", "th"], recursive=False)
        if len(cells) < 6:
            continue

        anchor = choose_song_anchor(cells)
        if anchor is None:
            continue

        title = clean_text(anchor.get_text(" ", strip=True))
        absolute = urljoin(WIKIWIKI_ROOT, anchor.get("href", "")).split("#", 1)[0]
        parsed = urlparse(absolute)
        page = (
            unquote(parsed.path[len("/arcaea/") :])
            if parsed.path.startswith("/arcaea/")
            else title
        )
        page = clean_text(page)

        # Same display title can legitimately identify different songs/pages
        # (e.g. collaboration duplicates). Preserve those. Exact title+target
        # duplicates are the only rows we suppress.
        dedupe_key = (normalize_key(title), normalize_key(page))
        if dedupe_key in seen:
            continue

        row_text = clean_text(tr.get_text(" ", strip=True))
        levels_raw = [clean_text(c.get_text(" ", strip=True)) for c in cells[-5:]]
        charts = {
            diff: {"level": parse_level(raw)}
            for diff, raw in zip(DIFFICULTIES, levels_raw, strict=True)
        }

        texts = [clean_text(c.get_text(" ", strip=True)) for c in cells]
        prefix = texts[: len(texts) - 5]
        version_idx = next((i for i, value in enumerate(prefix) if parse_version(value)), None)

        artist: str | None = None
        pack: str | None = None
        version_raw: str | None = None
        if version_idx is not None:
            version_raw = prefix[version_idx]
            if version_idx >= 1:
                pack = prefix[version_idx - 1] or None
            if version_idx >= 2:
                artist = prefix[version_idx - 2] or None

        songs.append(
            WikiWikiSong(
                title=title,
                page=page,
                url=absolute,
                artist=artist,
                pack=pack,
                added_version=parse_version(version_raw or ""),
                added_version_raw=version_raw,
                charts=charts,
                kind=classify_row(title, row_text),
            )
        )
        seen.add(dedupe_key)

    counts: dict[str, int] = {}
    for song in songs:
        counts[song.kind] = counts.get(song.kind, 0) + 1

    errors: list[str] = []
    warnings: list[str] = []

    if len(songs) < 500:
        errors.append(f"Only {len(songs)} song rows parsed; expected at least 500")

    for kind, expected in declared.items():
        if expected is None:
            continue
        actual = counts.get(kind, 0)
        if actual != expected:
            errors.append(f"Declared {kind} count is {expected}, parsed {actual}")

    expected_total = sum(v for v in declared.values() if v is not None)
    if expected_total and len(songs) != expected_total:
        errors.append(f"Declared category total is {expected_total}, parsed {len(songs)} rows")

    no_chart = [
        s.title
        for s in songs
        if s.kind == "normal" and not any(v["level"] for v in s.charts.values())
    ]
    if no_chart:
        errors.append(f"{len(no_chart)} normal songs have no visible difficulty level")

    # Duplicate visible titles are allowed, but call them out so downstream
    # matching never assumes title alone is a stable key.
    title_groups: dict[str, list[WikiWikiSong]] = {}
    for song in songs:
        title_groups.setdefault(normalize_key(song.title), []).append(song)
    duplicate_titles = {
        group[0].title: [s.page for s in group]
        for group in title_groups.values()
        if len(group) > 1
    }
    if duplicate_titles:
        warnings.append(
            f"{len(duplicate_titles)} visible titles map to multiple wiki pages; preserve page/source identity"
        )

    validation: dict[str, Any] = {
        "ok": not errors,
        "errors": errors,
        "warnings": warnings,
        "declared_by_kind": declared,
        "parsed_total_rows": len(songs),
        "parsed_by_kind": counts,
        "duplicate_visible_titles": duplicate_titles,
    }
    if no_chart:
        validation["no_chart_examples"] = no_chart[:20]

    return {
        "source": "arcaea_wikiwiki_jp",
        "source_url": WIKIWIKI_TITLE_URL,
        "fetched_at": utc_now(),
        "index_game_version": index_version,
        "validation": validation,
        "songs": [asdict(song) for song in songs],
    }


def write_json(data: Any, path: Path | None) -> None:
    text = json.dumps(data, ensure_ascii=False, indent=2) + "\n"
    if path is None:
        sys.stdout.write(text)
    else:
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(text, encoding="utf-8")


def cmd_wikiwiki_index(args: argparse.Namespace) -> int:
    client = HttpClient(timeout=args.timeout, retries=args.retries)
    result = parse_wikiwiki_title_index(client.get_text(args.url))
    write_json(result, Path(args.output) if args.output else None)

    validation = result["validation"]
    print(
        f"WikiWiki JP index: {validation['parsed_total_rows']} rows; "
        f"kind counts={validation['parsed_by_kind']}; "
        f"index version={result['index_game_version']}",
        file=sys.stderr,
    )
    for warning in validation["warnings"]:
        print(f"WARNING: {warning}", file=sys.stderr)
    for error in validation["errors"]:
        print(f"ERROR: {error}", file=sys.stderr)

    return 2 if args.strict and not validation["ok"] else 0


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description="Fetch normalized Arcaea wiki data")
    sub = parser.add_subparsers(dest="command", required=True)

    index = sub.add_parser("wikiwiki-index", help="Fetch + validate WikiWiki JP title index")
    index.add_argument("--url", default=WIKIWIKI_TITLE_URL)
    index.add_argument("--output", "-o")
    index.add_argument("--timeout", type=float, default=25.0)
    index.add_argument("--retries", type=int, default=3)
    index.add_argument("--strict", action="store_true")
    index.set_defaults(func=cmd_wikiwiki_index)
    return parser


def main() -> int:
    args = build_parser().parse_args()
    return int(args.func(args))


if __name__ == "__main__":
    raise SystemExit(main())
