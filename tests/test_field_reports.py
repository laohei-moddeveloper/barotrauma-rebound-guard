"""Synthetic report fixtures only; no private game logs are distributed."""
import importlib.util
import json
import pathlib
import tempfile
import unittest


SPEC = importlib.util.spec_from_file_location(
    "field_reports", pathlib.Path(__file__).resolve().parents[1] / "tools" / "Analyze-FieldReports.py")
REPORTS = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(REPORTS)
STAMP = "2026-10-19T01:23:45.1234567Z "
WINDOW = ("simulation window=10.0s updates/s=60.0 sim/wall=1.00 avg_update_ms=4.00 "
          "max_update_ms=70.00; large_corrections=7 adjusted=0 max_original_jump=3.50")


class FieldReportsTests(unittest.TestCase):
    def analyze(self, *lines):
        with tempfile.TemporaryDirectory() as directory:
            path = pathlib.Path(directory) / "synthetic.log"
            path.write_text("\n".join(STAMP + line for line in lines), encoding="utf-8-sig")
            return REPORTS.analyze(path)

    def test_same_window_evidence_keeps_distinct_causes(self):
        result = self.analyze(
            WINDOW,
            "step health: measured_steps=600; p95_update_ms_le=8; p99_update_ms_le=20; "
            "steps_over_16_667ms=9; steps_over_50ms=1; max_outside_update_gap_ms=150; "
            "gc_gen0=2; gc_gen1=1; gc_gen2=0; allocated_process_mb=1.25; "
            "worst_step_gc_collection_count_delta=1",
            "server input backlog: max_queue=42; vanilla_trim_events=2; vanilla_discarded_inputs=11",
            "server position queue: membership_checks=800; max_pending=50",
            "correction branch: controlled_character_events=3; other_character_events=4; "
            "rendered_displacement_not_measured=True",
            "generic Lua bridge: wrappers_built=8; empty_phases_avoided=100; contexts_released=0",
            "constraint preparation: observed_islands=40; conflict_fallbacks=3",
            "worst step subsystem ms: Character.UpdateAll=40.5; World.Step=20.0",
            "subsystem timings (every invocation; nested totals overlap): "
            "Character.UpdateAll calls=600 avg_ms=2.5 max_ms=40.5 thread_allocated_mb=0.75; "
            "World.Step calls=600 avg_ms=1.0 max_ms=20.0 thread_allocated_mb=0.1")
        summary, row = result["all_windows"], result["windows"][0]
        self.assertEqual(summary["measured_steps"], 600)
        self.assertEqual(summary["server_discarded_inputs"], 11)
        self.assertEqual(summary["controlled_correction_events"], 3)
        self.assertEqual(summary["lua_empty_phases_avoided"], 100)
        self.assertEqual(summary["allocated_process_mb"], 1.25)
        self.assertNotIn("session_p99_ms", summary)
        self.assertEqual(row["worst_step_subsystems_ms"]["Character.UpdateAll"], 40.5)
        self.assertEqual(row["subsystems"]["Character.UpdateAll"]["calls"], 600)
        self.assertEqual(result["subsystems"]["Character.UpdateAll"]["max_sample_ms"], 40.5)
        self.assertEqual(len(row["observations"]), 5)
        self.assertTrue(row["correction"]["rendered_displacement_not_measured"])

    def test_old_log_preserves_unknown_instead_of_zero(self):
        result = self.analyze(WINDOW, "subsystem timings: Character.UpdateAll avg_ms=2.5 max_ms=40.5")
        self.assertIsNone(result["all_windows"]["measured_steps"])
        self.assertIsNone(result["all_windows"]["server_discarded_inputs"])
        self.assertFalse(result["has_server_metrics"])
        self.assertEqual(result["subsystems"]["Character.UpdateAll"]["window_average_ms_median"], 2.5)

    def test_frame_intervals_do_not_become_simulation_or_gpu_time(self):
        result = self.analyze(
            WINDOW,
            "client frame: draws=300; draw_interval_samples=299; draw_interval_p99_ms_le=100; "
            "draw_intervals_over_50ms=3; update_call_max_ms=5; draw_call_max_ms=70; "
            "game_draw_call_max_ms=4; updates_with_multiple_simulation_steps=1; "
            "gpu_time_measured=False; call_times_include_waits=True")
        self.assertEqual(result["all_windows"]["measured_draws"], 300)
        self.assertEqual(result["all_windows"]["draw_intervals_over_50ms"], 3)
        frame = result["windows"][0]["client_frame"]
        self.assertFalse(frame["gpu_time_measured"])
        self.assertEqual(frame["game_draw_call_max_ms"], 4)
        self.assertIn("client_draw_intervals_over_50ms", result["windows"][0]["observations"])
        self.assertIn("client_update_ran_multiple_simulation_steps", result["windows"][0]["observations"])

    def test_multiple_windows_do_not_mix_peaks_and_backlog(self):
        result = self.analyze(
            WINDOW, "server input backlog: max_queue=40; vanilla_trim_events=1; vanilla_discarded_inputs=10",
            WINDOW.replace("max_update_ms=70.00", "max_update_ms=5.00"),
            "server input backlog: max_queue=1; vanilla_trim_events=0; vanilla_discarded_inputs=0")
        self.assertEqual(result["all_windows"]["server_discarded_inputs"], 10)
        self.assertEqual(result["worst_windows"][0]["server_input"]["max_queue"], 40)
        self.assertNotIn("server_discarded_queued_inputs", result["windows"][1]["observations"])

    def test_reload_does_not_attach_unscoped_diagnostics_to_old_window(self):
        result = self.analyze(WINDOW, "unloaded; version=0.4.0", "loaded; version=0.4.0",
                              "server input backlog: max_queue=100; vanilla_trim_events=9; vanilla_discarded_inputs=99",
                              WINDOW, "server input backlog: max_queue=0; vanilla_trim_events=0; vanilla_discarded_inputs=0")
        self.assertNotIn("server_input", result["windows"][0])
        self.assertEqual(result["all_windows"]["server_discarded_inputs"], 0)
        self.assertNotEqual(result["windows"][0]["session_index"], result["windows"][1]["session_index"])

    def test_non_finite_bounds_and_truncated_tail_are_valid_json(self):
        result = self.analyze(WINDOW,
                              "step health: measured_steps=600; p99_update_ms_le=Infinity; gc_gen0=0",
                              "body maintenance: eligible; parallel_frames=1",
                              "simulation window=10.0s updates/s=")
        self.assertIsNone(result["windows"][0]["step_health"]["p99_update_ms_le"])
        self.assertEqual(result["incomplete_simulation_lines"], 1)
        self.assertEqual(result["all_windows"]["windows"], 1)
        json.dumps(result, allow_nan=False)

    def test_no_simulation_is_not_an_empty_successful_session(self):
        result = self.analyze("loaded; version=0.4.0", "reference enumeration lifetime fix installed")
        self.assertIsNone(result["all_windows"])
        self.assertEqual(result["windows"], [])


if __name__ == "__main__":
    unittest.main()
