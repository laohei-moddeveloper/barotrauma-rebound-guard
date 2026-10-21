"""Read-only aggregation of Rebound Guard logs; never edits game files."""
import argparse
import hashlib
import json
import pathlib
import re
import statistics


NUMBER = r"[-+]?(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][-+]?\d+)?"
PAIRS = re.compile(r"([\w/]+)=(True|False|[-+]?Infinity|NaN|" + NUMBER + r")")
SECTIONS = {
    "client frame:": "client_frame",
    "step health:": "step_health",
    "worst step subsystem ms:": "worst_step_subsystems_ms",
    "server input backlog:": "server_input",
    "server position queue:": "server_position_queue",
    "correction branch:": "correction",
    "memory:": "memory",
    "generic Lua bridge:": "lua_bridge",
    "constraint preparation:": "constraint_preparation",
    "nearby item index:": "nearby_index",
    "container search:": "container_search",
}


def numeric_pairs(text):
    result = {}
    for key, raw in PAIRS.findall(text):
        if raw in ("True", "False"):
            result[key] = raw == "True"
        elif raw in ("Infinity", "+Infinity", "-Infinity", "NaN"):
            # Strict JSON uses null for non-finite histogram upper bounds.
            result[key] = None
        else:
            result[key] = float(raw)
    return result


def observations(row):
    signals = []
    health = row.get("step_health", {})
    if health.get("steps_over_50ms", 0) > 0:
        signals.append("simulation_steps_over_50ms")
    if health.get("worst_step_gc_collection_count_delta", 0) > 0:
        signals.append("gc_collection_overlaps_worst_step_not_proven_cause")
    if health.get("max_outside_update_gap_ms", 0) > 50:
        signals.append("gap_outside_simulation_requires_frame_render_or_wait_measurement")
    if row.get("server_input", {}).get("vanilla_trim_events", 0) > 0:
        signals.append("server_discarded_queued_inputs")
    if row.get("constraint_preparation", {}).get("conflict_fallbacks", 0) > 0:
        signals.append("parallel_preparation_blocked_by_competing_patch")
    if row.get("client_frame", {}).get("draw_intervals_over_50ms", 0) > 0:
        signals.append("client_draw_intervals_over_50ms")
    if row.get("client_frame", {}).get("updates_with_multiple_simulation_steps", 0) > 0:
        signals.append("client_update_ran_multiple_simulation_steps")
    return signals


def analyze(path):
    raw = path.read_bytes()
    lines = raw.decode("utf-8-sig").splitlines()
    windows, timings, states = [], {}, {}
    frames = parallel_ms = 0
    current, session = None, 0
    incomplete_windows = 0
    for line in lines:
        if re.search(r"(?:^|\s)(?:loaded|unloaded);", line):
            current = None
            session += 1
        if "simulation window=" in line:
            fields = numeric_pairs(line)
            required = {"window", "updates/s", "avg_update_ms", "max_update_ms", "large_corrections", "max_original_jump"}
            if not required.issubset(fields) or any(fields[key] is None for key in required):
                incomplete_windows += 1
                current = None
                continue
            row = {key: value for key, value in fields.items() if key in required | {"sim/wall", "adjusted"}}
            row["time_utc"] = line.split()[0]
            row["session_index"] = session
            windows.append(row)
            current = row
        if current is not None:
            for marker, section in SECTIONS.items():
                if marker in line:
                    detail = line.split(marker, 1)[1]
                    if section == "worst_step_subsystems_ms":
                        current[section] = {name: float(value) for name, value in re.findall(
                            r"([\w.]+)=(" + NUMBER + r")", detail)}
                    else:
                        current[section] = numeric_pairs(detail)
        if "subsystem timings" in line:
            metrics = {}
            for name, detail in re.findall(r"(\w+\.\w+)\s+([^;]+)", line.split("subsystem timings", 1)[1]):
                values = numeric_pairs(detail)
                if "avg_ms" not in values or "max_ms" not in values:
                    continue
                timings.setdefault(name, []).append(values)
                metrics[name] = values
            if current is not None:
                current["subsystems"] = metrics
        for kind in ("body maintenance:", "container search:", "nearby item index:"):
            if kind in line:
                state = line.split(kind, 1)[1].split(";")[0].strip()
                counts = states.setdefault(kind, {})
                counts[state] = counts.get(state, 0) + 1
        if "parallel_frames=" in line:
            values = numeric_pairs(line)
            if values.get("parallel_frames") is not None and values.get("total_ms") is not None:
                frames += int(values["parallel_frames"])
                parallel_ms += values["total_ms"]

    for row in windows:
        row["observations"] = observations(row)

    def sum_metric(rows, section, key):
        values = [row[section][key] for row in rows if key in row.get(section, {})]
        return sum(value for value in values if value is not None) if values else None

    def summary(rows):
        if not rows:
            return None
        ordered = sorted(row["avg_update_ms"] for row in rows)
        health = [row["step_health"] for row in rows if "step_health" in row]
        return dict(windows=len(rows), recorded_seconds=round(sum(row["window"] for row in rows), 1),
                    avg_step_ms_median=statistics.median(ordered),
                    window_average_p95_ms=ordered[min(len(ordered) - 1, int(len(ordered) * .95))],
                    max_single_step_ms=max(row["max_update_ms"] for row in rows),
                    correction_branch_count=int(sum(row["large_corrections"] for row in rows)),
                    max_target_displacement_sim_units=max(row["max_original_jump"] for row in rows),
                    windows_at_least_59_5_updates=sum(row["updates/s"] >= 59.5 for row in rows),
                    windows_with_step_health=len(health),
                    measured_steps=sum_metric(rows, "step_health", "measured_steps"),
                    steps_over_16_667ms=sum_metric(rows, "step_health", "steps_over_16_667ms"),
                    steps_over_50ms=sum_metric(rows, "step_health", "steps_over_50ms"),
                    allocated_process_mb=sum_metric(rows, "step_health", "allocated_process_mb"),
                    gc_gen0=sum_metric(rows, "step_health", "gc_gen0"),
                    gc_gen1=sum_metric(rows, "step_health", "gc_gen1"),
                    gc_gen2=sum_metric(rows, "step_health", "gc_gen2"),
                    server_trim_events=sum_metric(rows, "server_input", "vanilla_trim_events"),
                    server_discarded_inputs=sum_metric(rows, "server_input", "vanilla_discarded_inputs"),
                    controlled_correction_events=sum_metric(rows, "correction", "controlled_character_events"),
                    lua_empty_phases_avoided=sum_metric(rows, "lua_bridge", "empty_phases_avoided"),
                    measured_draws=sum_metric(rows, "client_frame", "draws"),
                    draw_intervals_over_50ms=sum_metric(rows, "client_frame", "draw_intervals_over_50ms"),
                    updates_with_multiple_simulation_steps=sum_metric(rows, "client_frame", "updates_with_multiple_simulation_steps"))

    return dict(file=path.name, sha256=hashlib.sha256(raw).hexdigest(), lines=len(lines),
                first_utc=lines[0].split()[0] if lines else None, last_utc=lines[-1].split()[0] if lines else None,
                has_client_correction_hook=any("client correction patch=True" in line for line in lines),
                has_server_metrics=any("server input backlog:" in line or "server position queue:" in line for line in lines),
                incomplete_simulation_lines=incomplete_windows,
                all_windows=summary(windows), near_full_rate_windows=summary([row for row in windows if row["updates/s"] >= 55]),
                worst_windows=sorted(windows, key=lambda row: row["max_update_ms"], reverse=True)[:10],
                windows=windows,
                body_parallel_frames=frames, body_parallel_wall_ms=round(parallel_ms, 2),
                feature_states=states, subsystems={name: dict(window_average_ms_median=statistics.median(value["avg_ms"] for value in values),
                                                            max_sample_ms=max(value["max_ms"] for value in values)) for name, values in timings.items()},
                caveat="Window average p95 is not per-step p95; per-window histogram upper bounds cannot be merged into a session p95/p99. Nested subsystem totals/allocations overlap. GC overlap is not proof of causation. Outside-update gaps may include rendering, waits, loading or pause. Queue counts are not queue age or packet latency. Correction targets are not rendered motion. No cross-process session match or A/B causality inferred.")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("logs", nargs="+", type=pathlib.Path)
    parser.add_argument("-o", "--output", required=True, type=pathlib.Path)
    args = parser.parse_args()
    results = [analyze(path) for path in args.logs]
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(results, ensure_ascii=False, indent=2, allow_nan=False), encoding="utf-8")
    for result in results:
        print(result["file"], json.dumps(result["all_windows"], ensure_ascii=False))
