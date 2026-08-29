#!/usr/bin/env python3
"""Fetch and normalize Arcaea wiki data.

Milestone 1 implemented here: discover and validate the song index from the
Japanese WikiWiki title-order page. The output is deliberately source-shaped
rather than the final RenderMyMind database so later source adapters can feed
one merger without changing the Android-side format.
"""

from __future__ import annotations

import argparse
import html
import json
import re
import sys
import time
import unicodedata
from dataclasses import dataclass, asdict
from datetime import datetime, timezone
from pathlib import Path
from typing import Any
from urllib.parse import unquote, urljoin, urlparse

import requests
from bs4 import BeautifulSoup

WIKIWIKI_ROOT = "https://wikiwiki.jp/arcaea/"
WIKIWIKI_TITLE_URL = urljoin(WIKIWIKI_ROOT, "%E3%82%BF%E3%82%A4%E3%83%88%E3%83%AB%E9%A0%86")
DIFFICULTIES = ("PST", "PRS", "FTR", "ETR", "BYD")
USER_AGENT = (
    "RenderMyMind-ArcaeaDB/0.1 "
    "(+https://github.com/thgillwtnorizoh/cheeseburger; respectful read-only fetcher)"
)


def utc_now() -> str:
    return datetime.now(timezone.utc).replace(microsecond=0).isoformat().replace("+00:00", "Z")


def clean_text(value: str | None) -> str:
    if not value:
        return ""
    value = html.unescape(value)
    value = value.replace("\xa0", " ")
    value = re.sub(r"\s+", " ", value)
    return value.strip()


def normalize_key(value: str) -> str:
    value = unicodedata.normalize("NFKC", value)
    return re.sub(r"\s+", " ", value).strip().casefold()


def parse_version(text: str) -> str | None:
    m = re.search(r"\b(\d+\.\d+(?:\.\d+)?)\b", text)
    return m.group(1) if m else None


def parse_declared_regular_count(text: str) -> int | None:
    # Current page wording: 収録曲数 542曲
    m = re.search(r"収録曲数\s*(\d+)\s*曲", text)
    return int(m.group(1)) if m else None


def parse_index_game_version(text: str) -> str | None:
    # Current page wording: iOS/Android : ver.6.16.0収録分
    m = re.search(r"iOS/Android\s*[:：]\s*ver\.\s*(\d+\.\d+(?:\.\d+)?)", text, re.I)
    return m.group(1) if m else None


def classify_row(title: str, row_text: str) -> str:
    joined = f"{title} {row_text}"
    if "AF譜面限定楽曲" in joined or "エイプリルフール" in joined:
        return "april_fools"
    if "Beyond譜面限定楽曲" in joined or "Beyond 譜面限定楽曲" in joined:
        return "beyond_exclusive"
    if "削除済" in joined:
        return "deleted"
    if normalize_key(title) == "tutorial":
        return "tutorial"
    return "normal"


def parse_level(text: str) -> str | None:
    text = clean_text(text)
    if not text:
        return None
    # Rendered tables may leave footnote markers around the visible level.
    m = re.search(r"(?:^|\s)(\?|\d{1,2}\+?)(?:\s|$)", text)
    if m:
        return m.group(1)
    if text == "?":
        return "?"
    return text if len(text) <= 8 else None


class FetchError(RuntimeError):
    pass


class HttpClient:
    def __init__(self, timeout: float = 25.0, retries: int = 3, delay: float = 1.0) -> None:
        self.timeout = timeout
        self.retries = retries
        self.delay = delay
        self.session = requests.Session()
        self.session.headers.update({"User-Agent": USER_AGENT, "Accept-Language": "en,ja;q=0.9"})

    def get_text(self, url: str) -> str:
        last: Exception | None = None
        for attempt in range(1, self.retries + 1):
            try:
                r = self.session.get(url, timeout=self.timeout)
                r.raise_for_status()
                if not r.encoding or r.encoding.lower() == "iso-8859-1":
                    r.encoding = r.apparent_encoding or "utf-8"
                return r.text
            except Exception as exc:  # requests has several useful subclasses, retry all network failures
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
    if p.netloc != "wikiwiki.jp":
        return False
    if not p.path.startswith("/arcaea/"):
        return False
    page = unquote(p.path[len("/arcaea/") :])
    if not page or page.startswith("#"):
        return False
    # Exclude obvious index/navigation pages rather than trying to blacklist song names.
    if page in {"タイトル順", "RecentChanges", "FrontPage", "MenuBar"}:
        return False
    return True


def choose_song_anchor(cells: list[Any]) -> Any | None:
    # Song is the first logical column. Search the first two cells because the
    # WikiWiki table uses a tiny color/side column next to Song.
    for cell in cells[:2]:
        for a in cell.find_all("a", href=True):
            if same_wiki_song_link(a.get("href", "")):
                text = clean_text(a.get_text(" ", strip=True))
                if text and not text.startswith("#"):
                    return a
    return None


def parse_wikiwiki_title_index(page_html: str) -> dict[str, Any]:
    soup = BeautifulSoup(page_html, "html.parser")
    page_text = clean_text(soup.get_text(" ", strip=True))

    declared_regular = parse_declared_regular_count(page_text)
    index_version = parse_index_game_version(page_text)

    found: list[WikiWikiSong] = []
    seen: set[str] = set()

    for tr in soup.find_all("tr"):
        cells = tr.find_all(["td", "th"], recursive=False)
        if len(cells) < 6:
            continue

        anchor = choose_song_anchor(cells)
        if anchor is None:
            continue

        title = clean_text(anchor.get_text(" ", strip=True))
        href = anchor.get("href", "")
        absolute = urljoin(WIKIWIKI_ROOT, href).split("#", 1)[0]
        parsed = urlparse(absolute)
        page = unquote(parsed.path[len("/arcaea/") :]) if parsed.path.startswith("/arcaea/") else title
        page = clean_text(page)

        # The displayed title is the logical identity here. A Beyond-only song
        # may deliberately link to its parent song page, so URL is not unique.
        dedupe_key = normalize_key(title)
        if dedupe_key in seen:
            continue

        row_text = clean_text(tr.get_text(" ", strip=True))

        # In the rendered title-order tables the right-most five logical cells
        # are PST/PRS/FTR/ETR/BYD. This remains robust if early columns use
        # colspan or a decorative side-color cell.
        levels_raw = [clean_text(c.get_text(" ", strip=True)) for c in cells[-5:]]
        charts = {
            diff: {"level": parse_level(raw)}
            for diff, raw in zip(DIFFICULTIES, levels_raw, strict=True)
        }

        # Typical row is Song | side-color | Composer | Pack | Update | 5 difficulties.
        # Work from the left but tolerate the side-color cell disappearing.
        texts = [clean_text(c.get_text(" ", strip=True)) for c in cells]
        song_cell_index = next((i for i, c in enumerate(cells) if anchor in c.descendants or anchor is c), 0)
        prefix = texts[: len(texts) - 5]
        after_song = prefix[song_cell_index + 1 :]
        # Drop empty/decorative side cell(s).
        after_song = [x for x in after_song if x]
        artist = after_song[0] if len(after_song) >= 1 else None
        pack = after_song[1] if len(after_song) >= 2 else None
        version_raw = after_song[2] if len(after_song) >= 3 else None

        # If a decorative cell contained text/classes and shifted extraction,
        # locate the version-looking prefix cell, then take the two cells before it.
        version_idx = next((i for i, x in enumerate(prefix) if parse_version(x)), None)
        if version_idx is not None:
            version_raw = prefix[version_idx]
            if version_idx >= 1:
                pack = prefix[version_idx - 1] or pack
            if version_idx >= 2:
                artist = prefix[version_idx - 2] or artist

        found.append(
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
    for song in found:
        counts[song.kind] = counts.get(song.kind, 0) + 1

    validation: dict[str, Any] = {
        "ok": True,
        "errors": [],
        "warnings": [],
        "declared_regular_song_count": declared_regular,
        "parsed_total_rows": len(found),
        "parsed_by_kind": counts,
    }

    if len(found) < 500:
        validation["errors"].append(f"Only {len(found)} song rows parsed; expected at least 500")

    normal_count = counts.get("normal", 0)
    if declared_regular is not None and normal_count != declared_regular:
        validation["warnings"].append(
            f"Wiki declares {declared_regular} regular songs but parser classified {normal_count}; "
            "the title page may have changed or classification needs review"
        )

    missing_title = [s.title for s in found if not s.title]
    if missing_title:
        validation["errors"].append(f"{len(missing_title)} rows have no title")

    # A chart index row should identify at least one difficulty. Special/deleted
    # rows are allowed to be sparse, but normal rows with none are suspicious.
    no_chart = [s.title for s in found if s.kind == "normal" and not any(v["level"] for v in s.charts.values())]
    if no_chart:
        validation["errors"].append(f"{len(no_chart)} normal songs have no visible difficulty level")
        validation["no_chart_examples"] = no_chart[:20]

    validation["ok"] = not validation["errors"]

    return {
        "source": "arcaea_wikiwiki_jp",
        "source_url": WIKIWIKI_TITLE_URL,
        "fetched_at": utc_now(),
        "index_game_version": index_version,
        "validation": validation,
        "songs": [asdict(s) for s in found],
    }


def write_json(data: Any, path: Path | None) -> None:
    text = json.dumps(data, ensure_ascii=False, indent=2) + "\n"
    if path is None:
        sys.stdout.write(text)
        return
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(text, encoding="utf-8")


def cmd_wikiwiki_index(args: argparse.Namespace) -> int:
    client = HttpClient(timeout=args.timeout, retries=args.retries)
    html_text = client.get_text(args.url)
    result = parse_wikiwiki_title_index(html_text)
    write_json(result, Path(args.output) if args.output else None)

    v = result["validation"]
    print(
        f"WikiWiki JP index: {v['parsed_total_rows']} rows; "
        f"kind counts={v['parsed_by_kind']}; index version={result['index_game_version']}",
        file=sys.stderr,
    )
    for warning in v["warnings"]:
        print(f"WARNING: {warning}", file=sys.stderr)
    for error in v["errors"]:
        print(f"ERROR: {error}", file=sys.stderr)

    if args.strict and not v["ok"]:
        return 2
    return 0


def build_parser() -> argparse.ArgumentParser:
    p = argparse.ArgumentParser(description="Fetch normalized Arcaea wiki data")
    sub = p.add_subparsers(dest="command", required=True)

    idx = sub.add_parser("wikiwiki-index", help="Fetch and validate the Japanese WikiWiki title index")
    idx.add_argument("--url", default=WIKIWIKI_TITLE_URL)
    idx.add_argument("--output", "-o")
    idx.add_argument("--timeout", type=float, default=25.0)
    idx.add_argument("--retries", type=int, default=3)
    idx.add_argument("--strict", action="store_true", help="Exit non-zero on validation errors")
    idx.set_defaults(func=cmd_wikiwiki_index)
    return p


def main() -> int:
    parser = build_parser()
    args = parser.parse_args()
    return int(args.func(args))


if __name__ == "__main__":
    raise SystemExit(main())
