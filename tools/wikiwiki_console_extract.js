(() => {
  "use strict";

  // Paste this whole file into DevTools Console while viewing a song page on
  // https://wikiwiki.jp/arcaea/ . It prints tracker database schema-v2 JSON and
  // copies it to the clipboard when the console/browser allows it.

  const SOURCE = "arcaea_wikiwiki_jp";
  const SOURCE_FORMAT = "wikiwiki_console_extract";
  const FORMAT = "arcaea_tracker_database";
  const SCHEMA_VERSION = 2;
  const PARSER_VERSION = "0.3.0";
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
    variant_title: null,
    variant_title_aliases: [],
    classification: classificationFromDifficulty(difficulty),
  });

  const cloneStandaloneChart = (chart) => ({
    ...chart,
    variant_title: null,
    variant_title_aliases: [],
    classification: chart.classification ? { ...chart.classification } : null,
  });

  const rowCells = (tr) => Array.from(tr.children)
    .filter((el) => el.matches?.("td,th"));

  const rowTexts = (tr) => rowCells(tr).map((el) => clean(el.innerText));

  const h1 = document.querySelector("h1");
  const title = clean(h1?.innerText) || decodeURIComponent(location.pathname.split("/").pop() || "");
  const pageLines = (document.body?.innerText || "").split(/\r?\n/).map(clean).filter(Boolean);

  const findChartTable = () => {
    for (const table of document.querySelectorAll("table")) {
      for (const tr of table.querySelectorAll("tr")) {
        const cells = rowCells(tr);
        const texts = cells.map((cell) => clean(cell.innerText));
        if (!texts.length) continue;
        if (!norm(texts[0]).startsWith("difficulty")) continue;

        // Some WikiWiki tables represent multiple charts with one difficulty
        // header cell using colspan. Last, for example, has one Beyond header
        // spanning both Last | Moment and Last | Eternity. Expand that colspan
        // into one semantic column per rendered value so level/notes stay paired.
        const diffs = [];
        for (const cell of cells.slice(1)) {
          const code = diffCode(cell.innerText);
          if (!code) continue;
          const span = Math.max(1, Number(cell.colSpan) || 1);
          for (let i = 0; i < span; i += 1) diffs.push(code);
        }
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
    // Rendered line order avoids values bleeding between difficulties. Keep the
    // nearest song-section title too, because duplicate difficulty columns need
    // constants assigned to the correct variant rather than last-write-wins.
    const out = [];
    let contextTitle = null;

    for (let i = 0; i < pageLines.length; i += 1) {
      const contextMatch = pageLines[i].match(/楽曲[「『]([^」』]+)[」』]/);
      if (contextMatch) contextTitle = clean(contextMatch[1]);

      if (!pageLines[i].includes("譜面定数")) continue;
      const windowAfter = pageLines.slice(i, i + 3).join(" ");
      const valueMatch = windowAfter.match(/譜面定数\s*[:：]\s*(未判明|\d{1,2}(?:\.\d+)?)/);
      if (!valueMatch) continue;

      let code = diffCode(pageLines[i]);
      if (!code) {
        for (let j = i - 1; j >= Math.max(0, i - 6); j -= 1) {
          if (pageLines[j].length > 32) continue;
          const candidate = diffCode(pageLines[j]);
          if (candidate) {
            code = candidate;
            break;
          }
        }
      }
      if (!code) continue;
      out.push({
        difficulty: code,
        value: valueMatch[1] === "未判明" ? null : Number(valueMatch[1]),
        contextTitle,
      });
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

  const variantTitlesFromHeading = (count) => {
    if (count < 2) return [];
    const titleKey = norm(title);
    for (const heading of document.querySelectorAll("h2,h3")) {
      const text = clean(heading.innerText);
      if (!text || !norm(text).includes(titleKey)) continue;
      const match = text.match(/[（(]([^()（）]+)[)）]/);
      if (!match) continue;
      const parts = match[1].split(/\s*\/\s*/).map(clean).filter(Boolean);
      if (parts.length >= count) return parts.slice(0, count);
    }
    return [];
  };

  const { table, diffs } = findChartTable();
  const columnCharts = diffs.map((difficulty, index) => ({
    difficulty,
    index,
    variantTitle: null,
    chart: blankChart(difficulty),
  }));
  const splitWarnings = [];

  // Identify repeated semantic difficulties before assigning section-specific
  // constants. For Last this maps its two BYD columns to Moment and Eternity.
  const columnGroups = new Map();
  for (const column of columnCharts) {
    if (!columnGroups.has(column.difficulty)) columnGroups.set(column.difficulty, []);
    columnGroups.get(column.difficulty).push(column);
  }
  for (const [difficulty, columns] of columnGroups) {
    if (columns.length < 2) continue;
    const variants = variantTitlesFromHeading(columns.length);
    columns.forEach((column, i) => {
      const variantTitle = variants[i] || null;
      column.variantTitle = variantTitle;
      if (variantTitle) {
        column.chart.variant_title = variantTitle;
        column.chart.variant_title_aliases = [variantTitle];
      }
    });
    if (variants.length < columns.length) {
      splitWarnings.push(`${difficulty}: ${columns.length} columns found but only ${variants.length} variant title(s) resolved`);
    } else {
      splitWarnings.push(`${difficulty}: ${columns.length} columns split by variant title`);
    }
  }

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
        columnCharts.forEach((column, i) => {
          column.chart.level = parseLevel(texts[i + 1]);
        });
      } else if (label === "notes") {
        // Exact match is deliberate. Some pages have Notes (Joy-Con), which
        // must not overwrite the normal mobile chart note counts.
        columnCharts.forEach((column, i) => {
          column.chart.notes = parseIntValue(texts[i + 1]);
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

  for (const record of parseConstants()) {
    const candidates = columnCharts.filter((column) => column.difficulty === record.difficulty);
    if (!candidates.length) continue;

    let target = null;
    if (candidates.length === 1) {
      target = candidates[0];
    } else if (record.contextTitle) {
      target = candidates.find((column) => column.variantTitle && norm(column.variantTitle) === norm(record.contextTitle)) || null;
    }
    if (!target) target = candidates.find((column) => column.chart.constant == null) || candidates[candidates.length - 1];
    target.chart.constant = record.value;
  }

  for (const [difficulty, value] of Object.entries(designers)) {
    for (const column of columnCharts.filter((candidate) => candidate.difficulty === difficulty)) {
      column.chart.chart_designer = value;
    }
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

  // Some layouts expose these values in rows outside the exact chart table.
  // Use conservative fallbacks only when the main table did not provide them.
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

  // Build one base entry plus one additional entry for every extra duplicate
  // difficulty column whose variant title can be resolved. This keeps schema v2
  // unchanged while avoiding a Frankenstein chart assembled from two columns.
  const baseCharts = {};
  const secondaryEntries = [];
  for (const [difficulty, columns] of columnGroups) {
    if (!columns.length) continue;
    baseCharts[difficulty] = columns[0].chart;

    for (let i = 1; i < columns.length; i += 1) {
      const column = columns[i];
      if (!column.variantTitle) continue;
      secondaryEntries.push({
        title: column.variantTitle,
        charts: { [difficulty]: cloneStandaloneChart(column.chart) },
      });
    }
  }

  const validateCharts = (charts, inheritedWarnings = []) => {
    const errors = [];
    const warnings = [...inheritedWarnings];
    if (!Object.keys(charts).length) errors.push("No difficulty columns found");
    if (Object.keys(charts).length && !Object.values(charts).some((c) => c.notes != null)) {
      warnings.push("No note counts found");
    }
    if (Object.keys(charts).length && !Object.values(charts).some((c) => c.constant != null)) {
      warnings.push("No known chart constants found");
    }

    for (const [difficulty, chart] of Object.entries(charts)) {
      if (chart.notes != null && chart.notes <= 0) errors.push(`${difficulty}: non-positive note count ${chart.notes}`);
      if (chart.constant != null && (chart.constant < 0 || chart.constant > 15)) {
        errors.push(`${difficulty}: implausible constant ${chart.constant}`);
      }
    }

    return {
      errors,
      warnings,
      missingConstants: Object.entries(charts)
        .filter(([, chart]) => chart.level && chart.constant == null)
        .map(([difficulty]) => difficulty),
      missingNotes: Object.entries(charts)
        .filter(([, chart]) => chart.level && chart.notes == null)
        .map(([difficulty]) => difficulty),
    };
  };

  const fetchedAt = new Date().toISOString();
  const pageUrl = location.href.split("#")[0];
  const buildEntry = (entryTitle, charts, entryArtwork, extraWarnings = []) => {
    const validation = validateCharts(charts, [...splitWarnings, ...extraWarnings]);
    return {
      source: SOURCE,
      song: {
        title: entryTitle,
        artist,
        pack,
        bpm,
        length,
        side,
        artwork: entryArtwork,
        added_version: addedVersion,
      },
      charts,
      _meta: {
        source_url: pageUrl,
        fetched_at: fetchedAt,
        source_updated_at: sourceUpdatedAt(),
        parser_version: PARSER_VERSION,
        artwork_credit: artworkCredit,
        missing_constants: validation.missingConstants,
        missing_notes: validation.missingNotes,
        validation: {
          ok: validation.errors.length === 0,
          errors: validation.errors,
          warnings: validation.warnings,
        },
      },
    };
  };

  const entries = [buildEntry(title, baseCharts, artwork)];
  for (const secondary of secondaryEntries) {
    entries.push(buildEntry(
      secondary.title,
      secondary.charts,
      null,
      [`Split from multi-chart WikiWiki page ${title}`],
    ));
  }

  const result = {
    format: FORMAT,
    schema_version: SCHEMA_VERSION,
    source_format: SOURCE_FORMAT,
    updated_at: fetchedAt,
    entries,
  };

  const json = JSON.stringify(result, null, 2);
  console.log(result);
  console.log(json);

  // Chrome/Edge DevTools expose copy(). Firefox may not, so also try the
  // standard Clipboard API. Failure is harmless because the JSON is printed.
  try {
    if (typeof copy === "function") {
      copy(json);
      console.info(`[Arcaea DB] Schema-v2 JSON copied to clipboard (${entries.length} entr${entries.length === 1 ? "y" : "ies"}).`);
    } else if (navigator.clipboard?.writeText) {
      navigator.clipboard.writeText(json)
        .then(() => console.info(`[Arcaea DB] Schema-v2 JSON copied to clipboard (${entries.length} entr${entries.length === 1 ? "y" : "ies"}).`))
        .catch(() => console.info("[Arcaea DB] Could not copy automatically; use the printed JSON."));
    }
  } catch (_) {
    console.info("[Arcaea DB] Could not copy automatically; use the printed JSON.");
  }

  return result;
})();
