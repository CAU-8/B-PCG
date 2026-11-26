// '진짜 지구와 비교' 탭. app.js 의 도구(h, $, api, q, S, fmt, cssVar)를 씁니다.
// 서버: GET /api/earth?run= (src/bpcg_studio/earth.py). 지표의 뜻과 규칙은 docs/studio.md '진짜 지구와 비교'.
"use strict";

const EARTH = { run: null, data: null, error: null, loading: false, open: new Set(), gen: 0 };

const SCALE_TEXT = {
  planet: {
    title: "행성 크기",
    text: "행성 지도(L0) 칸끼리, 지구 자료도 같은 크기 칸으로 평균해 비교합니다. 극지방 얼음판을 빼려고 대부분의 지표는 남위 60°~북위 60° 땅만 씁니다.",
  },
  hero: {
    title: "강 유역 크기",
    text: "히어로 유역(행성에서 고른 강 유역 하나를 25 m 칸으로 다시 푼 땅)을 약 100 m 칸으로 묶어, 진짜 지구의 90 m 고도 자료와 같은 방법으로 잽니다.",
  },
};
const RULE_TEXT = {
  overlap: "겹침: 두 분포를 같은 구간으로 나눠 넓이를 1로 맞춘 뒤 겹치는 넓이입니다. 80 이면 넓이의 80%가 겹칩니다.",
  ratio: "배수: 같으면 100, 두 배 어긋나면 50, 네 배 어긋나면 25 입니다.",
  curve: "배수: 같은 가로 값에서 두 곡선이 몇 배 어긋나는지 평균합니다. 같으면 100, 평균 두 배 어긋나면 50 입니다.",
  center: "가운데: 생성 값이 지구 값들 가운데 몇 번째쯤인지로 냅니다. 지구 값의 가운데값이면 100, 맨 끝이면 0 입니다.",
};
const STATUS_TEXT_EARTH = {
  no_reference: "지구 기준 값 없음",
  no_run_data: "이 실행에 없음",
  no_model: "생성기가 만들지 않음",
};
const SERIES_COLOR = {
  gen: "--earth-gen",
  earth: "--earth-ref",
  afrisar_lope: "--earth-extra1",
  afrisar_rabi: "--earth-extra2",
};

function seriesColor(key) {
  return cssVar(SERIES_COLOR[key] || "--muted");
}

// ---------------------------------------------------------------- 불러오기
async function loadEarth() {
  const run = S.run;
  const gen = ++EARTH.gen;
  EARTH.run = run;
  EARTH.data = null;
  EARTH.error = null;
  EARTH.loading = true;
  renderEarth();
  try {
    const d = await api(`/api/earth?${q({ run })}`);
    if (gen !== EARTH.gen) return;
    EARTH.data = d;
  } catch (e) {
    if (gen !== EARTH.gen) return;
    EARTH.error = e.message;
  }
  EARTH.loading = false;
  renderEarth();
}

function renderEarth() {
  const root = $("#tab-earth");
  if (!root || S.tab !== "earth") return;
  if (!S.run) {
    root.textContent = "";
    root.append(h("div", { class: "empty" }, "아직 볼 결과가 없습니다. 왼쪽에서 실행하거나 위에서 결과를 고르세요."));
    return;
  }
  if (EARTH.run !== S.run && !EARTH.loading) {
    loadEarth();
    return;
  }
  root.textContent = "";
  if (EARTH.loading || EARTH.run !== S.run) {
    root.append(h("div", { class: "empty" }, "지구 자료와 비교하는 중… (laptop 프로필 결과에서 약 1~3초)"));
    return;
  }
  if (EARTH.error) {
    root.append(h("div", { class: "banner error" }, `지구 비교를 하지 못했습니다: ${EARTH.error}`));
    return;
  }
  const d = EARTH.data;
  root.append(earthIntro(d));
  if (!d.reference) return;
  for (const scale of ["planet", "hero"]) {
    const rows = d.rows.filter((r) => r.scale === scale);
    if (rows.length) root.append(earthSection(scale, rows, d.levels[scale]));
    else if (d.levels[scale] === null)
      root.append(h("div", { class: "card" }, h("h3", null, SCALE_TEXT[scale].title), h("p", { class: "hint" }, scale === "planet" ? "이 실행에는 행성 묶음(planet/)이 없습니다. 평면 히어로만 만든 실행입니다." : "이 실행에는 히어로 묶음(hero/)이 없습니다.")));
  }
  root.append(earthSources(d));
}

// ---------------------------------------------------------------- 머리 카드
function pctText(v) {
  return v == null ? "—" : `${Math.round(v * 100)}`;
}

function earthIntro(d) {
  const card = h("div", { class: "card earth-intro" });
  card.append(
    h("div", { class: "card-head" }, h("h2", null, "진짜 지구와 얼마나 닮았나"), d.reference ? h("span", { class: "hint" }, `지구 기준 파일: ${d.reference.built} 에 만듦, 자료 ${d.reference.have.length}개`) : null),
    h(
      "p",
      { class: "explain" },
      "이 실행이 만든 행성과 강 유역을, 미리 받아 둔 진짜 지구 자료 일곱 가지(고도 ETOPO·GLO-90, 강수 CHELSA, 건조 지수, 하천 RiverATLAS, 침식률 OCTOPUS, 가봉 레이더 AfriSAR)와 ",
      h("b", null, "같은 칸 크기, 같은 계산"),
      "으로 잽니다. 지표마다 닮은 정도를 0~100 으로 적습니다(100 이면 같음).",
    ),
  );
  if (!d.reference) {
    card.append(h("div", { class: "banner warn" }, d.reference_error || "지구 기준 파일이 없습니다."));
    return card;
  }
  const s = d.summary;
  card.append(
    h(
      "div",
      { class: "earth-summary" },
      h("div", { class: "kv" }, h("span", { class: "k" }, "결과로 나온 값의 닮은 정도 (평균)"), h("span", { class: "big-pct" }, pctText(s.emergent_mean)), h("span", { class: "hint" }, `지표 ${s.n_emergent}개 평균`)),
      h("div", { class: "kv" }, h("span", { class: "k" }, "입력이 정한 값의 닮은 정도 (참고)"), h("span", { class: "big-pct muted" }, pctText(s.forced_mean)), h("span", { class: "hint" }, `지표 ${s.n_forced}개 평균`)),
    ),
    h(
      "p",
      { class: "explain" },
      h("b", null, "왜 둘로 나누나. "),
      "설정 값이나 정해 둔 식이 거의 정하는 지표(입력이 정한 값, forced)는 지구와 닮아도 생성기가 지구를 닮게 만든다는 근거가 되지 않습니다. 입력을 지구에 맞게 골랐다는 뜻일 뿐입니다. 그래서 위 큰 숫자는 입력에 없던 성질이 결과로 나온 지표(emergent)만으로 냅니다.",
    ),
  );
  const miss = Object.entries(d.reference.missing || {});
  if (miss.length)
    card.append(h("div", { class: "banner warn" }, h("div", null, h("b", null, "기준 파일에 빠진 자료 · "), "아래 자료는 기준 파일을 만들 때 읽지 못해, 그 자료를 쓰는 지표가 비어 있습니다."), miss.map(([k, why]) => h("div", { class: "line" }, `${k}: ${why}`))));
  return card;
}

// ---------------------------------------------------------------- 표
function kindBadge(r) {
  return h("span", { class: `badge kind-${r.kind}`, title: r.kind === "emergent" ? "입력에 없던 성질이 결과로 나온 값 (닮은 정도 평균에 들어감)" : r.kind === "forced" ? "입력(설정 값·정해 둔 식)이 거의 정하는 값 (평균에 넣지 않음)" : "생성기가 아직 만들지 않는 성질" }, r.kind_text);
}

function scoreCell(r) {
  if (r.status !== "ok") return h("span", { class: "hint" }, STATUS_TEXT_EARTH[r.status] || r.status);
  if (r.score == null) return h("span", { class: "hint" }, "셀 수 없음");
  const v = Math.max(0, Math.min(1, r.score));
  return h("div", { class: "score" }, h("div", { class: "score-bar" }, h("i", { style: `width:${(v * 100).toFixed(1)}%` })), h("span", { class: "num" }, pctText(v)));
}

function earthSection(scale, rows, level) {
  const t = SCALE_TEXT[scale];
  let sub = "";
  if (level && scale === "planet" && level.l0_km) sub = ` · L0 칸 약 ${level.l0_km < 100 ? level.l0_km.toFixed(1) : Math.round(level.l0_km)} km`;
  else if (level && level.spacing_m) sub = ` · ${level.spacing_m} m 칸 ${level.shape[0]} × ${level.shape[1]}`;
  const tbody = h("tbody");
  for (const r of rows) {
    const open = EARTH.open.has(r.id);
    const tr = h(
      "tr",
      { class: `earth-row${open ? " open" : ""}${r.status !== "ok" ? " skipped" : ""}`, tabindex: "0", "aria-expanded": open ? "true" : "false" },
      h("td", null, h("span", { class: "caret" }, open ? "▾" : "▸"), h("span", { class: "etitle" }, r.title), h("div", { class: "hint" }, r.what)),
      h("td", null, kindBadge(r)),
      h("td", null, r.gen_text || "—"),
      h("td", null, r.earth_text || "—"),
      h("td", null, scoreCell(r)),
    );
    const toggle = () => {
      if (EARTH.open.has(r.id)) EARTH.open.delete(r.id);
      else EARTH.open.add(r.id);
      renderEarth();
    };
    tr.addEventListener("click", toggle);
    tr.addEventListener("keydown", (e) => {
      if (e.key === "Enter" || e.key === " ") {
        e.preventDefault();
        toggle();
      }
    });
    tbody.append(tr);
    if (open) tbody.append(h("tr", { class: "earth-detail" }, h("td", { colspan: "5" }, earthDetail(r))));
  }
  return h(
    "div",
    { class: "card" },
    h("div", { class: "card-head" }, h("h3", null, t.title, h("span", { class: "hint" }, sub))),
    h("p", { class: "explain" }, t.text, " 줄을 누르면 그림과 읽는 법이 나옵니다."),
    h(
      "div",
      { class: "table-wrap" },
      h(
        "table",
        { class: "earth-table" },
        h("thead", null, h("tr", null, h("th", null, "지표"), h("th", null, "종류"), h("th", null, "생성"), h("th", null, "지구"), h("th", null, "닮은 정도"))),
        tbody,
      ),
    ),
  );
}

function earthDetail(r) {
  const wrap = h("div", { class: "earth-detail-body" });
  if (r.status !== "ok") {
    wrap.append(h("p", { class: "explain" }, r.reason || ""));
    if (r.how) wrap.append(h("p", { class: "explain" }, h("b", null, "읽는 법. "), r.how));
    return wrap;
  }
  if (r.chart) wrap.append(earthChart(r.chart));
  if (r.how) wrap.append(h("p", { class: "explain" }, h("b", null, "읽는 법. "), r.how));
  if (r.rule) wrap.append(h("p", { class: "explain" }, h("b", null, "닮은 정도. "), RULE_TEXT[r.rule]));
  if (r.note) wrap.append(h("p", { class: "explain" }, h("b", null, "주의. "), r.note));
  const src = EARTH.data && EARTH.data.sources ? EARTH.data.sources[r.source] : null;
  if (src) wrap.append(h("p", { class: "hint" }, `지구 자료: ${src.label}`));
  return wrap;
}

// ---------------------------------------------------------------- 자료 출처
function earthSources(d) {
  const det = h("details", { class: "card earth-sources" }, h("summary", null, h("b", null, "쓴 지구 자료와 출처 표기")));
  const tb = h("tbody");
  for (const [key, s] of Object.entries(d.sources || {})) {
    const have = d.reference.have.includes(key);
    tb.append(h("tr", null, h("td", null, s.label), h("td", null, have ? "씀" : "빠짐"), h("td", null, s.license), h("td", { class: "cite" }, s.citation)));
  }
  det.append(
    h("p", { class: "explain" }, "원자료(약 4.5 GB)는 git 에 올리지 않습니다. 'uv run bpcg earth-ref' 가 원자료를 한 번 읽어 분포·곡선·표만 남긴 기준 파일(configs/reference/earth.json, 수십 kB)을 만들고, 이 탭은 그 파일만 읽습니다. 받는 법은 data/README.md 에 있습니다."),
    h("div", { class: "table-wrap" }, h("table", null, h("thead", null, h("tr", null, h("th", null, "자료"), h("th", null, "기준 파일"), h("th", null, "라이선스"), h("th", null, "출처 표기"))), tb)),
  );
  return det;
}

// ---------------------------------------------------------------- 그림 (SVG)
const SVGNS = "http://www.w3.org/2000/svg";
function svg(tag, attrs, ...children) {
  const el = document.createElementNS(SVGNS, tag);
  for (const [k, v] of Object.entries(attrs || {})) if (v != null) el.setAttribute(k, String(v));
  for (const c of children.flat(Infinity)) if (c != null) el.append(c instanceof Node ? c : document.createTextNode(String(c)));
  return el;
}

function tickLabel(v, log10) {
  if (!log10) return fmt(v);
  const x = Math.pow(10, v);
  if (x >= 1000 || x < 0.01) return `10^${Math.round(v * 10) / 10}`;
  return String(+x.toPrecision(2));
}

function axes(g, W, H, m, xr, yr, opt) {
  const X = (v) => m.l + ((v - xr[0]) / (xr[1] - xr[0] || 1)) * (W - m.l - m.r);
  const Y = (v) => H - m.b - ((v - yr[0]) / (yr[1] - yr[0] || 1)) * (H - m.t - m.b);
  const muted = cssVar("--muted");
  const line = cssVar("--line");
  const xs = opt.xTicks || stepTicks(xr[0], xr[1], niceStep((xr[1] - xr[0]) / 6), 20);
  for (const v of xs) {
    g.append(svg("line", { x1: X(v), x2: X(v), y1: H - m.b, y2: H - m.b + 4, stroke: muted }));
    g.append(svg("text", { x: X(v), y: H - m.b + 16, "text-anchor": "middle", fill: muted, "font-size": 11 }, opt.xFmt ? opt.xFmt(v) : tickLabel(v, opt.xLog10)));
  }
  const ys = opt.yTicks || stepTicks(yr[0], yr[1], niceStep((yr[1] - yr[0]) / 4), 10);
  for (const v of ys) {
    g.append(svg("line", { x1: m.l, x2: W - m.r, y1: Y(v), y2: Y(v), stroke: line }));
    g.append(svg("text", { x: m.l - 6, y: Y(v) + 4, "text-anchor": "end", fill: muted, "font-size": 11 }, opt.yFmt ? opt.yFmt(v) : fmt(v)));
  }
  g.append(svg("line", { x1: m.l, x2: W - m.r, y1: H - m.b, y2: H - m.b, stroke: muted }));
  if (opt.xLabel) g.append(svg("text", { x: (m.l + W - m.r) / 2, y: H - 4, "text-anchor": "middle", fill: muted, "font-size": 11 }, opt.xLabel));
  if (opt.yLabel) g.append(svg("text", { x: 12, y: m.t + (H - m.t - m.b) / 2, "text-anchor": "middle", fill: muted, "font-size": 11, transform: `rotate(-90 12 ${m.t + (H - m.t - m.b) / 2})` }, opt.yLabel));
  return { X, Y };
}

function legend(series) {
  return h("div", { class: "chart-legend" }, series.map((s) => h("span", null, h("span", { class: "swatch", style: `background:${seriesColor(s.key)}` }), s.label)));
}

function earthChart(c) {
  const W = 640;
  const H = 240;
  const m = { l: 56, r: 12, t: 10, b: 38 };
  const root = svg("svg", { viewBox: `0 0 ${W} ${H}`, class: "earth-chart", role: "img" });
  const box = h("div", { class: "chart-wrap" }, root);
  if (c.type === "hist") {
    const e = c.edges;
    const ymax = Math.max(1e-9, ...c.series.flatMap((s) => s.p.filter((v) => v != null)));
    const { X, Y } = axes(root, W, H, m, [e[0], e[e.length - 1]], [0, ymax * 1.05], { xLog10: c.x_log10, xLabel: c.x_label, yFmt: (v) => `${Math.round(v * 1000) / 10}%`, yLabel: "넓이 몫" });
    for (const s of c.series) {
      let d = "";
      s.p.forEach((v, i) => {
        const y = Y(v || 0);
        d += `${i ? "L" : "M"}${X(e[i]).toFixed(1)},${y.toFixed(1)}L${X(e[i + 1]).toFixed(1)},${y.toFixed(1)}`;
      });
      const fill = s.key === "earth";
      if (fill) root.append(svg("path", { d: `${d}L${X(e[e.length - 1])},${Y(0)}L${X(e[0])},${Y(0)}Z`, fill: seriesColor(s.key), opacity: 0.18 }));
      root.append(svg("path", { d, fill: "none", stroke: seriesColor(s.key), "stroke-width": s.key.startsWith("afrisar") ? 1.4 : 2, "stroke-dasharray": s.key.startsWith("afrisar") ? "4 3" : null }));
    }
    box.append(legend(c.series));
  } else if (c.type === "curve") {
    const xs = c.x;
    const vals = c.series.flatMap((s) => s.y.filter((v) => v != null && (!c.y_log || v > 0)));
    if (!vals.length) {
      box.append(h("p", { class: "hint" }, "그릴 값이 없습니다."));
      return box;
    }
    const T = c.y_log ? (v) => Math.log10(v) : (v) => v;
    let y0 = Math.min(...vals.map(T));
    let y1 = Math.max(...vals.map(T));
    if (!c.y_log) y0 = Math.min(0, y0);
    if (y1 <= y0) y1 = y0 + 1;
    const pad = (y1 - y0) * 0.06;
    const yr = c.y_log ? [Math.floor(y0), Math.ceil(y1)] : [y0, y1 + pad];
    const yTicks = c.y_log ? Array.from({ length: yr[1] - yr[0] + 1 }, (_, i) => yr[0] + i) : null;
    const { X, Y } = axes(root, W, H, m, [xs[0], xs[xs.length - 1]], yr, { xLabel: c.x_label, yLabel: c.y_label, yTicks, yFmt: c.y_log ? (v) => tickLabel(v, true) : null });
    for (const s of c.series) {
      let d = "";
      let pen = false;
      s.y.forEach((v, i) => {
        if (v == null || (c.y_log && !(v > 0))) {
          pen = false;
          return;
        }
        d += `${pen ? "L" : "M"}${X(xs[i]).toFixed(1)},${Y(T(v)).toFixed(1)}`;
        pen = true;
        root.append(svg("circle", { cx: X(xs[i]), cy: Y(T(v)), r: 2.5, fill: seriesColor(s.key) }));
      });
      root.append(svg("path", { d, fill: "none", stroke: seriesColor(s.key), "stroke-width": 2 }));
    }
    box.append(legend(c.series));
  } else if (c.type === "bars" || c.type === "classes" || c.type === "orders") {
    let labels, series;
    if (c.type === "bars") {
      labels = [""];
      series = [
        { key: "gen", label: "생성", p: [c.values[0]] },
        { key: "earth", label: "지구", p: [c.values[1]] },
      ];
    } else if (c.type === "classes") {
      labels = c.labels;
      series = c.series;
    } else {
      const ks = [...new Set(c.series.flatMap((s) => Object.keys(s.counts)))].map(Number).sort((a, b) => a - b);
      labels = ks.map((k) => `${k}차`);
      series = c.series.map((s) => ({ key: s.key, label: s.label, p: ks.map((k) => s.counts[String(k)] || 0) }));
    }
    const logY = c.type === "orders";
    const all = series.flatMap((s) => s.p.filter((v) => v != null && v > 0));
    if (!all.length) {
      box.append(h("p", { class: "hint" }, "그릴 값이 없습니다."));
      return box;
    }
    const T = logY ? (v) => (v > 0 ? Math.log10(v) : 0) : (v) => v || 0;
    const ymax = Math.max(...all.map(T));
    const yr = logY ? [0, Math.ceil(ymax)] : [0, ymax * 1.15];
    const yTicks = logY ? Array.from({ length: yr[1] + 1 }, (_, i) => i) : null;
    const nG = labels.length;
    const { Y } = axes(root, W, H, m, [0, nG], yr, { xTicks: [], yTicks, yFmt: logY ? (v) => fmt(Math.pow(10, v)) : c.type === "classes" ? (v) => `${Math.round(v * 100)}%` : null, yLabel: logY ? "물줄기 수" : c.type === "classes" ? "넓이 몫" : null });
    const gw = (W - m.l - m.r) / nG;
    const bw = Math.min(46, (gw * 0.8) / series.length);
    labels.forEach((lab, i) => {
      const x0 = m.l + gw * i + (gw - bw * series.length) / 2;
      series.forEach((s, j) => {
        const v = s.p[i];
        if (v == null) return;
        const y = Y(T(v));
        root.append(svg("rect", { x: x0 + j * bw, y, width: bw - 3, height: Math.max(0, H - m.b - y), fill: seriesColor(s.key), opacity: 0.85 }, svg("title", null, `${s.label} ${lab}: ${fmt(v)}`)));
        if (c.type === "bars") root.append(svg("text", { x: x0 + j * bw + (bw - 3) / 2, y: y - 4, "text-anchor": "middle", "font-size": 11, fill: cssVar("--fg") }, fmt(v)));
      });
      if (lab) root.append(svg("text", { x: m.l + gw * (i + 0.5), y: H - m.b + 16, "text-anchor": "middle", "font-size": 11, fill: cssVar("--muted") }, lab));
    });
    box.append(legend(series));
  } else if (c.type === "strip") {
    const rowsN = c.series.length;
    const { X } = axes(root, W, H, m, [c.lo, c.hi], [0, rowsN], { xLabel: c.x_label, yTicks: [] });
    const rh = (H - m.t - m.b) / rowsN;
    c.series.forEach((s, i) => {
      const y = m.t + rh * (i + 0.5);
      root.append(svg("text", { x: m.l + 4, y: y - rh * 0.28, "font-size": 10.5, fill: cssVar("--muted") }, s.label));
      for (const v of s.values) root.append(svg("circle", { cx: X(v), cy: y + 4, r: s.key === "gen" ? 4 : 3, fill: seriesColor(s.key), opacity: 0.75 }));
      const sorted = [...s.values].sort((a, b) => a - b);
      if (sorted.length) {
        const med = sorted[Math.floor((sorted.length - 1) / 2)] / 2 + sorted[Math.ceil((sorted.length - 1) / 2)] / 2;
        root.append(svg("line", { x1: X(med), x2: X(med), y1: y - 6, y2: y + 14, stroke: seriesColor(s.key), "stroke-width": 2 }));
      }
    });
    box.append(h("p", { class: "hint" }, "점 하나가 유역 하나, 세로 선이 가운데값입니다."));
  } else if (c.type === "band") {
    const b = c.earth;
    const ys = [...b.q75.filter((v) => v != null), ...c.points.map((p) => p.y)];
    const { X, Y } = axes(root, W, H, m, [c.x[0] - 0.125, c.x[c.x.length - 1] + 0.125], [0, Math.max(5, ...ys) * 1.1], { xLabel: c.x_label, yLabel: c.y_label, xFmt: (v) => fmt(Math.pow(10, v)) });
    let up = "";
    let dn = "";
    let med = "";
    const idx = c.x.map((_, i) => i).filter((i) => b.q50[i] != null);
    idx.forEach((i, k) => {
      up += `${k ? "L" : "M"}${X(c.x[i]).toFixed(1)},${Y(b.q75[i]).toFixed(1)}`;
      med += `${k ? "L" : "M"}${X(c.x[i]).toFixed(1)},${Y(b.q50[i]).toFixed(1)}`;
    });
    [...idx].reverse().forEach((i) => (dn += `L${X(c.x[i]).toFixed(1)},${Y(b.q25[i]).toFixed(1)}`));
    if (idx.length) {
      root.append(svg("path", { d: `${up}${dn}Z`, fill: seriesColor("earth"), opacity: 0.2 }));
      root.append(svg("path", { d: med, fill: "none", stroke: seriesColor("earth"), "stroke-width": 2 }));
    }
    for (const p of c.points) root.append(svg("circle", { cx: X(p.x), cy: Y(p.y), r: 4, fill: seriesColor("gen"), opacity: 0.85 }));
    box.append(legend([{ key: "earth", label: "지구 유역 (가운데값과 가운데 절반)" }, { key: "gen", label: "생성 (히어로 안 유역)" }]));
  }
  return box;
}
