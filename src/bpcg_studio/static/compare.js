/* 방법 비교 화면 (docs/compare.md 5장).
   - 왼쪽: 비교 묶음을 시드끼리 묶은 목록. 방법을 누르면 가운데에 그 결과를 봅니다.
   - 고도 그림과 과정 단계 그림은 한 번에 한 장만 요청합니다(그리는 중이면 마지막 요청만 기다림).
   - 연산 과정: 단계 칩·슬라이더·[ ] 키로 한 장씩, 아래에 반복 곡선과 단계별 막대. */
"use strict";

const $ = (id) => document.getElementById(id);
const FAMILY = { procedural: "절차적 생성", simulation: "시뮬레이션", example: "예제 기반", ours: "B-PCG" };
const st = {
  sets: [],
  name: null,
  data: null,
  seeds: [],
  seed: null,
  id: null,
  scale: "self",
  process: null,
  stage: 0,
  runCursor: null,
  runTimer: null,
};

function store(key, value) {
  try { localStorage.setItem("bpcg-compare-" + key, JSON.stringify(value)); } catch (_) { /* 저장 못 해도 됨 */ }
}
function recall(key, fallback) {
  try {
    const v = localStorage.getItem("bpcg-compare-" + key);
    return v === null ? fallback : JSON.parse(v);
  } catch (_) {
    return fallback;
  }
}

async function api(path, opts) {
  const r = await fetch(path, opts);
  const text = await r.text();
  let body = null;
  try { body = text ? JSON.parse(text) : null; } catch (_) { body = { error: text }; }
  if (!r.ok) throw new Error((body && body.error) || `HTTP ${r.status}`);
  return body;
}

function fmt(v, digits = 3) {
  if (v === null || v === undefined || Number.isNaN(v)) return "–";
  const a = Math.abs(v);
  if (a !== 0 && (a < 1e-3 || a >= 1e7)) return v.toExponential(2);
  return new Intl.NumberFormat("ko-KR", { maximumSignificantDigits: digits }).format(v);
}
function esc(s) {
  return String(s).replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
}
function q(params) {
  return Object.entries(params).map(([k, v]) => `${k}=${encodeURIComponent(v)}`).join("&");
}

/* ------------------------------------------------------------ 그림 한 장씩 */
const imgQueue = { busy: false, pending: new Map() };
function requestImage(img, loading, url, onDone) {
  imgQueue.pending.set(img.id, { img, loading, url, onDone });
  loading.hidden = false;
  pumpImages();
}
function pumpImages() {
  if (imgQueue.busy || imgQueue.pending.size === 0) return;
  const [key, job] = imgQueue.pending.entries().next().value;
  imgQueue.pending.delete(key);
  imgQueue.busy = true;
  const done = (ok) => {
    imgQueue.busy = false;
    if (!imgQueue.pending.has(key)) job.loading.hidden = true;
    if (job.onDone) job.onDone(ok);
    pumpImages();
  };
  const probe = new Image();
  probe.onload = () => { job.img.src = job.url; done(true); };
  probe.onerror = () => { job.loading.textContent = "그림을 만들지 못했습니다"; done(false); };
  job.loading.textContent = "그리는 중…";
  probe.src = job.url;
}

/* ------------------------------------------------------------ 묶음 */
async function loadSets(keep) {
  const res = await api("/api/compare/sets");
  st.sets = res.sets;
  const sel = $("set-select");
  sel.innerHTML = "";
  for (const s of res.sets) {
    const o = document.createElement("option");
    o.value = s.name;
    o.textContent = `${s.name} (${s.grid ? s.grid.n + "²" : "?"}, 시드 ${s.seeds.length}, 방법 ${s.methods.length})`;
    sel.appendChild(o);
  }
  const cfg = $("run-config");
  cfg.innerHTML = res.configs.map((c) => `<option value="${esc(c)}">${esc(c)}</option>`).join("");
  showRun(res.run);
  const want = keep || recall("set", null);
  const pick = res.sets.find((s) => s.name === want) || res.sets[0];
  $("empty").hidden = !!pick;
  if (pick) {
    sel.value = pick.name;
    await loadSet(pick.name);
  } else {
    for (const id of ["view", "table-card", "process-card"]) $(id).hidden = true;
    $("groups").innerHTML = '<p class="hint">아직 비교 결과가 없습니다.</p>';
  }
}

async function loadSet(name) {
  st.name = name;
  store("set", name);
  st.data = await api(`/api/compare/set?${q({ name })}`);
  st.seeds = st.data.groups.map((g) => g.seed);
  const g = st.data.config.grid || {};
  const info = $("set-info");
  info.hidden = false;
  info.textContent = `${g.n}×${g.n}, ${g.dx} m (한 변 ${fmt((g.n * g.dx) / 1000)} km)`;
  renderGroups();
  const seed = st.seeds.includes(recall("seed", null)) ? recall("seed", null) : st.seeds[0];
  const entries = group(seed);
  const id = entries.some((e) => e.id === recall("id", null)) ? recall("id", null) : entries[0] && entries[0].id;
  if (seed !== undefined && id) await select(seed, id);
}

function group(seed) {
  const g = st.data.groups.find((x) => x.seed === seed);
  return g ? g.entries : [];
}
function entry(seed, id) {
  return group(seed).find((e) => e.id === id) || null;
}

function renderGroups() {
  const box = $("groups");
  box.innerHTML = "";
  for (const g of st.data.groups) {
    const sec = document.createElement("section");
    sec.className = "cmp-group";
    const total = g.entries.reduce((a, e) => a + (e.seconds || 0), 0);
    sec.innerHTML = `<h3><span>시드 ${g.seed}</span><span class="hint">${g.entries.length}개 · ${fmt(total)} s</span></h3>`;
    const ul = document.createElement("ul");
    for (const e of g.entries) {
      const li = document.createElement("li");
      const b = document.createElement("button");
      b.type = "button";
      b.className = "cmp-item";
      b.dataset.seed = g.seed;
      b.dataset.id = e.id;
      b.title = `${FAMILY[e.family] || e.family} · ${e.ref}`;
      b.innerHTML = `<span class="fam-dot fam-${esc(e.family)}"></span><span>${esc(e.id)}</span><span class="t">${fmt(e.seconds)} s</span>`;
      b.addEventListener("click", () => select(g.seed, e.id));
      li.appendChild(b);
      ul.appendChild(li);
    }
    sec.appendChild(ul);
    box.appendChild(sec);
  }
}

/* ------------------------------------------------------------ 고르기 */
async function select(seed, id) {
  st.seed = seed;
  st.id = id;
  store("seed", seed);
  store("id", id);
  for (const b of document.querySelectorAll(".cmp-item")) {
    b.setAttribute("aria-current", String(Number(b.dataset.seed) === seed && b.dataset.id === id));
  }
  const e = entry(seed, id);
  if (!e) return;
  for (const cid of ["view", "table-card", "process-card"]) $(cid).hidden = false;
  $("v-title").innerHTML = `<span class="fam-chip ${esc(e.family)}">${esc(FAMILY[e.family] || e.family)}</span>${esc(e.label)} <span class="hint">· ${esc(e.id)} · 시드 ${seed}</span>`;
  $("v-sub").textContent = e.ref;
  const facts = [
    ["생성 시간", `${fmt(e.seconds)} s` + (e.wall_seconds && e.wall_seconds - e.seconds > 0.01 ? ` (기록 포함 ${fmt(e.wall_seconds)} s)` : "")],
    ["고도", `${fmt(e.z_min)} ~ ${fmt(e.z_max)} m`],
    ["기복", `${fmt(e.metrics.relief_m)} m`],
    ["과정 단계", `${e.stages || 0}장`],
    ["만든 때", e.created || "–"],
  ];
  $("v-facts").innerHTML = facts.map(([k, v]) => `<dt>${esc(k)}</dt><dd>${esc(v)}</dd>`).join("");
  $("v-params").textContent = JSON.stringify({ params: e.params, info: e.info }, null, 1);
  showElevation();
  renderTable();
  await loadProcess();
}

function showElevation() {
  const e = entry(st.seed, st.id);
  if (!e) return;
  const url = `/api/compare/elevation.png?${q({ name: st.name, seed: st.seed, id: st.id, scale: st.scale, v: e.z_key || "" })}`;
  requestImage($("elev"), $("elev-loading"), url);
  let lo = e.z_min;
  let hi = e.z_max;
  if (st.scale === "group") {
    lo = Math.min(...group(st.seed).map((x) => x.z_min));
    hi = Math.max(...group(st.seed).map((x) => x.z_max));
  }
  $("elev-legend").innerHTML = gradientLegend(st.data.elevation_colors, lo, hi, "고도 m");
}

function gradientLegend(colors, lo, hi, unit) {
  return `<span>${esc(unit)}</span><div class="bar" style="background:linear-gradient(90deg,${colors.join(",")})"></div>`
    + `<div class="ends"><span>${fmt(lo)}</span><span>${fmt(hi)}</span></div>`;
}

/* ------------------------------------------------------------ 지표 표 */
function renderTable() {
  const defs = st.data.metrics;
  const rows = group(st.seed);
  $("t-title").textContent = `지표 · 시드 ${st.seed}`;
  let html = "<thead><tr><th title='인스턴스 id'>방법</th><th title='과정 기록을 뺀 생성 시간 (첫 컴파일은 미리 돌려 뺌)'>시간<br><span class='unit'>s</span></th>";
  for (const m of defs) {
    html += `<th title="${esc(m.help)}">${esc(m.label)}${m.unit ? `<br><span class="unit">${esc(m.unit)}</span>` : ""}</th>`;
  }
  html += "</tr></thead><tbody>";
  for (const e of rows) {
    html += `<tr data-id="${esc(e.id)}" aria-current="${e.id === st.id}"><td><span class="fam-dot fam-${esc(e.family)}" style="display:inline-block;margin-right:6px"></span>${esc(e.id)}</td><td>${fmt(e.seconds)}</td>`;
    for (const m of defs) html += `<td>${fmt(e.metrics[m.name])}</td>`;
    html += "</tr>";
  }
  html += "</tbody>";
  const t = $("m-table");
  t.innerHTML = html;
  for (const tr of t.querySelectorAll("tbody tr")) {
    tr.addEventListener("click", () => select(st.seed, tr.dataset.id));
  }
}

/* ------------------------------------------------------------ 연산 과정 */
async function loadProcess() {
  const key = `${st.name}|${st.seed}|${st.id}`;
  st.processKey = key;
  let pr;
  try {
    pr = await api(`/api/compare/process?${q({ name: st.name, seed: st.seed, id: st.id })}`);
  } catch (err) {
    pr = { stages: [], series: [], bars: [], error: String(err) };
  }
  if (st.processKey !== key) return; // 그사이 다른 것을 고름
  st.process = pr;
  const has = pr.stages.length > 0;
  $("p-empty").hidden = has || pr.series.length > 0;
  for (const id of ["st-chips", "st-slider"]) $(id).hidden = !has;
  $("stage").closest(".cmp-figure").hidden = !has;
  const chips = $("st-chips");
  chips.innerHTML = "";
  pr.stages.forEach((s, i) => {
    const b = document.createElement("button");
    b.type = "button";
    b.setAttribute("role", "tab");
    b.textContent = s.label;
    b.addEventListener("click", () => showStage(i));
    chips.appendChild(b);
  });
  $("st-slider").max = Math.max(0, pr.stages.length - 1);
  renderCharts(pr);
  if (has) showStage(Math.min(st.stage, pr.stages.length - 1));
}

function showStage(i) {
  const pr = st.process;
  if (!pr || !pr.stages.length) return;
  i = Math.max(0, Math.min(pr.stages.length - 1, i));
  st.stage = i;
  const s = pr.stages[i];
  [...$("st-chips").children].forEach((b, k) => b.setAttribute("aria-selected", String(k === i)));
  $("st-slider").value = i;
  $("st-count").textContent = `${i + 1} / ${pr.stages.length}`;
  $("st-label").textContent = s.label;
  $("st-kind").textContent = s.kind_label + (s.unit ? ` · ${s.unit}` : "");
  $("st-note").textContent = s.note || "";
  $("st-legend").innerHTML = stageLegend(s);
  const url = `/api/compare/stage.png?${q({ name: st.name, seed: st.seed, id: st.id, i, v: entry(st.seed, st.id).z_key || "" })}`;
  requestImage($("stage"), $("stage-loading"), url);
}

function stageLegend(s) {
  const lg = s.legend || {};
  if (lg.colors) return gradientLegend(lg.colors, lg.vmin, lg.vmax, s.unit || "값");
  if (lg.swatches) {
    const names = { 1: "1", 2: "2", 3: "3" };
    return Object.entries(lg.swatches).map(([k, c]) => `<span class="sw"><i style="background:${esc(c)}"></i>${esc(names[k] || k)}</span>`).join("")
      + '<span class="hint">회색 음영 = 최종 고도</span>';
  }
  if (lg.categorical) return '<span>번호마다 다른 색 (회색 = 없음)</span>';
  return "";
}

function renderCharts(pr) {
  const box = $("charts");
  box.innerHTML = "";
  for (const s of pr.series) box.appendChild(lineChart(s));
  for (const b of pr.bars) box.appendChild(barChart(b));
}

function lineChart(s) {
  const div = document.createElement("div");
  div.className = "cmp-chart";
  const W = 320;
  const H = 150;
  const L = 46;
  const B = 24;
  const xs = s.x;
  const ys = s.y;
  const pos = ys.filter((v) => v > 0);
  const logY = pos.length === ys.length && pos.length > 1 && Math.max(...pos) / Math.min(...pos) > 1e3;
  const ty = (v) => (logY ? Math.log10(v) : v);
  const tys = ys.map(ty);
  let y0 = Math.min(...tys);
  let y1 = Math.max(...tys);
  if (y1 === y0) { y1 = y0 + 1; }
  const x0 = Math.min(...xs);
  let x1 = Math.max(...xs);
  if (x1 === x0) x1 = x0 + 1;
  const px = (x) => L + ((x - x0) / (x1 - x0)) * (W - L - 8);
  const py = (y) => 8 + (1 - (y - y0) / (y1 - y0)) * (H - B - 8);
  const step = Math.max(1, Math.floor(xs.length / 800));
  let d = "";
  for (let k = 0; k < xs.length; k += step) d += `${k ? "L" : "M"}${px(xs[k]).toFixed(1)},${py(tys[k]).toFixed(1)}`;
  const yl = (v) => (logY ? fmt(10 ** v) : fmt(v));
  div.innerHTML = `<h4>${esc(s.label)}${s.unit ? ` <span class="hint">${esc(s.unit)}${logY ? ", 로그 축" : ""}</span>` : ""}</h4>`
    + `<svg viewBox="0 0 ${W} ${H}" role="img" aria-label="${esc(s.label)}">`
    + `<line class="axis" x1="${L}" y1="${H - B}" x2="${W - 8}" y2="${H - B}"/><line class="axis" x1="${L}" y1="8" x2="${L}" y2="${H - B}"/>`
    + `<path class="line" d="${d}"/>`
    + `<text x="${L - 4}" y="12" text-anchor="end">${yl(y1)}</text><text x="${L - 4}" y="${H - B}" text-anchor="end">${yl(y0)}</text>`
    + `<text x="${L}" y="${H - 8}">${fmt(x0)}</text><text x="${W - 8}" y="${H - 8}" text-anchor="end">${fmt(x1)}</text>`
    + `<text x="${(L + W) / 2}" y="${H - 8}" text-anchor="middle">${esc(s.xlabel || "")}</text></svg>`
    + `<span class="hint">처음 ${fmt(ys[0])} → 끝 ${fmt(ys[ys.length - 1])}</span>`;
  return div;
}

function barChart(b) {
  const div = document.createElement("div");
  div.className = "cmp-chart";
  const max = Math.max(...b.items.map((it) => Math.abs(it.value)), 1e-12);
  div.innerHTML = `<h4>${esc(b.label)}${b.unit ? ` <span class="hint">${esc(b.unit)}</span>` : ""}</h4>`
    + `<div class="cmp-bars">${b.items.map((it) => `<div class="row"><span class="lbl" title="${esc(it.label)}">${esc(it.label)}</span>`
      + `<div><div class="b" style="width:${((Math.abs(it.value) / max) * 100).toFixed(1)}%"></div></div><span class="v">${fmt(it.value)}</span></div>`).join("")}</div>`;
  return div;
}

/* ------------------------------------------------------------ 옮겨 다니기 */
function moveMethod(d) {
  const ids = group(st.seed).map((e) => e.id);
  const k = ids.indexOf(st.id);
  if (k < 0 || !ids.length) return;
  select(st.seed, ids[(k + d + ids.length) % ids.length]);
}
function moveSeed(d) {
  const k = st.seeds.indexOf(st.seed);
  if (k < 0 || !st.seeds.length) return;
  const seed = st.seeds[(k + d + st.seeds.length) % st.seeds.length];
  const ids = group(seed).map((e) => e.id);
  select(seed, ids.includes(st.id) ? st.id : ids[0]);
}

/* ------------------------------------------------------------ 실행 */
function showRun(run) {
  if (!run) return;
  const running = run.status === "running";
  $("run-start").disabled = running;
  $("run-cancel").hidden = !running;
  const prog = $("run-progress");
  if (run.progress) {
    prog.hidden = false;
    $("run-bar").style.width = `${(100 * run.progress[0]) / run.progress[1]}%`;
  } else {
    prog.hidden = !running;
  }
  const label = { idle: "", running: "실행 중", done: "끝났습니다", failed: "실패했습니다", cancelled: "멈췄습니다" };
  $("run-msg").textContent = label[run.status] ? `${label[run.status]}${run.name ? ` (${run.name})` : ""}` : "";
  $("run-msg").className = "msg" + (run.status === "failed" ? " error" : run.status === "done" ? " ok" : "");
  if (running && !st.runTimer) pollRun();
}

async function pollRun() {
  st.runTimer = null;
  const since = st.runCursor === null ? 0 : st.runCursor;
  let run;
  try {
    run = await api(`/api/compare/run?${q({ since })}`);
  } catch (err) {
    $("run-msg").textContent = String(err);
    return;
  }
  st.runCursor = run.cursor;
  const log = $("run-log");
  if (run.lines.length) {
    log.hidden = false;
    log.textContent += run.lines.join("\n") + "\n";
    log.scrollTop = log.scrollHeight;
  }
  showRun(run);
  if (run.status === "running") {
    st.runTimer = setTimeout(pollRun, 1000);
  } else if (run.status === "done") {
    await loadSets(run.name);
  }
}

async function startRun() {
  const body = {
    config: $("run-config").value,
    name: $("run-name").value.trim() || undefined,
    seeds: $("run-seeds").value.trim(),
    only: $("run-only").value.split(",").map((s) => s.trim()).filter(Boolean),
    n: $("run-n").value,
    dx: $("run-dx").value,
    force: $("run-force").checked,
  };
  $("run-log").textContent = "";
  st.runCursor = 0;
  try {
    const run = await api("/api/compare/run", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(body),
    });
    showRun(run);
  } catch (err) {
    $("run-msg").textContent = String(err);
    $("run-msg").className = "msg error";
  }
}

/* ------------------------------------------------------------ 시작 */
function bind() {
  $("set-select").addEventListener("change", (ev) => loadSet(ev.target.value));
  $("sets-refresh").addEventListener("click", () => loadSets(st.name));
  for (const b of document.querySelectorAll(".cmp-seg .chip-btn")) {
    b.addEventListener("click", () => {
      st.scale = b.dataset.scale;
      store("scale", st.scale);
      for (const x of document.querySelectorAll(".cmp-seg .chip-btn")) x.setAttribute("aria-pressed", String(x === b));
      showElevation();
    });
  }
  $("prev-m").addEventListener("click", () => moveMethod(-1));
  $("next-m").addEventListener("click", () => moveMethod(1));
  $("prev-s").addEventListener("click", () => moveSeed(-1));
  $("next-s").addEventListener("click", () => moveSeed(1));
  $("prev-st").addEventListener("click", () => showStage(st.stage - 1));
  $("next-st").addEventListener("click", () => showStage(st.stage + 1));
  $("st-slider").addEventListener("input", (ev) => showStage(Number(ev.target.value)));
  $("run-start").addEventListener("click", startRun);
  $("run-cancel").addEventListener("click", async () => {
    try { showRun(await api("/api/compare/cancel", { method: "POST", headers: { "Content-Type": "application/json" }, body: "{}" })); } catch (err) { $("run-msg").textContent = String(err); }
  });
  document.addEventListener("keydown", (ev) => {
    if (ev.target.closest("input, select, textarea") || ev.metaKey || ev.ctrlKey || ev.altKey) return;
    const map = { ArrowLeft: () => moveMethod(-1), ArrowRight: () => moveMethod(1), ArrowUp: () => moveSeed(-1), ArrowDown: () => moveSeed(1), "[": () => showStage(st.stage - 1), "]": () => showStage(st.stage + 1) };
    if (map[ev.key] && st.id) {
      ev.preventDefault();
      map[ev.key]();
    }
  });
  st.scale = recall("scale", "self") === "group" ? "group" : "self";
  for (const x of document.querySelectorAll(".cmp-seg .chip-btn")) x.setAttribute("aria-pressed", String(x.dataset.scale === st.scale));
}

bind();
loadSets().catch((err) => {
  $("empty").hidden = false;
  $("empty").textContent = `비교 목록을 읽지 못했습니다: ${err}`;
});
