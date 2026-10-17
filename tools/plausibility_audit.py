"""Audit saved B-PCG Planet/Hero bundles and emit metrics plus quick-look maps.

Example:
  uv run python tools/plausibility_audit.py --runs <seed-run-dirs...> --out out/plausibility
The generator remains unchanged; this read-only tool runs after bundle creation.
"""

from __future__ import annotations

import argparse
import json
from pathlib import Path
from typing import Any

import matplotlib.pyplot as plt
import numpy as np

try:
    from numba import njit
except ImportError:  # pragma: no cover - numba is a project dependency
    njit = None


FIELDS = {
    "elevation": ("surface/z_m.npy", "elevation_m"),
    "plate": ("plates/plate_id.npy", "plate_id"),
    "boundary": ("plates/boundary_type.npy", "boundary_type"),
    "uplift": ("plates/uplift_m_per_yr.npy", "uplift_m_per_yr"),
    "precipitation": ("climate/precip_m_per_yr.npy", "precip_m_per_yr"),
    "runoff": ("climate/runoff_eff_m_per_yr.npy", "runoff_eff_m_per_yr"),
    "discharge": ("surface/discharge_m3_per_yr.npy", "discharge_m3_per_yr"),
    "river": ("water/is_river.npy", "is_river"),
    "lake": ("water/is_lake.npy", "is_lake"),
    "geology": ("geology/surface_rock.npy", "surface_rock"),
    "slope": ("surface/slope.npy", "slope"),
    "relief": ("surface/relief_m.npy", "relief_m"),
    "temperature": ("climate/temperature_c.npy", "temperature_c"),
    "drainage_area": ("surface/drainage_area_m2.npy", "drainage_area_m2"),
    "cave_level_0": ("subsurface/cave_level_0_m.npy", "cave_level_0_m"),
    "is_ocean": ("plates/is_ocean.npy", "is_ocean"),
    "receiver": ("surface/receiver.npy", "receiver"),
    "runoff_raw": ("climate/runoff_m_per_yr.npy", "runoff_m_per_yr"),
    "water_level": ("water/water_level_m.npy", "water_level_m"),
}


def _cycle_counter():
    if njit is None:
        return None

    @njit(cache=True)
    def count_processed(receiver: np.ndarray) -> int:
        n = receiver.size
        indegree = np.zeros(n, dtype=np.int64)
        for c in range(n):
            r = receiver[c]
            if r != c:
                indegree[r] += 1
        queue = np.empty(n, dtype=np.int64)
        tail = 0
        for c in range(n):
            if indegree[c] == 0:
                queue[tail] = c
                tail += 1
        head = 0
        processed = 0
        while head < tail:
            c = queue[head]
            head += 1
            processed += 1
            r = receiver[c]
            if r != c:
                indegree[r] -= 1
                if indegree[r] == 0:
                    queue[tail] = r
                    tail += 1
        return processed

    return count_processed


COUNT_PROCESSED = _cycle_counter()


def load_fields(world: Path) -> tuple[dict[str, np.ndarray], dict[str, Any]]:
    manifest = json.loads((world / "manifest.json").read_text(encoding="utf-8"))
    fields: dict[str, np.ndarray] = {}
    for name, (relative, _) in FIELDS.items():
        path = world / relative
        if path.exists():
            fields[name] = np.load(path, mmap_mode="r", allow_pickle=False)
    return fields, manifest


def finite_summary(values: np.ndarray) -> dict[str, float | int]:
    numeric = np.asarray(values, dtype=np.float64)
    finite = numeric[np.isfinite(numeric)]
    if finite.size == 0:
        return {"finite_count": 0, "count": int(values.size)}
    p = np.percentile(finite, [1, 50, 90, 99, 99.9])
    return {
        "finite_count": int(finite.size),
        "count": int(values.size),
        "min": float(finite.min()),
        "p01": float(p[0]),
        "p50": float(p[1]),
        "p90": float(p[2]),
        "p99": float(p[3]),
        "p999": float(p[4]),
        "max": float(finite.max()),
    }


def correlation(a: np.ndarray, b: np.ndarray, mask: np.ndarray | None = None) -> float | None:
    if mask is None:
        mask = np.ones(a.size, dtype=bool)
    else:
        mask = np.asarray(mask, dtype=bool).reshape(-1)
    av = np.asarray(a).reshape(-1)
    bv = np.asarray(b).reshape(-1)
    valid = mask & np.isfinite(av) & np.isfinite(bv)
    if valid.sum() < 3:
        return None
    aa = av[valid].astype(np.float64, copy=False)
    bb = bv[valid].astype(np.float64, copy=False)
    if aa.std() == 0 or bb.std() == 0:
        return None
    return float(np.corrcoef(aa, bb)[0, 1])


def world_metrics(
    world: Path, fields: dict[str, np.ndarray], manifest: dict[str, Any]
) -> dict[str, Any]:
    result: dict[str, Any] = {
        "path": str(world),
        "kind": manifest.get("kind"),
        "seed": manifest.get("seed"),
        "profile": manifest.get("profile"),
        "fields": {},
        "checks": {},
        "causal_correlations": {},
    }
    for key, values in fields.items():
        if key != "receiver":
            result["fields"][key] = finite_summary(np.asarray(values))

    n = int(manifest.get("graph", {}).get("n_cells", 0))
    receiver = fields.get("receiver")
    if receiver is not None:
        r = np.asarray(receiver).reshape(-1).astype(np.int64, copy=False)
        valid = (r >= 0) & (r < r.size)
        self_sinks = r == np.arange(r.size)
        result["checks"]["receiver_all_indices_valid"] = bool(valid.all())
        result["checks"]["self_receiver_cells"] = int(self_sinks.sum())
        result["checks"]["self_receiver_land_cells"] = int(
            np.sum(
                self_sinks
                & ~np.asarray(fields.get("is_ocean", np.zeros(r.size)), dtype=bool).reshape(-1)
            )
        )
        if COUNT_PROCESSED is not None and valid.all():
            processed = COUNT_PROCESSED(r)
            result["checks"]["nonself_cycle_cells"] = int(r.size - processed)
        else:
            result["checks"]["nonself_cycle_cells"] = None

    if "river" in fields:
        result["checks"]["river_cells"] = int(np.asarray(fields["river"], dtype=bool).sum())
    if "lake" in fields:
        lake = np.asarray(fields["lake"], dtype=bool).reshape(-1)
        result["checks"]["lake_cells"] = int(lake.sum())
        lake_diag: dict[str, Any] = {
            "water_balance": "unmodeled; static depression fill has no storage/evaporation balance",
            "estimated_loss_evaporation_m3_per_yr": None,
        }
        if receiver is not None and "discharge" in fields and "elevation" in fields:
            r = np.asarray(receiver).reshape(-1).astype(np.int64, copy=False)
            q = np.asarray(fields["discharge"], dtype=np.float64).reshape(-1)
            z = np.asarray(fields["elevation"], dtype=np.float64).reshape(-1)
            valid_receiver = (r >= 0) & (r < r.size)
            lake_in = valid_receiver & ~lake & lake[np.clip(r, 0, max(r.size - 1, 0))]
            receiver_lake = lake[np.clip(r, 0, max(r.size - 1, 0))]
            lake_out = valid_receiver & lake & ~receiver_lake & (r != np.arange(r.size))
            lake_diag["estimated_inflow_m3_per_yr"] = float(q[lake_in].sum())
            lake_diag["estimated_outflow_m3_per_yr"] = float(q[lake_out].sum())
            lake_diag["spill_elevation_m"] = (
                float(np.nanmax(np.asarray(fields["water_level"], dtype=np.float64)[lake]))
                if lake.any() and "water_level" in fields
                else None
            )
            graph_path = world / manifest.get("graph", {}).get("file", "graph.npz")
            if lake.any() and graph_path.exists() and "water_level" in fields:
                with np.load(graph_path) as archive:
                    area = archive["area"].reshape(-1).astype(np.float64, copy=False)
                level = np.asarray(fields["water_level"], dtype=np.float64).reshape(-1)
                lake_diag["estimated_filled_storage_m3"] = float(
                    (np.maximum(level[lake] - z[lake], 0.0) * area[lake]).sum()
                )
            else:
                lake_diag["estimated_filled_storage_m3"] = None
        result["checks"]["lake_diagnostics"] = lake_diag
    if "cave_level_0" in fields:
        caves = np.asarray(fields["cave_level_0"])
        result["checks"]["cave_cells_level_0"] = int(np.isfinite(caves).sum())
    meta = manifest.get("meta", {})
    diag = (
        meta.get("diag", manifest.get("diag", {}))
        if isinstance(meta, dict)
        else manifest.get("diag", {})
    )
    solver = diag.get("solver", {}) if isinstance(diag, dict) else {}
    if isinstance(solver, dict):
        result["checks"]["solver_iterations"] = solver.get("iterations")
        result["checks"]["solver_converged"] = solver.get("converged")
    if "uplift" in fields and "relief" in fields:
        result["causal_correlations"]["uplift_vs_relief"] = correlation(
            fields["uplift"], fields["relief"]
        )
    if "precipitation" in fields and "runoff" in fields:
        result["causal_correlations"]["precipitation_vs_runoff"] = correlation(
            fields["precipitation"], fields["runoff"]
        )
    if "runoff" in fields and "discharge" in fields:
        result["causal_correlations"]["runoff_vs_log_discharge"] = correlation(
            fields["runoff"], np.log1p(np.maximum(fields["discharge"], 0.0))
        )
    if "temperature" in fields:
        graph = manifest.get("graph", {})
        if graph.get("kind") == "sphere" and "elevation" in fields:
            z = np.asarray(fields["elevation"]).reshape(-1)
            # Latitude is derived from the stored cube-sphere graph positions when available.
            graph_path = world / graph.get("file", "graph.npz")
            if graph_path.exists():
                with np.load(graph_path) as archive:
                    pos = archive["pos"]
                lat = np.degrees(np.arcsin(np.clip(pos[:, 2], -1.0, 1.0)))
                result["causal_correlations"]["temperature_vs_abs_latitude"] = correlation(
                    fields["temperature"], np.abs(lat)
                )
                result["causal_correlations"]["temperature_vs_elevation_land"] = correlation(
                    fields["temperature"], z, z > 0
                )
    if "precipitation" in fields and "elevation" in fields:
        result["causal_correlations"]["precipitation_vs_elevation_land"] = correlation(
            fields["precipitation"], fields["elevation"], np.asarray(fields["elevation"]) > 0
        )
    graph_path = world / manifest.get("graph", {}).get("file", "graph.npz")
    if graph_path.exists() and receiver is not None and "discharge" in fields:
        with np.load(graph_path) as archive:
            area = archive["area"].reshape(-1).astype(np.float64, copy=False)
        runoff = fields.get("runoff")
        if runoff is not None and area.size == n:
            source = area * np.asarray(runoff, dtype=np.float64).reshape(-1)
            q = np.asarray(fields["discharge"], dtype=np.float64).reshape(-1)
            # On Planet, local source terms and the receiver network fully determine q.
            # Hero bundles also include parent inflow, whose full cell array is currently
            # diagnostic-only and summarized in the manifest, so assess local source lower bound.
            if manifest.get("kind") == "planet":
                expected = source.copy()
                np.add.at(expected, r[~self_sinks], q[~self_sinks])
                denom = np.maximum(np.maximum(np.abs(expected), np.abs(q)), 1.0)
                result["checks"]["discharge_conservation_max_relative_error"] = float(
                    np.max(np.abs(expected - q) / denom)
                )
                tolerance = np.maximum(1e-8, np.abs(source) * 1e-6)
                result["checks"]["discharge_ge_local_runoff"] = bool(
                    np.all(q + tolerance >= source)
                )
            elif isinstance(diag, dict):
                boundary = diag.get("boundary", {})
                if isinstance(boundary, dict):
                    actual_inflow = float(boundary.get("inflow_m3_per_yr", 0.0))
                    l0_q = float(boundary.get("l0_discharge_m3_per_yr", 0.0))
                    local_runoff_total = float(source.sum())
                    expected_inflow = max(l0_q - local_runoff_total, 0.0)
                    error = abs(actual_inflow - expected_inflow)
                    result["checks"]["hero_boundary_inflow_m3_per_yr"] = actual_inflow
                    result["checks"]["hero_boundary_inflow_expected_m3_per_yr"] = expected_inflow
                    result["checks"]["hero_boundary_inflow_conserved"] = error <= max(
                        1e-6, expected_inflow * 1e-9
                    )
                    result["checks"]["hero_boundary_inflow_error_m3_per_yr"] = error
    result["checks"]["manifest_cell_count_matches"] = not n or all(
        np.asarray(v).size == n for v in fields.values()
    )
    return result


def face_layout(values: np.ndarray, manifest: dict[str, Any]) -> np.ndarray:
    graph = manifest.get("graph", {})
    shape = graph.get("shape", [])
    if graph.get("kind") == "sphere" and len(shape) == 3:
        return np.asarray(values).reshape(shape)
    if len(shape) == 2:
        return np.asarray(values).reshape(shape)
    return np.asarray(values).reshape(1, -1)


def save_maps(
    world: Path, fields: dict[str, np.ndarray], manifest: dict[str, Any], output: Path
) -> list[str]:
    wanted = [
        "elevation",
        "plate",
        "boundary",
        "uplift",
        "precipitation",
        "runoff",
        "discharge",
        "river",
        "lake",
        "geology",
    ]
    graph = manifest.get("graph", {})
    shape = graph.get("shape", [])
    if graph.get("kind") != "sphere" or len(shape) != 3:
        return []
    output.mkdir(parents=True, exist_ok=True)
    files: list[str] = []
    for name in wanted:
        if name not in fields:
            continue
        values = face_layout(fields[name], manifest)
        fig, axes = plt.subplots(2, 3, figsize=(12, 7), constrained_layout=True)
        cmap = "terrain" if name == "elevation" else "viridis"
        for face, ax in enumerate(axes.flat):
            image = ax.imshow(values[face], origin="lower", cmap=cmap, interpolation="nearest")
            ax.set_title(f"face {face}")
            ax.set_xticks([])
            ax.set_yticks([])
        fig.colorbar(image, ax=list(axes.flat), shrink=0.78, label=name)
        fig.suptitle(f"seed {manifest.get('seed')} · {manifest.get('kind')} · {name}")
        path = output / f"{manifest.get('kind')}_{name}.png"
        fig.savefig(path, dpi=120)
        plt.close(fig)
        files.append(str(path))

    if "elevation" in fields and "river" in fields:
        z = face_layout(fields["elevation"], manifest)
        river = face_layout(fields["river"], manifest).astype(bool)
        fig, axes = plt.subplots(2, 3, figsize=(12, 7), constrained_layout=True)
        for face, ax in enumerate(axes.flat):
            ax.imshow(z[face], origin="lower", cmap="terrain", interpolation="nearest")
            ax.imshow(
                np.ma.masked_where(~river[face], river[face]),
                origin="lower",
                cmap="Blues",
                alpha=0.8,
            )
            ax.set_title(f"face {face}")
            ax.set_xticks([])
            ax.set_yticks([])
        path = output / f"{manifest.get('kind')}_elevation_river.png"
        fig.savefig(path, dpi=120)
        plt.close(fig)
        files.append(str(path))
    return files


def compare_pathologies(current: dict[str, Any], baseline: dict[str, Any]) -> list[str]:
    warnings: list[str] = []
    checks = current.get("checks", {})
    old = baseline.get("checks", {})
    for key in ("cave_cells_level_0", "lake_cells", "river_cells"):
        before, after = old.get(key), checks.get(key)
        if (
            isinstance(before, int)
            and isinstance(after, int)
            and before > 0
            and after > 10 * before
        ):
            warnings.append(f"{key} is {after / before:.1f}x baseline")
    for field, metric in (("elevation", "max"), ("discharge", "max")):
        before = baseline.get("fields", {}).get(field, {}).get(metric)
        after = current.get("fields", {}).get(field, {}).get(metric)
        if (
            isinstance(before, (int, float))
            and isinstance(after, (int, float))
            and before > 0
            and after > 10 * before
        ):
            warnings.append(f"{field}.{metric} is {after / before:.1f}x baseline")
    return warnings


def planet_hero_continuity(planet_world: Path, hero_world: Path) -> dict[str, Any]:
    planet_fields, planet_manifest = load_fields(planet_world)
    hero_fields, hero_manifest = load_fields(hero_world)
    diag = hero_manifest.get("meta", {}).get("diag", {})
    boundary = diag.get("boundary", {}) if isinstance(diag, dict) else {}
    result: dict[str, Any] = {"applicable": bool(boundary)}
    if not boundary or "discharge" not in planet_fields or "discharge" not in hero_fields:
        result["status"] = "missing parent/child discharge diagnostics"
        return result

    parent_q = np.asarray(planet_fields["discharge"], dtype=np.float64).reshape(-1)
    child_q = np.asarray(hero_fields["discharge"], dtype=np.float64).reshape(-1)
    parent_index = int(boundary.get("l0_receiver", -1))
    parent_q_at_selection = (
        float(parent_q[parent_index]) if 0 <= parent_index < parent_q.size else None
    )
    inflow = float(boundary.get("inflow_m3_per_yr", 0.0))
    child_graph = hero_world / hero_manifest.get("graph", {}).get("file", "graph.npz")
    local_source = None
    if child_graph.exists() and "runoff" in hero_fields:
        with np.load(child_graph) as archive:
            area = archive["area"].reshape(-1).astype(np.float64, copy=False)
        local_source = float(
            (area * np.asarray(hero_fields["runoff"], dtype=np.float64).reshape(-1)).sum()
        )
    expected_inflow = (
        max(parent_q_at_selection - local_source, 0.0)
        if parent_q_at_selection is not None and local_source is not None
        else None
    )
    outlet_cells = np.asarray(boundary.get("outlet_cells", []), dtype=np.int64)
    outlet_cells = outlet_cells[(outlet_cells >= 0) & (outlet_cells < child_q.size)]
    downstream = float(child_q[outlet_cells].sum()) if outlet_cells.size else None
    inflow_cells = np.asarray(boundary.get("inflow_cells", []), dtype=np.int64)
    result.update(
        {
            "parent_selected_discharge_m3_per_yr": parent_q_at_selection,
            "boundary_incoming_discharge_m3_per_yr": inflow,
            "expected_boundary_incoming_discharge_m3_per_yr": expected_inflow,
            "boundary_inflow_conserved": (
                abs(inflow - expected_inflow) <= max(1e-6, abs(expected_inflow) * 1e-9)
                if expected_inflow is not None
                else None
            ),
            "hero_downstream_outlet_discharge_m3_per_yr": downstream,
            "hero_total_source_plus_inflow_m3_per_yr": (
                local_source + inflow if local_source is not None else None
            ),
            "outlet_discharge_conserved": (
                abs(downstream - (local_source + inflow)) <= max(1e-6, abs(downstream) * 1e-8)
                if downstream is not None and local_source is not None
                else None
            ),
            "inflow_port_count": int(inflow_cells.size),
            "inflow_ports_valid": bool(np.all((inflow_cells >= 0) & (inflow_cells < child_q.size))),
            "parent_donor_cell": int(boundary.get("l0_donor", -1)),
        }
    )
    result["status"] = "no positive parent inflow" if inflow <= 0 else "checked"
    return result


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--runs", nargs="+", required=True, type=Path, help="all-run directories to audit"
    )
    parser.add_argument(
        "--out", required=True, type=Path, help="directory for audit.json and diagnostic maps"
    )
    parser.add_argument(
        "--baseline", type=Path, help="optional baseline run directory for pathology ratios"
    )
    args = parser.parse_args()
    args.out.mkdir(parents=True, exist_ok=True)
    reports: list[dict[str, Any]] = []
    run_worlds: dict[str, dict[str, Path]] = {}
    for run in args.runs:
        run_worlds[run.name] = {}
        for kind in ("planet", "hero"):
            world = run / kind
            if not (world / "manifest.json").exists():
                continue
            fields, manifest = load_fields(world)
            report = world_metrics(world, fields, manifest)
            run_worlds[run.name][kind] = world
            report["diagnostic_maps"] = save_maps(
                world, fields, manifest, args.out / run.name / kind
            )
            if args.baseline:
                baseline_world = args.baseline / kind
                if (baseline_world / "manifest.json").exists():
                    old_fields, old_manifest = load_fields(baseline_world)
                    old_report = world_metrics(baseline_world, old_fields, old_manifest)
                    report["pathology_warnings"] = compare_pathologies(report, old_report)
            reports.append(report)
    continuity = []
    for pair in run_worlds.values():
        if "planet" in pair and "hero" in pair:
            continuity.append(
                {
                    "planet": str(pair["planet"]),
                    "hero": str(pair["hero"]),
                    "metrics": planet_hero_continuity(pair["planet"], pair["hero"]),
                }
            )
    result = {
        "suite": [str(p) for p in args.runs],
        "quality_axes": [
            "hard_invariants",
            "causal_consistency",
            "statistics",
            "visual_maps",
            "performance_external",
        ],
        "worlds": reports,
        "planet_hero_continuity": continuity,
    }
    (args.out / "audit.json").write_text(
        json.dumps(result, indent=2, allow_nan=False), encoding="utf-8"
    )
    print(
        json.dumps(
            {
                "worlds": len(reports),
                "output": str(args.out),
                "maps": sum(len(r["diagnostic_maps"]) for r in reports),
            },
            indent=2,
        )
    )


if __name__ == "__main__":
    main()
