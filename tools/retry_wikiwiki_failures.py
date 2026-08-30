#!/usr/bin/env python3
"""Retry only failed WikiWiki song-page fetches from a previous crawl artifact.

This deliberately does not rediscover or recrawl successful pages. It consumes the
previous artifact's validation.failures queue, deduplicates exact URLs for network
requests, and leaves any new failures in the output queue for a later job.
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
    pass


class RespectfulClient:
    def __init__(
        self,
        *,
        timeout: float,
        attempts: int,
        rate_backoff: float,
        rate_backoff_cap: float,
    ) -> None:
        self.timeout = timeout
        self.attempts = max(1, attempts)
        self.rate_backoff = max(1.0, rate_backoff)
        self.rate_backoff_cap = max(self.rate_backoff, rate_backoff_cap)
        self.session = requests.Session()
        self.session.headers.update(
            {
                "User-Agent": wiki.USER_AGENT,
                "Accept-Language": "en,ja;q=0.9",
            }
        )

    @staticmethod
    def _retry_after_seconds(value: str | None) -> float | None:
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
                wait = min(20.0, 3.0 * (2**attempt)) + random.uniform(0.0, 2.0)
                print(f"  network error: {exc}; retrying in {wait:.1f}s", flush=True)
                time.sleep(wait)
                continue

            if response.status_code == 429:
                last = RateLimitedError(f"429 Too Many Requests for {url}")
                if attempt + 1 >= self.attempts:
                    raise last

                server_wait = self._retry_after_seconds(response.headers.get("Retry-After"))
                if server_wait is not None:
                    wait = server_wait
                    reason = "server Retry-After"
                else:
                    wait = min(self.rate_backoff_cap, self.rate_backoff * (2**attempt))
                    wait += random.uniform(0.0, 5.0)
                    reason = "exponential 429 backoff"
                print(f"  429 throttle: sleeping {wait:.1f}s ({reason})", flush=True)
                time.sleep(wait)
                continue

            if 500 <= response.status_code < 600 and attempt + 1 < self.attempts:
                last = RuntimeError(f"HTTP {response.status_code} for {url}")
                wait = min(30.0, 5.0 * (2**attempt)) + random.uniform(0.0, 2.0)
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


def _group_failures(failures: list[dict[str, Any]]) -> list[tuple[str, list[dict[str, Any]]]]:
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
    return list(by_url.items())


def _failure_copy(item: dict[str, Any], error: str) -> dict[str, str]:
    return {
        "title": wiki.clean(str(item.get("title") or "")),
        "url": wiki.clean(str(item.get("url") or "")),
        "error": error,
    }


def _record_for_failure(base_record: dict[str, Any], failure: dict[str, Any], source_run_id: str) -> dict[str, Any]:
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


def _summarize(
    data: dict[str, Any],
    retry: dict[str, Any],
) -> dict[str, Any]:
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
            if chart.get("level") is None and chart.get("constant") is None and chart.get("notes") is None:
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


def retry_failures(args: argparse.Namespace) -> tuple[dict[str, Any], dict[str, Any]]:
    previous = _load(args.previous)
    previous_validation = previous["validation"]
    previous_failures = [x for x in previous_validation.get("failures", []) if isinstance(x, dict)]
    grouped = _group_failures(previous_failures)

    client = RespectfulClient(
        timeout=args.timeout,
        attempts=args.attempts,
        rate_backoff=args.rate_backoff,
        rate_backoff_cap=args.rate_backoff_cap,
    )

    recovered_records: list[dict[str, Any]] = []
    remaining_failures: list[dict[str, str]] = []
    network_attempted = 0
    network_succeeded = 0
    network_failed = 0
    deferred_urls = 0
    recovered_failure_records = 0
    consecutive_rate_limited_urls = 0
    hibernations = 0
    probing_after_hibernation = False
    deferred_from_index: int | None = None

    print(
        f"Previous queue: {len(previous_failures)} failure record(s), "
        f"{len(grouped)} unique URL(s)",
        flush=True,
    )

    for index, (url, failure_group) in enumerate(grouped):
        if deferred_from_index is not None:
            break

        title_text = " | ".join(x.get("title") or "(untitled)" for x in failure_group)
        print(f"[{index + 1}/{len(grouped)}] Fetching {title_text}", flush=True)
        network_attempted += 1
        hibernated_this_iteration = False

        try:
            page_html = client.get(url)
            base_record = wiki.parse_page(page_html, url, failure_group[0].get("title"))
            for failure in failure_group:
                recovered_records.append(_record_for_failure(base_record, failure, args.source_run_id))
            recovered_failure_records += len(failure_group)
            network_succeeded += 1
            consecutive_rate_limited_urls = 0
            probing_after_hibernation = False
            print(f"  recovered {len(failure_group)} failure record(s)", flush=True)
        except RateLimitedError as exc:
            network_failed += 1
            consecutive_rate_limited_urls += 1
            for failure in failure_group:
                remaining_failures.append(_failure_copy(failure, str(exc)))
            print(
                f"  still throttled ({consecutive_rate_limited_urls}/{args.circuit_breaker} consecutive URL failures)",
                flush=True,
            )

            if consecutive_rate_limited_urls >= args.circuit_breaker:
                if not probing_after_hibernation:
                    wait = args.cooldown + random.uniform(0.0, args.cooldown_jitter)
                    hibernations += 1
                    probing_after_hibernation = True
                    consecutive_rate_limited_urls = 0
                    hibernated_this_iteration = True
                    print(
                        f"  hibernating for {wait:.1f}s before a cautious probe; no requests during cooldown",
                        flush=True,
                    )
                    time.sleep(wait)
                else:
                    deferred_from_index = index + 1
                    print(
                        "  throttling persisted after hibernation; deferring every untouched URL to the next job",
                        flush=True,
                    )
        except Exception as exc:
            network_failed += 1
            consecutive_rate_limited_urls = 0
            for failure in failure_group:
                remaining_failures.append(_failure_copy(failure, str(exc)))
            print(f"  failed: {exc}", flush=True)

        if index + 1 < len(grouped) and not hibernated_this_iteration and deferred_from_index is None:
            wait = args.delay + random.uniform(0.0, args.jitter)
            if wait > 0:
                time.sleep(wait)

    if deferred_from_index is not None:
        untouched = grouped[deferred_from_index:]
        deferred_urls = len(untouched)
        for _url, failure_group in untouched:
            for failure in failure_group:
                remaining_failures.append(
                    _failure_copy(
                        failure,
                        "Deferred without request after persistent 429 throttling; queued for next retry job",
                    )
                )

    cumulative_songs = list(previous.get("songs", [])) + recovered_records
    invalid = [
        record.get("song", {}).get("title")
        for record in cumulative_songs
        if not record.get("_meta", {}).get("validation", {}).get("ok", False)
    ]
    invalid = [str(x) for x in invalid if x]

    target_total = int(previous_validation.get("requested", len(cumulative_songs) + len(previous_failures)))
    result = {
        "source": "arcaea_wikiwiki_jp",
        "fetched_at": wiki.utc_now(),
        "validation": {
            "ok": bool(cumulative_songs) and not remaining_failures and not invalid,
            "requested": target_total,
            "parsed": len(cumulative_songs),
            "failed": len(remaining_failures),
            "invalid": invalid,
            "failures": remaining_failures,
        },
        "songs": cumulative_songs,
    }

    retry = {
        "source_run_id": args.source_run_id or None,
        "failure_records_queued": len(previous_failures),
        "unique_urls_queued": len(grouped),
        "duplicate_failure_records_avoided_as_network_requests": len(previous_failures) - len(grouped),
        "network_urls_attempted": network_attempted,
        "network_urls_succeeded": network_succeeded,
        "network_urls_failed": network_failed,
        "network_urls_deferred_without_request": deferred_urls,
        "failure_records_recovered": recovered_failure_records,
        "failure_records_remaining": len(remaining_failures),
        "unique_urls_remaining": len({x.get("url") for x in remaining_failures if x.get("url")}),
        "hibernations": hibernations,
        "base_inter_request_delay_seconds": args.delay,
        "inter_request_jitter_seconds": args.jitter,
        "attempts_per_url": args.attempts,
        "circuit_breaker_consecutive_429_urls": args.circuit_breaker,
        "cooldown_seconds": args.cooldown,
    }
    return result, retry


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--previous", type=Path, required=True, help="Previous wikiwiki-jp-pages.json")
    parser.add_argument("--output", type=Path, required=True, help="Cumulative output JSON")
    parser.add_argument("--summary-output", type=Path, required=True)
    parser.add_argument("--remaining-output", type=Path, required=True)
    parser.add_argument("--source-run-id", default="")
    parser.add_argument("--delay", type=float, default=2.5, help="Base delay between distinct page URLs")
    parser.add_argument("--jitter", type=float, default=1.5, help="Additional random inter-request delay")
    parser.add_argument("--timeout", type=float, default=25.0)
    parser.add_argument("--attempts", type=int, default=2, help="Maximum request attempts per URL")
    parser.add_argument("--rate-backoff", type=float, default=20.0, help="Initial 429 backoff when Retry-After is absent")
    parser.add_argument("--rate-backoff-cap", type=float, default=120.0)
    parser.add_argument("--circuit-breaker", type=int, default=3, help="Consecutive rate-limited URLs before hibernation")
    parser.add_argument("--cooldown", type=float, default=180.0, help="Hibernation after sustained throttling")
    parser.add_argument("--cooldown-jitter", type=float, default=30.0)
    args = parser.parse_args()

    if args.circuit_breaker < 1:
        parser.error("--circuit-breaker must be at least 1")
    if args.delay < 0 or args.jitter < 0 or args.cooldown < 0 or args.cooldown_jitter < 0:
        parser.error("delay/cooldown values cannot be negative")

    result, retry = retry_failures(args)
    summary = _summarize(result, retry)

    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    args.summary_output.write_text(json.dumps(summary, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    args.remaining_output.write_text(
        json.dumps(
            {
                "source": "arcaea_wikiwiki_jp",
                "generated_at": wiki.utc_now(),
                "source_run_id": args.source_run_id or None,
                "remaining_count": len(result["validation"]["failures"]),
                "remaining_unique_urls": len({
                    x.get("url") for x in result["validation"]["failures"] if x.get("url")
                }),
                "failures": result["validation"]["failures"],
            },
            ensure_ascii=False,
            indent=2,
        ) + "\n",
        encoding="utf-8",
    )

    print(json.dumps(summary, ensure_ascii=False, indent=2), flush=True)
    return 0


if __name__ == "__main__":
    sys.exit(main())
