#!/usr/bin/env python3
"""Fetch and normalize Arcaea wiki data.

Milestones implemented:
1. Discover and validate the Japanese WikiWiki title-order song index.
2. Follow individual WikiWiki song pages and normalize useful metadata,
   especially exact chart constants and note counts.

The source adapters deliberately produce source-shaped records. A later merger
will combine WikiWiki, Miraheze, Fandom and the CN wiki into the final RMDB.
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
from typing import Any, Iterable
from urllib.parse import quote, unquote, urljoin, urlparse

import requests
from bs4 import BeautifulSoup, Tag

WIKIWIKI_ROOT = "https://wikiwiki.jp/arcaea/"
WIKIWIKI_TITLE_URL = urljoin(
    WIKIWIKI_ROOT,
    "%E3%82%BF%E3%82%A4%E3%83%88%E3%83%AB%E9%A0%86",
)
INDEX_DIFFICULTIES = ("PST", "PRS", "FTR", "ETR", "BYD")
ALL_DIFFICULTIES = ("PST", "PRS", "FTR", "ETR", "BYD", "INS")
DIFFICULTY_ALIASES = {
    "past": "PST",
    "pst": "PST",
    "present": "PRS",
    "prs": "PRS",
    "future": "FTR",
    "ftr": "FTR",
    "eternal": "ETR",
    "etr": "ETR",
    "beyond": "BYD",
    "byd": "BYD",
    "inscribed": "INS",
    "ins": "INS",
}
USER_AGENT = (
    "RenderMyMind-ArcaeaDB/0.3 "
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


def parse_int(text: str) -> int | None:
    text = clean_text(text).replace(",", "")
    m = re.search(r"(?<![\d.])(\d{1,7})(?![\d.])", text)
    return int(m.group(1)) if m else None


def parse_float(text: str) -> float | None:
    text = clean_text(text)
    m = re.search(r"(?<!\d)(\d{1,2}(?:\.\d+)?)(?!\d)", text)
    return float(m.group(1)) if m else None


def normalize_difficulty(text: str) -> str | None:
    key = normalize_key(clean_text(text).replace("[", "").replace("]", ""))
    for name, code in DIFFICULTY_ALIASES.items():
        if re.search(rf"\b{re.escape(name)}\b", key):
            return code
    return None


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
        page = unquote(parsed.path[len("/arcaea/") :]) if parsed.path.startswith("/arcaea/") else title
        page = clean_text(page)

        dedupe_key = (normalize_key(title), normalize_key(page))
        if dedupe_key in seen:
            continue

        row_text = clean_text(tr.get_text(" ", strip=True))
        levels_raw = [clean_text(c.get_text(" ", strip=True)) for c in cells[-5:]]
        charts = {
            diff: {"level": parse_level(raw)}
            for diff, raw in zip(INDEX_DIFFICULTIES, levels_raw, strict=True)
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
        if expected is not None and counts.get(kind, 0) != expected:
            errors.append(f"Declared {kind} count is {expected}, parsed {counts.get(kind, 0)}")
    expected_total = sum(v for v in declared.values() if v is not None)
    if expected_total and len(songs) != expected_total:
        errors.append(f"Declared category total is {expected_total}, parsed {len(songs)} rows")

    no_chart = [s.title for s in songs if s.kind == "normal" and not any(v["level"] for v in s.charts.values())]
    if no_chart:
        errors.append(f"{len(no_chart)} normal songs have no visible difficulty level")

    title_groups: dict[str, list[WikiWikiSong]] = {}
    for song in songs:
        title_groups.setdefault(normalize_key(song.title), []).append(song)
    duplicate_titles = {
        group[0].title: [s.page for s in group]
        for group in title_groups.values()
        if len(group) > 1
    }
    if duplicate_titles:
        warnings.append(f"{len(duplicate_titles)} visible titles map to multiple wiki pages; preserve page/source identity")

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


def row_cells(tr: Tag) -> list[Tag]:
    return [c for c in tr.find_all(["td", "th"], recursive=False) if isinstance(c, Tag)]


def row_texts(tr: Tag) -> list[str]:
    return [clean_text(c.get_text(" ", strip=True)) for c in row_cells(tr)]


def first_image_url(tag: Tag, base_url: str) -> str | None:
    img = tag.find("img")
    if not img:
        return None
    src = img.get("data-src") or img.get("src")
    if not src:
        return None
    absolute = urljoin(base_url, src)
    if "Now printing" in (img.get("alt") or "") or "Now_printing" in absolute:
        return None
    return absolute


def find_chart_table(soup: BeautifulSoup) -> tuple[Tag | None, list[str]]:
    for table in soup.find_all("table"):
        rows = table.find_all("tr")
        for tr in rows:
            texts = row_texts(tr)
            if not texts:
                continue
            if normalize_key(texts[0]).startswith("difficulty"):
                diffs = [d for d in (normalize_difficulty(t) for t in texts[1:]) if d]
                if diffs:
                    return table, diffs
    return None, []


def parse_labeled_designers(text: str) -> dict[str, str]:
    result: dict[str, str] = {}
    matches = list(re.finditer(r"\[\s*(Past|Present|Future|Eternal|Beyond|Inscribed)\s*\]", text, re.I))
    for i, match in enumerate(matches):
        diff = normalize_difficulty(match.group(1))
        if not diff:
            continue
        start = match.end()
        end = matches[i + 1].start() if i + 1 < len(matches) else len(text)
        designer = clean_text(text[start:end].strip(" /,;"))
        if designer:
            result[diff] = designer
    return result


def parse_constants_from_text(page_text: str) -> dict[str, float | None]:
    constants: dict[str, float | None] = {}
    difficulty_words = "Past|Present|Future|Eternal|Beyond|Inscribed"
    patterns = [
        rf"(?is)\b({difficulty_words})\b[^\n]{{0,100}}?譜面定数\s*[:：]\s*(未判明|\d{{1,2}}(?:\.\d+)?)",
        rf"(?is)\b({difficulty_words})\b[^\n]{{0,100}}?Chart\s*Constant\s*[:：]?\s*(unknown|\?|\d{{1,2}}(?:\.\d+)?)",
    ]
    for pattern in patterns:
        for m in re.finditer(pattern, page_text, re.I):
            diff = normalize_difficulty(m.group(1))
            if not diff:
                continue
            raw = clean_text(m.group(2))
            if raw in {"未判明", "?"} or raw.casefold() == "unknown":
                constants.setdefault(diff, None)
            else:
                try:
                    constants[diff] = float(raw)
                except ValueError:
                    pass
    return constants


def extract_last_modified(soup: BeautifulSoup) -> str | None:
    text = soup.get_text("\n", strip=True)
    m = re.search(r"Last-modified:\s*([^\n]+)", text, re.I)
    return clean_text(m.group(1)) if m else None


def parse_wikiwiki_song_page(page_html: str, source_url: str, requested_title: str | None = None) -> dict[str, Any]:
    soup = BeautifulSoup(page_html, "html.parser")
    h1 = soup.find("h1")
    title = clean_text(h1.get_text(" ", strip=True)) if h1 else clean_text(requested_title)
    if not title:
        title = clean_text(requested_title) or unquote(urlparse(source_url).path.rsplit("/", 1)[-1])

    chart_table, table_diffs = find_chart_table(soup)
    charts: dict[str, dict[str, Any]] = {
        d: {"level": None, "constant": None, "notes": None, "chart_designer": None}
        for d in table_diffs
    }

    artist: str | None = None
    pack: str | None = None
    bpm: int | float | str | None = None
    length: str | None = None
    side: str | None = None
    artwork: str | None = None
    artwork_credit: str | None = None
    added_version: str | None = None
    chart_designers: dict[str, str] = {}

    if chart_table:
        for tr in chart_table.find_all("tr"):
            cells = row_cells(tr)
            texts = [clean_text(c.get_text(" ", strip=True)) for c in cells]
            if not texts:
                continue
            label = normalize_key(texts[0]).replace(" ", "")

            if label.startswith("composer") and len(texts) >= 2:
                artist = texts[1] or artist
                if "artwork" in [normalize_key(t) for t in texts]:
                    ai = next((i for i, t in enumerate(texts) if normalize_key(t) == "artwork"), None)
                    if ai is not None and ai + 1 < len(texts):
                        artwork_credit = texts[ai + 1] or artwork_credit
                    if ai is not None and ai + 1 < len(cells):
                        artwork = first_image_url(cells[ai + 1], source_url) or artwork

            elif label in {"chartdesigner", "chart&designer"} or label.startswith("chartdesigner"):
                designer_text = " ".join(texts[1:])
                chart_designers.update(parse_labeled_designers(designer_text))

            elif label.startswith("level"):
                for diff, value in zip(table_diffs, texts[1:], strict=False):
                    charts.setdefault(diff, {"level": None, "constant": None, "notes": None, "chart_designer": None})["level"] = parse_level(value)

            elif label.startswith("notes"):
                for diff, value in zip(table_diffs, texts[1:], strict=False):
                    charts.setdefault(diff, {"level": None, "constant": None, "notes": None, "chart_designer": None})["notes"] = parse_int(value)

            elif label.startswith("length") and len(texts) >= 2:
                length = next((t for t in texts[1:] if re.search(r"\b\d{1,2}:\d{2}\b", t)), None) or length

            elif label == "bpm" and len(texts) >= 2:
                raw_bpm = next((t for t in texts[1:] if t), "")
                if re.fullmatch(r"\d+(?:\.\d+)?", raw_bpm):
                    bpm_value = float(raw_bpm)
                    bpm = int(bpm_value) if bpm_value.is_integer() else bpm_value
                else:
                    bpm = raw_bpm or bpm

            elif label.startswith("pack") and len(texts) >= 2:
                pack = next((t for t in texts[1:] if t), None) or pack

            elif label.startswith("side") and len(texts) >= 2:
                side = next((t for t in texts[1:] if t), None) or side

            if "update" in label and "version" in label:
                versions = [parse_version(t) for t in texts[1:]]
                added_version = next((v for v in versions if v), added_version)

        # Some WikiWiki tables split "Update / Version" across rowspan rows.
        if added_version is None:
            table_text = clean_text(chart_table.get_text(" ", strip=True))
            m = re.search(r"Mobile\s+ver\.(\d+\.\d+(?:\.\d+)?)", table_text, re.I)
            if m:
                added_version = m.group(1)
            else:
                versions = re.findall(r"\bver\.\s*(\d+\.\d+(?:\.\d+)?)", table_text, re.I)
                if versions:
                    added_version = versions[0]

    page_text = soup.get_text("\n", strip=True)
    constants = parse_constants_from_text(page_text)
    for diff, constant in constants.items():
        charts.setdefault(diff, {"level": None, "constant": None, "notes": None, "chart_designer": None})["constant"] = constant

    for diff, designer in chart_designers.items():
        charts.setdefault(diff, {"level": None, "constant": None, "notes": None, "chart_designer": None})["chart_designer"] = designer

    # If there was only one unlabelled chart designer, use it for all visible charts.
    if chart_table and charts and not any(c["chart_designer"] for c in charts.values()):
        for tr in chart_table.find_all("tr"):
            texts = row_texts(tr)
            if texts and normalize_key(texts[0]).replace(" ", "").startswith("chartdesigner"):
                raw = clean_text(" ".join(texts[1:]))
                if raw and "[" not in raw:
                    for chart in charts.values():
                        chart["chart_designer"] = raw
                break

    # Artwork often sits in a rowspan cell, so fall back to the first sizeable image in the chart table.
    if artwork is None and chart_table:
        for img in chart_table.find_all("img"):
            src = img.get("data-src") or img.get("src")
            alt = clean_text(img.get("alt") or "")
            if src and "Now printing" not in alt and "Now_printing" not in src:
                artwork = urljoin(source_url, src)
                break

    errors: list[str] = []
    warnings: list[str] = []
    if not charts:
        errors.append("No chart columns found")
    if charts and not any(c.get("notes") is not None for c in charts.values()):
        warnings.append("No note counts found")
    if charts and not any(c.get("constant") is not None for c in charts.values()):
        warnings.append("No known chart constants found")

    for diff, chart in charts.items():
        notes = chart.get("notes")
        constant = chart.get("constant")
        if notes is not None and notes <= 0:
            errors.append(f"{diff} note count is not positive: {notes}")
        if constant is not None and not (0.0 <= float(constant) <= 15.0):
            errors.append(f"{diff} constant is implausible: {constant}")

    missing_constants = [d for d, c in charts.items() if c.get("level") and c.get("constant") is None]
    missing_notes = [d for d, c in charts.items() if c.get("level") and c.get("notes") is None]

    return {
        "source": "arcaea_wikiwiki_jp",
        "song": {
            "title": title,
            "artist": artist,
            "pack": pack,
            "bpm": bpm,
            "length": length,
            "side": side,
            "artwork": artwork,
            "added_version": added_version,
        },
        "charts": charts,
        "_meta": {
            "source_url": source_url,
            "fetched_at": utc_now(),
            "source_updated_at": extract_last_modified(soup),
            "parser_version": "0.3.0",
            "artwork_credit": artwork_credit,
            "missing_constants": missing_constants,
            "missing_notes": missing_notes,
            "validation": {
                "ok": not errors,
                "errors": errors,
                "warnings": warnings,
            },
        },
    }


def build_song_url(title_or_page: str) -> str:
    return WIKIWIKI_ROOT + quote(title_or_page, safe="")


def select_index_entries(index_data: dict[str, Any], titles: list[str] | None, all_entries: bool) -> list[dict[str, Any]]:
    songs = list(index_data.get("songs", []))
    if all_entries:
        return songs
    wanted = {normalize_key(t) for t in (titles or [])}
    selected = [s for s in songs if normalize_key(s.get("title", "")) in wanted or normalize_key(s.get("page", "")) in wanted]
    found = {normalize_key(s.get("title", "")) for s in selected} | {normalize_key(s.get("page", "")) for s in selected}
    missing = [t for t in (titles or []) if normalize_key(t) not in found]
    for title in missing:
        selected.append({"title": title, "page": title, "url": build_song_url(title)})
    return selected


def fetch_wikiwiki_pages(
    client: HttpClient,
    entries: Iterable[dict[str, Any]],
    delay: float = 0.2,
    max_pages: int | None = None,
) -> dict[str, Any]:
    records: list[dict[str, Any]] = []
    failures: list[dict[str, str]] = []
    entries_list = list(entries)
    if max_pages is not None:
        entries_list = entries_list[:max_pages]

    for index, entry in enumerate(entries_list):
        title = clean_text(entry.get("title") or entry.get("page"))
        url = entry.get("url") or build_song_url(entry.get("page") or title)
        try:
            page_html = client.get_text(url)
            record = parse_wikiwiki_song_page(page_html, url, title)
            record["_meta"]["index_entry"] = {
                "title": entry.get("title"),
                "page": entry.get("page"),
                "kind": entry.get("kind"),
            }
            records.append(record)
        except Exception as exc:
            failures.append({"title": title, "url": url, "error": str(exc)})
        if delay > 0 and index + 1 < len(entries_list):
            time.sleep(delay)

    invalid = [r["song"]["title"] for r in records if not r["_meta"]["validation"]["ok"]]
    return {
        "source": "arcaea_wikiwiki_jp",
        "fetched_at": utc_now(),
        "validation": {
            "ok": not failures and not invalid and bool(records),
            "requested": len(entries_list),
            "parsed": len(records),
            "failed": len(failures),
            "invalid": invalid,
            "failures": failures,
        },
        "songs": records,
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
        f"kind counts={validation['parsed_by_kind']}; index version={result['index_game_version']}",
        file=sys.stderr,
    )
    for warning in validation["warnings"]:
        print(f"WARNING: {warning}", file=sys.stderr)
    for error in validation["errors"]:
        print(f"ERROR: {error}", file=sys.stderr)
    return 2 if args.strict and not validation["ok"] else 0


def cmd_wikiwiki_pages(args: argparse.Namespace) -> int:
    client = HttpClient(timeout=args.timeout, retries=args.retries)
    if args.index:
        index_data = json.loads(Path(args.index).read_text(encoding="utf-8"))
        entries = select_index_entries(index_data, args.title, args.all)
    else:
        if args.all:
            raise SystemExit("--all requires --index")
        if not args.title:
            raise SystemExit("Provide at least one --title or use --index --all")
        entries = [{"title": t, "page": t, "url": build_song_url(t)} for t in args.title]

    result = fetch_wikiwiki_pages(client, entries, delay=args.delay, max_pages=args.max_pages)
    write_json(result, Path(args.output) if args.output else None)
    v = result["validation"]
    print(
        f"WikiWiki JP pages: requested={v['requested']} parsed={v['parsed']} failed={v['failed']} invalid={len(v['invalid'])}",
        file=sys.stderr,
    )
    for failure in v["failures"]:
        print(f"FETCH ERROR: {failure['title']}: {failure['error']}", file=sys.stderr)
    return 2 if args.strict and not v["ok"] else 0


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

    pages = sub.add_parser("wikiwiki-pages", help="Fetch + normalize individual WikiWiki JP song pages")
    pages.add_argument("--index", help="Previously generated wikiwiki index JSON")
    pages.add_argument("--title", action="append", help="Song title/page; repeat for multiple songs")
    pages.add_argument("--all", action="store_true", help="Fetch every entry from --index")
    pages.add_argument("--max-pages", type=int, default=None)
    pages.add_argument("--delay", type=float, default=0.2, help="Delay between page requests in seconds")
    pages.add_argument("--output", "-o")
    pages.add_argument("--timeout", type=float, default=25.0)
    pages.add_argument("--retries", type=int, default=3)
    pages.add_argument("--strict", action="store_true")
    pages.set_defaults(func=cmd_wikiwiki_pages)
    return parser


def main() -> int:
    args = build_parser().parse_args()
    return int(args.func(args))


if __name__ == "__main__":
    raise SystemExit(main())
