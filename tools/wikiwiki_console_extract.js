(() => {
  "use strict";

  // Paste this whole file into DevTools Console while viewing a song page on
  // https://wikiwiki.jp/arcaea/ . It prints tracker database schema-v2 JSON and
  // copies it to the clipboard when the console/browser allows it.

  const SOURCE = "arcaea_wikiwiki_jp";
  const SOURCE_FORMAT = "wikiwiki_console_extract";
  const FORMAT = "arcaea_tracker_database";
  const SCHEMA_VERSION = 2;
  const PARSER_VERSION = "0.2.0";
  const DIFF_NAMES = new Map([
    ["past", "PST"], ["pst", "PST"],
    ["present", "PRS"], ["prs", "PRS"],
    ["future", "FTR"], ["ftr", "FTR"],
    ["eternal", "ETR"], ["etr", "ETR"],
    ["beyond", "BYD"], ["byd", "BYD"],
    ["inscribed", "INS"], ["ins", "INS"],
  ]);

  const clean = (value) => String(value ?? "")
    .replace(/\u00a0/g, " ")
    .replace(/\s+/g, " ")
    .trim();

  const norm = (value) => clean(value)
    .normalize("NFKC")
    .toLocaleLowerCase("en-US");

  const diffCode = (value) => {
    const text = ` ${norm(value).replace(/[\[\]]/g, " ")} `;
    for (const [name, code] of DIFF_NAMES) {
      const re = new RegExp(`(^|[^a-z])${name.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")}([^a-z]|$)`, "i");
      if (re.test(text)) return code;
    }
    return null;
  };

  const classificationFromDifficulty = (difficulty) => {
    switch (difficulty) {
      case "PST":
        return { ratingClass: 0, ratingClassAlias: null, bydType: null, source: "inferred-semantic" };
      case "PRS":
        return { ratingClass: 1, ratingClassAlias: null, bydType: null, source: "inferred-semantic" };
      case "FTR":
        return { ratingClass: 2, ratingClassAlias: null, bydType: null, source: "inferred-semantic" };
      case "BYD":
        return { ratingClass: 3, ratingClassAlias: null, bydType: 0, source: "inferred-semantic" };
      case "ETR":
        return { ratingClass: 4, ratingClassAlias: null, bydType: null, source: "inferred-semantic" };
      case "INS":
        return { ratingClass: 3, ratingClassAlias: 1, bydType: 1, source: "inferred-semantic" };
      default:
        return null;
    }
  };

  const parseLevel = (value) => {
    const m = clean(value).match(/(?:^|\s)(\?|\d{1,2}\+?)(?:\s|$)/);
    return m ? m[1] : null;
  };

  const parseIntValue = (value) => {
    const m = clean(value).replace(/,/g, "").match(/(?:^|\D)(\d{1,7})(?:\D|$)/);
    return m ? Number(m[1]) : null;
  };

  const blankChart = (difficulty) => ({
    level: null,
    constant: null,
    notes: null,
    chart_designer: null,
    classification: classificationFromDifficulty(difficulty),
  });

  const rowCells = (tr) => Array.from(tr.children)
    .filter((el) => el.matches?.("td,th"));

  const rowTexts = (tr) => rowCells(tr).map((el) => clean(el.innerText));

  const findChartTable = () => {
    for (const table of document.querySelectorAll("table")) {
      for (const tr of table.querySelectorAll("tr")) {
        const texts = rowTexts(tr);
        if (!texts.length) continue;
        if (!norm(texts[0]).startsWith("difficulty")) continue;
        const diffs = texts.slice(1).map(diffCode).filter(Boolean);
        if (diffs.length) return { table, diffs };
      }
    }
    return { table: null, diffs: [] };
  };

  const findLabelValue = (texts, label) => {
    const wanted = norm(label);
    const index = texts.findIndex((v) => norm(v) === wanted);
    if (index < 0) return null;
    return texts.slice(index + 1).map(clean).find(Boolean) ?? null;
  };

  const absoluteImageFromCell = (cell) => {
    const img = cell?.querySelector?.("img");
    if (!img) return null;
    const src = img.dataset?.src || img.currentSrc || img.src || img.getAttribute("src");
    if (!src) return null;
    const alt = clean(img.alt);
    const absolute = new URL(src, location.href).href;
    if (/Now printing/i.test(alt) || /Now_printing/i.test(absolute)) return null;
    return absolute;
  };

  const parseDesignerGroups = (text) => {
    const out = {};
    const re = /\[([^\]]+)\]/g;
    const groups = [];
    let match;
    while ((match = re.exec(text)) !== null) {
      const codes = match[1]
        .split(/[/,]/)
        .map((part) => diffCode(part.trim()))
        .filter(Boolean);
      groups.push({ match, codes });
    }

    for (let i = 0; i < groups.length; i += 1) {
      const current = groups[i];
      if (!current.codes.length) continue;
      let end = text.length;
      for (let j = i + 1; j < groups.length; j += 1) {
        if (groups[j].codes.length) {
          end = groups[j].match.index;
          break;
        }
      }
      let designer = clean(text.slice(current.match.index + current.match[0].length, end)
        .replace(/^[\s/,;:\-]+|[\s/,;:\-]+$/g, ""));
      designer = clean(designer.split(/アートワーク画像|Artwork/i, 1)[0]);
      if (!designer) continue;
      for (const code of current.codes) out[code] = designer;
    }
    return out;
  };

  const parseConstants = () => {
    // WikiWiki can nest later difficulty bullets inside earlier <li> nodes.
    // Using rendered line order avoids values bleeding between difficulties.
    const lines = (document.body?.innerText || "")
      .split(/\r?\n/)
      .map(clean)
      .filter(Boolean);
    const out = {};

    for (let i = 0; i < lines.length; i += 1) {
      if (!lines[i].includes("譜面定数")) continue;
      const windowAfter = lines.slice(i, i + 3).join(" ");
      const valueMatch = windowAfter.match(/譜面定数\s*[:：]\s*(未判明|\d{1,2}(?:\.\d+)?)/);
      if (!valueMatch) continue;

      let code = diffCode(lines[i]);
      if (!code) {
        for (let j = i - 1; j >= Math.max(0, i - 6); j -= 1) {
          if (lines[j].length > 32) continue;
          const candidate = diffCode(lines[j]);
          if (candidate) {
            code = candidate;
            break;
          }
        }
      }
      if (!code) continue;
      out[code] = valueMatch[1] === "未判明" ? null : Number(valueMatch[1]);
    }
    return out;
  };

  const parseVersion = (text) => {
    const m = clean(text).match(/\b(\d+\.\d+(?:\.\d+)?)\b/);
    return m ? m[1] : null;
  };

  const sourceUpdatedAt = () => {
    const text = document.body?.innerText || "";
    const m = text.match(/Last-modified:\s*([^\n\r]+)/i);
    return m ? clean(m[1]) : null;
  };

  const { table, diffs } = findChartTable();
  const charts = Object.fromEntries(diffs.map((d) => [d, blankChart(d)]));

  let artist = null;
  let pack = null;
  let bpm = null;
  let length = null;
  let side = null;
  let artwork = null;
  let artworkCredit = null;
  let addedVersion = null;
  const designers = {};

  if (table) {
    for (const tr of table.querySelectorAll("tr")) {
      const cells = rowCells(tr);
      const texts = cells.map((c) => clean(c.innerText));
      if (!texts.length) continue;
      const label = norm(texts[0]).replace(/\s/g, "");

      const composer = findLabelValue(texts, "Composer");
      if (composer) artist = composer;

      texts.forEach((text, i) => {
        if (norm(text) !== "artwork") return;
        if (texts[i + 1]) artworkCredit = texts[i + 1];
        if (cells[i + 1]) artwork = absoluteImageFromCell(cells[i + 1]) || artwork;
      });

      if (label.startsWith("chartdesigner")) {
        const raw = [];
        for (const value of texts.slice(1)) {
          if (norm(value) === "artwork") break;
          raw.push(value);
        }
        Object.assign(designers, parseDesignerGroups(raw.join(" ")));
      } else if (label === "level") {
        diffs.forEach((d, i) => {
          charts[d].level = parseLevel(texts[i + 1]);
        });
      } else if (label === "notes") {
        // Exact match is deliberate. Some pages have Notes (Joy-Con), which
        // must not overwrite the normal mobile chart note counts.
        diffs.forEach((d, i) => {
          charts[d].notes = parseIntValue(texts[i + 1]);
        });
      } else if (label === "length") {
        const m = texts.slice(1).join(" ").match(/\b\d{1,2}:\d{2}\b/);
        if (m) length = m[0];
      } else if (label === "bpm") {
        const raw = texts.slice(1).find(Boolean) || "";
        if (/^\d+(?:\.\d+)?$/.test(raw)) {
          const n = Number(raw);
          bpm = Number.isInteger(n) ? n : n;
        } else if (raw) {
          bpm = raw;
        }
      } else if (label === "pack") {
        pack = texts.slice(1).find(Boolean) || pack;
      } else if (label === "side") {
        side = texts.slice(1).find(Boolean) || side;
      }
    }

    const tableText = clean(table.innerText);
    let m = tableText.match(/Mobile\s+ver\.\s*(\d+\.\d+(?:\.\d+)?)/i);
    if (m) {
      addedVersion = m[1];
    } else {
      m = tableText.match(/\bver\.\s*(\d+\.\d+(?:\.\d+)?)/i);
      if (m) addedVersion = m[1];
    }
  }

  for (const [d, value] of Object.entries(parseConstants())) {
    charts[d] ||= blankChart(d);
    charts[d].constant = value;
  }
  for (const [d, value] of Object.entries(designers)) {
    charts[d] ||= blankChart(d);
    charts[d].chart_designer = value;
  }

  if (!artwork && table) {
    for (const cell of table.querySelectorAll("td,th")) {
      const candidate = absoluteImageFromCell(cell);
      if (candidate) {
        artwork = candidate;
        break;
      }
    }
  }

  const h1 = document.querySelector("h1");
  const title = clean(h1?.innerText) || decodeURIComponent(location.pathname.split("/").pop() || "");

  // Some layouts expose these values in rows outside the exact chart table.
  // Use conservative fallbacks only when the main table did not provide them.
  const pageLines = (document.body?.innerText || "").split(/\r?\n/).map(clean).filter(Boolean);
  const valueAfterLabel = (...labels) => {
    for (let i = 0; i < pageLines.length - 1; i += 1) {
      if (labels.some((label) => norm(pageLines[i]) === norm(label))) return pageLines[i + 1];
    }
    return null;
  };
  if (!pack) pack = valueAfterLabel("Pack");
  if (!side) side = valueAfterLabel("Side");
  if (!length) {
    const raw = valueAfterLabel("Length");
    const m = raw?.match(/\b\d{1,2}:\d{2}\b/);
    if (m) length = m[0];
  }
  if (!addedVersion) {
    const raw = valueAfterLabel("Added", "Version", "Added version");
    addedVersion = parseVersion(raw || "");
  }

  const errors = [];
  const warnings = [];
  if (!Object.keys(charts).length) errors.push("No difficulty columns found");
  if (Object.keys(charts).length && !Object.values(charts).some((c) => c.notes != null)) {
    warnings.push("No note counts found");
  }
  if (Object.keys(charts).length && !Object.values(charts).some((c) => c.constant != null)) {
    warnings.push("No known chart constants found");
  }

  for (const [d, chart] of Object.entries(charts)) {
    if (chart.notes != null && chart.notes <= 0) errors.push(`${d}: non-positive note count ${chart.notes}`);
    if (chart.constant != null && (chart.constant < 0 || chart.constant > 15)) {
      errors.push(`${d}: implausible constant ${chart.constant}`);
    }
  }

  const missingConstants = Object.entries(charts)
    .filter(([, c]) => c.level && c.constant == null)
    .map(([d]) => d);
  const missingNotes = Object.entries(charts)
    .filter(([, c]) => c.level && c.notes == null)
    .map(([d]) => d);

  const fetchedAt = new Date().toISOString();
  const entry = {
    source: SOURCE,
    song: {
      title,
      artist,
      pack,
      bpm,
      length,
      side,
      artwork,
      added_version: addedVersion,
    },
    charts,
    _meta: {
      source_url: location.href.split("#")[0],
      fetched_at: fetchedAt,
      source_updated_at: sourceUpdatedAt(),
      parser_version: PARSER_VERSION,
      artwork_credit: artworkCredit,
      missing_constants: missingConstants,
      missing_notes: missingNotes,
      validation: {
        ok: errors.length === 0,
        errors,
        warnings,
      },
    },
  };

  const result = {
    format: FORMAT,
    schema_version: SCHEMA_VERSION,
    source_format: SOURCE_FORMAT,
    updated_at: fetchedAt,
    entries: [entry],
  };

  const json = JSON.stringify(result, null, 2);
  console.log(result);
  console.log(json);

  // Chrome/Edge DevTools expose copy(). Firefox may not, so also try the
  // standard Clipboard API. Failure is harmless because the JSON is printed.
  try {
    if (typeof copy === "function") {
      copy(json);
      console.info("[Arcaea DB] Schema-v2 JSON copied to clipboard.");
    } else if (navigator.clipboard?.writeText) {
      navigator.clipboard.writeText(json)
        .then(() => console.info("[Arcaea DB] Schema-v2 JSON copied to clipboard."))
        .catch(() => console.info("[Arcaea DB] Could not copy automatically; use the printed JSON."));
    }
  } catch (_) {
    console.info("[Arcaea DB] Could not copy automatically; use the printed JSON.");
  }

  return result;
})();
