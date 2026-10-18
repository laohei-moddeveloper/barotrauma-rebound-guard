"""Read-only aggregation of Rebound Guard logs; never edits game files."""
import argparse
import hashlib
import json
import pathlib
import re
import statistics


def analyze(path):
    raw = path.read_bytes()
    lines = raw.decode("utf-8-sig").splitlines()
    windows, timings, states = [], {}, {}
    frames = parallel_ms = 0
    for line in lines:
        if "simulation window=" in line:
            row = {key: float(value) for key, value in re.findall(
                r"(window|updates/s|sim/wall|avg_update_ms|max_update_ms|large_corrections|adjusted|max_original_jump)=([\d.]+)", line)}
            row["time_utc"] = line.split()[0]
            windows.append(row)
        if "subsystem timings" in line:
            for name, mean, maximum in re.findall(r"(\w+\.\w+)(?: calls=\d+)? avg_ms=([\d.]+) max_ms=([\d.]+)", line):
                timings.setdefault(name, []).append((float(mean), float(maximum)))
        for kind in ("body maintenance:", "container search:", "nearby item index:"):
            if kind in line:
                state = line.split(kind, 1)[1].split(";")[0].strip()
                counts = states.setdefault(kind, {})
                counts[state] = counts.get(state, 0) + 1
        if "parallel_frames=" in line:
            frames += int(re.search(r"parallel_frames=(\d+)", line)[1])
            parallel_ms += float(re.search(r"total_ms=([\d.]+)", line)[1])

    def summary(rows):
        if not rows:
            return None
        ordered = sorted(row["avg_update_ms"] for row in rows)
        return dict(windows=len(rows), recorded_seconds=round(sum(row["window"] for row in rows), 1),
                    avg_step_ms_median=statistics.median(ordered),
                    window_average_p95_ms=ordered[min(len(ordered) - 1, int(len(ordered) * .95))],
                    max_single_step_ms=max(row["max_update_ms"] for row in rows),
                    correction_branch_count=int(sum(row["large_corrections"] for row in rows)),
                    max_target_displacement_sim_units=max(row["max_original_jump"] for row in rows),
                    windows_at_least_59_5_updates=sum(row["updates/s"] >= 59.5 for row in rows))

    return dict(file=path.name, sha256=hashlib.sha256(raw).hexdigest(), lines=len(lines),
                first_utc=lines[0].split()[0] if lines else None, last_utc=lines[-1].split()[0] if lines else None,
                has_client_correction_hook=any("client correction patch=True" in line for line in lines),
                has_server_metrics=any("server input backlog:" in line or "server position queue:" in line for line in lines),
                all_windows=summary(windows), near_full_rate_windows=summary([row for row in windows if row["updates/s"] >= 55]),
                worst_windows=sorted(windows, key=lambda row: row["max_update_ms"], reverse=True)[:10],
                body_parallel_frames=frames, body_parallel_wall_ms=round(parallel_ms, 2),
                feature_states=states, subsystems={name: dict(window_average_ms_median=statistics.median(mean for mean, maximum in values),
                                                            max_sample_ms=max(maximum for mean, maximum in values)) for name, values in timings.items()},
                caveat="Window average p95 is not per-step p95. Old 32-step subsystem sampling may alias update intervals. Loading/pause/external stalls are not excluded. Correction targets are not rendered motion. No causality or A/B baseline inferred.")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("logs", nargs="+", type=pathlib.Path)
    parser.add_argument("-o", "--output", required=True, type=pathlib.Path)
    args = parser.parse_args()
    results = [analyze(path) for path in args.logs]
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(results, ensure_ascii=False, indent=2), encoding="utf-8")
    for result in results:
        print(result["file"], json.dumps(result["all_windows"], ensure_ascii=False))
