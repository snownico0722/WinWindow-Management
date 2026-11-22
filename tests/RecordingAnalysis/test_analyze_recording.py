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
