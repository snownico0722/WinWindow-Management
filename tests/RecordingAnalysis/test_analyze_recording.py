"""Run with python -m unittest discover -s tests/RecordingAnalysis."""

import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
from zipfile import ZipFile

SCRIPT = Path(__file__).resolve().parents[2] / ".agents/skills/winwindow-personalization/analyze_recording.py"
spec = importlib.util.spec_from_file_location("analyze_recording", SCRIPT)
analysis = importlib.util.module_from_spec(spec)
spec.loader.exec_module(analysis)


class RecordingAnalysisTests(unittest.TestCase):
    def test_interaction_identity_and_sequence_survive_overlapping_exports(self):
        recording = analysis.Recording()
        event = {"Sequence": 8, "Session": "s", "Tick": 100, "WindowId": 7, "Kind": "right-down",
                 "Identity": {"ExecutablePath": r"C:\Tools\Notes.exe", "Title": "项目 · 甲"}}
        recording.add("interactions", event)
        recording.add("interactions", {"Version": 1, "Session": "s", "Events": [event], "Dropped": 1})
        report = recording.report()
        self.assertEqual(report["Interactions"], [event])
        self.assertEqual(report["Identities"][7]["Title"], ["项目 · 甲"])
        self.assertEqual(report["DroppedDiskInteractions"], 1)

    def test_overlap_keeps_unique_focus_time_and_deduplicates_co_use(self):
        rect = {"X": -100, "Y": 0, "Width": 100, "Height": 100}
        keys = ["A" * 24, "B" * 24, "B" * 24]
        frame = {"Windows": [{"Id": i, "AppKey": key, "Rect": rect, "WorkArea": rect, "Dpi": 120,
                             "VisibleRatio": .5} for i, key in enumerate(keys, 1)]}

        def coverage(end, status="observed"):
            return {"FromTick": 10, "ToTick": end, "InitialId": 1, "Status": status,
                    "Events": [{"Tick": 12, "Id": 2}, {"Tick": 14, "Id": 1}],
                    "PointerActions": [{"Id": 1, "Count": 3}]}

        row = {"Session": "a", "Version": 7, "Tick": 20, "GeometryTick": 20, "Frame": frame,
               "FocusCoverage": coverage(20), "Windows": [{"Id": i, "VisibleRatio": .5} for i in (1,2,3)],
               "CoUse": [{"ForegroundId": 1, "VisiblePeerId": i, "ForegroundMsWithPeerVisibleAtBothEnds": 8}
                         for i in (2,3)]}
        boundary = {"Session": "a", "Version": 1, "Reason": "export", "FocusCoverage": coverage(16)}
        with tempfile.TemporaryDirectory() as tmp:
            paths = [Path(tmp) / f"{v}.zip" for v in (1,2)]
            for path in paths:
                with ZipFile(path, "w") as z:
                    z.writestr("activity-0.jsonl", json.dumps(row) + "\n" +
                               json.dumps({**row, "Tick": 30, "FocusCoverage": coverage(30, "sampling-gap"),
                                           "CoUse": None, "Frame": None}))
                    z.writestr("focus-0.jsonl", json.dumps(boundary))
            report = analysis.analyze(paths)
        session = report["Sessions"]["a"]
        self.assertEqual(report["DuplicateRowsSkipped"], 3)
        self.assertEqual(session["Coverage"]["UnknownFocusIntervals"], [[20, 30]])
        self.assertEqual(session["Coverage"]["PointerKnownMilliseconds"], 10)
        self.assertEqual(session["Coverage"]["EventForegroundMilliseconds"], 10)
        self.assertEqual(session["Apps"][keys[0]]["EventForegroundPercent"], 80)
        self.assertEqual(session["Apps"][keys[0]]["PointerActions"], 3)
        self.assertEqual(session["CoUse"][0]["Milliseconds"], 8)
        self.assertEqual(len(session["Switches"]), 2)
        self.assertEqual(session["Apps"][keys[0]]["CommonLocations"][0]["WorkAreaFraction"], [0.,0.,1.,1.])

    def test_missed_assistance_scores_only_pre_gesture_evidence_and_each_changed_dimension(self):
        from datetime import datetime, timedelta, timezone
        record = analysis.Recording()
        epoch = datetime(2026, 11, 22, tzinfo=timezone.utc)
        box = lambda x, y, width, height: dict(zip(("X", "Y", "Width", "Height"), (x, y, width, height)))
        work = box(-1000, 0, 1000, 800)
        original, manual = box(-850, 100, 400, 400), box(-900, 200, 450, 400)

        def time(tick):
            return (epoch + timedelta(milliseconds=tick)).isoformat()

        def frame(tick, geometry=original, **window):
            return {"Time": time(tick), "Truncated": False, "Displays": [{"Bounds": work, "WorkArea": work}],
                    "Windows": [{**dict(Id=2, Rect=geometry, Manageable=True, Foreground=True, WorkArea=work, Dpi=120), **window}]}

        def focus(a, b, events=()):
            return {"Status": "observed", "FromTick": a, "ToTick": b, "InitialId": 2,
                    "ObservedEndId": 2, "Events": list(events), "OmittedEvents": 0}

        def sample(tick, frm, preceding):
            record.add("activity", {"Time": time(tick), "Version": 7, "Tick": tick, "GeometryTick": tick,
                                    "FocusCoverage": preceding, "Frame": frm})

        def gesture(number, start, release, release_dpi=120):
            events = [{"GestureId": number, "WindowId": 2, "Stage": stage, "Tick": tick,
                       "Frame": geometry, "WorkArea": work, "Dpi": 120 if stage == "start" else release_dpi,
                       "AutomationMarked": False}
                      for stage, tick, geometry in (("start", start, original), ("release", release, manual))]
            record.add("gestures", {"Version": 1, "Events": events})
            record.add("adjustments", {"Version": 5, "Time": time(release), "WindowId": 2, "GestureId": number,
                       "Start": original, "End": manual, "StartWorkArea": work, "WorkArea": work, "Dpi": 120,
                       "Outcome": "stable", "StartContext": frame(start), "EndContext": frame(release, manual)})

        plan = {"Version": 9, "RequestId": "kept", "Trigger": "automatic-workspace", "Outcome": "observed-match",
                "Before": frame(1100), "Actual": frame(1700), "Targets": [], "Signals": {"Main": 2}}
        record.add("plans", plan)
        record.add("plans", {**plan, "RequestId": "later", "Before": frame(2500, manual), "Actual": frame(3000, manual)})
        sample(2000, frame(2000), focus(1000, 2000))
        sample(3000, frame(3000, manual), focus(2000, 3000))
        gesture(1, 2300, 2600)

        # Foreground continuity observes an A->B->A switch even though the endpoints are equal.
        sample(20000, frame(20000), focus(19300, 20000))
        sample(22000, frame(22000, manual), focus(20000, 22000, [{"Tick": 20800, "Id": 9}, {"Tick": 21000, "Id": 2}]))
        gesture(2, 21300, 21600)
        gesture(3, 40000, 40500)

        sample(50000, frame(50000), focus(49000, 50000))
        sample(52000, frame(52000, manual), focus(50000, 52000))
        gesture(4, 51000, 51300)

        # Another recently verified keep within the same per-axis trial should
        # not count twice, even though it has its own separate request ID.
        record.add("plans", {**plan, "RequestId": "another-keep", "Before": frame(51800), "Actual": frame(51900)})
        gesture(5, 52500, 52800)
        sample(53000, frame(53000, manual), focus(52000, 53000))

        sample(60000, frame(60000), focus(59000, 60000))
        sample(62000, frame(62000, manual), {**focus(60000, 62000), "OmittedEvents": 1})
        gesture(6, 61000, 61200)
        gesture(7, 61500, 61800, release_dpi=96)

        report = record.report()
        missed = report["MissedAssistance"]
        self.assertEqual(missed["StatusCounts"], {"no-system-placement": 1, "task-changed": 1,
                         "unknown-stale-or-absent-activity": 1, "no-recorded-request": 1,
                         "duplicate-trial-or-small-gesture": 1, "unknown-focus-gap": 1,
                         "display-changed": 1})
        self.assertEqual(missed["ScoredDimensions"], {"width": 2, "vertical-position": 2})
        first = missed["Gestures"][0]
        self.assertEqual(first["RequestId"], "kept")
        self.assertLess(first["EvidenceTick"], first["GestureStartTick"])

        def replay(target, timestamp=time(1100), signature="same"):
            return {"InputFingerprint": signature, "BeforeTime": timestamp, "Truncated": False,
                    "Trigger": "automatic-workspace", "ProjectedWindows": [{"Id": 2, "Before": original, "Rect": target}]}

        keys = ("session", "kept", False), ("session", "kept", True)
        analysis.score_missed_replays("session", report,
                    {key: replay(original) for key in keys},
                    {keys[0]: replay(manual), keys[1]: replay(manual, timestamp=time(2500))})
        self.assertEqual(first["Replay"]["fixed"]["Axes"]["width"],
                         {"Keep": .05, "Baseline": .05, "Candidate": 0, "CandidateImprovesOverKeep": True,
                          "CandidateImprovesOverBaseline": True})
        self.assertEqual(first["Replay"]["resizable"]["Status"], "unknown-replay-input")
        self.assertEqual(missed["Gestures"][3]["Replay"]["fixed"]["Status"], "unknown-no-request")
        self.assertIsNone(missed["ObservedSavedDrags"])

        probe = missed["Gestures"][3]
        self.assertEqual(probe["ProbeRequestId"], "offline-activity-50000")
        probe_key = ("session", probe["ProbeRequestId"], False)

        def probe_replay(target):
            return {**replay(target, timestamp=time(50000)), "Trigger": analysis.ACTIVITY_PROBE_TRIGGER}

        analysis.score_missed_replays("session", report, {probe_key: probe_replay(original)},
                                      {probe_key: probe_replay(manual)}, probe=True)
        self.assertEqual(probe["ProbeReplay"]["fixed"]["Status"], "paired")
        self.assertEqual(probe["Replay"]["fixed"]["Status"], "unknown-no-request")
        self.assertNotIn("ProbeReplay", first)

    def test_probe_export_preserves_preexisting_keyframes_without_gesture_data(self):
        frame = {"Time": "2026-11-22T10:03:00Z", "Truncated": False, "Displays": [{"Bounds": {"X": 0, "Y": 0, "Width": 1920, "Height": 1080},
                    "WorkArea": {"X": 0, "Y": 0, "Width": 1920, "Height": 1040}}],
                 "Windows": [{"Id": 4, "Manageable": True, "Foreground": True}]}
        activity = {"Session": "test", "Version": 7, "Tick": 100, "GeometryTick": 100, "Frame": frame}
        with tempfile.TemporaryDirectory() as tmp:
            source, output = Path(tmp) / "source.zip", Path(tmp) / "probes.zip"
            with ZipFile(source, "w") as archive:
                archive.writestr("activity-000.jsonl", json.dumps(activity))
            analysis.write_activity_probes([source, source], output)
            with ZipFile(output) as archive:
                self.assertEqual(archive.namelist(), ["layout-decisions.jsonl"])
                probe, = map(json.loads, archive.read("layout-decisions.jsonl").splitlines())
            self.assertEqual(probe["RequestId"], "offline-activity-100")
            self.assertEqual(probe["Trigger"], "offline-activity-probe")
            self.assertEqual(probe["Before"], frame)
            self.assertNotIn("Actual", probe)
            self.assertNotIn("Signals", probe)
            self.assertNotIn("Rules", probe)
            self.assertEqual(probe["Targets"], [])

    def test_surfaces_keep_host_geometry_and_release_timing(self):
        record = analysis.Recording()
        child = {"X": 50, "Y": 100, "Width": 50, "Height": 100}
        host = {"X": 0, "Y": 0, "Width": 200, "Height": 200}
        row = {"Version": 1, "Session": "a", "WindowId": 2, "HostId": 1, "ChildRect": child,
               "HostRect": host, "ChildApp": "A" * 24, "HostApp": "B" * 24, "Rule": "peek"}
        record.add("surfaces", {**row, "Tick": 4, "Action": "suppress"})
        record.add("surfaces", {**row, "Tick": 504, "Action": "release"})
        result = record.report()
        self.assertEqual(result["SurfaceActions"], {"suppress": 1, "release": 1})
        self.assertEqual(result["SurfaceEvents"][0]["ChildToHostAreaRatio"], .125)
        self.assertEqual(result["SurfaceEvents"][1]["MillisecondsSinceObservedSuppression"], 500)
        self.assertEqual(result["Identities"][1]["Identity"], "B" * 24)

    def test_compare_separates_sessions_and_uses_original_exposure_loss(self):
        with tempfile.TemporaryDirectory() as tmp:
            baseline, candidate = (Path(tmp) / name for name in ("baseline.txt", "candidate.txt"))
            rows = [{"Session": session, "RequestId": "shared", "assumeResizable": resize,
                     "PostArrangeRegret": .2, "ProtectedRegionsPreserved": True, "Moves": [],
                     "SceneEffects": [{"Rule": "keep", "SubjectId": 2, "BeforeVisible": .6, "AfterVisible": .6,
                                       "LostVisible": .01, "MaxLoss": .05}]}
                    for session in ("a","b") for resize in (True,False)]
            baseline.write_text("PASS normal replay header\n" + "\n".join(map(json.dumps, rows)), encoding="utf-8")
            rows[0] = {**rows[0], "PostArrangeRegret": .3, "ProtectedRegionsPreserved": False,
                       "SceneEffects": [{**rows[0]["SceneEffects"][0], "AfterVisible": .7, "LostVisible": .06}]}
            candidate.write_text("\n".join(map(json.dumps, rows)), encoding="utf-8")
            report = analysis.compare(baseline, candidate)
            self.assertTrue(report["CompletePairing"])
            request = report["Sessions"]["a"]["Assumptions"]["resizable"]["Requests"][0]
            self.assertEqual(request["Harm"], ["protected-region-regression", "horizontal-regret-increased"])
            self.assertTrue(request["ReferenceVisibilityLosses"][0]["LossLimitExceeded"])
            self.assertEqual(request["ReferenceVisibilityLosses"][0]["AdditionalLossOfOriginalExposure"], .05)
            candidate.write_text("\n".join(map(json.dumps, rows[1:])), encoding="utf-8")
            self.assertFalse(analysis.compare(baseline, candidate)["CompletePairing"])

    def test_reference_evaluation_counts_only_same_subject_and_observation(self):
        def effect(subject, lost, before=.6, rule="eval"):
            return {"Rule": rule, "SubjectId": subject, "BeforeVisible": before,
                    "AfterVisible": .8, "LostVisible": lost, "MaxLoss": .025}

        pairs = {
            "many": ([effect(1, .1), effect(2, 0)], [effect(1, .02), effect(2, 0)]),
            "saved": ([effect(2, .2)], [effect(2, 0)]),
            "changed": ([effect(1, .7)], [effect(9, .1)]),
            "null": ([effect(1, None)], [effect(1, .2)]),
            "truncated": ([effect(1, .9)], [effect(1, .05)]),
            "basis": ([effect(1, .3)], [effect(1, .05, before=.5)]),
            "absent": (None, [effect(1, .12)]),
        }
        with tempfile.TemporaryDirectory() as tmp:
            traces = [Path(tmp) / f"{name}.txt" for name in ("baseline", "candidate")]
            for side, path in enumerate(traces):
                rows = [{"Session": "capture", "RequestId": request, "assumeResizable": assume,
                         "Truncated": request == "truncated", "EvaluationEffects": effects[side]}
                        for request, effects in pairs.items() for assume in (True, False)]
                path.write_text("\n".join(map(json.dumps, rows)), encoding="utf-8")
            result = analysis.compare(*traces)
        self.assertTrue(result["CompletePairing"])
        summary = result["ReferenceEvaluationByAssumption"]["resizable"]
        self.assertEqual(summary["JointlyObservableRequests"], 2)
        self.assertEqual(summary["JointlyObservableSubjects"], 3)
        self.assertEqual(summary["Baseline"]["PercentOfJointlyObservableRequestsWithLoss"], 100)
        self.assertEqual(summary["Baseline"]["SumLostVisible"], .3)
        self.assertEqual(summary["Candidate"]["PercentOfJointlyObservableRequestsWithLoss"], 50)
        self.assertEqual(summary["Candidate"]["SumLostVisible"], .02)
        self.assertEqual(summary["RequestCoverage"], {"jointly-observable": 2, "no-joint-subjects": 3,
                                                       "evaluation-missing": 1, "truncated": 1})
        self.assertEqual(summary["ExcludedSubjects"], {"different-observation-basis": 1,
                                                       "missing-or-changed-subject": 2, "null-measurement": 1})
        local = result["Sessions"]["capture"]["Assumptions"]["resizable"]["Summary"]["ReferenceEvaluation"]
        self.assertEqual(local["ByRuleAndSubject"][1]["JointlyObservableRequests"], 2)
        empty = analysis.summarize_reference_evaluation([])
        self.assertEqual(empty["JointlyObservableSubjects"], 0)
        self.assertIsNone(empty["Candidate"]["SumLostVisible"])
        self.assertIsNone(empty["Candidate"]["PercentOfJointlyObservableRequestsWithLoss"])


if __name__ == "__main__":
    unittest.main()
