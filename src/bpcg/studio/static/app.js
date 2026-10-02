// B-PCG 스튜디오 화면. 바깥 라이브러리 없이 DOM 과 canvas 만 씁니다.
// 서버 API 는 src/bpcg/studio/server.py 머리 표를 보세요.
"use strict";

// ---------------------------------------------------------------- 작은 도구
const $ = (sel, root = document) => root.querySelector(sel);
const $$ = (sel, root = document) => [...root.querySelectorAll(sel)];

function h(tag, attrs, ...children) {
  const el = document.createElement(tag);
  for (const [k, v] of Object.entries(attrs || {})) {
    if (v == null || v === false) continue;
    if (k === "class") el.className = v;
    else if (k === "text") el.textContent = v;
    else if (k === "style") el.style.cssText = v;
    else if (k.startsWith("on")) el.addEventListener(k.slice(2), v);
    else if (k === "dataset") Object.assign(el.dataset, v);
    else el.setAttribute(k, v === true ? "" : v);
  }
  for (const c of children.flat(Infinity)) {
    if (c == null || c === false) continue;
    el.append(c instanceof Node ? c : document.createTextNode(String(c)));
  }
  return el;
}

const store = {
  get(key, fallback) {
    try {
      const v = localStorage.getItem("bpcg-studio:" + key);
      return v == null ? fallback : JSON.parse(v);
    } catch (e) {
      return fallback;
    }
  },
  set(key, value) {
    try {
      localStorage.setItem("bpcg-studio:" + key, JSON.stringify(value));
    } catch (e) {
      /* 사생활 보호 창 등에서는 저장하지 않습니다 */
    }
  },
};

async function api(path, opts) {
  const r = await fetch(path, opts);
  const ct = r.headers.get("content-type") || "";
  if (!r.ok) {
    let msg = `${r.status}`;
    let details = {};
    if (ct.includes("json")) {
      try {
        const j = await r.json();
        msg = j.error || j.message || msg;
        if (typeof j.detail === "string" && j.detail) msg += ` (${j.detail})`;
        // 서버는 키별 오류를 details 또는 errors 로 보냅니다
        details = j.details || j.errors || {};
      } catch (err) {
        /* 본문이 JSON 이 아니면 상태 번호만 보여 줍니다 */
      }
    }
    const e = new Error(msg);
    e.status = r.status;
    e.details = details;
    throw e;
  }
  if (ct.includes("json")) return r.json();
  return r.arrayBuffer();
}
const post = (path, body) =>
  api(path, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(body) });
const q = (o) => new URLSearchParams(o).toString();

function fmt(v) {
  if (v == null || !Number.isFinite(v)) return "없음";
  const a = Math.abs(v);
  if (a !== 0 && (a >= 1e6 || a < 1e-3)) return v.toExponential(2).replace("e+", "e");
  if (a >= 100) return Math.round(v).toLocaleString("ko-KR");
  if (a >= 10) return v.toFixed(1);
  return String(+v.toPrecision(3));
}
function fmtInt(v) {
  return v == null ? "—" : Math.round(v).toLocaleString("ko-KR");
}
function fmtDur(s) {
  if (s == null || !Number.isFinite(s)) return "—";
  if (s < 60) return `${s.toFixed(s < 10 ? 1 : 0)} s`;
  const m = Math.floor(s / 60);
  const r = Math.round(s % 60);
  return m >= 60 ? `${Math.floor(m / 60)}시간 ${m % 60}분` : `${m}분 ${r}초`;
}
function fmtCells(n) {
  if (n >= 1e8) return `${(n / 1e8).toFixed(1)}억`;
  if (n >= 1e4) return `${Math.round(n / 1e4).toLocaleString("ko-KR")}만`;
  return n.toLocaleString("ko-KR");
}
function deepEqual(a, b) {
  return JSON.stringify(a) === JSON.stringify(b);
}
// 파이썬 round() 와 같은 반올림입니다 (딱 .5 이면 짝수 쪽). 생성기의 n = round(size_m / spacing_m) 와 맞춥니다.
function roundHalfEven(x) {
  const f = Math.floor(x);
  const d = x - f;
  if (d > 0.5) return f + 1;
  if (d < 0.5) return f;
  return f % 2 === 0 ? f : f + 1;
}
// lo..hi 안의 step 배수 눈금. 개수 상한이 있어 범위가 이상해도(아주 큰 값, 아주 작은 간격) 끝없이 돌지 않습니다.
function stepTicks(lo, hi, step, limit = 60) {
  const out = [];
  if (!(step > 0) || !Number.isFinite(lo) || !Number.isFinite(hi) || hi < lo) return out;
  const start = Math.ceil(lo / step);
  const top = hi + 1e-9 * Math.abs(hi);
  for (let i = 0; i < limit; i++) {
    const v = (start + i) * step;
    if (v > top) break;
    out.push(v);
  }
  return out;
}
const isNarrow = () => window.matchMedia && window.matchMedia("(max-width: 900px)").matches;
const STATUS_TEXT = {
  queued: "기다림",
  running: "도는 중",
  done: "끝",
  failed: "실패",
  cancelled: "취소됨",
  interrupted: "멈춤 (서버 꺼짐)",
  unknown: "알 수 없음",
};

// ---------------------------------------------------------------- 상태
const S = {
  meta: null,
  schema: null,
  params: new Map(), // key → 매개변수 설명
  overrides: store.get("overrides", {}), // key → 바꾼 값
  errors: {}, // key → 오류 글 (화면에서 읽다가 난 오류, 실행 요청이 돌려준 오류)
  serverErrors: {}, // key → POST /api/validate 가 돌려준 오류 글
  validateGen: 0,
  starting: false, // POST /api/jobs 를 보내는 중 (실행 단추를 막음)
  runs: [],
  skippedRuns: [], // 이름 때문에 열 수 없는 out/ 폴더 (서버가 알려 주면)
  runsSig: "",
  jobDone: null, // 도는 실행에서 끝난 단계 수 (바뀌면 실행 목록을 다시 읽음)
  run: store.get("run", null),
  summary: null,
  job: null, // 지금 보는 실행 상태 (진행 탭)
  jobTimer: null,
  logLines: [],
  logTotal: 0,
  tab: store.get("tab", "progress"),
  catFilter: null, // 분류 거르기 (매개변수 표를 읽은 뒤 정함)
  onlyChanged: false,
  maps: {},
  godotTimer: null,
};
const AREA_KEYS = [
  "profile.hero.size_m",
  "profile.hero.spacing_m",
  "profile.corridor.length_m",
  "profile.corridor.width_m",
  "profile.corridor.voxel_m",
];
// 히어로 한 변 칸 수 상한. 서버(/api/meta 의 max_hero_side = isqrt(MAX_HERO_CELLS))가 주면 그 값을 씁니다.
const MAX_HERO_SIDE_FALLBACK = 5000;
const SLOW_CELLS = 4_000_000;
function maxHeroSide() {
  const v = S.meta ? Number(S.meta.max_hero_side) : NaN;
  return Number.isInteger(v) && v > 0 ? v : MAX_HERO_SIDE_FALLBACK;
}

// ---------------------------------------------------------------- 매개변수 패널
function currentValue(key) {
  const p = S.params.get(key);
  return key in S.overrides ? S.overrides[key] : p ? p.default : undefined;
}

function parseInput(p, raw) {
  if (p.type === "bool") return { value: !!raw };
  if (p.type === "int" || p.type === "float") {
    const s = String(raw).trim().replace(/_/g, "");
    if (s === "") return { error: "값을 넣어 주세요" };
    const v = Number(s);
    if (!Number.isFinite(v)) return { error: "숫자가 아닙니다" };
    if (p.type === "int" && !Number.isInteger(v)) return { error: "정수여야 합니다" };
    return { value: v };
  }
  if (p.type === "list") {
    try {
      const v = JSON.parse(raw);
      if (!Array.isArray(v)) return { error: "[0.02, 0.08] 같은 리스트여야 합니다" };
      if (Array.isArray(p.default) && p.default.length && v.length !== p.default.length)
        return { error: `원소 ${p.default.length}개여야 합니다` };
      return { value: v };
    } catch (e) {
      return { error: "리스트를 읽지 못했습니다 (예: [0.02, 0.08])" };
    }
  }
  return { value: String(raw) };
}

function setOverride(key, value) {
  const p = S.params.get(key);
  if (!p) return;
  if (deepEqual(value, p.default)) delete S.overrides[key];
  else S.overrides[key] = value;
  delete S.errors[key];
  store.set("overrides", S.overrides);
  refreshParamRow(key);
  updateChangedCount();
  updateAreaDerived();
  scheduleValidate();
}

function displayValue(p, v) {
  if (p.type === "list") return JSON.stringify(v);
  if (p.type === "float" && Number.isInteger(v) && Math.abs(v) < 1e15) return String(v);
  return String(v);
}

function paramRow(p) {
  const row = h("div", { class: "param", dataset: { key: p.key } });
  const cat = S.schema.categories.find((c) => c.id === p.category);
  const head = h(
    "div",
    { class: "param-head" },
    h("code", { class: "pkey", title: p.key }, p.name),
    h("span", { class: `badge cat-${p.category}`, title: cat ? cat.description : "" }, cat ? cat.label : p.category),
    h("span", { class: "badge changed", hidden: true }, "바뀜"),
    !p.editable ? h("span", { class: "badge ro", title: p.read_only_reason }, "여기서 안 바꿈") : null,
  );
  let input;
  if (p.type === "bool") {
    input = h("input", { type: "checkbox", "aria-label": p.key });
    input.addEventListener("change", () => setOverride(p.key, input.checked));
  } else {
    input = h("input", {
      type: p.type === "int" || p.type === "float" ? "number" : "text",
      step: p.type === "int" ? "1" : "any",
      "aria-label": p.key,
    });
    input.addEventListener("change", () => {
      const r = parseInput(p, input.value);
      if (r.error) {
        S.errors[p.key] = r.error;
        refreshParamRow(p.key, true);
        return;
      }
      setOverride(p.key, r.value);
    });
  }
  input.disabled = !p.editable;
  const reset = h("button", { class: "reset", type: "button", title: "기본값으로 되돌리기", "aria-label": "기본값으로" }, "↺");
  reset.addEventListener("click", () => setOverride(p.key, p.default));
  const extra = [];
  if (p.note) extra.push(p.note);
  if (p.learned) extra.push("configs/learned 의 맞춘 값");
  row.append(
    head,
    h("div", { class: "param-input" }, input, h("span", { class: "unit" }, p.unit || "—"), reset),
    p.help ? h("p", { class: "help" }, p.help) : null,
    h(
      "p",
      { class: "pmeta" },
      `기본 ${displayValue(p, p.default)}${p.unit ? " " + p.unit : ""}`,
      extra.length ? ` · ${extra.join(" · ")}` : "",
      p.history ? h("span", { class: "hist" }, ` · 손 보정 기록: ${p.history}`) : null,
    ),
    h("p", { class: "perr", hidden: true }),
  );
  return row;
}

function refreshParamRow(key, keepInput) {
  const p = S.params.get(key);
  for (const row of $$(`.param[data-key="${CSS.escape(key)}"]`)) {
    const v = currentValue(key);
    const input = $("input", row);
    if (!keepInput) {
      if (p.type === "bool") input.checked = !!v;
      else input.value = displayValue(p, v);
    }
    const changed = key in S.overrides;
    row.classList.toggle("changed", changed);
    $(".badge.changed", row).hidden = !changed;
    $(".reset", row).disabled = !changed;
    const err = S.errors[key] || S.serverErrors[key];
    $(".perr", row).hidden = !err;
    $(".perr", row).textContent = err || "";
    input.classList.toggle("invalid", !!err);
  }
}

function updateChangedCount() {
  const n = Object.keys(S.overrides).length;
  $("#changed-count").textContent = n;
  for (const d of $$("details.group")) {
    const keys = $$(".param", d).map((r) => r.dataset.key);
    const k = keys.filter((x) => x in S.overrides).length;
    $(".g-count", d).textContent = k ? `${k}개 바뀜` : "";
  }
}

function buildParamPanel() {
  const groups = $("#param-groups");
  groups.textContent = "";
  const area = $("#area-params");
  area.textContent = "";
  for (const key of AREA_KEYS) {
    const p = S.params.get(key);
    if (p) area.append(paramRow(p));
  }
  let lastBanner = null;
  for (const sec of S.schema.sections) {
    const keys = sec.params.filter((k) => !AREA_KEYS.includes(k));
    if (!keys.length) continue;
    if (sec.banner && sec.banner !== lastBanner) {
      groups.append(h("div", { class: "chain-label" }, sec.banner));
      lastBanner = sec.banner;
    }
    const d = h(
      "details",
      { class: "group", dataset: { section: sec.id } },
      h(
        "summary",
        null,
        h("span", { class: "g-title" }, sec.label),
        h("code", { class: "g-chain" }, sec.id),
        h("span", { class: "g-count" }),
      ),
    );
    const body = h("div", { class: "group-body" });
    if (sec.comment) body.append(h("p", { class: "hint" }, sec.comment));
    for (const k of keys) body.append(paramRow(S.params.get(k)));
    d.append(body);
    groups.append(d);
  }
  for (const key of S.params.keys()) refreshParamRow(key);
  updateChangedCount();
  applyParamFilter();
  updateAreaDerived();
}

function buildCatFilter() {
  const box = $("#cat-filter");
  box.textContent = "";
  const all = S.schema.categories.map((c) => c.id);
  const hidden = new Set(store.get("catHidden", []));
  S.catFilter = new Set(all.filter((id) => !hidden.has(id)));
  for (const c of S.schema.categories) {
    const b = h(
      "button",
      { class: "chip-btn", type: "button", "aria-pressed": S.catFilter.has(c.id) ? "true" : "false", title: c.description },
      h("span", { class: "dot", style: `background: var(--cat-${c.id})` }),
      c.label,
    );
    b.addEventListener("click", () => {
      if (S.catFilter.has(c.id)) S.catFilter.delete(c.id);
      else S.catFilter.add(c.id);
      b.setAttribute("aria-pressed", S.catFilter.has(c.id) ? "true" : "false");
      store.set("catHidden", all.filter((id) => !S.catFilter.has(id)));
      applyParamFilter();
    });
    box.append(b);
  }
  const only = h("button", { class: "chip-btn", type: "button", "aria-pressed": "false" }, "바뀐 것만");
  only.addEventListener("click", () => {
    S.onlyChanged = !S.onlyChanged;
    only.setAttribute("aria-pressed", S.onlyChanged ? "true" : "false");
    applyParamFilter();
  });
  box.append(only);
}

function applyParamFilter() {
  const text = $("#param-search").value.trim().toLowerCase();
  for (const d of $$("#param-groups details.group")) {
    let shown = 0;
    for (const row of $$(".param", d)) {
      const p = S.params.get(row.dataset.key);
      const hay = `${p.key} ${p.help} ${p.note} ${p.history} ${d.querySelector(".g-title").textContent}`.toLowerCase();
      const ok =
        (!text || hay.includes(text)) &&
        S.catFilter.has(p.category) &&
        (!S.onlyChanged || p.key in S.overrides);
      row.hidden = !ok;
      if (ok) shown++;
    }
    d.hidden = shown === 0;
    if (text || S.onlyChanged) d.open = shown > 0;
  }
}

// 영역·해상도: 칸 수·실제 크기·비용 짐작·경고
function updateAreaDerived() {
  const box = $("#area-derived");
  if (!S.schema) return;
  const size = Number(currentValue("profile.hero.size_m"));
  const dx = Number(currentValue("profile.hero.spacing_m"));
  const len = Number(currentValue("profile.corridor.length_m"));
  const wid = Number(currentValue("profile.corridor.width_m"));
  const vox = Number(currentValue("profile.corridor.voxel_m"));
  box.textContent = "";
  const setBlocked = (blocked) => {
    $("#run-btn").dataset.blockedByArea = blocked ? "1" : "";
    updateRunButton();
  };
  if (!(Number.isFinite(size) && Number.isFinite(dx) && size > 0 && dx > 0)) {
    box.append(h("div", { class: "note error" }, "히어로 한 변과 칸 간격은 0 보다 커야 합니다. 고치기 전에는 실행할 수 없습니다."));
    setBlocked(true);
    return;
  }
  // 생성기(hero.domain.hero_grid_size)와 같은 규칙: n = round(size/spacing) (짝수 쪽 반올림),
  // size ≥ 3·spacing, n ≤ 상한. 어긋나면 행성 단계까지 돈 뒤에야 멈추므로 여기서 막습니다.
  const maxSide = maxHeroSide();
  const n = Math.max(roundHalfEven(size / dx), 1);
  const tooSmall = size < 3 * dx;
  const tooBig = n > maxSide;
  const cells = n * n;
  const side = n * dx;
  const lap = (S.meta && S.meta.laptop_hero) || { cells: 1280 * 1280 };
  const ratio = cells / lap.cells;
  const corSamples = vox > 0 ? Math.floor(len / vox + 1) * Math.floor(wid / vox + 1) : 0;
  const dl = h(
    "dl",
    null,
    h("dt", null, "히어로 한 변"),
    h("dd", null, `${fmtInt(n)}칸 × ${fmt(dx)} m = ${(side / 1000).toLocaleString("ko-KR", { maximumFractionDigits: 3 })} km`),
    h("dt", null, "히어로 칸 수"),
    h("dd", null, `${fmtInt(n)}² = ${fmtInt(cells)} (${fmtCells(cells)}칸)`),
    h("dt", null, "계산량"),
    h("dd", null, `노트북 기본(${fmtCells(lap.cells)}칸)의 약 ${ratio < 10 ? +ratio.toPrecision(2) : Math.round(ratio)}배`),
    h("dt", null, "회랑"),
    h("dd", null, vox > 0 ? `${fmt(len)} × ${fmt(wid)} m, 간격 ${fmt(vox)} m → 높이맵 ${fmtCells(corSamples)} 표본` : "간격이 0 보다 커야 합니다"),
  );
  const bar = h("div", { class: "cost-bar", title: "노트북 기본의 4배를 꽉 찬 막대로 봅니다" }, h("i", { style: `width:${Math.min(100, (ratio / 4) * 100)}%` }));
  const notes = h("div", { class: "notes" });
  if (tooSmall)
    notes.append(
      h(
        "div",
        { class: "note error" },
        `히어로 한 변(size_m ${fmt(size)} m)은 칸 간격(spacing_m ${fmt(dx)} m)의 3배(${fmt(3 * dx)} m) 이상이어야 합니다. 생성기가 받지 않는 설정이라 실행을 막았습니다.`,
      ),
    );
  else if (Math.abs(side - size) > 1e-6)
    notes.append(h("div", { class: "note info" }, `한 변 ${fmt(size)} m 는 간격의 배수가 아니라 ${fmt(side)} m (${n}칸)로 맞춥니다.`));
  if (tooBig)
    notes.append(
      h(
        "div",
        { class: "note error" },
        `한 변 ${fmtInt(n)}칸은 생성기 상한(${fmtInt(maxSide)}²칸)을 넘습니다. size_m 을 ${fmtInt(maxSide * dx)} m 이하로 줄이거나 spacing_m 을 ${fmt(size / maxSide)} m 이상으로 늘리세요. 고치기 전에는 실행할 수 없습니다.`,
      ),
    );
  else if (cells > SLOW_CELLS)
    notes.append(h("div", { class: "note warn" }, `칸이 ${fmtCells(cells)}개라 노트북에서는 오래 걸리고 메모리를 많이 씁니다 (약 ${Math.round(ratio)}배).`));
  if (len > side || wid > side)
    notes.append(h("div", { class: "note warn" }, `회랑(${fmt(len)} × ${fmt(wid)} m)이 히어로 한 변(${fmt(side)} m)보다 커서 히어로 크기에 맞춰 줄어듭니다.`));
  if (side > 40000)
    notes.append(h("div", { class: "note info" }, "히어로가 크면 바다에서 먼 육지 자리가 필요합니다. 행성에 그런 자리가 없으면 평면 히어로(가짜 경계조건)로 바뀌고 진행·개요 탭에 경고가 뜹니다."));
  box.append(dl, bar, notes);
  setBlocked(tooSmall || tooBig);
}

// ---------------------------------------------------------------- 서버 검사 (POST /api/validate)
// 바꾼 값을 서버 규칙(형식·히어로 크기 등)으로 미리 검사해 키별 오류를 보여 줍니다.
let validateTimer = null;
function scheduleValidate() {
  clearTimeout(validateTimer);
  validateTimer = setTimeout(runValidate, 400);
}

function requestBody() {
  return {
    planet: $("#planet").value,
    profile: $("#profile").value,
    seed: Number($("#seed").value || 0),
    flat: $("#flat").checked,
    figures: $("#figures").checked,
    overrides: S.overrides,
  };
}

async function runValidate() {
  if (!S.schema) return;
  const gen = ++S.validateGen;
  let res;
  try {
    res = await post("/api/validate", requestBody());
  } catch (e) {
    // 4xx 는 검사 결과로 보고, 서버가 잠깐 없거나(네트워크) 5xx 면 조용히 넘깁니다 (실행할 때 다시 검사).
    if (!(e.status >= 400 && e.status < 500) || e.status === 404 || e.status === 405) return;
    res = { ok: false, message: e.message, errors: e.details || {} };
  }
  if (gen !== S.validateGen) return;
  const ok = !res || res.ok !== false;
  const errs = ok ? {} : res.errors || res.details || {};
  showServerErrors(errs, ok ? "" : res.message || res.error || "바꾼 값에 문제가 있습니다");
}

function showServerErrors(errs, message) {
  const keys = new Set([...Object.keys(S.serverErrors), ...Object.keys(errs)]);
  S.serverErrors = { ...errs };
  for (const k of keys) if (S.params.has(k)) refreshParamRow(k, true);
  const vm = $("#validate-msg");
  if (!vm) return;
  if (!message) {
    vm.hidden = true;
    vm.textContent = "";
    return;
  }
  const list = Object.entries(errs).map(([k, v]) => `${k}: ${v}`);
  vm.textContent = `서버 검사: ${message}${list.length ? " — " + list.join(" / ") : ""}`;
  vm.hidden = false;
}

async function loadSchema() {
  const planet = $("#planet").value;
  const profile = $("#profile").value;
  S.schema = await api(`/api/params?${q({ planet, profile })}`);
  S.params = new Map(S.schema.params.map((p) => [p.key, p]));
  for (const k of Object.keys(S.overrides)) if (!S.params.has(k)) delete S.overrides[k];
  buildCatFilter();
  buildParamPanel();
  buildReference();
  const prof = (S.meta.profiles || []).find((p) => p.name === profile);
  $("#profile-hint").textContent = prof ? `${prof.hint}. ${prof.description}` : "";
  scheduleValidate();
}

// ---------------------------------------------------------------- 실행
function updateRunButton() {
  const btn = $("#run-btn");
  const running = S.job && ["queued", "running"].includes(S.job.status) && S.job.isActive;
  btn.disabled = !!running || !!btn.dataset.blockedByArea || S.starting;
  btn.textContent = S.starting ? "시작하는 중…" : "실행";
  btn.title = btn.dataset.blockedByArea ? "영역·해상도 상자의 빨간 오류를 고쳐야 실행할 수 있습니다" : "";
  $("#cancel-btn").hidden = !running;
}

async function startRun() {
  if (S.starting) return; // 두 번 누름 막기 (요청이 가는 동안 단추도 꺼 둠)
  S.starting = true;
  updateRunButton();
  const msg = $("#run-msg");
  msg.className = "msg";
  msg.textContent = "실행을 시작합니다…";
  try {
    const st = await post("/api/jobs", requestBody());
    msg.className = "msg ok";
    msg.textContent = `시작했습니다: ${st.id}`;
    S.errors = {};
    showServerErrors({}, "");
    for (const k of S.params.keys()) refreshParamRow(k, true);
    await loadRuns();
    selectRun(st.run, { keepTab: true });
    selectTab("progress");
    watchJob(st.id, true);
    // 좁은 화면에서는 결과(진행 탭)가 설정 패널 위에 있으니 그쪽으로 옮겨 줍니다.
    if (isNarrow()) $("#results").scrollIntoView({ behavior: REDUCED_MOTION ? "auto" : "smooth", block: "start" });
  } catch (e) {
    msg.className = "msg error";
    msg.textContent = e.message;
    if (e.details) {
      S.errors = { ...e.details };
      for (const k of Object.keys(e.details)) {
        if (S.params.has(k)) refreshParamRow(k, true);
        const row = $(`.param[data-key="${CSS.escape(k)}"]`);
        if (row) {
          const d = row.closest("details");
          if (d) d.open = true;
        }
      }
      const list = Object.entries(e.details).map(([k, v]) => `${k}: ${v}`);
      if (list.length) msg.textContent += " — " + list.join(" / ");
    }
  } finally {
    S.starting = false;
    updateRunButton();
  }
}

async function cancelRun() {
  if (!S.job) return;
  try {
    await post("/api/jobs/cancel", { id: S.job.id });
    $("#run-msg").textContent = "취소를 요청했습니다";
  } catch (e) {
    $("#run-msg").textContent = e.message;
  }
}

function watchJob(id, active) {
  clearTimeout(S.jobTimer);
  S.pollGen = (S.pollGen || 0) + 1; // 이전 감시를 멈춤 (두 감시가 겹쳐 기록이 두 번 붙지 않게)
  S.logLines = [];
  S.logTotal = 0;
  S.jobDone = null;
  S.job = { id, status: "running", isActive: active };
  pollJob(id, S.pollGen);
}

// 끝난(또는 건너뛴) 단계 수. 이 수가 바뀌면 새 묶음(행성·히어로·회랑)이 생겼을 수 있습니다.
function doneStages(st) {
  return ((st.progress && st.progress.stages) || []).filter((s) => s.status === "done" || s.status === "skipped").length;
}

async function pollJob(id, gen) {
  try {
    const st = await api(`/api/job?${q({ id, since: S.logTotal })}`);
    if (!S.job || S.job.id !== id || gen !== S.pollGen) return;
    const wasActive = ["queued", "running"].includes(S.job.status) && S.job.isActive;
    S.job = { ...st, isActive: S.job.isActive };
    if (st.log) {
      if (st.log.tail_only) S.logLines = st.log.lines.slice();
      else S.logLines.push(...st.log.lines);
      if (S.logLines.length > 3000) S.logLines.splice(0, S.logLines.length - 3000);
      S.logTotal = st.log.tail_only ? 0 : st.log.start + st.log.lines.length;
    }
    renderProgress();
    updateRunButton();
    const active = ["queued", "running"].includes(st.status);
    if (active && S.job.isActive) {
      // 단계가 끝날 때마다 실행 목록을 다시 읽어, 도는 중에도 지도 탭이 새로 생긴 묶음을 알게 합니다.
      const done = doneStages(st);
      if (S.jobDone != null && done !== S.jobDone) loadRuns().catch(() => {});
      S.jobDone = done;
      S.jobTimer = setTimeout(() => pollJob(id, gen), 1000);
    } else if (wasActive && !active) {
      S.job.isActive = false;
      updateRunButton();
      for (const m of Object.values(S.maps)) m.stale = true;
      const msg = $("#run-msg");
      msg.className = st.status === "done" ? "msg ok" : "msg error";
      msg.textContent = `${st.id}: ${STATUS_TEXT[st.status] || st.status}${st.error ? " — " + st.error : ""}`;
      await loadRuns({ noSummary: true });
      if (S.run === st.run) await loadSummary(true);
    }
  } catch (e) {
    if (S.job && S.job.id === id && S.job.isActive && gen === S.pollGen)
      S.jobTimer = setTimeout(() => pollJob(id, gen), 2500);
  }
}

// ---------------------------------------------------------------- 실행 목록
// opts.noSummary: 고른 실행이 바뀌었어도 개요를 다시 읽지 않음 (부른 쪽이 따로 읽을 때)
async function loadRuns(opts) {
  const r = await api("/api/runs");
  const before = runInfo(S.run);
  S.runs = r.runs || [];
  S.skippedRuns = r.skipped || [];
  const sel = $("#run-select");
  const studio = S.runs.filter((x) => x.source === "studio");
  const ext = S.runs.filter((x) => x.source !== "studio");
  const label = (x) => {
    const n = Object.keys(x.overrides || {}).length;
    const st = STATUS_TEXT[x.status] || x.status;
    const bits = [x.name, x.profile ? `프로필 ${x.profile}` : null, x.seed != null ? `시드 ${x.seed}` : null];
    if (x.flat) bits.push("평면");
    if (n) bits.push(`바꾼 값 ${n}`);
    return `${bits.filter(Boolean).join(" · ")} — ${st}`;
  };
  const skipName = (x) => (typeof x === "string" ? x : x.name || x.key || "?");
  const skipWhy = (x) => (typeof x === "string" ? "" : x.reason || x.message || "");
  // 목록이 그대로면 고르기 상자를 다시 만들지 않습니다 (도는 중 자주 읽어도 열린 상자가 닫히지 않게).
  const sig = JSON.stringify([S.runs.map((x) => [x.key, label(x)]), S.skippedRuns]);
  if (sig !== S.runsSig) {
    S.runsSig = sig;
    sel.textContent = "";
    if (!S.runs.length) sel.append(h("option", { value: "" }, "아직 결과가 없습니다 — 실행 설정에서 실행하세요"));
    if (studio.length) sel.append(h("optgroup", { label: "스튜디오 실행 (out/studio)" }, studio.map((x) => h("option", { value: x.key }, label(x)))));
    if (ext.length) sel.append(h("optgroup", { label: "다른 실행 (out/, 읽기 전용)" }, ext.map((x) => h("option", { value: x.key }, label(x)))));
    if (S.skippedRuns.length)
      sel.append(
        h(
          "optgroup",
          { label: "열 수 없는 폴더 (out/ 아래, 이유는 옆 글)" },
          S.skippedRuns.map((x) => h("option", { value: "", disabled: true, title: skipWhy(x) }, `${skipName(x)}${skipWhy(x) ? " — " + skipWhy(x) : ""}`)),
        ),
      );
  }
  if (S.run && S.runs.some((x) => x.key === S.run)) {
    sel.value = S.run;
    updateRunChip();
    updateGodotButtons();
    // 고른 실행에 새 묶음이 생겼거나 상태가 바뀌었으면 지도를 다시 불러오고 개요도 새로 읽습니다.
    const now = runInfo(S.run);
    if (before && before.key === now.key && (!deepEqual(before.has, now.has) || before.status !== now.status)) {
      for (const m of Object.values(S.maps)) m.stale = true;
      if (S.tab === "planet" || S.tab === "hero") renderTab(S.tab);
      if (!(opts && opts.noSummary)) loadSummary(true);
    }
  } else if (S.runs.length) {
    S.run = null;
    selectRun(S.runs[0].key);
  }
}

function runInfo(key) {
  return S.runs.find((x) => x.key === key) || null;
}

function updateRunChip() {
  const info = runInfo(S.run);
  const chip = $("#run-chip");
  chip.hidden = !info;
  if (!info) return;
  chip.className = `status-pill st-${info.status}`;
  chip.textContent = (info.read_only ? "읽기 전용 · " : "") + (STATUS_TEXT[info.status] || info.status);
}

function selectRun(key, opts) {
  if (!key) return;
  const changed = S.run !== key;
  S.run = key;
  store.set("run", key);
  $("#run-select").value = key;
  const info = runInfo(key);
  updateRunChip();
  updateGodotButtons();
  if (changed) {
    for (const m of Object.values(S.maps)) m.stale = true;
    S.summary = null; // 다른 실행의 개요가 잠깐이라도 새 실행 것처럼 보이지 않게
    loadSummary(true);
    // 진행 탭: 스튜디오 실행이면 그 기록을 봅니다
    if (info && info.source === "studio") {
      const id = key.replace(/^studio\//, "");
      const isActive = S.meta && S.meta.active_job === id;
      if (!S.job || S.job.id !== id) watchJob(id, isActive || ["queued", "running"].includes(info.status));
    } else {
      clearTimeout(S.jobTimer);
      S.job = null;
      renderProgress();
    }
  }
  if (!(opts && opts.keepTab)) renderTab(S.tab);
}

async function loadSummary(force) {
  if (!S.run) return;
  const key = S.run;
  try {
    const s = await api(`/api/run?${q({ run: key })}`);
    if (S.run !== key) return;
    S.summary = s;
  } catch (e) {
    if (S.run !== key) return;
    S.summary = { error: e.message };
  }
  if (force) {
    renderOverview();
    renderFigures();
    if (S.tab === "reference") buildReference();
    if (S.tab === "planet" || S.tab === "hero") renderTab(S.tab);
  }
}

// ---------------------------------------------------------------- 탭
function selectTab(tab) {
  S.tab = tab;
  store.set("tab", tab);
  for (const b of $$(".tab")) b.setAttribute("aria-selected", b.dataset.tab === tab ? "true" : "false");
  for (const p of $$(".tabpanel")) p.hidden = p.id !== `tab-${tab}`;
  renderTab(tab);
}

function renderTab(tab) {
  if (tab === "progress") renderProgress();
  else if (tab === "overview") renderOverview();
  else if (tab === "figures") renderFigures();
  else if (tab === "reference") buildReference();
  else if (tab === "planet" || tab === "hero") {
    if (!S.maps[tab]) S.maps[tab] = new MapView(tab, $(`#tab-${tab}`));
    S.maps[tab].show();
  }
}

function warningsBanner(list) {
  if (!list || !list.length) return null;
  return h(
    "div",
    { class: "banner warn", role: "alert" },
    list.map((w) => [h("div", null, h("b", null, "주의 · "), w.message), w.line ? h("div", { class: "line" }, w.line) : null]),
  );
}

// ---------------------------------------------------------------- 첫 화면
// 움직임 줄이기를 고른 사용자에게는 움직이는 로고(gif) 대신 첫 장면을 멈춘 그림으로 보여 줍니다.
const REDUCED_MOTION = window.matchMedia && window.matchMedia("(prefers-reduced-motion: reduce)").matches;
function logoImage(img) {
  img.addEventListener("error", () => (img.hidden = true));
  const still = () => {
    if (!REDUCED_MOTION || !img.naturalWidth || img.hidden) return;
    const c = document.createElement("canvas");
    c.width = img.naturalWidth;
    c.height = img.naturalHeight;
    c.getContext("2d").drawImage(img, 0, 0);
    c.className = img.className;
    if (img.id) c.id = img.id;
    c.style.width = `${img.width}px`;
    c.style.height = `${img.height}px`;
    c.setAttribute("role", "img");
    c.setAttribute("aria-label", img.alt || "B-PCG 로고");
    img.replaceWith(c);
  };
  if (img.complete) still();
  else img.addEventListener("load", still, { once: true });
  return img;
}

function welcome(info) {
  const logo = logoImage(h("img", { src: "/static/logo.gif", alt: "B-PCG 로고", width: "128", height: "128" }));
  const external = info && info.source !== "studio";
  return h(
    "div",
    { class: "card welcome" },
    logo,
    h("h2", null, external ? "진행 기록이 없는 실행입니다" : "B-PCG 스튜디오"),
    h(
      "p",
      null,
      external
        ? "이 결과는 스튜디오 밖(bpcg all 명령)에서 만든 실행이라 단계별 진행 기록이 없습니다. 개요·행성 지도·히어로 지도·그림 탭에서 결과를 보세요."
        : "판·비·강이 만든 행성을 매개변수부터 Godot 화면까지 한곳에서 돌려 봅니다. 처음이면 tiny 프로필로 몇 초 만에 전체를 한 번 돌려 보세요.",
    ),
    external
      ? null
      : h(
          "ol",
          null,
          h("li", null, "실행 설정(넓은 화면은 왼쪽, 좁은 화면은 아래)에서 프로필·시드를 고르고, 바꿀 매개변수를 고칩니다 (바뀐 값에는 '바뀜' 표시)."),
          h("li", null, "'실행'을 누르면 이 탭에 단계별 진행률과 기록이 나옵니다."),
          h("li", null, "끝나면 개요·지도·그림 탭에서 결과를 보고, 칸을 눌러 모든 값을 확인합니다."),
          h("li", null, "위의 'Godot로 보기'로 구운 회랑을 걸어 봅니다."),
        ),
  );
}

// ---------------------------------------------------------------- 진행 탭
const STAGE_ICON = { done: "✓", running: "●", pending: "○", skipped: "–", failed: "✕", cancelled: "✕" };
const STAGE_TEXT = { done: "끝", running: "도는 중", pending: "기다림", skipped: "건너뜀", failed: "실패", cancelled: "취소됨" };

function renderProgress() {
  const root = $("#tab-progress");
  if (S.tab !== "progress") return;
  root.textContent = "";
  const info = runInfo(S.run);
  if (!S.job) {
    root.append(welcome(info));
    return;
  }
  const st = S.job;
  const pr = st.progress || { percent: 0, stages: [] };
  const status = st.status || "running";
  const head = h(
    "div",
    { class: "card-head" },
    h("h2", null, `실행 ${st.id}`),
    h("span", { class: `status-pill st-${status}` }, STATUS_TEXT[status] || status),
  );
  const active = ["queued", "running"].includes(status);
  const ended = ["failed", "cancelled", "interrupted"].includes(status);
  const barCls = status === "done" ? "done" : ended ? "failed" : "running";
  // 지금 단계·남은 시간은 도는 실행에만 뜻이 있습니다 (서버가 꺼져 멈춘 실행의 옛 기록은 쓰지 않음).
  const cur = active ? (pr.current ? pr.current.label : "—") : status === "done" ? "모든 단계 끝" : ended ? `없음 (${STATUS_TEXT[status]})` : "—";
  const eta = active ? (pr.eta_s != null ? `${fmtDur(pr.eta_s)} (${pr.eta_basis || "짐작"})` : "계산 중") : "—";
  const spec = st.spec || {};
  const changed = Object.entries(spec.overrides || {});
  const top = h(
    "div",
    { class: "card" },
    head,
    warningsBanner(pr.warnings),
    st.error ? h("div", { class: "banner error" }, st.error) : null,
    h(
      "div",
      { class: "progress-top" },
      h(
        "div",
        { class: "progress-numbers" },
        h("span", { class: "big-pct" }, `${(pr.percent || 0).toFixed(1)}%`),
        h("div", { class: "kv" }, h("span", { class: "k" }, "지금 단계"), h("span", { class: "v" }, cur)),
        h("div", { class: "kv" }, h("span", { class: "k" }, "지난 시간"), h("span", { class: "v" }, fmtDur(pr.elapsed_s))),
        h("div", { class: "kv" }, h("span", { class: "k" }, "남은 시간"), h("span", { class: "v" }, eta)),
      ),
      h(
        "div",
        { class: `bar ${barCls}`, role: "progressbar", "aria-valuemin": "0", "aria-valuemax": "100", "aria-valuenow": String(pr.percent || 0), "aria-label": "전체 진행률" },
        h("div", { class: "fill", style: `width:${pr.percent || 0}%` }),
      ),
      h(
        "p",
        { class: "explain" },
        h("b", null, "전체 진행률 [%]. "),
        `단계마다 무게(${pr.weights_from || "기본 무게"})를 주고, 끝난 단계의 무게 합에 지금 단계의 안쪽 진행(솔버는 반복 수 / 반복 상한)을 더한 값입니다. 솔버는 상한보다 일찍 수렴하는 일이 많아 그 단계 끝에서 한 번에 뛸 수 있습니다.`,
      ),
    ),
    h(
      "p",
      { class: "hint" },
      `프로필 ${spec.profile} · 시드 ${spec.seed}${spec.flat ? " · 평면 히어로" : ""} · 바꾼 값 ${changed.length}개`,
      changed.length ? ` (${changed.map(([k, v]) => `${k} = ${JSON.stringify(v)}`).join(", ")})` : "",
    ),
  );
  const list = h("div", { class: "stages" });
  for (const s of pr.stages || []) {
    // 실행이 끝났는데 '도는 중'으로 남은 단계(서버가 꺼진 실행 등)는 실패·취소로 보여 줍니다.
    let sst = s.status;
    let note = s.note;
    if (!active && sst === "running") {
      sst = status === "cancelled" ? "cancelled" : status === "done" ? "done" : "failed";
      if (!note && status === "interrupted") note = "스튜디오 서버가 꺼져서 멈췄습니다";
      else if (!note && ended) note = STATUS_TEXT[status];
    }
    const time = s.seconds != null ? fmtDur(s.seconds) : sst === "skipped" ? "건너뜀" : "";
    list.append(
      h(
        "div",
        { class: `stage ${sst}` },
        h("span", { class: "icon", role: "img", "aria-label": STAGE_TEXT[sst] || sst }, STAGE_ICON[sst] || "?"),
        h(
          "div",
          null,
          h("div", { class: "name" }, s.label),
          h("div", { class: "detail" }, note ? `${s.detail} · ${note}` : s.detail),
          sst === "running" && s.sub > 0 ? h("div", { class: "sub" }, h("i", { style: `width:${Math.round(s.sub * 100)}%` })) : null,
          s.result ? h("div", { class: "result" }, s.result) : null,
        ),
        h("span", { class: `phase ph-${s.phase}` }, s.phase),
        h("span", { class: "time" }, time),
      ),
    );
  }
  const stagesCard = h(
    "div",
    { class: "card" },
    h("div", { class: "card-head" }, h("h3", null, "단계 목록"), h("span", { class: "hint" }, "걸린 시간 [s]")),
    h(
      "div",
      { class: "legend-row" },
      Object.entries({ done: "끝", running: "도는 중", pending: "기다림", skipped: "건너뜀", failed: "실패·취소" }).map(([k, v]) =>
        h("span", null, h("b", null, STAGE_ICON[k]), v),
      ),
      h("span", null, "· 색: ", h("span", { class: "ph-행성" }, "행성 "), h("span", { class: "ph-히어로" }, "히어로 "), h("span", { class: "ph-굽기" }, "굽기 "), h("span", { class: "ph-그림" }, "그림")),
    ),
    list,
    h("p", { class: "explain" }, "각 줄은 파이프라인이 찍는 기록 줄(예: '[2단계] 솔버 끝')로 끝을 압니다. 줄 아래 회색 글은 그 단계가 남긴 결과 줄입니다."),
  );
  const pre = h("pre", { class: "log", "aria-label": "실행 기록" });
  pre.textContent = S.logLines.join("\n");
  const logCard = h(
    "div",
    { class: "card" },
    h("div", { class: "card-head" }, h("h3", null, "실행 기록 (마지막 줄들)"), h("span", { class: "hint" }, `전체 기록: ${st.run ? st.run + "/job.log" : ""}`)),
    pre,
    (st.commands || []).length ? h("details", null, h("summary", { class: "hint" }, "실행한 명령 보기"), (st.commands || []).map((c) => h("div", { class: "cmd" }, c))) : null,
  );
  root.append(top, stagesCard, logCard);
  pre.scrollTop = pre.scrollHeight;
}

// ---------------------------------------------------------------- 개요 탭
const PHASE_COLOR = { planet: "var(--planet)", hero: "var(--hero)", corridor: "var(--bake)" };
const PHASE_TEXT = { planet: "행성", hero: "히어로", corridor: "굽기" };
// 0..1 비율로 나오는 점수표 항목 ('기준 100%' 와 견주기 쉽게 퍼센트도 붙임)
const FRACTION_SCORES = new Set(["ocean_fraction", "gw_surface_fraction", "river_reach_fraction", "rock_consistency", "cave_in_soluble", "flat_fraction"]);

function renderOverview() {
  const root = $("#tab-overview");
  if (S.tab !== "overview") return;
  root.textContent = "";
  const s = S.summary;
  if (!S.run) {
    root.append(h("div", { class: "empty" }, "아직 볼 결과가 없습니다."));
    return;
  }
  if (!s) {
    root.append(h("div", { class: "empty" }, "불러오는 중…"));
    return;
  }
  if (s.error) {
    root.append(h("div", { class: "banner error" }, s.error));
    return;
  }
  root.append(warningsBanner(s.warnings) || "");
  root.append(
    h(
      "div",
      { class: "card" },
      h("div", { class: "card-head" }, h("h2", null, s.name), h("span", { class: "hint mono" }, s.path)),
      h("dl", { class: "facts" }, s.facts.map((f) => h("div", null, h("dt", null, f.label), h("dd", null, f.value)))),
      h("p", { class: "hint" }, `설정 해시 ${s.config_digest || "—"} · git ${s.git_commit ? s.git_commit.slice(0, 8) : "—"}`),
    ),
  );

  // 바뀐 값. 서버는 실행 뒤에 생기거나 없어진 키를 diff_missing 에 status 와 함께 따로 줍니다.
  // status 가 없으면(옛 서버) 한쪽 값이 null 인 키를 '이 실행에 없던 키'·'지금 기본값에 없는 키'로
  // 봅니다 (TOML 설정에는 null 값이 없음).
  const diffStatus = (d) => {
    if (d.status) return d.status;
    if (d.value === null && d.default !== null) return "added_since_run";
    if (d.default === null && d.value !== null) return "removed";
    return "changed";
  };
  const diffs = [...(s.diff || []), ...(s.diff_missing || [])];
  const changedDiffs = diffs.filter((d) => diffStatus(d) === "changed");
  const otherDiffs = diffs.filter((d) => diffStatus(d) !== "changed");
  const diffCard = h(
    "div",
    { class: "card" },
    h("h3", null, `기본값에서 바꾼 설정 (${changedDiffs.length}개)`),
    h("p", { class: "explain" }, "이 실행의 설정(묶음 manifest 의 config)을 지금 configs/ 의 기본값과 비교했습니다. 기본값 파일을 고친 뒤에는 바꾸지 않은 실행도 여기에 차이가 보일 수 있습니다."),
  );
  if (changedDiffs.length) {
    diffCard.append(
      h(
        "div",
        { class: "table-wrap" },
        h(
          "table",
          null,
          h("thead", null, h("tr", null, h("th", null, "키"), h("th", null, "분류"), h("th", { class: "num" }, "기본값"), h("th", { class: "num" }, "이 실행"), h("th", null, "단위"))),
          h(
            "tbody",
            null,
            changedDiffs.map((d) =>
              h(
                "tr",
                null,
                h("td", null, h("code", null, d.key)),
                h("td", null, h("span", { class: `badge cat-${d.category}` }, catLabel(d.category))),
                h("td", { class: "num" }, JSON.stringify(d.default)),
                h("td", { class: "num" }, JSON.stringify(d.value)),
                h("td", null, d.unit || ""),
              ),
            ),
          ),
        ),
      ),
    );
  } else diffCard.append(h("p", { class: "hint" }, "바꾼 값이 없습니다 (기본 설정으로 돌린 실행)."));
  if (otherDiffs.length) {
    const why = (d) => (d.note ? `— ${d.note}` : diffStatus(d) === "added_since_run" ? "— (이 실행 뒤에 생긴 키)" : "— (지금 기본값에 없는 키)");
    diffCard.append(
      h(
        "details",
        { class: "other-diff" },
        h("summary", { class: "hint" }, `실행 뒤에 생기거나 없어진 키 ${otherDiffs.length}개 (바꾼 값으로 세지 않음)`),
        h(
          "div",
          { class: "table-wrap" },
          h(
            "table",
            null,
            h("thead", null, h("tr", null, h("th", null, "키"), h("th", { class: "num" }, "지금 기본값"), h("th", { class: "num" }, "이 실행"))),
            h(
              "tbody",
              null,
              otherDiffs.map((d) => {
                const added = diffStatus(d) === "added_since_run";
                return h(
                  "tr",
                  null,
                  h("td", null, h("code", null, d.key)),
                  h("td", { class: added ? "num" : "num hint" }, added ? JSON.stringify(d.default) : why(d)),
                  h("td", { class: added ? "num hint" : "num" }, added ? why(d) : JSON.stringify(d.value)),
                );
              }),
            ),
          ),
        ),
      ),
    );
  }

  // 솔버 수렴
  const solverCard = h("div", { class: "card" }, h("h3", null, "솔버 수렴"));
  const sv = s.solver || {};
  const rows = [];
  const convPill = (c) => (c == null ? h("span", { class: "pill info" }, "기록 없음") : h("span", { class: `pill ${c ? "ok" : "bad"}` }, c ? "수렴" : "수렴 안 함"));
  for (const [name, label] of [["planet", "행성 L0"], ["hero", "히어로 L2"]]) {
    const x = sv[name];
    // 지각 세기 한계의 첫 풀이: 융기를 줄일 칸을 고르려고 행성을 먼저 한 번 풉니다. 여기서
    // 수렴하지 않으면 덜 풀린 지형으로 융기를 줄인 것이라 따로 한 줄로 보여 줍니다.
    const sl = name === "planet" && s.stages && s.stages.planet ? s.stages.planet.strength_limit : null;
    if (sl && sl.applied !== false && sl.first_pass_iterations != null) {
      const bits = [];
      if (sl.first_pass_z_mean_max_m != null) bits.push(`첫 풀이 최고 평균 고도 ${fmt(sl.first_pass_z_mean_max_m)} m`);
      if (sl.elevation_limit_m != null) bits.push(`한계 ${fmt(sl.elevation_limit_m)} m`);
      if (sl.reduced_cells != null) bits.push(`융기를 줄인 칸 ${fmtInt(sl.reduced_cells)}`);
      rows.push(
        h(
          "tr",
          null,
          h("td", null, h("span", { class: "swatch", style: `background:${PHASE_COLOR.planet}` }), "행성 L0 · 지각 세기 한계 첫 풀이", bits.length ? h("div", { class: "sub" }, bits.join(" · ")) : null),
          h("td", { class: "num" }, `${sl.first_pass_iterations} / ${(x && x.max_iter) ?? "—"}`),
          h("td", null, convPill(sl.first_pass_converged)),
          h("td", { class: "num" }, "—"),
          h("td", { class: "num" }, fmtDur(sl.seconds)),
        ),
      );
    }
    if (!x) continue;
    rows.push(
      h(
        "tr",
        null,
        h("td", null, h("span", { class: "swatch", style: `background:${PHASE_COLOR[name]}` }), label),
        h("td", { class: "num" }, `${x.iterations ?? "—"} / ${x.max_iter ?? "—"}`),
        h("td", null, convPill(x.converged)),
        h("td", { class: "num" }, fmtInt(x.n_frozen)),
        h("td", { class: "num" }, fmtDur(x.seconds)),
      ),
    );
  }
  solverCard.append(
    h(
      "div",
      { class: "table-wrap" },
      h(
        "table",
        null,
        h("thead", null, h("tr", null, h("th", null, "격자"), h("th", { class: "num" }, "반복 / 상한"), h("th", null, "수렴"), h("th", { class: "num" }, "고정 칸"), h("th", { class: "num" }, "시간"))),
        h("tbody", null, rows),
      ),
    ),
    h(
      "p",
      { class: "explain" },
      h("b", null, "고정 칸"),
      "은 물길 방향이 계속 오가서 방향을 고정한 칸 수입니다. 많으면 그 지역(대개 임계 경사 산비탈)의 물길이 불안정하다는 뜻입니다. ",
      h("b", null, "지각 세기 한계 첫 풀이"),
      "는 융기를 줄일 칸을 고르려고 행성을 먼저 한 번 푼 것입니다. 여기서 수렴하지 않으면 덜 풀린 지형을 보고 융기를 줄였다는 뜻이라, 산맥 높이를 볼 때 함께 보세요.",
    ),
  );
  const chartBox = h("div", { class: "two" });
  chartBox.append(
    convergenceChart(sv, "max_dz", "반복마다 가장 크게 바뀐 고도 [m]", "고도 변화가 멈춤 기준(landscape.stop_dz_m)보다 작아지고 방향 변화가 0 이면 수렴입니다. 세로축은 로그 눈금입니다."),
    convergenceChart(sv, "n_changed", "반복마다 물길 방향이 바뀐 칸 수 [칸]", "처음에는 거의 모든 칸이 바뀌다가 물길망이 자리를 잡으면 0 으로 떨어집니다. 세로축은 로그 눈금(0 은 맨 아래)입니다."),
  );
  solverCard.append(chartBox);

  // 걸린 시간
  const tCard = h(
    "div",
    { class: "card" },
    h("h3", null, "단계별 걸린 시간 [s]"),
    h("div", { class: "chart-legend" }, ["planet", "hero", "corridor"].map((k) => h("span", null, h("span", { class: "swatch box", style: `background:${PHASE_COLOR[k]}` }), { planet: "행성", hero: "히어로", corridor: "굽기" }[k]))),
  );
  const tm = s.timings || {};
  const all = [];
  const add = (rows, phase, skip) => {
    for (const r of rows || []) if (!(skip || []).includes(r.key)) all.push({ ...r, phase });
  };
  add(tm.planet, "planet", ["total", "stages", "stages_detail"]);
  add(tm.planet_detail, "planet", ["total"]);
  add(tm.hero, "hero", ["total", "stages", "stages_detail"]);
  add(tm.hero_detail, "hero", ["total"]);
  add(tm.corridor, "corridor", ["total"]);
  const maxT = Math.max(1e-6, ...all.map((r) => r.seconds));
  const timing = h("div", { class: "timing" });
  for (const r of all) {
    timing.append(
      h(
        "div",
        { class: "trow" },
        // 같은 이름이 여러 단계에 나올 수 있어(예: 히어로 '동굴 층'과 굽기 '동굴') 단계 이름을 앞에 붙입니다.
        h("span", null, h("span", { class: "tphase" }, `${PHASE_TEXT[r.phase] || r.phase} · `), r.label),
        h("span", { class: "tbar" }, h("i", { style: `width:${(r.seconds / maxT) * 100}%; background:${PHASE_COLOR[r.phase]}` })),
        h("span", { class: "tval" }, r.seconds < 0.01 ? "<0.01" : r.seconds.toFixed(r.seconds < 10 ? 2 : 1)),
      ),
    );
  }
  tCard.append(
    all.length ? timing : h("p", { class: "hint" }, "시간 기록이 없습니다."),
    h("p", { class: "explain" }, "묶음 manifest 와 회랑 manifest 에 적힌 계산 시간입니다(파일 쓰기·읽기 시간은 빠짐). 노트북에서는 지각 세기 한계의 첫 풀이, 히어로 솔버, 동굴 메시가 가장 오래 걸립니다."),
  );

  // 점수표
  const scCard = h(
    "div",
    { class: "card" },
    h("h3", null, "점수표"),
    h(
      "p",
      { class: "explain" },
      h("b", null, "검사"),
      "는 반드시 맞아야 하는 규칙이고, ",
      h("b", null, "결과"),
      "는 입력으로 정하지 않았는데 나온 값이라 지구 값과 견줍니다. ",
      h("b", null, "반쯤 입력"),
      "은 입력이 거의 정하는 값이라 지구다움의 근거로 세지 않습니다. 판정: ",
      h("span", { class: "pill ok" }, "통과"),
      " ",
      h("span", { class: "pill bad" }, "불합격"),
      " ",
      h("span", { class: "pill info" }, "판정 없음"),
    ),
  );
  const pillFor = (x) => (x == null ? "" : x.pass === true ? h("span", { class: "pill ok" }, "통과") : x.pass === false ? h("span", { class: "pill bad" }, "불합격") : h("span", { class: "pill info" }, "판정 없음"));
  // 값이 null 이면 계산하지 못했거나 건너뛴 항목입니다 (판정과 메모 줄에 이유가 있음).
  const val = (x, key) => {
    if (!x || x.value == null) return "—";
    const v = x.value;
    if (typeof v === "boolean") return v ? "예" : "아니오";
    const unit = x.unit === "cells" ? "칸" : x.unit ? " " + x.unit : "";
    const pct = !x.unit && typeof v === "number" && v >= 0 && v <= 1 && FRACTION_SCORES.has(key) ? ` (${+(v * 100).toFixed(1)}%)` : "";
    return `${fmt(v)}${unit}${pct}`;
  };
  scCard.append(
    h(
      "div",
      { class: "table-wrap" },
      h(
        "table",
        null,
        h("thead", null, h("tr", null, h("th", null, "항목"), h("th", { class: "num" }, "행성 L0"), h("th", { class: "num" }, "히어로 L2"), h("th", null, "종류"), h("th", null, "지구·기준"))),
        h(
          "tbody",
          null,
          (s.scorecard || []).map((r) =>
            h(
              "tr",
              null,
              h("td", null, h("div", null, h("b", null, r.label)), h("div", { class: "sub" }, r.meaning), h("div", { class: "sub mono" }, [r.planet && r.planet.note, r.hero && r.hero.note].filter(Boolean).join(" / "))),
              h("td", { class: "num" }, val(r.planet, r.key), h("div", null, pillFor(r.planet))),
              h("td", { class: "num" }, val(r.hero, r.key), h("div", null, pillFor(r.hero))),
              h("td", null, r.kind_label),
              h("td", null, r.earth || "—"),
            ),
          ),
        ),
      ),
    ),
  );

  // 회랑
  const cor = s.corridor;
  const corCard = cor
    ? h(
        "div",
        { class: "card" },
        h("h3", null, "엔진으로 구운 회랑"),
        h(
          "dl",
          { class: "facts" },
          h("div", null, h("dt", null, "방향 · 기준"), h("dd", null, `${cor.axis === "north" ? "남북" : "동서"} · ${cor.apex_kind === "fan" ? "선상지 꼭짓점" : cor.apex_kind === "river" ? "가장 큰 강" : cor.apex_kind || "—"}`)),
          h("div", null, h("dt", null, "동굴 입구"), h("dd", null, fmtInt(cor.caves.n_entrances))),
          h("div", null, h("dt", null, "동굴 메시 삼각형"), h("dd", null, fmtInt(cor.caves.faces_kept))),
          h("div", null, h("dt", null, "엔진 높이 기준 (y_offset)"), h("dd", null, cor.frame ? `${fmt(cor.frame.y_offset_m)} m` : "—")),
        ),
        h("p", { class: "hint" }, `파일: ${(cor.files || []).join(", ")}`),
        h("p", { class: "explain" }, "위 막대의 'Godot로 보기'를 누르면 이 파일들을 engine/baked/ 로 복사하고 Godot 가 가져오게(import) 한 뒤 걸어 볼 수 있게 띄웁니다."),
      )
    : null;

  root.append(h("div", { class: "two" }, diffCard, corCard || h("div")), solverCard, tCard, scCard);
}

function catLabel(id) {
  const c = S.schema && S.schema.categories.find((x) => x.id === id);
  return c ? c.label : id;
}

function convergenceChart(sv, key, title, text) {
  const canvas = h("canvas", { role: "img", "aria-label": title });
  const series = [];
  for (const name of ["planet", "hero"]) {
    const x = sv[name];
    if (x && x.history && x.history.length) series.push({ name, color: name === "planet" ? cssVar("--planet") : cssVar("--hero"), pts: x.history.map((r) => [r.iteration, r[key]]) });
  }
  const wrap = h(
    "div",
    { class: "chart-wrap" },
    h("h4", null, title),
    canvas,
    h("div", { class: "chart-legend" }, series.map((sr) => h("span", null, h("span", { class: "swatch", style: `background:${sr.color}` }), sr.name === "planet" ? "행성 L0" : "히어로 L2")), h("span", null, "가로: 반복 번호")),
    h("p", { class: "explain" }, text),
  );
  requestAnimationFrame(() => drawLineChart(canvas, series, true));
  if (!series.length) wrap.append(h("p", { class: "hint" }, "반복 기록이 없습니다."));
  return wrap;
}

function cssVar(name) {
  return getComputedStyle(document.documentElement).getPropertyValue(name).trim() || "#888";
}

function drawLineChart(canvas, series, logY) {
  const dpr = window.devicePixelRatio || 1;
  const W = canvas.clientWidth || 400;
  const H = canvas.clientHeight || 220;
  canvas.width = W * dpr;
  canvas.height = H * dpr;
  const ctx = canvas.getContext("2d");
  ctx.scale(dpr, dpr);
  ctx.clearRect(0, 0, W, H);
  if (!series.length) return;
  const m = { l: 54, r: 10, t: 8, b: 26 };
  const xs = series.flatMap((s) => s.pts.map((p) => p[0]));
  const ys = series.flatMap((s) => s.pts.map((p) => p[1])).filter((v) => v > 0);
  const x0 = Math.min(...xs), x1 = Math.max(...xs, x0 + 1);
  let y0 = logY ? Math.floor(Math.log10(Math.min(...ys, 1))) : 0;
  let y1 = logY ? Math.ceil(Math.log10(Math.max(...ys, 10))) : Math.max(...ys);
  if (y1 <= y0) y1 = y0 + 1;
  const X = (v) => m.l + ((v - x0) / (x1 - x0)) * (W - m.l - m.r);
  const Y = (v) => {
    const t = logY ? (v > 0 ? Math.log10(v) : y0) : v;
    return H - m.b - ((t - y0) / (y1 - y0)) * (H - m.t - m.b);
  };
  const muted = cssVar("--muted");
  const line = cssVar("--line");
  ctx.font = "11px system-ui, sans-serif";
  ctx.strokeStyle = line;
  ctx.fillStyle = muted;
  ctx.lineWidth = 1;
  for (let e = y0; e <= y1; e++) {
    const yy = Y(Math.pow(10, e));
    ctx.beginPath();
    ctx.moveTo(m.l, yy);
    ctx.lineTo(W - m.r, yy);
    ctx.stroke();
    ctx.textAlign = "right";
    ctx.textBaseline = "middle";
    ctx.fillText(e === 0 ? "1" : `10^${e}`, m.l - 6, yy);
  }
  const step = niceStep((x1 - x0) / 6);
  ctx.textAlign = "center";
  ctx.textBaseline = "top";
  for (let v = Math.ceil(x0 / step) * step; v <= x1; v += step) ctx.fillText(String(v), X(v), H - m.b + 6);
  for (const s of series) {
    ctx.strokeStyle = s.color;
    ctx.lineWidth = 2;
    ctx.beginPath();
    s.pts.forEach(([x, y], i) => (i ? ctx.lineTo(X(x), Y(y)) : ctx.moveTo(X(x), Y(y))));
    ctx.stroke();
  }
}

function niceStep(raw) {
  if (!(raw > 0)) return 1;
  const p = Math.pow(10, Math.floor(Math.log10(raw)));
  const f = raw / p;
  return (f < 1.5 ? 1 : f < 3.5 ? 2 : f < 7.5 ? 5 : 10) * p;
}

// ---------------------------------------------------------------- 그림 탭
function renderFigures() {
  const root = $("#tab-figures");
  if (S.tab !== "figures") return;
  root.textContent = "";
  const s = S.summary;
  if (!s || s.error) {
    root.append(h("div", { class: "empty" }, s && s.error ? s.error : "불러오는 중…"));
    return;
  }
  root.append(
    h(
      "p",
      { class: "explain" },
      "analysis/figures/render_results.py 가 그린 그림입니다. 각 그림 아래에 무엇을 그렸는지, 어떻게 읽는지, 무엇을 보면 되는지 적었습니다. 그림을 누르면(또는 Tab 으로 고르고 Enter) 크게 봅니다. 같은 값을 직접 골라 보려면 행성 지도·히어로 지도 탭을 쓰세요.",
    ),
  );
  if (!s.figures.length) {
    root.append(
      h(
        "div",
        { class: "empty" },
        s.levels && !s.levels.planet
          ? "평면 히어로 실행이라 그림 스크립트를 돌리지 않았습니다 (행성 묶음이 필요). 히어로 지도 탭에서 결과를 보세요."
          : "그림이 없습니다. '끝나면 결과 그림 그리기'를 켜고 실행하거나, uv run python analysis/figures/render_results.py <실행 폴더> 로 그리세요.",
      ),
    );
    return;
  }
  const grid = h("div", { class: "figs" });
  for (const f of s.figures) {
    const src = `/api/figure?${q({ run: S.run, name: f.file })}`;
    const img = h("img", { src, alt: f.title, loading: "lazy" });
    // 키보드로도 크게 볼 수 있게 그림을 단추로 감쌉니다.
    const open = h("button", { type: "button", class: "fig-btn", "aria-label": `${f.title} 크게 보기` }, img);
    open.addEventListener("click", () => {
      const dlg = $("#lightbox");
      $("img", dlg).src = src;
      $("img", dlg).alt = f.title;
      dlg.showModal();
    });
    grid.append(
      h(
        "figure",
        { class: "fig card" },
        h("h3", null, f.title),
        open,
        h(
          "figcaption",
          null,
          h("dl", null, f.what ? [h("dt", null, "무엇인가"), h("dd", null, f.what)] : null, f.how ? [h("dt", null, "어떻게 읽나"), h("dd", null, f.how)] : null, f.look ? [h("dt", null, "볼 점"), h("dd", null, f.look)] : null),
          h("span", { class: "hint mono" }, f.file),
        ),
      ),
    );
  }
  root.append(grid);
}

// ---------------------------------------------------------------- 매개변수 설명 탭
function buildReference() {
  const root = $("#tab-reference");
  if (S.tab !== "reference" || !S.schema) return;
  root.textContent = "";
  const filter = h("input", { type: "search", placeholder: "이름·설명으로 찾기", "aria-label": "매개변수 설명 찾기" });
  // 고른 결과의 값: 서버가 주는 그 실행의 전체 설정(config_flat)에서 모든 키를 채웁니다.
  // 옛 서버라 config_flat 이 없으면 그 실행 프로필의 기본값과 다른 키(diff)만 보입니다.
  const s = S.summary && !S.summary.error ? S.summary : null;
  const info = runInfo(S.run);
  const flat = s && s.config_flat && typeof s.config_flat === "object" ? s.config_flat : null;
  const fromDiff = {};
  if (s && !flat)
    for (const d of s.diff || []) if ((!d.status || d.status === "changed") && !(d.value === null && d.default !== null)) fromDiff[d.key] = d.value;
  const runProfile = (s && s.profile) || (flat && flat["profile.name"]) || (info && info.profile) || null;
  const runPlanet = (flat && flat["planet.name"]) || null;
  const cfgText = (p, v) => (v === null ? "null" : typeof v === "object" ? JSON.stringify(v) : displayValue(p, v));
  const tbody = h("tbody");
  const render = () => {
    const t = filter.value.trim().toLowerCase();
    tbody.textContent = "";
    for (const p of S.schema.params) {
      const hay = `${p.key} ${p.help} ${p.note}`.toLowerCase();
      if (t && !hay.includes(t)) continue;
      let runCell;
      if (!S.run) runCell = h("td", { class: "num" }, "");
      else if (!s) runCell = h("td", { class: "num hint" }, S.summary && S.summary.error ? "—" : "…");
      else if (flat) {
        if (p.key in flat) {
          const v = flat[p.key];
          const differs = !deepEqual(v, p.default);
          runCell = h("td", { class: differs ? "num differs" : "num", title: differs ? "기본값 열과 다릅니다" : null }, cfgText(p, v));
        } else runCell = h("td", { class: "num hint" }, "— (이 실행에 없던 키)");
      } else runCell = h("td", { class: p.key in fromDiff ? "num differs" : "num" }, p.key in fromDiff ? cfgText(p, fromDiff[p.key]) : "");
      tbody.append(
        h(
          "tr",
          null,
          h("td", null, h("code", null, p.key)),
          h("td", null, h("span", { class: `badge cat-${p.category}` }, catLabel(p.category))),
          h("td", null, p.help, p.note ? h("div", { class: "sub" }, p.note) : null, p.history ? h("div", { class: "sub" }, `손 보정 기록: ${p.history}`) : null),
          h("td", null, p.unit || "—"),
          h("td", { class: "num" }, displayValue(p, p.default)),
          h("td", { class: "num" }, p.key in S.overrides ? displayValue(p, S.overrides[p.key]) : ""),
          runCell,
          h("td", { class: "sub" }, p.source),
        ),
      );
    }
  };
  filter.addEventListener("input", render);
  const notices = [];
  if (S.run && S.summary && S.summary.error) notices.push(`고른 결과의 설정을 읽지 못했습니다: ${S.summary.error}`);
  if (runProfile && runProfile !== S.schema.profile)
    notices.push(
      `고른 결과(${S.run})는 프로필 ${runProfile} 로 실행했습니다. '기본값' 열은 실행 설정에서 고른 프로필 ${S.schema.profile} 의 값이라 두 열이 달라도 그 실행이 값을 바꾼 것은 아닐 수 있습니다. 같은 기준으로 보려면 실행 설정의 프로필을 ${runProfile} 로 바꾸세요.`,
    );
  if (runPlanet && runPlanet !== S.schema.planet) notices.push(`고른 결과는 행성 설정 ${runPlanet} 로 실행했습니다. '기본값' 열은 ${S.schema.planet} 의 값입니다.`);
  const hint = flat
    ? "'고른 결과의 값'은 위에서 고른 실행이 실제로 쓴 값입니다(묶음 manifest 의 config). '기본값' 열과 다른 칸은 색으로 표시합니다."
    : `'고른 결과의 값'은 고른 실행이 그 실행 프로필${runProfile ? `(${runProfile})` : ""}의 기본값과 다르게 쓴 값만 보입니다. 빈칸은 그 실행 프로필의 기본값을 썼다는 뜻이라, 이 표의 '기본값' 열과는 다를 수 있습니다.`;
  root.append(
    h(
      "div",
      { class: "card" },
      h("h2", null, `매개변수 설명 (${S.schema.planet} + 프로필 ${S.schema.profile})`),
      notices.length ? h("div", { class: "banner warn", role: "status" }, notices.map((n) => h("div", null, n))) : null,
      h("p", { class: "explain" }, (S.schema.header || []).join(" ")),
      h(
        "div",
        { class: "table-wrap" },
        h(
          "table",
          null,
          h("thead", null, h("tr", null, h("th", null, "분류"), h("th", null, "뜻"))),
          h("tbody", null, S.schema.categories.map((c) => h("tr", null, h("td", null, h("span", { class: `badge cat-${c.id}` }, c.label)), h("td", null, c.description)))),
        ),
      ),
      filter,
      h(
        "div",
        { class: "table-wrap" },
        h(
          "table",
          null,
          h(
            "thead",
            null,
            h(
              "tr",
              null,
              h("th", null, "키"),
              h("th", null, "분류"),
              h("th", null, "설명"),
              h("th", null, "단위"),
              h("th", { class: "num" }, `기본값 (${S.schema.profile})`),
              h("th", { class: "num" }, "실행 설정에서 바꾼 값"),
              h("th", { class: "num" }, runProfile ? `고른 결과의 값 (${runProfile})` : "고른 결과의 값"),
              h("th", null, "파일"),
            ),
          ),
          tbody,
        ),
      ),
      h("p", { class: "hint" }, hint),
    ),
  );
  render();
}

// ---------------------------------------------------------------- 색표
const CMAPS = {
  viridis: ["#440154", "#482475", "#414487", "#355f8d", "#2a788e", "#21918c", "#22a884", "#44bf70", "#7ad151", "#bddf26", "#fde725"],
  magma: ["#000004", "#140e36", "#3b0f70", "#641a80", "#8c2981", "#b73779", "#de4968", "#f7705c", "#fe9f6d", "#fecf92", "#fcfdbf"],
  cividis: ["#00224e", "#083370", "#35456c", "#4f576c", "#666970", "#7d7c78", "#948e77", "#aea371", "#c8b866", "#e5cf52", "#fee838"],
  blues: ["#f7fbff", "#e3eef9", "#d0e1f2", "#b7d4ea", "#94c4df", "#6aaed6", "#4a98c9", "#2e7ebc", "#1764ab", "#084a91", "#08306b"],
  ylgnbu: ["#ffffd9", "#f1faba", "#d6efb3", "#abdeb7", "#73c8bd", "#40b5c4", "#2498c1", "#2072b1", "#234da0", "#1f2f87", "#081d58"],
  ylorbr: ["#ffffe5", "#fff9c5", "#feeba2", "#fed778", "#febb47", "#fe9829", "#f07818", "#d85a09", "#b84203", "#8e3104", "#662506"],
  diverging: ["#053061", "#2065ab", "#4393c3", "#90c4dd", "#d1e5f0", "#f7f6f6", "#fddbc7", "#f3a481", "#d6604d", "#b1182b", "#67001f"],
  gray: ["#111111", "#f4f4f4"],
  land: ["#3c8e61", "#43974f", "#5ea04b", "#7da853", "#96ae58", "#b0b55d", "#bcad62", "#c4a46f", "#d6b196", "#e7c9bf", "#f7ecec"],
  ocean: ["#083c7d", "#0d57a1", "#2171b5", "#3b8bc2", "#5ba3d0", "#7fb9da", "#a6cee4", "#c7dbef", "#d9e8f5"],
};
const CMAP_LABELS = {
  terrain: "지형 (해수면에서 바다·육지)",
  viridis: "viridis (밝을수록 큼)",
  magma: "magma (밝을수록 큼)",
  cividis: "cividis (색각 이상 친화)",
  blues: "파랑 (물)",
  ylgnbu: "노랑-초록-파랑 (비)",
  ylorbr: "노랑-갈색 (흙·퇴적)",
  diverging: "파랑-흰색-빨강 (가운데 기준)",
  gray: "회색",
};
const NAN_RGB = [205, 210, 206];

function hexRgb(hx) {
  const n = parseInt(hx.slice(1), 16);
  return [(n >> 16) & 255, (n >> 8) & 255, n & 255];
}
function makeLut(stops, n = 256) {
  const rgb = stops.map(hexRgb);
  const lut = new Uint8ClampedArray(n * 3);
  for (let i = 0; i < n; i++) {
    const t = (i / (n - 1)) * (rgb.length - 1);
    const k = Math.min(Math.floor(t), rgb.length - 2);
    const f = t - k;
    for (let c = 0; c < 3; c++) lut[i * 3 + c] = rgb[k][c] * (1 - f) + rgb[k + 1][c] * f;
  }
  return lut;
}
const LUTS = {};
function lut(name) {
  if (!LUTS[name]) LUTS[name] = makeLut(CMAPS[name]);
  return LUTS[name];
}

// 지형 색표에서 바다(파랑)와 육지 색을 나눌 높이. 서버가 split 을 주면(예: 기준 고도 필드에 그 실행의
// 해수면) 그 값을, 아니면 0 m(해수면 기준 높이)를 씁니다.
function terrainSplit(meta) {
  return meta && Number.isFinite(meta.split) ? meta.split : 0;
}

// 값 → [0, 1] 과 색. terrain 은 나눌 높이(terrainSplit)를 가운데로 두는 두 갈래 색입니다.
function colorizer(meta, view) {
  const lo = view.min;
  const hi = view.max;
  const log = view.log;
  const cmap = view.cmap;
  if (meta.kind === "categorical") {
    const map = new Map();
    for (const c of meta.categories || []) map.set(c.value, hexRgb(c.color));
    return (v) => {
      if (!Number.isFinite(v)) return NAN_RGB;
      return map.get(Math.round(v)) || hashColor(Math.round(v));
    };
  }
  const L = log ? Math.log10(Math.max(lo, 1e-300)) : lo;
  const Hh = log ? Math.log10(Math.max(hi, 1e-300)) : hi;
  const span = Hh - L || 1;
  if (cmap === "terrain") {
    const oc = lut("ocean");
    const ld = lut("land");
    const sp = terrainSplit(meta);
    return (v) => {
      if (!Number.isFinite(v)) return NAN_RGB;
      if (v < sp && lo < sp) {
        const t = Math.min(Math.max((v - lo) / (sp - lo), 0), 1);
        const i = Math.round(t * 255) * 3;
        return [oc[i], oc[i + 1], oc[i + 2]];
      }
      const base = Math.max(lo, sp);
      const t = Math.min(Math.max((v - base) / (hi - base || 1), 0), 1);
      const i = Math.round(t * 255) * 3;
      return [ld[i], ld[i + 1], ld[i + 2]];
    };
  }
  const tab = lut(cmap in CMAPS ? cmap : "viridis");
  return (v) => {
    if (!Number.isFinite(v)) return NAN_RGB;
    let x = v;
    if (log) {
      if (v <= 0) return [tab[0], tab[1], tab[2]];
      x = Math.log10(v);
    }
    const t = Math.min(Math.max((x - L) / span, 0), 1);
    const i = Math.round(t * 255) * 3;
    return [tab[i], tab[i + 1], tab[i + 2]];
  };
}
function hashColor(v) {
  const hue = ((v * 0.618033988749895) % 1 + 1) % 1;
  const a = 0.55 * Math.min(0.62, 1 - 0.62);
  const f = (n) => {
    const k = (n + hue * 12) % 12;
    return Math.round(255 * (0.62 - a * Math.max(-1, Math.min(k - 3, 9 - k, 1))));
  };
  return [f(0), f(8), f(4)];
}

// ---------------------------------------------------------------- 지도
// 격자·필드 설명 캐시. 열쇠와 주소(&v=)에 /api/level 의 version(그 실행의 행성·히어로·회랑 manifest 가
// 바뀌면 달라짐)을 넣어, 같은 이름으로 다시 만든 실행의 옛 격자·색 범위를 쓰지 않습니다.
const GRID_CACHE = new Map();
const META_CACHE = new Map();
function cacheKey(run, level, version, name) {
  return `${run}|${level}|${version == null ? "" : version}|${name}`;
}
function fieldQuery(run, level, version, name) {
  const o = { run, level, name };
  if (version != null) o.v = version;
  return q(o);
}
function remember(cache, key, value, limit) {
  cache.set(key, value);
  while (cache.size > limit) cache.delete(cache.keys().next().value);
  return value;
}
async function fetchGrid(run, level, version, name) {
  const key = cacheKey(run, level, version, name);
  if (GRID_CACHE.has(key)) return GRID_CACHE.get(key);
  const buf = await api(`/api/field.bin?${fieldQuery(run, level, version, name)}`);
  return remember(GRID_CACHE, key, new Float32Array(buf), 30);
}
async function fetchMeta(run, level, version, name) {
  const key = cacheKey(run, level, version, name);
  if (META_CACHE.has(key)) return META_CACHE.get(key);
  const meta = await api(`/api/field?${fieldQuery(run, level, version, name)}`);
  return remember(META_CACHE, key, meta, 200);
}
// 서버가 version 을 주지 않으면(옛 서버) 지도를 다시 불러올 때 그 실행·단계의 캐시를 비웁니다.
function dropCache(run, level) {
  const prefix = `${run}|${level}|`;
  for (const cache of [GRID_CACHE, META_CACHE]) for (const k of [...cache.keys()]) if (k.startsWith(prefix)) cache.delete(k);
}

// 음영(기복 그림자)을 처음부터 켜 두는 높이 필드. 다른 필드는 음영이 색을 바꿔 색 막대와 어긋나므로
// 꺼 둔 채 시작합니다 (켜고 끄기는 필드마다 기억).
const ELEVATION_FIELDS = new Set(["z_m", "z_mean_m", "z_platform_m"]);
// 0 m 가 해수면이 아닌 높이 필드 (해수면을 정하기 전 기준 고도)
const NOT_SEA_LEVEL_FIELDS = new Set(["z_platform_m"]);

class MapView {
  constructor(level, root) {
    this.level = level;
    this.root = root;
    this.stale = true;
    this.run = null;
    this.lm = null; // 단계 설명 (/api/level)
    this.gen = 0; // 불러오기 차례. 늦게 온 옛 응답(다른 실행·다른 필드)을 버립니다.
    this.field = store.get(`field-${level}`, null);
    this.meta = null;
    this.data = null;
    this.view = { min: 0, max: 1, log: false, cmap: "viridis" };
    this.overlays = {};
    this.overlayData = {};
    this.overlayMetaB = null;
    this.shadeData = null;
    this.zoom = { s: 0, ox: 0, oy: 0 };
    this.zoomed = false;
    this.sel = null;
    this.img = document.createElement("canvas");
    // 창 크기가 바뀌면 다시 맞춥니다 (지도 DOM 을 다시 만들어도 듣는 곳은 하나만 둠).
    window.addEventListener("resize", () => {
      if (S.tab !== this.level || !this.lm || !this.canvas || !this.zoom.s) return;
      this.fit();
      this.draw();
      if (this.meta) this.drawLegend();
    });
  }

  // 이 실행에 이 단계 결과가 아직(또는 아예) 없을 때 보여 줄 글
  missingText(info) {
    const what = this.level === "planet" ? "행성" : "히어로";
    if (["queued", "running"].includes(info.status))
      return `${what} 단계가 아직 끝나지 않았습니다 (실행이 도는 중). 단계가 끝나면 이 탭이 저절로 다시 불러옵니다.`;
    if (this.level === "planet" && info.flat) return "평면 히어로 실행이라 행성 결과가 없습니다. 히어로 지도 탭을 보세요.";
    if (["failed", "cancelled", "interrupted"].includes(info.status))
      return `실행이 '${STATUS_TEXT[info.status]}' 상태로 끝나 ${what} 결과가 없습니다. 진행 탭의 기록을 보세요.`;
    return `이 실행에는 ${what} 결과가 없습니다.`;
  }

  async show() {
    if (!S.run) {
      this.root.textContent = "";
      this.root.append(h("div", { class: "empty" }, "아직 볼 결과가 없습니다."));
      this.run = null;
      return;
    }
    if (!this.stale && this.run === S.run) {
      this.draw();
      return;
    }
    const gen = ++this.gen;
    const run = S.run;
    const info = runInfo(run);
    if (info && info.has && !info.has[this.level]) {
      // stale 을 그대로 두어 다음에 이 탭을 열거나 실행 목록이 바뀌면 다시 확인합니다.
      this.root.textContent = "";
      this.root.append(h("div", { class: "empty" }, this.missingText(info)));
      return;
    }
    const prevRun = this.run;
    const prevLm = this.lm;
    const keepDom = prevRun === run && prevLm && this.canvas && this.root.contains(this.canvas);
    this.stale = false;
    this.run = run;
    if (keepDom) this.loading.hidden = false;
    else {
      this.root.textContent = "";
      this.root.append(h("div", { class: "empty" }, "지도 정보를 불러오는 중… (행성은 처음 한 번 위경도 격자를 만듭니다)"));
    }
    let lm;
    try {
      lm = await api(`/api/level?${q({ run, level: this.level })}`);
    } catch (e) {
      if (gen !== this.gen) return;
      this.stale = true; // 다음에 이 탭을 열 때 다시 시도합니다
      this.root.textContent = "";
      this.root.append(h("div", { class: "banner error" }, e.message));
      return;
    }
    if (gen !== this.gen || S.run !== run) return;
    // 같은 실행이고 version 이 같으면 바뀐 것이 없으니 보던 화면(필드·확대·색 범위)을 그대로 둡니다.
    if (keepDom && lm.version != null && lm.version === prevLm.version) {
      this.loading.hidden = true;
      this.draw();
      return;
    }
    if (lm.version == null) dropCache(run, this.level);
    // 다른 실행이거나 격자 크기가 바뀌면 확대·이동을 버리고 새 격자에 맞춥니다.
    const sameGrid = prevRun === run && prevLm && prevLm.width === lm.width && prevLm.height === lm.height;
    if (!sameGrid) {
      this.zoomed = false;
      this.zoom = { s: 0, ox: 0, oy: 0 };
    }
    this.lm = lm;
    this.overlays = {};
    for (const o of lm.overlays) this.overlays[o.id] = store.get(`ov-${this.level}-${o.id}`, o.default);
    this.overlayData = {};
    this.overlayMetaB = null;
    this.shadeData = null;
    this.meta = null;
    this.data = null;
    this.sel = null;
    this.build();
    const names = lm.groups.flatMap((g) => g.fields.map((f) => f.name));
    await this.setField(names.includes(this.field) ? this.field : lm.default_field);
  }

  build() {
    const lm = this.lm;
    this.root.textContent = "";
    const fieldSel = h("select", { "aria-label": "필드" });
    for (const g of lm.groups) fieldSel.append(h("optgroup", { label: g.name }, g.fields.map((f) => h("option", { value: f.name }, `${f.label}${f.unit ? ` [${f.unit}]` : ""}`))));
    fieldSel.addEventListener("change", () => this.setField(fieldSel.value));
    const cmapSel = h("select", { "aria-label": "색표" });
    for (const [k, v] of Object.entries(CMAP_LABELS)) cmapSel.append(h("option", { value: k }, v));
    cmapSel.addEventListener("change", () => {
      this.view.cmap = cmapSel.value;
      this.renderImage();
    });
    const minIn = h("input", { type: "number", step: "any", "aria-label": "색 범위 아래" });
    const maxIn = h("input", { type: "number", step: "any", "aria-label": "색 범위 위" });
    this.rangeMsg = h("p", { class: "note error range-msg", role: "status", hidden: true });
    const onRange = () => {
      if (!this.meta) return;
      const a = Number(minIn.value);
      const b = Number(maxIn.value);
      let msg = "";
      if (minIn.value.trim() === "" || maxIn.value.trim() === "" || !Number.isFinite(a) || !Number.isFinite(b)) msg = "색 범위에는 숫자를 넣어 주세요.";
      else if (!(b > a)) msg = "색 범위의 위 값이 아래 값보다 커야 합니다.";
      else if (this.view.log && !(a > 0)) {
        // 로그 눈금에서 0 이하는 log10 이 -∞·NaN 이라 색과 눈금을 정할 수 없습니다.
        const lo = this.logFloor();
        msg = `로그 눈금에서는 아래 값이 0 보다 커야 합니다${lo != null ? ` (이 필드의 가장 작은 양수 ${fmt(lo)})` : ""}. 0 이하까지 보려면 '로그 눈금'을 끄세요.`;
      }
      if (msg) {
        this.showRangeMsg(msg);
        this.syncControls(); // 입력 칸을 지금 쓰는 범위로 되돌림
        return;
      }
      this.showRangeMsg("");
      this.view.min = a;
      this.view.max = b;
      this.renderImage();
    };
    minIn.addEventListener("change", onRange);
    maxIn.addEventListener("change", onRange);
    const autoBtn = h("button", { class: "btn small", type: "button" }, "자동");
    autoBtn.addEventListener("click", () => this.resetView());
    const logBox = h("input", { type: "checkbox" });
    logBox.addEventListener("change", () => {
      if (!this.meta) return;
      if (logBox.checked) {
        const lo = this.logFloor();
        const hi = this.view.max > 0 ? this.view.max : (this.meta.stats || {}).max;
        if (lo == null || !(hi > lo)) {
          logBox.checked = false;
          this.showRangeMsg("이 필드에는 0 보다 큰 값이 없어 로그 눈금을 쓸 수 없습니다.");
          return;
        }
        this.view.log = true;
        this.view.max = hi;
        if (!(this.view.min > 0) || this.view.min >= this.view.max) this.view.min = lo < hi ? lo : hi * 1e-3;
      } else this.view.log = false;
      this.showRangeMsg("");
      this.syncControls();
      this.renderImage();
    });
    const shadeBox = h("input", { type: "checkbox" });
    shadeBox.addEventListener("change", async () => {
      store.set(`shade-${this.level}-${this.field}`, shadeBox.checked);
      await this.ensureShade();
      this.renderImage();
    });
    this.ctl = { fieldSel, cmapSel, minIn, maxIn, logBox, shadeBox };

    const ovBox = h("div", { class: "overlay-legend", role: "group", "aria-label": "겹쳐 그리기" }, h("span", { class: "hint" }, "겹쳐 그리기:"));
    for (const o of lm.overlays) {
      const cb = h("input", { type: "checkbox" });
      cb.checked = !!this.overlays[o.id];
      cb.addEventListener("change", async () => {
        this.overlays[o.id] = cb.checked;
        store.set(`ov-${this.level}-${o.id}`, cb.checked);
        await this.ensureOverlays();
        this.renderImage();
      });
      const sw = o.mode === "category" ? h("i", { style: "background: linear-gradient(90deg,#d1495b 33%,#2e86de 33% 66%,#8d99a6 66%)" }) : h("i", { style: `background:${o.color}` });
      ovBox.append(h("label", null, cb, sw, o.label));
    }
    for (const r of lm.rects || []) ovBox.append(h("span", null, h("i", { style: `background:${r.color}` }), r.label));
    for (const m of lm.markers || []) ovBox.append(h("span", null, h("i", { style: "background:#ffd400; height: 10px; width: 10px; border-radius: 50%; border: 2px solid #111" }), m.label));

    const controls = h(
      "div",
      { class: "map-controls" },
      h("label", { class: "field" }, h("span", null, "필드"), fieldSel),
      h("label", { class: "field" }, h("span", null, "색표"), cmapSel),
      h("div", { class: "field" }, h("span", null, "색 범위 (표시 단위)"), h("div", { class: "range" }, minIn, h("span", null, "~"), maxIn, autoBtn)),
      h("label", { class: "check" }, logBox, h("span", null, "로그 눈금")),
      h("label", { class: "check", title: "높이 필드는 처음부터 켜 둡니다. 다른 필드에 켜면 색이 색 막대보다 어둡거나 밝아질 수 있습니다." }, shadeBox, h("span", null, "음영 (기복 그림자)")),
    );
    this.desc = h("div", { class: "field-desc" });
    this.canvas = h("canvas", {
      tabindex: "0",
      role: "application",
      "aria-roledescription": "지도",
      "aria-label": "지도. 마우스를 올리면 값, 누르면 칸 정보. 키보드: 화살표로 칸 옮기기(Shift 는 10칸), Enter 로 칸 정보, + / − 로 확대·축소, 0 으로 전체 보기.",
    });
    this.loading = h("div", { class: "map-loading" }, "불러오는 중…");
    const resetZoom = h("button", { class: "btn small", type: "button", title: "전체 보기" }, "전체 보기");
    resetZoom.addEventListener("click", () => {
      this.fit();
      this.draw();
    });
    this.wrap = h("div", { class: "map-canvas-wrap" }, this.canvas, this.loading, h("div", { class: "map-tools" }, resetZoom));
    this.kbInfo = h("p", { class: "hint kb-info", "aria-live": "polite" });
    this.colorbar = h("canvas", { role: "img", "aria-label": "색 막대" });
    this.catLegend = h("div", { class: "cat-legend" });
    this.legendNote = h("p", { class: "hint" });
    this.probeBox = h("div", { class: "card probe" }, h("h3", null, "칸 정보"), h("p", { class: "hint" }, "지도를 누르면(또는 지도를 고르고 Enter) 그 칸의 모든 값(이름·단위)과 땅속 층 기둥을 여기에 보여 줍니다."));
    const mapCard = h(
      "div",
      { class: "card" },
      h("div", { class: "card-head" }, h("h2", null, lm.label), h("span", { class: "hint" }, this.level === "planet" ? `위경도 지도 ${lm.width}×${lm.height} · 면당 ${lm.n_per_face}칸 (약 ${fmt(lm.spacing_km)} km)` : `${lm.shape[1]}×${lm.shape[0]}칸, ${fmt(lm.spacing_m)} m 간격 → ${lm.width}×${lm.height} 픽셀 (${lm.factor}칸 묶음)`)),
      controls,
      this.rangeMsg,
      this.desc,
      ovBox,
      this.wrap,
      this.kbInfo,
      h("div", { class: "legend" }, h("div", { class: "colorbar" }, this.colorbar), this.catLegend, this.legendNote),
      h(
        "p",
        { class: "hint" },
        "휠(또는 두 손가락 벌리기)로 확대·축소, 끌어서 옮기기, 누르면 칸 정보. 키보드는 지도를 고른 뒤 화살표·Enter·+/−·0. ",
        this.level === "planet" ? "위경도 지도라 극 근처가 옆으로 늘어나 보입니다." : "가로는 동, 세로는 북 [km] (유역 가운데가 0).",
      ),
    );
    this.root.append(h("div", { class: "map-layout" }, mapCard, this.probeBox));
    this.bindCanvas();
  }

  showRangeMsg(msg) {
    if (!this.rangeMsg) return;
    this.rangeMsg.textContent = msg;
    this.rangeMsg.hidden = !msg;
  }

  async setField(name) {
    const gen = this.gen;
    const lm = this.lm;
    const run = this.run;
    this.field = name;
    store.set(`field-${this.level}`, name);
    this.ctl.fieldSel.value = name;
    this.loading.hidden = false;
    try {
      const [meta, data] = await Promise.all([fetchMeta(run, this.level, lm.version, name), fetchGrid(run, this.level, lm.version, name)]);
      if (this.field !== name || gen !== this.gen) return;
      if (data.length !== lm.width * lm.height) {
        // 격자 크기가 단계 설명과 다르면 그 사이에 실행이 다시 만들어진 것입니다.
        dropCache(run, this.level);
        this.stale = true;
        throw new Error("지도 격자 크기가 맞지 않습니다 (그 사이 실행이 다시 만들어진 듯합니다). 실행 목록 새로 고침(↻)을 누르거나 탭을 다시 여세요.");
      }
      this.meta = meta;
      this.data = data;
      await Promise.all([this.ensureOverlays(), this.ensureShade()]);
      if (this.field !== name || gen !== this.gen) return;
      this.resetView(true);
      this.renderDesc();
      if (!this.zoomed) this.fit();
      this.renderImage();
    } catch (e) {
      if (gen !== this.gen) return;
      this.desc.textContent = "";
      this.desc.append(h("div", { class: "banner error" }, e.message));
    } finally {
      if (gen === this.gen) this.loading.hidden = true;
    }
  }

  // 로그 눈금 아래 끝으로 쓸 수 있는 가장 작은 양수 (없으면 null)
  logFloor() {
    const st = (this.meta && this.meta.stats) || {};
    if (st.positive_min > 0) return st.positive_min;
    const hi = this.view.max > 0 ? this.view.max : st.max > 0 ? st.max : null;
    return hi == null ? null : hi * 1e-3;
  }

  resetView(quiet) {
    const m = this.meta;
    this.view.cmap = m.cmap === "categorical" ? "viridis" : m.cmap;
    // 기준 고도처럼 0 m 가 해수면이 아닌 필드는, 서버가 나눌 높이(split)를 주지 않으면 지형 색표
    // (0 m 아래를 바다 색으로 칠함) 대신 cividis 로 봅니다.
    if (this.view.cmap === "terrain" && NOT_SEA_LEVEL_FIELDS.has(m.name) && !Number.isFinite(m.split)) this.view.cmap = "cividis";
    this.view.log = m.scale === "log";
    if (m.range) {
      this.view.min = m.range[0];
      this.view.max = m.range[1];
    }
    if (this.view.log && !(this.view.min > 0 && this.view.max > this.view.min)) {
      const lo = this.logFloor();
      if (lo != null && this.view.max > lo) this.view.min = lo;
      else this.view.log = false;
    }
    this.showRangeMsg("");
    this.syncControls();
    if (!quiet) this.renderImage();
  }

  syncControls() {
    const c = this.ctl;
    const cat = this.meta.kind === "categorical";
    c.cmapSel.value = this.view.cmap;
    c.cmapSel.disabled = cat;
    c.minIn.disabled = c.maxIn.disabled = cat;
    c.minIn.value = cat ? "" : +this.view.min.toPrecision(4);
    c.maxIn.value = cat ? "" : +this.view.max.toPrecision(4);
    const st = this.meta.stats || {};
    // 0 보다 큰 값이 하나라도 있어야 로그 눈금을 켤 수 있습니다.
    c.logBox.disabled = cat || !(st.positive_min > 0 || st.max > 0);
    c.logBox.checked = this.view.log;
    c.shadeBox.checked = this.shadeOn();
  }

  // 높이 필드인가 (음영을 처음부터 켜 둘지 정함). 서버가 shade_default 를 주면 그 값을 씁니다.
  isElevation() {
    if (this.meta && this.meta.name === this.field && typeof this.meta.shade_default === "boolean") return this.meta.shade_default;
    return this.field === (this.lm && this.lm.elevation) || ELEVATION_FIELDS.has(this.field);
  }

  shadeOn() {
    return !!store.get(`shade-${this.level}-${this.field}`, this.isElevation());
  }

  renderDesc() {
    const m = this.meta;
    const st = m.stats || {};
    this.desc.textContent = "";
    const statsLine =
      m.kind === "categorical"
        ? `칸 ${fmtInt(st.n)}개 중 값이 있는 칸 ${(100 * (st.finite_fraction || 0)).toFixed(1)}%`
        : st.min != null
          ? `최소 ${fmt(st.min)} · 중앙 ${fmt(st.median)} · 최대 ${fmt(st.max)} ${m.unit} · 값이 있는 칸 ${(100 * (st.finite_fraction || 0)).toFixed(1)}%`
          : "값이 있는 칸이 없습니다 (모두 빈칸)";
    this.desc.append(
      h(
        "div",
        { class: "title" },
        h("h3", null, m.label),
        h("span", { class: "badge cat-physics" }, m.unit ? `단위 ${m.unit}` : "단위 없음"),
        h("span", { class: "badge cat-learn" }, m.group),
        m.derived ? h("span", { class: "badge cat-hand" }, "계산값") : null,
        h("code", { class: "hint" }, m.name),
      ),
      h("p", { class: "desc" }, m.description, m.factor !== 1 ? ` (저장 단위 ${m.unit_raw} 를 ${m.unit} 로 바꿔 보여 줌)` : ""),
      m.read ? h("p", { class: "read" }, h("b", null, "어떻게 읽나. "), m.read) : null,
      h("p", { class: "stats" }, statsLine),
    );
  }

  async ensureOverlays() {
    const lm = this.lm;
    const run = this.run;
    const need = lm.overlays.filter((o) => this.overlays[o.id]).map((o) => o.field);
    for (const f of need) {
      if (this.overlayData[f]) continue;
      const d = await fetchGrid(run, this.level, lm.version, f);
      if (this.lm !== lm) return;
      if (d.length === lm.width * lm.height) this.overlayData[f] = d;
    }
    if (need.includes("boundary_type") && !this.overlayMetaB) {
      const mb = await fetchMeta(run, this.level, lm.version, "boundary_type");
      if (this.lm === lm) this.overlayMetaB = mb;
    }
  }

  async ensureShade() {
    if (!this.shadeOn() || this.shadeData) return;
    const lm = this.lm;
    const z = await fetchGrid(this.run, this.level, lm.version, lm.elevation);
    const W = lm.width;
    const H = lm.height;
    if (this.lm !== lm || z.length !== W * H) return;
    const gx = new Float32Array(W * H);
    const gy = new Float32Array(W * H);
    const mags = [];
    for (let y = 0; y < H; y++) {
      for (let x = 0; x < W; x++) {
        const i = y * W + x;
        const xl = this.level === "planet" ? (x - 1 + W) % W : Math.max(x - 1, 0);
        const xr = this.level === "planet" ? (x + 1) % W : Math.min(x + 1, W - 1);
        const yu = Math.max(y - 1, 0);
        const yd = Math.min(y + 1, H - 1);
        const a = z[y * W + xr] - z[y * W + xl];
        const b = z[yd * W + x] - z[yu * W + x];
        gx[i] = Number.isFinite(a) ? a / 2 : 0;
        gy[i] = Number.isFinite(b) ? b / 2 : 0;
        if (i % 7 === 0) mags.push(Math.hypot(gx[i], gy[i]));
      }
    }
    mags.sort((p, r) => p - r);
    const g95 = mags[Math.floor(mags.length * 0.95)] || 1;
    const k = 1.2 / (g95 || 1);
    const L = [-0.55, -0.55, 0.63];
    const ln = Math.hypot(...L);
    const shade = new Float32Array(W * H);
    for (let i = 0; i < W * H; i++) {
      const nx = -gx[i] * k;
      const ny = -gy[i] * k;
      const n = Math.hypot(nx, ny, 1);
      const lam = (nx * L[0] + ny * L[1] + L[2]) / (n * ln);
      shade[i] = Math.min(Math.max(0.45 + 0.75 * (lam - L[2] / ln) + 0.55, 0.35), 1.25);
    }
    this.shadeData = shade;
  }

  renderImage() {
    if (!this.data || !this.meta) return;
    const W = this.lm.width;
    const H = this.lm.height;
    this.img.width = W;
    this.img.height = H;
    const ctx = this.img.getContext("2d");
    const im = ctx.createImageData(W, H);
    const px = im.data;
    const col = colorizer(this.meta, this.view);
    // 음영은 사용자가 이 필드에 켰을 때만 (높이 필드는 처음부터 켜짐). 범주 필드는 약하게.
    const sh = this.shadeOn() && this.shadeData ? this.shadeData : null;
    const shadeStrength = this.meta.kind === "categorical" ? 0.5 : 1.0;
    for (let i = 0; i < W * H; i++) {
      const c = col(this.data[i]);
      let f = 1;
      if (sh && Number.isFinite(this.data[i])) f = 1 + (sh[i] - 1) * shadeStrength;
      px[i * 4] = c[0] * f;
      px[i * 4 + 1] = c[1] * f;
      px[i * 4 + 2] = c[2] * f;
      px[i * 4 + 3] = 255;
    }
    // 겹쳐 그리기
    for (const o of this.lm.overlays) {
      if (!this.overlays[o.id]) continue;
      const d = this.overlayData[o.field];
      if (!d) continue;
      if (o.mode === "mask") {
        const c = hexRgb(o.color);
        for (let i = 0; i < W * H; i++) if (d[i] > 0) setPx(px, i, c);
      } else if (o.mode === "edge") {
        const c = hexRgb(o.color);
        for (let y = 0; y < H; y++)
          for (let x = 0; x < W; x++) {
            const i = y * W + x;
            const v = d[i];
            if ((x + 1 < W && d[i + 1] !== v) || (y + 1 < H && d[i + W] !== v)) setPx(px, i, c);
          }
      } else if (o.mode === "category") {
        const cats = new Map(((this.overlayMetaB && this.overlayMetaB.categories) || []).map((c) => [c.value, hexRgb(c.color)]));
        for (let i = 0; i < W * H; i++) {
          const v = d[i];
          if (v > 0 && cats.has(v)) setPx(px, i, cats.get(v));
        }
      }
    }
    ctx.putImageData(im, 0, 0);
    this.draw();
    this.drawLegend();
  }

  plotRect() {
    const cw = this.wrap.clientWidth || 800;
    const m = { l: 56, r: 12, t: 10, b: 34 };
    const pw = cw - m.l - m.r;
    const aspect = this.lm.height / this.lm.width;
    const ph = Math.min(pw * aspect, window.innerHeight * 0.72);
    return { x: m.l, y: m.t, w: pw, h: ph, cw, ch: ph + m.t + m.b };
  }

  fit() {
    if (!this.lm || !this.wrap) return;
    const r = this.plotRect();
    const s = Math.min(r.w / this.lm.width, r.h / this.lm.height);
    this.zoom = { s, ox: (r.w - this.lm.width * s) / 2, oy: (r.h - this.lm.height * s) / 2, s0: s };
    this.zoomed = false;
  }

  // 화면 점 (sx, sy) 를 가운데로 확대 배율 s 에 맞춤. anchor(데이터 픽셀)를 주면 그 점이 (sx, sy) 에 옴.
  zoomTo(sx, sy, s, anchor) {
    const [dx, dy] = anchor || this.toData(sx, sy);
    const s0 = this.zoom.s0 || this.zoom.s;
    const ns = Math.min(Math.max(s, s0), s0 * 32);
    const r = this.plotRect();
    this.zoom.s = ns;
    this.zoom.ox = sx - r.x - dx * ns;
    this.zoom.oy = sy - r.y - dy * ns;
    this.zoomed = true;
    this.draw();
  }

  // 데이터 픽셀 ↔ 화면
  toScreen(dx, dy) {
    const r = this.plotRect();
    return [r.x + this.zoom.ox + dx * this.zoom.s, r.y + this.zoom.oy + dy * this.zoom.s];
  }
  toData(sx, sy) {
    const r = this.plotRect();
    return [(sx - r.x - this.zoom.ox) / this.zoom.s, (sy - r.y - this.zoom.oy) / this.zoom.s];
  }
  // 데이터 픽셀 → 지도 좌표 (경도·위도 또는 동·북 km)
  dataToGeo(dx, dy) {
    const lm = this.lm;
    if (this.level === "planet") return [-180 + (dx / lm.width) * 360, 90 - (dy / lm.height) * 180];
    const e = lm.extent;
    return [e.x_km[0] + (dx / lm.width) * (e.x_km[1] - e.x_km[0]), e.y_km[1] - (dy / lm.height) * (e.y_km[1] - e.y_km[0])];
  }
  geoToData(gx, gy) {
    const lm = this.lm;
    if (this.level === "planet") return [((gx + 180) / 360) * lm.width, ((90 - gy) / 180) * lm.height];
    const e = lm.extent;
    return [((gx - e.x_km[0]) / (e.x_km[1] - e.x_km[0])) * lm.width, ((e.y_km[1] - gy) / (e.y_km[1] - e.y_km[0])) * lm.height];
  }

  draw() {
    if (!this.canvas || !this.lm || !this.zoom.s) return;
    const r = this.plotRect();
    const dpr = window.devicePixelRatio || 1;
    this.canvas.width = r.cw * dpr;
    this.canvas.height = r.ch * dpr;
    this.canvas.style.height = `${r.ch}px`;
    const ctx = this.canvas.getContext("2d");
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    ctx.clearRect(0, 0, r.cw, r.ch);
    ctx.save();
    ctx.beginPath();
    ctx.rect(r.x, r.y, r.w, r.h);
    ctx.clip();
    ctx.imageSmoothingEnabled = this.zoom.s < 1 && this.meta && this.meta.kind !== "categorical";
    const [x0, y0] = this.toScreen(0, 0);
    ctx.drawImage(this.img, x0, y0, this.lm.width * this.zoom.s, this.lm.height * this.zoom.s);
    // 사각형 (회랑)
    for (const rc of this.lm.rects || []) {
      const [ax, ay] = this.toScreen(...this.geoToData(rc.x0, rc.y1));
      const [bx, by] = this.toScreen(...this.geoToData(rc.x1, rc.y0));
      ctx.strokeStyle = rc.color;
      ctx.lineWidth = 2.5;
      ctx.strokeRect(ax, ay, bx - ax, by - ay);
      ctx.font = "600 12px system-ui, sans-serif";
      ctx.fillStyle = rc.color;
      ctx.fillText("회랑", ax + 3, ay - 4);
    }
    // 표시 (히어로 위치)
    for (const mk of this.lm.markers || []) {
      const [sx, sy] = this.toScreen(...this.geoToData(mk.lon, mk.lat));
      ctx.beginPath();
      ctx.arc(sx, sy, 7, 0, Math.PI * 2);
      ctx.fillStyle = "#ffd400";
      ctx.fill();
      ctx.lineWidth = 2;
      ctx.strokeStyle = "#111";
      ctx.stroke();
      ctx.font = "600 12px system-ui, sans-serif";
      ctx.lineWidth = 3;
      ctx.strokeStyle = "rgba(255,255,255,0.9)";
      ctx.strokeText(mk.label, sx + 10, sy + 4);
      ctx.fillStyle = "#111";
      ctx.fillText(mk.label, sx + 10, sy + 4);
    }
    // 고른 칸
    if (this.sel) {
      const [sx, sy] = this.toScreen(this.sel[0] + 0.5, this.sel[1] + 0.5);
      ctx.strokeStyle = "#ff2d55";
      ctx.lineWidth = 2;
      ctx.beginPath();
      ctx.arc(sx, sy, 9, 0, Math.PI * 2);
      ctx.stroke();
    }
    ctx.restore();
    // 축
    ctx.strokeStyle = cssVar("--line");
    ctx.strokeRect(r.x + 0.5, r.y + 0.5, r.w, r.h);
    ctx.fillStyle = cssVar("--muted");
    ctx.font = "11px system-ui, sans-serif";
    const [g0x, g0y] = this.dataToGeo(...this.toData(r.x, r.y + r.h));
    const [g1x, g1y] = this.dataToGeo(...this.toData(r.x + r.w, r.y));
    const stepX = niceStep((g1x - g0x) / 7);
    const stepY = niceStep((g1y - g0y) / 5);
    ctx.textAlign = "center";
    ctx.textBaseline = "top";
    for (const v of stepTicks(g0x, g1x, stepX, 40)) {
      const [sx] = this.toScreen(...this.geoToData(v, 0));
      ctx.fillRect(sx, r.y + r.h, 1, 4);
      ctx.fillText(this.level === "planet" ? `${fmtTick(v)}°` : fmtTick(v), sx, r.y + r.h + 6);
    }
    ctx.textAlign = "right";
    ctx.textBaseline = "middle";
    for (const v of stepTicks(g0y, g1y, stepY, 40)) {
      const [, sy] = this.toScreen(...this.geoToData(0, v));
      ctx.fillRect(r.x - 4, sy, 4, 1);
      ctx.fillText(this.level === "planet" ? `${fmtTick(v)}°` : fmtTick(v), r.x - 7, sy);
    }
    ctx.save();
    ctx.translate(12, r.y + r.h / 2);
    ctx.rotate(-Math.PI / 2);
    ctx.textAlign = "center";
    ctx.fillText(this.level === "planet" ? "위도 (°)" : "북 (km)", 0, 0);
    ctx.restore();
    ctx.textAlign = "center";
    ctx.textBaseline = "bottom";
    ctx.fillText(this.level === "planet" ? "경도 (°)" : "동 (km)", r.x + r.w / 2, r.ch - 1);
  }

  drawLegend() {
    const m = this.meta;
    const st = m.stats || {};
    const ff = st.finite_fraction;
    const gapNote = ff != null && ff < 1 ? "회색은 빈칸(값 없음)입니다." : "";
    const shadeNote =
      this.shadeOn() && this.shadeData && !this.isElevation()
        ? "음영을 켜서 실제 색이 색 막대·범례보다 어둡거나 밝을 수 있습니다. 정확한 값은 마우스를 올리거나 칸을 눌러 확인하세요."
        : "";
    this.catLegend.textContent = "";
    const cb = this.colorbar;
    if (m.kind === "categorical") {
      cb.parentElement.hidden = true;
      const counts = st.counts || {};
      const total = Object.values(counts).reduce((a, b) => a + b, 0) || 1;
      const cats = (m.categories || []).filter((c) => String(c.value) in counts);
      const shown = cats.slice(0, 40);
      for (const c of shown) this.catLegend.append(h("span", null, h("i", { style: `background:${c.color}` }), c.label, h("em", null, `${((100 * counts[String(c.value)]) / total).toFixed(1)}%`)));
      if (cats.length > shown.length) this.catLegend.append(h("span", { class: "hint" }, `… 외 ${cats.length - shown.length}개`));
      this.legendNote.textContent = [`범례: ${m.label}. 옆 숫자는 전체 칸 중 비율입니다.`, gapNote, shadeNote].filter(Boolean).join(" ");
      return;
    }
    cb.parentElement.hidden = false;
    const dpr = window.devicePixelRatio || 1;
    const W = cb.clientWidth || 600;
    const H = 46;
    cb.width = W * dpr;
    cb.height = H * dpr;
    const ctx = cb.getContext("2d");
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    ctx.clearRect(0, 0, W, H);
    const pad = 56;
    const bw = W - pad - 12;
    const col = colorizer(m, this.view);
    const { min, max, log } = this.view;
    // 로그 눈금은 0 < 아래 < 위 일 때만 그릴 수 있습니다 (아니면 log10 이 -∞·NaN).
    const logOk = log && Number.isFinite(min) && Number.isFinite(max) && min > 0 && max > min;
    const sp = terrainSplit(m);
    const twoSlope = !log && this.view.cmap === "terrain" && min < sp && max > sp;
    const toVal = (t) => {
      if (logOk) return Math.pow(10, Math.log10(min) + t * (Math.log10(max) - Math.log10(min)));
      if (twoSlope) return t < 0.5 ? min + (t / 0.5) * (sp - min) : sp + ((t - 0.5) / 0.5) * (max - sp);
      return min + t * (max - min);
    };
    for (let x = 0; x < bw; x++) {
      const c = col(toVal(x / (bw - 1)));
      ctx.fillStyle = `rgb(${c[0]},${c[1]},${c[2]})`;
      ctx.fillRect(pad + x, 4, 1.5, 14);
    }
    ctx.strokeStyle = cssVar("--line");
    ctx.strokeRect(pad + 0.5, 4.5, bw, 14);
    ctx.fillStyle = cssVar("--muted");
    ctx.font = "11px system-ui, sans-serif";
    ctx.textAlign = "right";
    ctx.textBaseline = "middle";
    ctx.fillText(m.unit || "값", pad - 8, 11);
    ctx.textAlign = "center";
    ctx.textBaseline = "top";
    const ticks = [];
    if (logOk) {
      const e0 = Math.ceil(Math.log10(min));
      const e1 = Math.floor(Math.log10(max));
      const every = Math.max(1, Math.ceil((e1 - e0 + 1) / 12)); // 자릿수가 많으면 건너뛰며 (많아야 12개쯤)
      for (let e = e0, k = 0; e <= e1 && k < 40; e += every, k++) ticks.push(Math.pow(10, e));
    } else if (!log) {
      if (twoSlope) {
        for (const v of stepTicks(min, sp, niceStep((sp - min) / 3))) if (v < sp) ticks.push(v);
        for (const v of stepTicks(sp, max, niceStep((max - sp) / 3))) ticks.push(v);
        if (sp !== 0) ticks.push(sp);
      } else ticks.push(...stepTicks(min, max, niceStep((max - min) / 6)));
    }
    for (const v of ticks) {
      let t;
      if (logOk) t = (Math.log10(v) - Math.log10(min)) / (Math.log10(max) - Math.log10(min));
      else if (twoSlope) t = v < sp ? 0.5 * ((v - min) / (sp - min)) : 0.5 + 0.5 * ((v - sp) / (max - sp));
      else t = (v - min) / (max - min);
      if (!(t >= -1e-9 && t <= 1 + 1e-9)) continue;
      const x = pad + t * bw;
      ctx.fillRect(x, 18, 1, 5);
      ctx.fillText(logOk ? `10^${Math.round(Math.log10(v))}` : fmtTick(v), x, 25);
    }
    const notes = [`색 막대: ${m.label} [${m.unit || "단위 없음"}]${logOk ? ", 로그 눈금" : ""}.`];
    if (log && !logOk) notes.push("로그 눈금 범위가 올바르지 않습니다 (아래 값이 0 보다 크고 위 값보다 작아야 함). '자동'을 누르거나 범위를 고치세요.");
    if (twoSlope) {
      if (NOT_SEA_LEVEL_FIELDS.has(m.name) && !Number.isFinite(m.split))
        notes.push("지형 색표는 0 m 에서 파랑과 초록을 나누지만, 이 필드는 해수면을 정하기 전 높이라 0 m 가 바닷가가 아닙니다 (이 실행의 해수면은 개요 탭에 있음). 파랑이 곧 바다라는 뜻이 아닙니다.");
      else if (sp !== 0)
        notes.push(`이 실행의 해수면(이 필드 기준 ${fmt(sp)} m) 아래는 바다 색(파랑), 위는 육지 색입니다. 막대 왼쪽 절반이 바다, 오른쪽 절반이 육지라 눈금 간격이 양쪽에서 다릅니다.`);
      else notes.push("0 m(해수면) 아래는 바다 색(파랑), 위는 육지 색입니다. 막대 왼쪽 절반이 바다, 오른쪽 절반이 육지라 눈금 간격이 양쪽에서 다릅니다.");
    }
    notes.push("범위 밖 값은 양 끝 색으로 칠합니다.", gapNote, shadeNote);
    this.legendNote.textContent = notes.filter(Boolean).join(" ");
  }

  // 데이터 픽셀 (ix, iy) 의 값 글 (말풍선·키보드 안내에 씀)
  cellInfo(ix, iy) {
    const W = this.lm.width;
    const v = this.data[iy * W + ix];
    const [gx, gy] = this.dataToGeo(ix + 0.5, iy + 0.5);
    const m = this.meta;
    let valText;
    if (m.kind === "categorical") {
      const c = (m.categories || []).find((x) => x.value === Math.round(v));
      valText = Number.isFinite(v) ? (c ? c.label : String(Math.round(v))) : "빈칸";
    } else valText = Number.isFinite(v) ? `${fmt(v)} ${m.unit}` : "빈칸 (값 없음)";
    const extra = [];
    for (const o of this.lm.overlays) {
      const d = this.overlayData[o.field];
      if (this.overlays[o.id] && d && d[iy * W + ix] > 0 && o.mode !== "edge") extra.push(o.label.split(" (")[0]);
    }
    const where = this.level === "planet" ? `위도 ${gy.toFixed(2)}°, 경도 ${gx.toFixed(2)}°` : `동 ${gx.toFixed(2)} km, 북 ${gy.toFixed(2)} km`;
    return { label: m.label, valText, extra, where };
  }

  hover(sx, sy, ev, tip) {
    const [dx, dy] = this.toData(sx, sy);
    const ix = Math.floor(dx);
    const iy = Math.floor(dy);
    if (!this.data || !this.meta || ix < 0 || iy < 0 || ix >= this.lm.width || iy >= this.lm.height) {
      tip.hidden = true;
      return;
    }
    const c = this.cellInfo(ix, iy);
    tip.textContent = "";
    tip.append(h("div", null, h("div", null, h("b", null, `${c.label}: `), c.valText), c.extra.length ? h("div", null, c.extra.join(" · ")) : null, h("div", { class: "t-sub" }, c.where)));
    tip.hidden = false;
    tip.style.left = `${Math.min(ev.clientX + 14, window.innerWidth - 330)}px`;
    tip.style.top = `${ev.clientY + 14}px`;
  }

  // 고른 칸이 화면 밖이면 그 칸이 가운데 오게 옮깁니다 (키보드로 칸을 옮길 때)
  keepVisible() {
    const r = this.plotRect();
    const [sx, sy] = this.toScreen(this.sel[0] + 0.5, this.sel[1] + 0.5);
    if (sx < r.x || sx > r.x + r.w || sy < r.y || sy > r.y + r.h) {
      this.zoom.ox = r.w / 2 - (this.sel[0] + 0.5) * this.zoom.s;
      this.zoom.oy = r.h / 2 - (this.sel[1] + 0.5) * this.zoom.s;
      this.zoomed = true;
    }
  }

  selectCell(ix, iy, probe) {
    this.sel = [ix, iy];
    this.draw();
    if (probe) this.probe(ix, iy);
  }

  bindCanvas() {
    const tip = $("#tooltip");
    const cv = this.canvas;
    // 마우스·펜·손가락을 pointer 이벤트 하나로 받습니다: 한 손가락(마우스)으로 끌면 옮기고,
    // 두 손가락으로 벌리거나 오므리면 확대·축소, 움직이지 않고 떼면 칸 정보.
    const pts = new Map(); // pointerId → [x, y] (캔버스 안 좌표)
    let drag = null;
    let pinch = null;
    const local = (ev) => {
      const rect = cv.getBoundingClientRect();
      return [ev.clientX - rect.left, ev.clientY - rect.top];
    };
    const two = () => {
      const [a, b] = [...pts.values()];
      return { mid: [(a[0] + b[0]) / 2, (a[1] + b[1]) / 2], d: Math.hypot(a[0] - b[0], a[1] - b[1]) || 1 };
    };
    cv.addEventListener("pointerdown", (ev) => {
      if (!this.lm || !this.zoom.s || (ev.pointerType === "mouse" && ev.button !== 0)) return;
      try {
        cv.setPointerCapture(ev.pointerId);
      } catch (e) {
        /* 캡처를 못 해도 끌기는 됩니다 */
      }
      const p = local(ev);
      pts.set(ev.pointerId, p);
      if (pts.size === 1) drag = { x: p[0], y: p[1], ox: this.zoom.ox, oy: this.zoom.oy, moved: false };
      else if (pts.size === 2) {
        const g = two();
        pinch = { d0: g.d, s0: this.zoom.s, anchor: this.toData(...g.mid) };
        drag = null;
      }
      tip.hidden = true;
    });
    cv.addEventListener("pointermove", (ev) => {
      if (!this.lm || !this.zoom.s) return;
      const p = local(ev);
      if (pts.has(ev.pointerId)) pts.set(ev.pointerId, p);
      if (pinch && pts.size >= 2) {
        const g = two();
        this.zoomTo(g.mid[0], g.mid[1], pinch.s0 * (g.d / pinch.d0), pinch.anchor);
        return;
      }
      if (drag && pts.has(ev.pointerId)) {
        const dx = p[0] - drag.x;
        const dy = p[1] - drag.y;
        if (!drag.moved && Math.abs(dx) + Math.abs(dy) > 3) drag.moved = true;
        if (drag.moved) {
          // 실제로 끌었을 때만 확대 상태로 봅니다 (그냥 누른 것은 확대를 잠그지 않음)
          this.zoom.ox = drag.ox + dx;
          this.zoom.oy = drag.oy + dy;
          this.zoomed = true;
          this.draw();
        }
        tip.hidden = true;
        return;
      }
      if (ev.pointerType === "touch") return;
      this.hover(p[0], p[1], ev, tip);
    });
    const end = (ev, cancelled) => {
      if (!pts.has(ev.pointerId)) return;
      const p = local(ev);
      pts.delete(ev.pointerId);
      if (pinch) {
        if (pts.size < 2) pinch = null;
        // 한 손가락이 남으면 그 손가락으로 이어서 옮깁니다 (칸 정보는 열지 않음)
        if (pts.size === 1) {
          const [r] = [...pts.values()];
          drag = { x: r[0], y: r[1], ox: this.zoom.ox, oy: this.zoom.oy, moved: true };
        } else drag = null;
        return;
      }
      const d = drag;
      drag = null;
      if (cancelled || !d || d.moved) return;
      const [dx, dy] = this.toData(p[0], p[1]);
      const ix = Math.floor(dx);
      const iy = Math.floor(dy);
      if (ix < 0 || iy < 0 || ix >= this.lm.width || iy >= this.lm.height) return;
      this.selectCell(ix, iy, true);
    };
    cv.addEventListener("pointerup", (ev) => end(ev, false));
    cv.addEventListener("pointercancel", (ev) => end(ev, true));
    cv.addEventListener("pointerleave", () => {
      if (!pts.size) tip.hidden = true;
    });
    cv.addEventListener(
      "wheel",
      (ev) => {
        if (!this.lm || !this.zoom.s) return;
        ev.preventDefault();
        const [sx, sy] = local(ev);
        this.zoomTo(sx, sy, this.zoom.s * Math.exp(-ev.deltaY * 0.0015));
      },
      { passive: false },
    );
    // 키보드: 화살표로 고른 칸 옮기기(Shift 는 10칸), Enter·Space 로 칸 정보, +/− 확대·축소, 0 전체 보기
    cv.addEventListener("keydown", (ev) => {
      if (!this.lm || !this.data || !this.zoom.s || ev.altKey || ev.ctrlKey || ev.metaKey) return;
      const W = this.lm.width;
      const H = this.lm.height;
      const step = ev.shiftKey ? 10 : 1;
      const mv = { ArrowLeft: [-step, 0], ArrowRight: [step, 0], ArrowUp: [0, -step], ArrowDown: [0, step] }[ev.key];
      const center = () => [Math.floor(W / 2), Math.floor(H / 2)];
      if (mv) {
        ev.preventDefault();
        const [x, y] = this.sel || center();
        const nx = this.sel ? Math.min(Math.max(x + mv[0], 0), W - 1) : x;
        const ny = this.sel ? Math.min(Math.max(y + mv[1], 0), H - 1) : y;
        this.sel = [nx, ny];
        this.keepVisible();
        this.draw();
        const c = this.cellInfo(nx, ny);
        this.kbInfo.textContent = `고른 칸 — ${c.label}: ${c.valText}${c.extra.length ? " · " + c.extra.join(" · ") : ""} (${c.where}). Enter 를 누르면 칸 정보를 엽니다.`;
      } else if (ev.key === "Enter" || ev.key === " ") {
        ev.preventDefault();
        const [x, y] = this.sel || center();
        this.selectCell(x, y, true);
      } else if (ev.key === "+" || ev.key === "=" || ev.key === "-" || ev.key === "_") {
        ev.preventDefault();
        const r = this.plotRect();
        const [sx, sy] = this.sel ? this.toScreen(this.sel[0] + 0.5, this.sel[1] + 0.5) : [r.x + r.w / 2, r.y + r.h / 2];
        const f = ev.key === "+" || ev.key === "=" ? 1.5 : 1 / 1.5;
        this.zoomTo(sx, sy, this.zoom.s * f);
      } else if (ev.key === "0") {
        ev.preventDefault();
        this.fit();
        this.draw();
      }
    });
  }

  async probe(px, py) {
    const box = this.probeBox;
    box.textContent = "";
    box.append(h("h3", null, "칸 정보"), h("p", { class: "hint" }, "불러오는 중…"));
    try {
      const r = await api(`/api/probe?${q({ run: this.run, level: this.level, px, py })}`);
      box.textContent = "";
      box.append(h("h3", null, "칸 정보"), h("p", { class: "where" }, r.where.label));
      if (this.level === "hero" && this.lm.factor > 1) box.append(h("p", { class: "hint" }, `지도 한 픽셀은 ${this.lm.factor}×${this.lm.factor}칸을 묶은 것이고, 여기 값은 그 가운데 칸입니다.`));
      for (const g of r.groups) {
        const tb = h("tbody");
        for (const it of g.items) {
          const pick = it.name !== "receiver";
          const label = [it.label, it.derived ? h("span", { class: "hint" }, " (계산)") : null];
          // 이름 칸을 단추로 두어 키보드(Tab·Enter)로도 그 필드를 지도에 그릴 수 있게 합니다.
          const nameCell = pick ? h("button", { type: "button", class: "row-btn" }, label) : label;
          const tr = h(
            "tr",
            { class: `${it.name === this.field ? "sel" : ""}${pick ? "" : " nopick"}`, title: pick ? `${it.name} — 누르면 이 필드를 지도에 그립니다` : it.name },
            h("td", null, nameCell),
            h("td", { class: "num" }, it.display),
            h("td", { class: "hint" }, it.unit || ""),
          );
          if (pick) tr.addEventListener("click", () => this.setField(it.name));
          tb.append(tr);
        }
        box.append(h("h4", null, g.name), h("table", null, tb));
      }
      if (r.strata) {
        const col = h("div", { class: "strata" });
        for (const s of r.strata) {
          const range = s.bedrock ? `${fmt(s.top_m)} m 아래 (기반암)` : s.top_m == null ? `${fmt(s.bottom_m)} m 위` : `${fmt(s.bottom_m)} ~ ${fmt(s.top_m)} m`;
          col.append(h("div", { class: `stratum ${s.state === "깎여 없어짐" ? "gone" : s.state === "지표에 드러남" ? "surface" : ""}`, title: s.state }, h("i", { style: `background:${s.color}` }), h("span", null, s.rock, h("span", { class: "hint" }, ` · ${s.state}`)), h("span", { class: "num hint" }, range)));
        }
        box.append(h("h4", null, "땅속 층 기둥 (위에서 아래로)"), col, h("p", { class: "hint" }, "층 바닥 고도 [m] 로 나눈 암석 층입니다. 지표보다 위에 있던 층은 깎여 없어졌고(줄 그음), 진하게 칠한 층이 지금 지표에 드러난 암석입니다."));
      }
    } catch (e) {
      box.textContent = "";
      box.append(h("h3", null, "칸 정보"), h("div", { class: "banner error" }, e.message));
    }
  }
}

function setPx(px, i, c) {
  px[i * 4] = c[0];
  px[i * 4 + 1] = c[1];
  px[i * 4 + 2] = c[2];
}
function fmtTick(v) {
  const a = Math.abs(v);
  if (a < 1e-9) return "0";
  if (a >= 1e5 || a < 1e-2) return v.toExponential(0).replace("e+", "e");
  return String(+v.toPrecision(4));
}

// ---------------------------------------------------------------- Godot
function updateGodotButtons(st) {
  const info = runInfo(S.run);
  const hasCor = !!(info && info.has && info.has.corridor);
  const busy = st ? st.busy : S.godotBusy;
  for (const id of ["#godot-play", "#godot-editor"]) {
    const b = $(id);
    b.disabled = !hasCor || !!busy;
    b.title = hasCor ? `${S.run} 의 회랑 파일을 engine/baked 로 복사하고 Godot 를 띄웁니다` : "고른 결과에 회랑(corridor/)이 없습니다";
  }
}

function renderGodotStatus(st) {
  const el = $("#godot-status");
  S.godotBusy = st.busy;
  el.className = "godot-status" + (st.state === "error" ? " error" : st.state === "launched" ? " ok" : "");
  const baked = st.baked ? `engine/baked: ${st.baked.run || "(이름 없음)"}` : "engine/baked: 비어 있음";
  let text;
  if (st.state === "idle") text = st.godot ? `Godot 있음 · ${baked}` : st.godot_message || "Godot 를 찾지 못했습니다";
  else text = `${st.message}${st.state === "launched" || st.state === "error" ? " · " + baked : ""}`;
  el.textContent = text;
  el.title = [st.message, ...(st.detail || []), st.godot ? `실행 파일: ${st.godot}` : st.godot_message].filter(Boolean).join("\n");
  updateGodotButtons(st);
}

async function pollGodot() {
  clearTimeout(S.godotTimer);
  try {
    const st = await api("/api/godot");
    renderGodotStatus(st);
    if (st.busy) S.godotTimer = setTimeout(pollGodot, 1000);
  } catch (e) {
    $("#godot-status").textContent = e.message;
  }
}

async function launchGodot(mode) {
  try {
    const st = await post("/api/godot/launch", { run: S.run, mode });
    renderGodotStatus(st);
    S.godotTimer = setTimeout(pollGodot, 800);
  } catch (e) {
    const el = $("#godot-status");
    el.className = "godot-status error";
    el.textContent = e.message;
  }
}

// ---------------------------------------------------------------- 시작
async function init() {
  const brand = $("#brand-logo");
  if (brand) logoImage(brand);
  S.meta = await api("/api/meta");
  $("#version").textContent = `bpcg ${S.meta.version}`;
  const planet = $("#planet");
  for (const p of S.meta.planets) planet.append(h("option", { value: p }, p));
  planet.value = store.get("planet", "earth");
  if (!planet.value) planet.value = S.meta.planets[0];
  const profile = $("#profile");
  for (const p of S.meta.profiles) profile.append(h("option", { value: p.name }, p.name));
  profile.value = store.get("profile", "tiny");
  if (!profile.value) profile.value = "tiny";
  $("#seed").value = store.get("seed", 0);
  $("#flat").checked = store.get("flat", false);
  $("#figures").checked = store.get("figures", true);
  planet.addEventListener("change", () => {
    store.set("planet", planet.value);
    loadSchema();
  });
  profile.addEventListener("change", () => {
    store.set("profile", profile.value);
    loadSchema();
  });
  $("#seed").addEventListener("change", () => {
    store.set("seed", Number($("#seed").value || 0));
    scheduleValidate();
  });
  $("#flat").addEventListener("change", () => store.set("flat", $("#flat").checked));
  $("#figures").addEventListener("change", () => store.set("figures", $("#figures").checked));
  $("#run-btn").addEventListener("click", startRun);
  $("#cancel-btn").addEventListener("click", cancelRun);
  $("#reset-all").addEventListener("click", () => {
    S.overrides = {};
    S.errors = {};
    store.set("overrides", {});
    for (const k of S.params.keys()) refreshParamRow(k);
    updateChangedCount();
    updateAreaDerived();
    applyParamFilter();
    scheduleValidate();
  });
  $("#param-search").addEventListener("input", applyParamFilter);
  $("#run-select").addEventListener("change", (e) => selectRun(e.target.value));
  // 새로 고침: 실행 목록과 함께 고른 실행의 개요·지도도 다시 읽습니다 (같은 이름으로 다시 만든 실행도
  // 새로 보이게. 지도는 /api/level 의 version 이 같으면 보던 화면을 그대로 둠).
  $("#runs-refresh").addEventListener("click", async () => {
    try {
      await loadRuns({ noSummary: true });
      for (const m of Object.values(S.maps)) m.stale = true;
      if (S.run) await loadSummary(true);
    } catch (e) {
      const msg = $("#run-msg");
      msg.className = "msg error";
      msg.textContent = `실행 목록을 읽지 못했습니다: ${e.message}`;
    }
  });
  for (const b of $$(".tab")) b.addEventListener("click", () => selectTab(b.dataset.tab));
  $("#godot-play").addEventListener("click", () => launchGodot("play"));
  $("#godot-editor").addEventListener("click", () => launchGodot("editor"));
  $("#lightbox .close").addEventListener("click", () => $("#lightbox").close());
  $("#lightbox").addEventListener("click", (e) => {
    if (e.target === $("#lightbox")) $("#lightbox").close();
  });

  await loadSchema();
  renderGodotStatus(S.meta.godot);
  await loadRuns();
  if (S.meta.active_job) {
    const key = `studio/${S.meta.active_job}`;
    selectRun(key, { keepTab: true });
    watchJob(S.meta.active_job, true);
  } else if (S.run) {
    const r = S.run;
    S.run = null;
    selectRun(r, { keepTab: true });
  }
  selectTab(S.tab);
}

init().catch((e) => {
  document.body.prepend(h("div", { class: "banner error", style: "margin: 12px" }, `스튜디오를 시작하지 못했습니다: ${e.message}`));
});
