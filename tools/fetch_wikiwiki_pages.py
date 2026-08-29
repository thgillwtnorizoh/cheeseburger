#!/usr/bin/env python3
"""Fetch individual Arcaea WikiWiki JP song pages and normalize chart data."""

from __future__ import annotations

import argparse
import html
import json
import re
import sys
import time
import unicodedata
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Iterable
from urllib.parse import quote, urljoin, urlparse, unquote

import requests
from bs4 import BeautifulSoup, Tag

ROOT = "https://wikiwiki.jp/arcaea/"
USER_AGENT = (
    "RenderMyMind-ArcaeaDB/0.5 "
    "(+https://github.com/thgillwtnorizoh/cheeseburger; respectful read-only fetcher)"
)
DIFF_NAMES = {
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


def utc_now() -> str:
    return datetime.now(timezone.utc).replace(microsecond=0).isoformat().replace("+00:00", "Z")


def clean(value: str | None) -> str:
    if not value:
        return ""
    value = html.unescape(value).replace("\xa0", " ")
    return re.sub(r"\s+", " ", value).strip()


def key(value: str) -> str:
    return unicodedata.normalize("NFKC", clean(value)).casefold()


def diff_code(value: str) -> str | None:
    text = key(value).replace("[", " ").replace("]", " ")
    for name, code in DIFF_NAMES.items():
        if re.search(rf"\b{re.escape(name)}\b", text):
            return code
    return None


def parse_level(value: str) -> str | None:
    value = clean(value)
    m = re.search(r"(?:^|\s)(\?|\d{1,2}\+?)(?:\s|$)", value)
    return m.group(1) if m else None


def parse_int(value: str) -> int | None:
    value = clean(value).replace(",", "")
    m = re.search(r"(?<!\d)(\d{1,7})(?!\d)", value)
    return int(m.group(1)) if m else None


def row_cells(tr: Tag) -> list[Tag]:
    return list(tr.find_all(["td", "th"], recursive=False))


def row_texts(tr: Tag) -> list[str]:
    return [clean(c.get_text(" ", strip=True)) for c in row_cells(tr)]


def blank_chart() -> dict[str, Any]:
    return {"level": None, "constant": None, "notes": None, "chart_designer": None}


class Client:
    def __init__(self, timeout: float, retries: int) -> None:
        self.timeout = timeout
        self.retries = retries
        self.session = requests.Session()
        self.session.headers.update({
            "User-Agent": USER_AGENT,
            "Accept-Language": "en,ja;q=0.9",
        })

    def get(self, url: str) -> str:
        last: Exception | None = None
        for attempt in range(self.retries):
            try:
                r = self.session.get(url, timeout=self.timeout)
                r.raise_for_status()
                if not r.encoding or r.encoding.lower() == "iso-8859-1":
                    r.encoding = r.apparent_encoding or "utf-8"
                return r.text
            except Exception as exc:
                last = exc
                if attempt + 1 < self.retries:
                    time.sleep(1 + attempt)
        raise RuntimeError(f"GET failed: {url}: {last}")


def find_chart_table(soup: BeautifulSoup) -> tuple[Tag | None, list[str]]:
    for table in soup.find_all("table"):
        for tr in table.find_all("tr"):
            texts = row_texts(tr)
            if texts and key(texts[0]).startswith("difficulty"):
                diffs = [d for d in (diff_code(v) for v in texts[1:]) if d]
                if diffs:
                    return table, diffs
    return None, []


def find_label_value(texts: list[str], label: str) -> str | None:
    target = key(label)
    for i, text in enumerate(texts):
        if key(text) == target:
            return next((clean(v) for v in texts[i + 1:] if clean(v)), None)
    return None


def image_from_cell(cell: Tag, base_url: str) -> str | None:
    img = cell.find("img")
    if not img:
        return None
    src = img.get("data-src") or img.get("src")
    if not src:
        return None
    alt = clean(img.get("alt") or "")
    absolute = urljoin(base_url, src)
    if "Now printing" in alt or "Now_printing" in absolute:
        return None
    return absolute


def parse_designer_groups(text: str) -> dict[str, str]:
    out: dict[str, str] = {}
    groups = list(re.finditer(r"\[([^\]]+)\]", text))
    diff_groups = [
        (m, [d for d in (diff_code(n.strip()) for n in re.split(r"[/,]", m.group(1))) if d])
        for m in groups
    ]
    for i, (match, codes) in enumerate(diff_groups):
        if not codes:
            continue
        end = len(text)
        for later_match, later_codes in diff_groups[i + 1:]:
            if later_codes:
                end = later_match.start()
                break
        designer = clean(text[match.end():end].strip(" /,;:-"))
        designer = re.split(r"(?:アートワーク画像|Artwork)", designer, maxsplit=1, flags=re.I)[0].strip()
        if not designer:
            continue
        for code in codes:
            out[code] = designer
    return out


def parse_constants(soup: BeautifulSoup) -> dict[str, float | None]:
    """Parse chart constants from rendered document order.

    WikiWiki's nested list HTML can make later difficulty bullets descendants
    of earlier bullets. Parent-node parsing therefore leaks values upward.
    Line-order parsing associates each constant with the nearest short
    difficulty label immediately before it instead.
    """
    lines = [clean(line) for line in soup.get_text("\n", strip=True).splitlines() if clean(line)]
    out: dict[str, float | None] = {}

    for i, line in enumerate(lines):
        if "譜面定数" not in line:
            continue

        window_after = " ".join(lines[i:i + 3])
        m = re.search(r"譜面定数\s*[:：]\s*(未判明|\d{1,2}(?:\.\d+)?)", window_after)
        if not m:
            continue

        code = diff_code(line)
        if code is None:
            for j in range(i - 1, max(-1, i - 6), -1):
                candidate = lines[j]
                if len(candidate) > 32:
                    continue
                candidate_code = diff_code(candidate)
                if candidate_code:
                    code = candidate_code
                    break
        if code is None:
            continue

        raw = m.group(1)
        out[code] = None if raw == "未判明" else float(raw)

    return out


def last_modified(soup: BeautifulSoup) -> str | None:
    m = re.search(r"Last-modified:\s*([^\n]+)", soup.get_text("\n", strip=True), re.I)
    return clean(m.group(1)) if m else None


def parse_page(page_html: str, url: str, requested_title: str | None = None) -> dict[str, Any]:
    soup = BeautifulSoup(page_html, "html.parser")
    h1 = soup.find("h1")
    title = clean(h1.get_text(" ", strip=True)) if h1 else clean(requested_title)
    if not title:
        title = unquote(urlparse(url).path.rsplit("/", 1)[-1])

    table, diffs = find_chart_table(soup)
    charts = {d: blank_chart() for d in diffs}

    artist = pack = length = side = artwork = artwork_credit = added_version = None
    bpm: int | float | str | None = None
    designers: dict[str, str] = {}

    if table:
        for tr in table.find_all("tr"):
            cells = row_cells(tr)
            texts = [clean(c.get_text(" ", strip=True)) for c in cells]
            if not texts:
                continue
            label = key(texts[0]).replace(" ", "")

            composer = find_label_value(texts, "Composer")
            if composer:
                artist = composer

            for i, text in enumerate(texts):
                if key(text) == "artwork":
                    if i + 1 < len(texts) and texts[i + 1]:
                        artwork_credit = texts[i + 1]
                    if i + 1 < len(cells):
                        artwork = image_from_cell(cells[i + 1], url) or artwork

            if label.startswith("chartdesigner"):
                raw_parts = []
                for value in texts[1:]:
                    if key(value) == "artwork":
                        break
                    raw_parts.append(value)
                designers.update(parse_designer_groups(" ".join(raw_parts)))

            elif label == "level":
                for d, value in zip(diffs, texts[1:], strict=False):
                    charts[d]["level"] = parse_level(value)

            elif label == "notes":
                for d, value in zip(diffs, texts[1:], strict=False):
                    charts[d]["notes"] = parse_int(value)

            elif label == "length":
                m = re.search(r"\b\d{1,2}:\d{2}\b", " ".join(texts[1:]))
                if m:
                    length = m.group(0)

            elif label == "bpm":
                raw = next((v for v in texts[1:] if v), "")
                if re.fullmatch(r"\d+(?:\.\d+)?", raw):
                    n = float(raw)
                    bpm = int(n) if n.is_integer() else n
                elif raw:
                    bpm = raw

            elif label == "pack":
                pack = next((v for v in texts[1:] if v), pack)

            elif label == "side":
                side = next((v for v in texts[1:] if v), side)

        table_text = clean(table.get_text(" ", strip=True))
        m = re.search(r"Mobile\s+ver\.\s*(\d+\.\d+(?:\.\d+)?)", table_text, re.I)
        if m:
            added_version = m.group(1)
        else:
            versions = re.findall(r"\bver\.\s*(\d+\.\d+(?:\.\d+)?)", table_text, re.I)
            if versions:
                added_version = versions[0]

    for d, value in parse_constants(soup).items():
        charts.setdefault(d, blank_chart())["constant"] = value
    for d, designer in designers.items():
        charts.setdefault(d, blank_chart())["chart_designer"] = designer

    if artwork is None and table:
        for cell in table.find_all(["td", "th"]):
            candidate = image_from_cell(cell, url)
            if candidate:
                artwork = candidate
                break

    errors: list[str] = []
    warnings: list[str] = []
    if not charts:
        errors.append("No difficulty columns found")
    if charts and not any(c["notes"] is not None for c in charts.values()):
        warnings.append("No note counts found")
    if charts and not any(c["constant"] is not None for c in charts.values()):
        warnings.append("No known chart constants found")

    for d, c in charts.items():
        if c["notes"] is not None and c["notes"] <= 0:
            errors.append(f"{d}: non-positive note count {c['notes']}")
        if c["constant"] is not None and not (0 <= c["constant"] <= 15):
            errors.append(f"{d}: implausible constant {c['constant']}")

    missing_constants = [d for d, c in charts.items() if c["level"] and c["constant"] is None]
    missing_notes = [d for d, c in charts.items() if c["level"] and c["notes"] is None]

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
            "source_url": url,
            "fetched_at": utc_now(),
            "source_updated_at": last_modified(soup),
            "parser_version": "0.5.0",
            "artwork_credit": artwork_credit,
            "missing_constants": missing_constants,
            "missing_notes": missing_notes,
            "validation": {"ok": not errors, "errors": errors, "warnings": warnings},
        },
    }


def build_url(title_or_page: str) -> str:
    return ROOT + quote(title_or_page, safe="")


def select_entries(index: dict[str, Any] | None, titles: list[str], all_entries: bool) -> list[dict[str, Any]]:
    if index is None:
        return [{"title": t, "page": t, "url": build_url(t)} for t in titles]
    songs = list(index.get("songs", []))
    if all_entries:
        return songs
    wanted = {key(t) for t in titles}
    selected = [s for s in songs if key(s.get("title", "")) in wanted or key(s.get("page", "")) in wanted]
    matched = {key(s.get("title", "")) for s in selected} | {key(s.get("page", "")) for s in selected}
    for title in titles:
        if key(title) not in matched:
            selected.append({"title": title, "page": title, "url": build_url(title)})
    return selected


def fetch_pages(client: Client, entries: Iterable[dict[str, Any]], delay: float, max_pages: int | None) -> dict[str, Any]:
    entries = list(entries)
    if max_pages is not None:
        entries = entries[:max_pages]
    records: list[dict[str, Any]] = []
    failures: list[dict[str, str]] = []
    for i, entry in enumerate(entries):
        title = clean(entry.get("title") or entry.get("page"))
        url = entry.get("url") or build_url(entry.get("page") or title)
        try:
            record = parse_page(client.get(url), url, title)
            record["_meta"]["index_entry"] = {
                "title": entry.get("title"),
                "page": entry.get("page"),
                "kind": entry.get("kind"),
            }
            records.append(record)
        except Exception as exc:
            failures.append({"title": title, "url": url, "error": str(exc)})
        if delay and i + 1 < len(entries):
            time.sleep(delay)

    invalid = [r["song"]["title"] for r in records if not r["_meta"]["validation"]["ok"]]
    return {
        "source": "arcaea_wikiwiki_jp",
        "fetched_at": utc_now(),
        "validation": {
            "ok": bool(records) and not failures and not invalid,
            "requested": len(entries),
            "parsed": len(records),
            "failed": len(failures),
            "invalid": invalid,
            "failures": failures,
        },
        "songs": records,
    }


def main() -> int:
    p = argparse.ArgumentParser()
    p.add_argument("--index")
    p.add_argument("--title", action="append", default=[])
    p.add_argument("--all", action="store_true")
    p.add_argument("--max-pages", type=int)
    p.add_argument("--delay", type=float, default=0.2)
    p.add_argument("--timeout", type=float, default=25)
    p.add_argument("--retries", type=int, default=3)
    p.add_argument("--output", "-o")
    p.add_argument("--strict", action="store_true")
    args = p.parse_args()

    index = json.loads(Path(args.index).read_text(encoding="utf-8")) if args.index else None
    if args.all and index is None:
        p.error("--all requires --index")
    if not args.all and not args.title:
        p.error("provide --title at least once, or use --all --index")

    entries = select_entries(index, args.title, args.all)
    result = fetch_pages(Client(args.timeout, args.retries), entries, args.delay, args.max_pages)
    text = json.dumps(result, ensure_ascii=False, indent=2) + "\n"
    if args.output:
        path = Path(args.output)
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(text, encoding="utf-8")
    else:
        sys.stdout.write(text)

    v = result["validation"]
    print(f"WikiWiki pages: requested={v['requested']} parsed={v['parsed']} failed={v['failed']} invalid={len(v['invalid'])}", file=sys.stderr)
    return 2 if args.strict and not v["ok"] else 0


if __name__ == "__main__":
    raise SystemExit(main())
