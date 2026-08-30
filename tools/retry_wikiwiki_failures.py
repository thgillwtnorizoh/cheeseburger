#!/usr/bin/env python3
"""Retry only failed WikiWiki song-page fetches from a previous crawl artifact.

The worker never rediscoveries or recrawls pages that were already successful in the
source artifact. Exact failed URLs are deduplicated for network requests, every result
is checkpointed immediately, and anything not recovered remains queued for a later job.
"""

from __future__ import annotations

import argparse
import copy
import json
import random
import sys
import time
from collections import Counter
from datetime import datetime, timezone
from email.utils import parsedate_to_datetime
from pathlib import Path
from typing import Any

import requests

import fetch_wikiwiki_pages as wiki


class RateLimitedError(RuntimeError):
    def __init__(self, url: str, retry_after: float | None = None) -> None:
        super().__init__(f"429 Too Many Requests for {url}")
        self.url = url
        self.retry_after = retry_after


class RespectfulClient:
    """Small sequential client. 429 responses are never retried immediately."""

    def __init__(self, *, timeout: float, attempts: int) -> None:
        self.timeout = timeout
        self.attempts = max(1, attempts)
        self.session = requests.Session()
        self.session.headers.update(
            {
                "User-Agent": wiki.USER_AGENT,
                "Accept-Language": "en,ja;q=0.9",
            }
        )

    @staticmethod
    def retry_after_seconds(value: str | None) -> float | None:
        if not value:
            return None
        value = value.strip()
        try:
            return max(0.0, float(value))
        except ValueError:
            pass
        try:
            when = parsedate_to_datetime(value)
            if when.tzinfo is None:
                when = when.replace(tzinfo=timezone.utc)
            return max(0.0, (when - datetime.now(timezone.utc)).total_seconds())
        except (TypeError, ValueError, OverflowError):
            return None

    def get(self, url: str) -> str:
        last: Exception | None = None
        for attempt in range(self.attempts):
            try:
                response = self.session.get(url, timeout=self.timeout)
            except requests.RequestException as exc:
                last = exc
                if attempt + 1 >= self.attempts:
                    break
                wait = min(30.0, 5.0 * (2**attempt)) + random.uniform(0.0, 3.0)
                print(f"  network error: {exc}; retrying in {wait:.1f}s", flush=True)
                time.sleep(wait)
                continue

            if response.status_code == 429:
                # Deliberately do not retry this URL in-place. Leave it in the next
                # queue and let the outer worker hibernate before one cautious probe.
                raise RateLimitedError(
                    url,
                    self.retry_after_seconds(response.headers.get("Retry-After")),
                )

            if 500 <= response.status_code < 600 and attempt + 1 < self.attempts:
                last = RuntimeError(f"HTTP {response.status_code} for {url}")
                wait = min(45.0, 8.0 * (2**attempt)) + random.uniform(0.0, 3.0)
                print(f"  HTTP {response.status_code}: retrying in {wait:.1f}s", flush=True)
                time.sleep(wait)
                continue

            try:
                response.raise_for_status()
            except requests.RequestException as exc:
                raise RuntimeError(f"GET failed: {url}: {exc}") from exc

            if not response.encoding or response.encoding.lower() == "iso-8859-1":
                response.encoding = response.apparent_encoding or "utf-8"
            return response.text

        raise RuntimeError(f"GET failed: {url}: {last}")


def _load(path: Path) -> dict[str, Any]:
    data = json.loads(path.read_text(encoding="utf-8"))
    if not isinstance(data, dict) or not isinstance(data.get("validation"), dict):
        raise ValueError(f"{path} is not a WikiWiki crawl result")
    if not isinstance(data.get("songs"), list):
        raise ValueError(f"{path} has no songs array")
    failures = data["validation"].get("failures")
    if not isinstance(failures, list):
        raise ValueError(f"{path} has no validation.failures array")
    return data


def _group_failures(failures: list[dict[str, Any]]) -> dict[str, list[dict[str, Any]]]:
    by_url: dict[str, list[dict[str, Any]]] = {}
    for item in failures:
        if not isinstance(item, dict):
            continue
        title = wiki.clean(str(item.get("title") or ""))
        url = wiki.clean(str(item.get("url") or ""))
        if not url:
            if not title:
                continue
            url = wiki.build_url(title)
        normalized = dict(item)
        normalized["title"] = title
        normalized["url"] = url
        by_url.setdefault(url, []).append(normalized)
    return by_url


def _failure_copy(item: dict[str, Any], error: str) -> dict[str, str]:
    return {
        "title": wiki.clean(str(item.get("title") or "")),
        "url": wiki.clean(str(item.get("url") or "")),
        "error": error,
    }


def _record_for_failure(
    base_record: dict[str, Any], failure: dict[str, Any], source_run_id: str
) -> dict[str, Any]:
    record = copy.deepcopy(base_record)
    meta = record.setdefault("_meta", {})
    meta["index_entry"] = {
        "title": failure.get("title"),
        "page": None,
        "kind": None,
    }
    meta["retry"] = {
        "recovered_from_run_id": source_run_id or None,
        "recovered_at": wiki.utc_now(),
    }
    return record


def _flatten_pending(pending: dict[str, list[dict[str, Any]]]) -> list[dict[str, Any]]:
    return [item for group in pending.values() for item in group]


def _build_result(
    previous_validation: dict[str, Any],
    songs: list[dict[str, Any]],
    pending: dict[str, list[dict[str, Any]]],
) -> dict[str, Any]:
    remaining = _flatten_pending(pending)
    invalid = [
        record.get("song", {}).get("title")
        for record in songs
        if not record.get("_meta", {}).get("validation", {}).get("ok", False)
    ]
    invalid = [str(x) for x in invalid if x]
    target_total = int(previous_validation.get("requested", len(songs) + len(remaining)))
    return {
        "source": "arcaea_wikiwiki_jp",
        "fetched_at": wiki.utc_now(),
        "validation": {
            "ok": bool(songs) and not remaining and not invalid,
            "requested": target_total,
            "parsed": len(songs),
            "failed": len(remaining),
            "invalid": invalid,
            "failures": remaining,
        },
        "songs": songs,
    }


def _summarize(data: dict[str, Any], retry: dict[str, Any]) -> dict[str, Any]:
    songs = data.get("songs", [])
    validation = data.get("validation", {})
    charts: list[tuple[str | None, str, dict[str, Any]]] = []
    errors: list[dict[str, str]] = []
    warnings: list[dict[str, str]] = []
    suspicious: list[dict[str, Any]] = []
    error_counts: Counter[str] = Counter()
    warning_counts: Counter[str] = Counter()

    for record in songs:
        title = record.get("song", {}).get("title")
        rv = record.get("_meta", {}).get("validation", {})
        rec_errors = list(rv.get("errors", []))
        rec_warnings = list(rv.get("warnings", []))
        if rec_errors or rec_warnings:
            suspicious.append(
                {
                    "title": title,
                    "url": record.get("_meta", {}).get("source_url"),
                    "errors": rec_errors,
                    "warnings": rec_warnings,
                }
            )
        for message in rec_errors:
            errors.append({"title": str(title), "message": str(message)})
            error_counts[str(message)] += 1
        for message in rec_warnings:
            warnings.append({"title": str(title), "message": str(message)})
            warning_counts[str(message)] += 1
        for difficulty, chart in record.get("charts", {}).items():
            if (
                chart.get("level") is None
                and chart.get("constant") is None
                and chart.get("notes") is None
            ):
                continue
            charts.append((title, difficulty, chart))

    charts_with_cc = sum(chart.get("constant") is not None for _, _, chart in charts)
    charts_with_notes = sum(chart.get("notes") is not None for _, _, chart in charts)
    return {
        "retry": retry,
        "songs_pages_target_total": validation.get("requested", 0),
        "pages_fetched_cumulative": validation.get("parsed", len(songs)),
        "pages_failed_remaining": validation.get("failed", 0),
        "invalid_pages": len(validation.get("invalid", [])),
        "charts_parsed": len(charts),
        "charts_with_cc": charts_with_cc,
        "charts_missing_cc": len(charts) - charts_with_cc,
        "charts_with_note_counts": charts_with_notes,
        "charts_missing_note_counts": len(charts) - charts_with_notes,
        "validation_errors": len(errors),
        "validation_warnings": len(warnings),
        "fetch_failures": validation.get("failures", []),
        "invalid_titles": validation.get("invalid", []),
        "error_counts": dict(error_counts),
        "warning_counts": dict(warning_counts),
        "suspicious_pages": suspicious,
    }


def _write_checkpoint(
    args: argparse.Namespace,
    result: dict[str, Any],
    retry: dict[str, Any],
) -> dict[str, Any]:
    summary = _summarize(result, retry)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(
        json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8"
    )
    args.summary_output.write_text(
        json.dumps(summary, ensure_ascii=False, indent=2) + "\n", encoding="utf-8"
    )
    failures = result["validation"]["failures"]
    args.remaining_output.write_text(
        json.dumps(
            {
                "source": "arcaea_wikiwiki_jp",
                "generated_at": wiki.utc_now(),
                "source_run_id": args.source_run_id or None,
                "remaining_count": len(failures),
                "remaining_unique_urls": len(
                    {x.get("url") for x in failures if x.get("url")}
                ),
                "failures": failures,
            },
            ensure_ascii=False,
            indent=2,
        )
        + "\n",
        encoding="utf-8",
    )
    return summary


def retry_failures(args: argparse.Namespace) -> dict[str, Any]:
    previous = _load(args.previous)
    previous_validation = previous["validation"]
    previous_failures = [
        x for x in previous_validation.get("failures", []) if isinstance(x, dict)
    ]
    grouped = _group_failures(previous_failures)
    ordered = list(grouped.items())
    pending: dict[str, list[dict[str, Any]]] = copy.deepcopy(grouped)
    songs: list[dict[str, Any]] = copy.deepcopy(previous.get("songs", []))

    client = RespectfulClient(timeout=args.timeout, attempts=args.attempts)

    network_attempted = 0
    network_succeeded = 0
    network_failed = 0
    recovered_failure_records = 0
    hibernations = 0
    probing_after_hibernation = False
    stopped_after_persistent_429 = False

    def retry_meta() -> dict[str, Any]:
        remaining = _flatten_pending(pending)
        return {
            "source_run_id": args.source_run_id or None,
            "failure_records_queued": len(previous_failures),
            "unique_urls_queued": len(grouped),
            "duplicate_failure_records_avoided_as_network_requests": len(previous_failures)
            - len(grouped),
            "network_urls_attempted": network_attempted,
            "network_urls_succeeded": network_succeeded,
            "network_urls_failed": network_failed,
            "failure_records_recovered": recovered_failure_records,
            "failure_records_remaining": len(remaining),
            "unique_urls_remaining": len(pending),
            "hibernations": hibernations,
            "stopped_after_persistent_429": stopped_after_persistent_429,
            "initial_cooldown_seconds": args.initial_cooldown,
            "base_inter_request_delay_seconds": args.delay,
            "inter_request_jitter_seconds": args.jitter,
            "network_attempts_per_url": args.attempts,
            "cooldown_seconds": args.cooldown,
            "cooldown_jitter_seconds": args.cooldown_jitter,
            "checkpoint_after_every_url": True,
        }

    def checkpoint() -> dict[str, Any]:
        result = _build_result(previous_validation, songs, pending)
        return _write_checkpoint(args, result, retry_meta())

    print(
        f"Previous queue: {len(previous_failures)} failure record(s), "
        f"{len(grouped)} unique URL(s)",
        flush=True,
    )
    checkpoint()

    if args.initial_cooldown > 0 and ordered:
        print(
            f"Initial courtesy cooldown: {args.initial_cooldown:.1f}s before first request",
            flush=True,
        )
        time.sleep(args.initial_cooldown)

    for index, (url, failure_group) in enumerate(ordered):
        # A URL can disappear only after a successful checkpoint.
        if url not in pending:
            continue

        title_text = " | ".join(
            str(x.get("title") or "(untitled)") for x in failure_group
        )
        print(f"[{index + 1}/{len(ordered)}] Fetching {title_text}", flush=True)
        network_attempted += 1

        try:
            page_html = client.get(url)
            base_record = wiki.parse_page(page_html, url, failure_group[0].get("title"))
            for failure in failure_group:
                songs.append(
                    _record_for_failure(base_record, failure, args.source_run_id)
                )
            recovered_failure_records += len(failure_group)
            network_succeeded += 1
            pending.pop(url, None)
            probing_after_hibernation = False
            print(f"  recovered {len(failure_group)} failure record(s)", flush=True)
            checkpoint()

            if index + 1 < len(ordered):
                wait = args.delay + random.uniform(0.0, args.jitter)
                if wait > 0:
                    print(f"  courtesy spacing: {wait:.1f}s", flush=True)
                    time.sleep(wait)

        except RateLimitedError as exc:
            network_failed += 1
            pending[url] = [_failure_copy(item, str(exc)) for item in failure_group]
            checkpoint()

            if probing_after_hibernation:
                stopped_after_persistent_429 = True
                print(
                    "  probe after hibernation also received 429; stopping now and "
                    "leaving this plus every untouched URL for the next job",
                    flush=True,
                )
                checkpoint()
                break

            server_wait = exc.retry_after or 0.0
            wait = max(args.cooldown, server_wait) + random.uniform(
                0.0, args.cooldown_jitter
            )
            hibernations += 1
            probing_after_hibernation = True
            print(
                f"  429 throttle: hibernating {wait:.1f}s before one cautious probe; "
                "this URL remains queued for next job",
                flush=True,
            )
            checkpoint()
            time.sleep(wait)

        except Exception as exc:
            network_failed += 1
            pending[url] = [_failure_copy(item, str(exc)) for item in failure_group]
            probing_after_hibernation = False
            print(f"  failed: {exc}; keeping URL in next queue", flush=True)
            checkpoint()

            if index + 1 < len(ordered):
                wait = args.delay + random.uniform(0.0, args.jitter)
                if wait > 0:
                    time.sleep(wait)

    result = _build_result(previous_validation, songs, pending)
    summary = _write_checkpoint(args, result, retry_meta())
    print(json.dumps(summary, ensure_ascii=False, indent=2), flush=True)
    return summary


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--previous", type=Path, required=True, help="Previous wikiwiki-jp-pages.json"
    )
    parser.add_argument("--output", type=Path, required=True, help="Cumulative output JSON")
    parser.add_argument("--summary-output", type=Path, required=True)
    parser.add_argument("--remaining-output", type=Path, required=True)
    parser.add_argument("--source-run-id", default="")
    parser.add_argument(
        "--initial-cooldown", type=float, default=60.0, help="Quiet period before the first request"
    )
    parser.add_argument(
        "--delay", type=float, default=8.0, help="Base delay between distinct page URLs"
    )
    parser.add_argument(
        "--jitter", type=float, default=4.0, help="Additional random inter-request delay"
    )
    parser.add_argument("--timeout", type=float, default=25.0)
    parser.add_argument(
        "--attempts", type=int, default=2, help="Attempts for network/5xx errors; 429 is never retried in-place"
    )
    parser.add_argument(
        "--cooldown", type=float, default=180.0, help="Minimum hibernation after the first 429"
    )
    parser.add_argument("--cooldown-jitter", type=float, default=60.0)
    args = parser.parse_args()

    if args.attempts < 1:
        parser.error("--attempts must be at least 1")
    if any(
        value < 0
        for value in (
            args.initial_cooldown,
            args.delay,
            args.jitter,
            args.cooldown,
            args.cooldown_jitter,
        )
    ):
        parser.error("delay/cooldown values cannot be negative")

    retry_failures(args)
    return 0


if __name__ == "__main__":
    sys.exit(main())
