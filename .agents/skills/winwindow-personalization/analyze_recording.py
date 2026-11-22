#!/usr/bin/env python3
"""Summarize WinWindow recordings or compare two ordinary LayoutModel replay outputs."""

import argparse
from bisect import bisect_left
from collections import Counter, defaultdict
from datetime import datetime, timedelta
import hashlib
import json
from pathlib import Path
import sys
from zipfile import BadZipFile, ZIP_DEFLATED, ZipFile


def field_counts(rows, field):
    return dict(Counter(str(row[field]) for row in rows if row.get(field) is not None))


def percent(part, whole):
    return round(100 * part / whole, 2) if whole else None


def rect(value):
    if value and all(key in value for key in ("X", "Y", "Width", "Height")):
        return [value[k] for k in ("X", "Y", "Width", "Height")]
    return None


def normalized(box, area):
    if box and area and area[2] > 0 and area[3] > 0:
        return [round(x, 4) for x in ((box[0] - area[0]) / area[2],
                (box[1] - area[1]) / area[3], box[2] / area[2], box[3] / area[3])]
    return None


def ratio(window):
    return (window.get("Exposure") or {}).get("Visible", window.get("VisibleRatio"))


def context(frame):
    if not frame:
        return None
    windows = []
    for w in frame.get("Windows", []):
        if not (w.get("AppKey") or w.get("Manageable") or w.get("SurfaceState") or w.get("Foreground")):
            continue
        box, area = rect(w.get("Rect")), rect(w.get("WorkArea"))
        exposure = w.get("Exposure") or {}
        windows.append({"Id": w["Id"], "AppKey": w.get("AppKey"), "Foreground": w.get("Foreground"),
                        "Dpi": w.get("Dpi"), "ZOrder": w.get("ZOrder"), "Rect": box, "WorkArea": area,
                        "Manageable": w.get("Manageable"), "LocalSurface": w.get("LocalSurface"),
                        "WorkAreaFraction": normalized(box, area), "VisibleRatio": ratio(w),
                        "OffscreenRatio": exposure.get("Offscreen"), "VisibleCells": exposure.get("Cells"),
                        "LargestFragment": rect(exposure.get("LargestFragment"))})
    return {"Time": frame.get("Time"), "Truncated": frame.get("Truncated"),
            "Displays": [{"Bounds": rect(d.get("Bounds")), "WorkArea": rect(d.get("WorkArea"))}
                         for d in frame.get("Displays") or []], "Windows": windows}


def stream_of(name):
    if name == "layout-decisions.jsonl":
        return "plans"
    if name == "gestures.jsonl":
        return "gestures"
    return next((s for s in ("activity", "focus", "workspace", "plans", "gestures", "adjustments", "behavior", "surfaces", "interactions")
                 if name.startswith(s + "-") and name.endswith(".jsonl")), None)


def stamp(value):
    return datetime.fromisoformat(value.replace("Z", "+00:00"))


class Recording:
    def __init__(self, include_probe_frames=False):
        self.probe_frames = {} if include_probe_frames else None
        self.counts = Counter()
        self.versions = defaultdict(set)
        self.times = []
        self.ids = defaultdict(lambda: defaultdict(set))
        self.activity = {}
        self.geometry = {}
        self.boundaries = {}
        self.behavior = {}
        self.surfaces = {}
        self.interactions = {}
        self.dropped_interactions = 0
        self.adjustments = {}
        self.gestures = {}
        self.dropped = 0
        self.plans = {}
        self.plan_evidence = {}
        self.frame_counts = Counter()
        self.display_profiles = set()

    def raw_identity(self, window, identity):
        if window and identity:
            for field in ("App", "ExecutablePath", "ClassName", "Title", "Handle", "ProcessId", "ProcessStartedUtc"):
                if identity.get(field) is not None:
                    self.ids[window][field].add(identity[field])

    def frame(self, frame, source):
        if not frame:
            return
        self.frame_counts[(source, bool(frame.get("Truncated")))] += 1
        if frame.get("Time"):
            self.times.append(frame["Time"])
        if frame.get("Displays"):
            self.display_profiles.add(tuple(sorted((tuple(rect(d["Bounds"])), tuple(rect(d["WorkArea"])))
                                                   for d in frame["Displays"])))
        for w in frame.get("Windows", []):
            self.raw_identity(w["Id"], w.get("Identity"))
            if w.get("AppKey"):
                self.ids[w["Id"]]["AppKeys"].add(w["AppKey"])
            if w.get("Dpi"):
                self.ids[w["Id"]]["Dpi"].add(w["Dpi"])

    def add(self, stream, row):
        self.counts[stream] += 1
        if row.get("Version") is not None:
            self.versions[stream].add(row["Version"])
        if row.get("Time"):
            self.times.append(row["Time"])
        if stream == "activity":
            if row.get("Frame"):
                record = context(row["Frame"])
                self.frame(row["Frame"], stream)
                self.geometry[row["GeometryTick"]] = record
                if self.probe_frames is not None:
                    self.probe_frames[row["GeometryTick"]] = row["Frame"]
            row.pop("Frame", None)
            row.pop("Processes", None)
            previous = self.activity.get(row["Tick"])
            if previous is None or row["Version"] > previous["Version"]:
                self.activity[row["Tick"]] = row
            for w in row.get("Windows") or []:
                if w.get("ApplicationRole") not in (None, "", "Unknown"):
                    self.ids[w["Id"]]["Roles"].add(w["ApplicationRole"])
        elif stream == "focus" and row.get("FocusCoverage"):
            coverage = row["FocusCoverage"]
            key = (coverage.get("FromTick"), coverage["ToTick"], row.get("Reason"))
            self.boundaries[key] = coverage
        elif stream == "behavior":
            key = (row.get("Segment", 0), row["Tick"])
            target = self.behavior.setdefault(key, {"Segment": key[0], "Tick": key[1], "Time": row.get("Time"),
                                                   "NavigationSources": row.get("NavigationSources"), "Events": []})
            for event in row.get("Events") or []:
                if event.get("App") and event.get("WindowId"):
                    self.ids[event["WindowId"]]["ExeNames"].add(event["App"].lower())
                if event not in target["Events"]:
                    target["Events"].append(event)
        elif stream == "interactions":
            self.dropped_interactions += row.get("Dropped", 0)
            for event in row.get("Events", [row]):
                self.interactions[event["Sequence"]] = event
                self.raw_identity(event.get("WindowId"), event.get("Identity"))
        elif stream == "surfaces":
            self.raw_identity(row.get("WindowId"), row.get("ChildIdentity"))
            self.raw_identity(row.get("HostId"), row.get("HostIdentity"))
            key = (row["Tick"], row["WindowId"], row.get("HostId"), row["Action"])
            for id_field, app_field in (("WindowId", "ChildApp"), ("HostId", "HostApp")):
                if row.get(id_field) and row.get(app_field):
                    self.ids[row[id_field]]["AppKeys"].add(row[app_field])
            child, host = rect(row.get("ChildRect")), rect(row.get("HostRect"))
            row["ChildRect"], row["HostRect"] = child, host
            row["ChildFractionOfHost"] = normalized(child, host)
            row["ChildToHostAreaRatio"] = (round(child[2] * child[3] / (host[2] * host[3]), 4)
                                            if child and host and host[2] > 0 and host[3] > 0 else None)
            self.surfaces[key] = row
        elif stream == "workspace":
            self.frame(row.get("Before"), "workspace-before")
            self.frame(row.get("After"), "workspace-after")
        elif stream == "plans":
            self.frame(row.get("Before"), "plan-before")
            self.frame(row.get("Actual"), "plan-actual")
            key = row["RequestId"]
            previous = self.plans.get(key)
            has_actual = bool(row.get("Actual"))
            if previous is None or (has_actual, row["Version"]) > (
                    previous["ActualObserved"], previous["Version"]):
                before = row.get("Before") or {}
                active = next((w for w in before.get("Windows", []) if w.get("Foreground")), {})
                targets = [move["Id"] for move in row.get("Targets") or []]
                self.plan_evidence[key] = context(before), context(row.get("Actual"))
                self.plans[key] = {"Version": row["Version"], "Trigger": row["Trigger"],
                                   "Outcome": row.get("Outcome"), "ActualObserved": has_actual,
                                   "FullDesktop": row.get("FullDesktop"), "Truncated": before.get("Truncated"),
                                   "Time": before.get("Time"), "ForegroundId": active.get("Id"),
                                   "ActualTime": (row.get("Actual") or {}).get("Time"),
                                   "SubmittedChange": bool(targets or row.get("Layers") or row.get("MinimizeTarget")),
                                   "MainId": (row.get("Signals") or {}).get("Main"),
                                   "ObservedChangeEvents": (row.get("Signals") or {}).get("ObservedChangeEvents"),
                                   "OmittedChanges": (row.get("Signals") or {}).get("OmittedChanges"),
                                   "InstanceUses": (row.get("Signals") or {}).get("InstanceUses"),
                                   "ReadingInput": (row.get("Signals") or {}).get("ReadingInput"),
                                   "CandidateGroups": [{"Ids": g.get("Ids"), "Selected": g.get("Selected"),
                                       "Limits": (g.get("Task") or {}).get("Limits"),
                                       "AxisCoverage": g.get("AxisCoverage"), "HeldAxes": [
                                           {"Held": c["Held"], "Rejection": c.get("Rejection")} for c in g.get("Candidates") or []
                                           if c.get("Held")]} for g in row.get("Groups") or []],
                                   "ProposedTargets": (row.get("Proposal") or {}).get("Targets"),
                                   "SubmittedTargets": row.get("Targets"), "Failures": row.get("SubmissionFailures"),
                                   "TargetWindowIds": targets, "UndoOfRequest": row.get("UndoOfRequest"),
                                   "SceneEffects": row.get("SceneEffects"),
                                   "OriginSide": (row.get("Signals") or {}).get("OriginSide"),
                                   "ShadowRules": (row.get("Signals") or {}).get("ShadowRules"),
                                   "ShadowReplay": row.get("ShadowReplay"),
                                   "RepeatedReversal": {wid: signal["RepeatedReversal"] for wid, signal in
                                                        ((row.get("Signals") or {}).get("Attention") or {}).items()
                                                        if signal.get("RepeatedReversal")}}
        elif stream == "adjustments":
            for field in ("StartContext", "EndContext", "SettledContext"):
                self.frame(row.get(field), stream)
            key = row.get("GestureId") or (row["WindowId"], row.get("Time"))
            old = self.adjustments.get(key)
            if old is None or row["Version"] > old["Version"]:
                work, start_work = rect(row.get("WorkArea")), rect(row.get("StartWorkArea"))
                start, end = rect(row.get("Start")), rect(row.get("End"))
                self.adjustments[key] = {
                    **{k: row.get(k) for k in ("Version", "Time", "WindowId", "GestureId", "Dpi",
                       "Outcome", "DurationMilliseconds", "AfterRequest", "PriorPlacement", "TrialFirstGestureId")},
                    "Start": start, "End": end, "StartWorkArea": start_work, "EndWorkArea": work,
                    "StartFraction": normalized(start, start_work), "EndFraction": normalized(end, work),
                    "ChangedAxesPx": dict(zip(("X", "Y", "Width", "Height"), (b-a for a,b in zip(start,end))))
                    if start and end else None,
                    "Before": context(row.get("StartContext")), "AtRelease": context(row.get("EndContext")),
                    "Settled": context(row.get("SettledContext"))}
        elif stream == "gestures":
            self.dropped += row.get("DroppedEvents", 0)
            for event in row.get("Events") or []:
                key = (event["GestureId"], event["WindowId"], event["Stage"], event["Tick"])
                previous = self.gestures.get(key)
                if previous is None or len(event.get("OuterPath") or []) > len(previous.get("OuterPath") or []):
                    event["Frame"] = rect(event.get("Frame"))
                    event["WorkArea"] = rect(event.get("WorkArea"))
                    self.gestures[key] = event

    def identity(self, window):
        if window == 0:
            return "untracked-foreground"
        keys = self.ids[window].get("AppKeys", ())
        if len(keys) == 1:
            return next(iter(keys))
        return f"window:{window}"

    def report(self):
        use = activity_report(self)
        releases = {(event["GestureId"], event["WindowId"]): event for event in self.gestures.values()
                    if event["Stage"] == "release"}
        for row in self.adjustments.values():
            row["Identity"] = self.identity(row["WindowId"])
            release = releases.get((row["GestureId"], row["WindowId"]), {})
            marked = release.get("AutomationMarked")
            row["AutomationMarked"] = marked
            row["Evidence"] = ("automation-marked" if marked else row["Outcome"] if row["Outcome"] in
                               ("stable", "assisted-release") else "unconfirmed")
        surface_events = sorted(self.surfaces.values(), key=lambda row: row["Tick"])
        local_since = {}
        for event in surface_events:
            key = event["WindowId"], event.get("HostId")
            if event["Action"] == "suppress":
                local_since.setdefault(key, event["Tick"])
            if event["Action"] == "release":
                started = local_since.pop(key, None)
                event["MillisecondsSinceObservedSuppression"] = event["Tick"] - started if started is not None else None
        missed = missed_opportunities(self)
        interventions = intervention_assessment(self, missed)
        return {"Streams": dict(self.counts), "Versions": {k: sorted(v) for k, v in self.versions.items()},
                "FirstTime": min(self.times, key=stamp, default=None), "LastTime": max(self.times, key=stamp, default=None),
                "Displays": self.display_profiles_report(),
                "FrameCounts": [{"Source": k[0], "Truncated": k[1], "Count": v} for k, v in sorted(self.frame_counts.items())],
                "Identities": {k: {"Identity": self.identity(k), **{f: sorted(vals) for f, vals in attrs.items()}}
                               for k, attrs in sorted(self.ids.items())},
                **use, "MissedAssistance": missed, "InterventionAssessment": interventions, "PlanTriggers": field_counts(self.plans.values(), "Trigger"),
                "Plans": self.plans, "AdjustmentEvidence": field_counts(self.adjustments.values(), "Evidence"),
                "Adjustments": sorted(self.adjustments.values(), key=lambda row: row["Time"] or ""),
                "Gestures": sorted(self.gestures.values(), key=lambda row: row["Tick"]), "DroppedGestureEvents": self.dropped,
                "BehaviorEvents": [self.behavior[key] for key in sorted(self.behavior)],
                "SurfaceActions": field_counts(self.surfaces.values(), "Action"),
                "SurfaceEvents": surface_events,
                "InteractionKinds": field_counts(self.interactions.values(), "Kind"),
                "Interactions": sorted(self.interactions.values(), key=lambda e: (e["Tick"], e["Sequence"])),
                "DroppedDiskInteractions": self.dropped_interactions}

    def display_profiles_report(self):
        return [[{"Bounds": b, "WorkArea": w} for b, w in conf] for conf in sorted(self.display_profiles)]


def foreground_spans(coverage):
    start, end, active = coverage["FromTick"], coverage["ToTick"], coverage["InitialId"]
    for event in coverage.get("Events") or []:
        tick = max(start, min(end, event["Tick"]))
        if tick > start:
            yield start, tick, active
        start, active = tick, event["Id"]
    if end > start:
        yield start, end, active


# Observational windows limit attribution. The grace interval avoids treating a snapshot
# taken by the move/size-start hook (or wall/monotonic clock skew) as prior evidence.
MISSED_FRESH_MS = 15_000
MISSED_PRE_START_MS = 250
MISSED_TRIAL_MS = 2_000
ACTIVITY_PROBE_TRIGGER = "offline-activity-probe"


def activity_probe_id(tick):
    return f"offline-activity-{tick}"


def matching_window(frame, window_id):
    if not frame or frame.get("Truncated") is not False:
        return None
    matches = [w for w in frame["Windows"] if w["Id"] == window_id]
    return matches[0] if len(matches) == 1 else None


def same_display(frame, other, work):
    def signature(sample):
        displays = sample["Displays"]
        if any(not d["Bounds"] or not d["WorkArea"] for d in displays):
            return []
        return sorted((tuple(d["Bounds"]), tuple(d["WorkArea"])) for d in displays)
    first, second = signature(frame), signature(other)
    return bool(first and first == second and sum(area == tuple(work) for _, area in first) == 1)


def focus_continuity(coverages, begin, end, window):
    spans = []
    for coverage in coverages:
        a, b = coverage.get("FromTick"), coverage["ToTick"]
        if a is None or b <= begin or a >= end or coverage["Status"] != "observed" or coverage.get("OmittedEvents"):
            continue
        events = coverage.get("Events") or []
        if coverage.get("ObservedEndId") != (events[-1]["Id"] if events else coverage.get("InitialId")):
            continue
        for start, stop, active in foreground_spans(coverage):
            if start < end and stop > begin:
                if active not in (None, 0, window):
                    return "task-changed"
                if active == window:
                    spans.append((start, stop))
    edge = begin
    for start, stop in sorted(spans):
        if start > edge:
            break
        edge = max(edge, stop)
    return None if edge >= end else "unknown-focus-gap"


def changed_dimensions(start, end, work):
    # Moving a left/top edge changes the origin, but is not an independent translation.
    # If both edges move unequally, the translation component is ambiguous.
    for location, size, unit, pos_name, size_name in (
        (0, 2, work[2], "horizontal-position", "width"), (1, 3, work[3], "vertical-position", "height")):
        threshold = max(4, unit * .01)
        if abs(end[size] - start[size]) >= threshold:
            yield size_name
        elif abs(end[location] + end[size]/2 - start[location] - start[size]/2) >= threshold:
            yield pos_name


def axis_error(predicted, manual, work, axis):
    index = {"horizontal-position": 0, "vertical-position": 1, "width": 2, "height": 3}[axis]
    offset = index % 2 + 2
    value = lambda box: box[index] + box[offset] / 2 if index < 2 else box[index]
    return round(abs(value(predicted) - value(manual)) / work[offset], 6)


def axis_diagnosis(groups, window, axis, value, tolerance):
    sets = [a for group in groups or [] for i, member in enumerate(group.get("Ids") or []) if member == window
            for a in group.get("AxisCoverage") or [] if a["WindowIndex"] == i and a["Axis"] == axis]
    if not sets:
        reasons = [limit for g in groups or [] if window in (g.get("Ids") or []) and g.get("Selected") == "keep"
                   for limit in (g.get("Limits") or []) if limit.startswith(("verified-", "settled-", "existing-"))]
        return "held-before-search:" + reasons[0] if reasons else "unknown-candidate-coverage"
    for field, verdict in (("Feasible", "feasible-not-selected"), ("Generated", "generated-but-rejected")):
        if any(abs(v - value) <= tolerance for a in sets for v in a[field]):
            return verdict
    return "axis-not-generated"


def intervention_assessment(record, missed):
    # 分母包括保持原状的请求。没有松手或缺少回执时，无法判定满意，也无法推算真实省下的拖动。
    corrections = []
    for a in record.adjustments.values():
        req = a.get("AfterRequest")
        if not req or a.get("Evidence") not in ("stable", "assisted-release"):
            continue
        plan = record.plans.get(req)
        if not plan or not a["Start"] or not a["End"] or not a["EndWorkArea"]:
            continue
        window = a["WindowId"]
        proposed = {t["Id"]: rect(t["Target"]) for t in plan.get("ProposedTargets") or []}.get(window)
        submitted = {t["Id"]: rect(t["Target"]) for t in plan.get("SubmittedTargets") or []}.get(window)
        failures = {t["Id"] for t in plan.get("Failures") or []}
        actual = matching_window(record.plan_evidence[req][1], window) if record.plan_evidence[req][1] else None
        tol = max(3, round(4 * (a.get("Dpi") or 96) / 96))
        for dim in changed_dimensions(a["Start"], a["End"], a["EndWorkArea"]):
            axis, idx = axis_field(dim)
            if window in failures:
                diagnosis = "submission-failed"
            elif plan["Outcome"] == "intervened" or (actual and actual.get("Rect") and
                    any(abs(x - y) > tol for x, y in zip(actual["Rect"], a["Start"]))):
                diagnosis = "intervening-change"
            elif proposed and (not submitted or proposed[idx] != submitted[idx]):
                diagnosis = "changed-before-submission"
            elif submitted and actual and actual.get("Rect") and abs(submitted[idx] - actual["Rect"][idx]) > tol:
                diagnosis = "submitted-axis-not-realized"
            elif plan["Truncated"] or not actual or not submitted:
                diagnosis = "unknown-verification"
            else:
                diagnosis = axis_diagnosis(plan["CandidateGroups"], window, axis, a["End"][idx], tol)
                if abs(submitted[idx] - a["End"][idx]) <= tol:
                    diagnosis = "matches-submitted-axis"
            corrections.append({"RequestId": req, "GestureId": a["GestureId"], "WindowId": window,
                                "Axis": axis, "ReleasedValue": a["End"][idx], "Tolerance": tol, "Diagnosis": diagnosis})
    return {"Requests": len(record.plans), "RequestsWithSubmittedChanges": sum(p["SubmittedChange"] for p in record.plans.values()),
            "ReceiptOutcomes": field_counts(record.plans.values(), "Outcome"), "LinkedCorrectionAxes": corrections,
            "CorrectionDiagnoses": field_counts(corrections, "Diagnosis"), "MissedGestureStatuses": missed["StatusCounts"]}


def axis_field(dimension):
    return {"horizontal-position": ("X", 0), "vertical-position": ("Y", 1),
            "width": ("Width", 2), "height": ("Height", 3)}[dimension]


def missed_opportunities(record):
    activity_ticks = sorted(record.activity)
    geometry_ticks = sorted(record.geometry)
    coverages = [row["FocusCoverage"] for row in record.activity.values() if row.get("FocusCoverage")]
    coverages.extend(record.boundaries.values())
    timed_plans = sorted((stamp(p["Time"]), request) for request, p in record.plans.items() if p["Time"])
    plan_times = [time for time, _ in timed_plans]
    events = defaultdict(list)
    for event in record.gestures.values():
        events[event["GestureId"], event["WindowId"]].append(event)
    episodes, seen_axes, seen_request_axes = [], {}, set()
    valid = {"no-system-placement", "no-recorded-request"}

    def assess(row):
        window_id = row["WindowId"]
        if row["Evidence"] not in ("stable", "assisted-release"):
            return ("automation" if row["Evidence"] == "automation-marked" or row["Outcome"] == "automation"
                    else "unknown-endpoint"), {}
        gesture_events = events[row["GestureId"], window_id]
        starts = [ev for ev in gesture_events if ev["Stage"] == "start"]
        releases = [ev for ev in gesture_events if ev["Stage"] == "release"]
        if len(starts) != 1 or len(releases) != 1:
            return "unknown-gesture-boundary", {}
        start, release = starts[0], releases[0]
        tick, release_tick = start["Tick"], release["Tick"]
        marks = start.get("AutomationMarked"), release.get("AutomationMarked")
        if True in marks:
            return "automation", {}
        if (marks != (False, False) or tick >= release_tick or release.get("StartTick") not in (None, tick)
                or start["Frame"] != row["Start"] or release["Frame"] != row["End"]):
            return "unknown-gesture-mismatch", {}
        work, dpi = start["WorkArea"], start.get("Dpi")
        if not normalized(row["Start"], work) or not row["End"] or not dpi or not release.get("Dpi"):
            return "unknown-display", {}
        if work != release["WorkArea"] or dpi != release["Dpi"]:
            return "display-changed", {}
        if work != row["StartWorkArea"] or work != row["EndWorkArea"] or dpi != row["Dpi"]:
            return "unknown-display", {}
        before, at_release = row["Before"], row["AtRelease"]
        begin_window = matching_window(before, window_id)
        end_window = matching_window(at_release, window_id)
        if not begin_window or not end_window or not before["Time"] or not at_release["Time"]:
            return "unknown-gesture-context", {}
        if begin_window["Foreground"] is not True or end_window["Foreground"] is not True:
            return "task-changed", {}
        if not same_display(before, at_release, work):
            return "unknown-or-changed-display-topology", {}

        # Use a nearby activity sample strictly before the event to establish the
        # wall clock for request timestamps. Focus continuity stays in monotonic ticks.
        near = bisect_left(activity_ticks, tick) - 1
        if near < 0 or tick - activity_ticks[near] > MISSED_FRESH_MS:
            return "unknown-stale-or-absent-activity", {}
        anchor = record.activity[activity_ticks[near]]
        if not anchor.get("Time"):
            return "unknown-clock", {}
        start_time = stamp(anchor["Time"]) + timedelta(milliseconds=tick - anchor["Tick"])
        observed_start_time = stamp(before["Time"])
        if abs((observed_start_time - start_time).total_seconds()) > MISSED_PRE_START_MS / 1000:
            return "unknown-clock", {}
        fresh_from = start_time - timedelta(milliseconds=MISSED_FRESH_MS)
        strict_before = min(start_time, observed_start_time) - timedelta(milliseconds=MISSED_PRE_START_MS)
        recent = timed_plans[bisect_left(plan_times, fresh_from):bisect_left(plan_times, max(start_time, observed_start_time))]
        # A recorded system placement is a separate correction cohort, including
        # the older-than-30s links absent from AfterRequest.
        if row.get("PriorPlacement") or row.get("AfterRequest") or start.get("Prior") or release.get("Prior"):
            return "prior-system-placement", {}
        end_tick = release_tick + 1
        left = bisect_left(geometry_ticks, tick - MISSED_PRE_START_MS) - 1
        source, evidence_tick, request = None, None, None
        if recent:
            plan_time, request = recent[-1]
            plan = record.plans[request]
            if plan["Trigger"] not in ("automatic-workspace", "automatic-opening"):
                return "other-request-trigger", {"RequestId": request}
            if plan["SubmittedChange"]:
                return "prior-system-placement", {"RequestId": request}
            actual_time = plan["ActualTime"]
            if (plan_time >= strict_before
                    or len(recent) > 1 and recent[-2][0] == plan_time
                    or not actual_time or not plan_time <= stamp(actual_time) < strict_before):
                return "unknown-request-verification", {"RequestId": request}
            if plan["Outcome"] != "observed-match":
                return "unknown-request-verification", {"RequestId": request}
            source = record.plan_evidence[request][0]
            actual = record.plan_evidence[request][1]
            verified = matching_window(actual, window_id)
            if (not verified or verified["Rect"] != row["Start"] or verified["Dpi"] != dpi
                    or not same_display(actual, before, work)):
                return "unknown-request-verification", {"RequestId": request}
            evidence_tick = tick + round((plan_time - start_time).total_seconds() * 1000)
            # The earlier request must itself belong to this foreground task; an
            # explicit record of a different task main is a negative control.
            if plan["MainId"] not in (None, 0, window_id):
                return "task-changed", {"RequestId": request}
        elif left >= 0 and tick - geometry_ticks[left] <= MISSED_FRESH_MS:
            evidence_tick = geometry_ticks[left]
            source = record.geometry[evidence_tick]
            if not source["Time"] or stamp(source["Time"]) >= strict_before:
                return "unknown-clock", {}
            if "plans" not in record.versions:
                return "unknown-plan-coverage", {}
        else:
            return "unknown-stale-or-absent-geometry", {}

        evidence = {"GestureStartTick": tick, "GestureReleaseTick": release_tick,
                    "EvidenceTick": evidence_tick, "EvidenceTime": source["Time"], "RequestId": request}
        historical = matching_window(source, window_id)
        if not historical:
            return "unknown-prior-window", evidence
        if historical["Foreground"] is not True:
            return "task-changed", evidence
        if (historical["WorkArea"] != work or historical["Dpi"] != dpi
                or begin_window["WorkArea"] != work or begin_window["Dpi"] != dpi
                or end_window["WorkArea"] != work or end_window["Dpi"] != dpi
                or not same_display(source, before, work)):
            return "unknown-or-changed-display-topology", evidence
        continuity = focus_continuity(coverages, evidence_tick, end_tick, window_id)
        if continuity:
            return continuity, evidence
        if historical["Rect"] != row["Start"]:
            return "geometry-changed-before-gesture", evidence
        if any(evidence_tick < ev["Tick"] <= release_tick and window_id in (ev["WindowId"], ev.get("HostId"))
               for ev in record.surfaces.values()):
            return "task-surface-changed", evidence
        if any(evidence_tick <= ev["Tick"] < tick and ev["WindowId"] == window_id
               and ev["GestureId"] != row["GestureId"] and ev["Stage"] == "release"
               and any(begin["Stage"] == "start" and ev["Frame"] != begin["Frame"]
                       for begin in events[ev["GestureId"], window_id])
               for ev in record.gestures.values()):
            return "intervening-gesture", evidence
        if not request and historical["Manageable"] is True and not historical["LocalSurface"]:
            evidence["ProbeRequestId"] = activity_probe_id(evidence_tick)
        return "no-system-placement" if request else "no-recorded-request", evidence

    def gesture_order(row):
        starts = [ev["Tick"] for ev in events[row["GestureId"], row["WindowId"]] if ev["Stage"] == "start"]
        return min(starts, default=0), row["Time"] or ""

    for row in sorted(record.adjustments.values(), key=gesture_order):
        status, evidence = assess(row)
        entry = {"WindowId": row["WindowId"], "GestureId": row["GestureId"], "Time": row["Time"],
                 "Status": status, **evidence}
        if status in valid:
            tick = evidence["GestureStartTick"]
            entry["Keep"], entry["ManualRelease"], entry["WorkArea"] = row["Start"], row["End"], row["EndWorkArea"]
            entry["Axes"] = axes = []
            for axis in changed_dimensions(entry["Keep"], entry["ManualRelease"], entry["WorkArea"]):
                key = row["WindowId"], tuple(entry["WorkArea"]), row["Dpi"], axis
                previous = seen_axes.get(key)
                trial = row.get("TrialFirstGestureId")
                seen_axes[key] = (evidence["GestureReleaseTick"], trial)
                request_key = (entry["RequestId"], *key)
                repeats_request = entry["RequestId"] and request_key in seen_request_axes
                repeats_trial = previous and (trial is not None and trial == previous[1]
                                              or 0 <= tick - previous[0] < MISSED_TRIAL_MS)
                if repeats_request or repeats_trial:
                    continue
                if entry["RequestId"]:
                    seen_request_axes.add(request_key)
                axes.append({"Dimension": axis, "KeepError": axis_error(entry["Keep"], entry["ManualRelease"],
                                                                       entry["WorkArea"], axis)})
            if entry["RequestId"]:
                plan = record.plans[entry["RequestId"]]
                for item in axes:
                    name, idx = axis_field(item["Dimension"])
                    item["HistoricalSearch"] = axis_diagnosis(plan.get("CandidateGroups"), row["WindowId"], name,
                        row["End"][idx], max(3, round(4 * (row.get("Dpi") or 96) / 96)))
            if not axes:
                entry["Status"] = "duplicate-trial-or-small-gesture"
        episodes.append(entry)
    adjustments = {(row["GestureId"], row["WindowId"]) for row in record.adjustments.values()}
    raw = Counter()
    for key, batch in events.items():
        stages = {event["Stage"] for event in batch}
        if {"start", "release"} <= stages:
            raw["with-adjustment" if key in adjustments else "without-adjustment"] += 1
        else:
            raw["missing-start-or-release"] += 1
    return {"StatusCounts": field_counts(episodes, "Status"),
            "RawGestureCoverage": dict(raw) if "gestures" in record.versions else None,
            "ScoredDimensions": dict(Counter(axis["Dimension"] for e in episodes for axis in e.get("Axes", []))),
            "Gestures": episodes, "ObservedSavedDrags": None}

def activity_report(record):
    foreground, pointer, old_foreground, background = (Counter() for _ in range(4))
    roles, visibility, presence = defaultdict(Counter), defaultdict(Counter), Counter()
    location, co_use, co_use_context, switches = defaultdict(dict), Counter(), defaultdict(dict), Counter()
    sources, gaps = Counter(), []
    intervals, candidates, accounted = [], [], []
    span_start = span_end = None
    sample_count = geometry_count = missing_geometry_count = co_use_intervals_ms = 0
    for tick, row in sorted(record.activity.items()):
        sample_count += 1
        duration = row.get("ObservedMilliseconds") or 0
        coverage = row.get("FocusCoverage")
        usage = row.get("Usage")
        spans = []
        point = None
        if coverage and coverage.get("FromTick") is not None:
            a, b = coverage["FromTick"], coverage["ToTick"]
            intervals.append((a, b))
            sources[coverage["Status"]] += 1
            if coverage["Status"] == "observed":
                spans = list(foreground_spans(coverage))
                if not coverage.get("OmittedPointerActions"):
                    point = coverage.get("PointerActions")
        elif 0 < duration <= 8000:
            a, b = tick - duration, tick
            intervals.append((a,b))
            sources["usage-only" if usage is not None else "focus-not-recorded"] += 1
            if usage:
                spans = [(a+s["OffsetMs"], a+s["OffsetMs"]+s["DurationMs"], s["Id"])
                         for s in usage.get("Foreground") or []]
                point = usage.get("PointerActions")
        if spans:
            candidates.append((a, b, 0, spans, point))
        frame = record.geometry.get(row.get("GeometryTick"))
        frame_windows = {w["Id"]: w for w in frame["Windows"]} if frame else {}
        if frame is None:
            missing_geometry_count += 1
        else:
            geometry_count += 1
            for w in frame["Windows"]:
                app = record.identity(w["Id"])
                geom_key = (tuple(w["Rect"] or []), tuple(w["WorkArea"] or []), w["Dpi"],
                            w["VisibleCells"], tuple(w["LargestFragment"] or []))
                chosen = location[app].setdefault(geom_key, {"Rect": w["Rect"], "WorkArea": w["WorkArea"],
                    "Dpi": w["Dpi"], "WorkAreaFraction": w["WorkAreaFraction"], "VisibleCells": w["VisibleCells"],
                    "LargestFragment": w["LargestFragment"], "ActivitySampleWindowCount": 0})
                chosen["ActivitySampleWindowCount"] += 1
        apps_here = {}
        for w in row.get("Windows") or []:
            app = record.identity(w["Id"])
            group = apps_here.setdefault(app, defaultdict(list))
            for key in ("ForegroundMs", "BackgroundVisibleMs", "VisibleRatio", "LargestFragmentRatio"):
                if w.get(key) is not None:
                    group[key].append(w[key])
            if w.get("ApplicationRole"):
                roles[app][w["ApplicationRole"]] += 1
        for app, info in apps_here.items():
            presence[app] += 1
            if info["ForegroundMs"]:
                old_foreground[app] += max(info["ForegroundMs"])
            if info["BackgroundVisibleMs"]:
                background[app] += max(info["BackgroundVisibleMs"])
            if info["VisibleRatio"]:
                values = visibility[app]
                visible = max(info["VisibleRatio"])
                values["Samples"] += 1
                values["SumVisible"] += visible
                values["Exposed"] += visible > 0
                if info["LargestFragmentRatio"]:
                    values["FragmentSamples"] += 1
                    values["SumFragment"] += max(info["LargestFragmentRatio"])
        if row.get("CoUse") is not None and spans:
            co_use_intervals_ms += sum(y-x for x,y,_ in spans)
            # Multiple peer windows with one app key can share precisely the same foreground time.
            pairs = {}
            for pair in row["CoUse"]:
                pid = pair["VisiblePeerId"]
                key = (pair["ForegroundId"], record.identity(pid))
                duration = pair["ForegroundMsWithPeerVisibleAtBothEnds"]
                if key not in pairs or duration > pairs[key][0]:
                    pairs[key] = (duration, pid)
            for (fid, peer), (duration, pid) in pairs.items():
                app_pair = (record.identity(fid), peer)
                co_use[app_pair] += duration
                front, behind = frame_windows.get(fid), frame_windows.get(pid)
                if not front or not behind:
                    continue
                geom_key = tuple((tuple(w["Rect"] or []), tuple(w["WorkArea"] or []), w["Dpi"], w["VisibleCells"])
                                 for w in (front, behind))
                placement = co_use_context[app_pair].setdefault(geom_key, {
                    "Foreground": {k: front[k] for k in ("Rect", "WorkArea", "Dpi", "WorkAreaFraction")},
                    "Peer": {k: behind[k] for k in ("Rect", "WorkArea", "Dpi", "WorkAreaFraction",
                                                   "VisibleCells", "VisibleRatio")},
                    "ObservedMilliseconds": 0})
                placement["ObservedMilliseconds"] += duration
    for (a,b,reason), coverage in record.boundaries.items():
        sources[f"{reason}:{coverage['Status']}"] += 1
        if a is None or a >= b:
            continue
        intervals.append((a,b))
        if coverage["Status"] == "observed":
            point = coverage.get("PointerActions") if not coverage.get("OmittedPointerActions") else None
            candidates.append((a, b, 1, list(foreground_spans(coverage)), point))

    # Full observations take precedence over earlier export snapshots that finish inside them.
    unique_spans = []
    pointer_ms = 0
    for a,b,_,spans,points in sorted(candidates, key=lambda c: (c[2], c[0], -c[1])):
        if not spans:
            continue
        index = max(0, bisect_left(accounted, (a,)) - 1)
        overlaps = []
        while index < len(accounted) and accounted[index][0] < b:
            if accounted[index][1] > a:
                overlaps.append(accounted[index])
            index += 1
        if points is not None and not overlaps:
            pointer_ms += b - a
            for p in points:
                pointer[record.identity(p["Id"])] += p["Count"]
        for x,y,wid in spans:
            for u,v in overlaps:
                if u >= y:
                    break
                if x < u:
                    unique_spans.append((x,u,wid))
                x = max(x,v)
                if x >= y:
                    break
            if x < y:
                unique_spans.append((x,y,wid))
        i = bisect_left(accounted, (a,))
        if i and accounted[i-1][1] >= a:
            i -= 1
            a = accounted[i][0]
        j = i
        while j < len(accounted) and accounted[j][0] <= b:
            b = max(b, accounted[j][1])
            j += 1
        accounted[i:j] = [(a,b)]
    previous = None
    for a,b,wid in sorted(unique_spans):
        app = record.identity(wid)
        foreground[app] += b-a
        if previous and previous[1] == a and previous[2] != wid:
            before = record.identity(previous[2])
            switches[(before, app)] += 1
        previous = (a,b,wid)
    ticks = list(record.activity)
    if intervals or ticks:
        span_start = min([a for a,_ in intervals] + ticks)
        span_end = max([b for _,b in intervals] + ticks)
        cursor = span_start
        for a,b in accounted:
            if cursor < a:
                gaps.append([cursor,a])
            cursor = b
        if cursor < span_end:
            gaps.append([cursor,span_end])
    observed = sum(foreground.values())
    windows_by_app = defaultdict(list)
    for wid, attrs in record.ids.items():
        if attrs.get("AppKeys") or attrs.get("ExeNames") or attrs.get("Roles"):
            windows_by_app[record.identity(wid)].append(wid)
    apps = set(presence) | set(foreground) | set(background) | set(pointer) | set(location) | set(windows_by_app)
    return {"Coverage": {"ActivitySamples": sample_count, "FirstTick": span_start, "LastTick": span_end,
                        "EventForegroundMilliseconds": observed, "UntrackedForegroundMilliseconds": foreground["untracked-foreground"],
                        "PointerKnownMilliseconds": pointer_ms, "CoUseEvidenceMilliseconds": co_use_intervals_ms,
                        "UnknownFocusMilliseconds": sum(b-a for a,b in gaps), "UnknownFocusIntervals": gaps,
                        "IntervalStatuses": dict(sources), "GeometryResolvedSamples": geometry_count,
                        "GeometryMissingSamples": missing_geometry_count,
                        "EventFocusCoveragePercent": percent(observed, span_end - span_start) if span_end is not None else None},
            "Apps": {app: {"ObservedWindowIds": sorted(windows_by_app[app]),
                           "ObservedExecutableNames": sorted({name for wid in windows_by_app[app]
                                                               for name in record.ids[wid].get("ExeNames", ())}),
                           "ActivityPresenceSamples": presence[app],
                           "ActivityPresencePercent": percent(presence[app], sample_count),
                           "EventForegroundMilliseconds": foreground[app] if observed else None,
                           "EventForegroundPercent": percent(foreground[app], observed),
                           "LegacyEndpointForegroundMilliseconds": old_foreground.get(app),
                           "LegacyEndpointBackgroundVisibleMilliseconds": background.get(app),
                           "PointerActions": pointer[app] if pointer_ms else None,
                           "VisibilitySamples": visibility[app]["Samples"],
                           "VisibleAtSamplePercent": percent(visibility[app]["Exposed"], visibility[app]["Samples"]),
                           "MeanLargestWindowVisiblePercent": percent(visibility[app]["SumVisible"], visibility[app]["Samples"]),
                           "MeanLargestFragmentPercent": percent(visibility[app]["SumFragment"], visibility[app]["FragmentSamples"]),
                           "RolesByWindowSample": dict(roles[app]),
                           "CommonLocations": sorted(location[app].values(),
                                key=lambda x: -x["ActivitySampleWindowCount"])[:8]} for app in sorted(apps)},
            "CoUse": [{"Foreground": app, "VisiblePeer": peer, "Milliseconds": ms,
                       "PercentOfAppEventForeground": percent(ms, foreground[app]),
                       "CommonRelativePlacements": sorted(co_use_context[(app,peer)].values(),
                                                          key=lambda v: -v["ObservedMilliseconds"])[:5]}
                      for (app,peer),ms in co_use.most_common()],
            "Switches": [{"From": src, "To": dst, "Count": n} for (src,dst),n in switches.most_common()]}


def read_recordings(paths, include_probe_frames=False):
    sessions = defaultdict(lambda: Recording(include_probe_frames))
    seen = set()
    archives, malformed = [], []
    dup = 0
    for path in paths:
        streams = Counter()
        archive_sessions = set()
        with ZipFile(path) as archive:
            snapshots = any(entry.filename == "timeline.jsonl" for entry in archive.infolist())
            for entry in archive.infolist():
                stream = stream_of(Path(entry.filename).name)
                if not stream:
                    continue
                with archive.open(entry) as reader:
                    for number, raw in enumerate(reader, 1):
                        if not raw.strip():
                            continue
                        try:
                            row = json.loads(raw)
                        except (UnicodeError, json.JSONDecodeError):
                            malformed.append({"Archive": Path(path).name, "Entry": entry.filename, "Line": number})
                            continue
                        if not isinstance(row, dict) or not row.get("Session"):
                            malformed.append({"Archive": Path(path).name, "Entry": entry.filename, "Line": number})
                            continue
                        streams[stream] += 1
                        archive_sessions.add(row["Session"])
                        fingerprint = (stream, hashlib.sha256(raw.strip()).digest())
                        if fingerprint in seen:
                            dup += 1
                            continue
                        seen.add(fingerprint)
                        sessions[row["Session"]].add(stream, row)
        archives.append({"Archive": Path(path).name, "Streams": dict(streams), "Sessions": sorted(archive_sessions),
                         "HasDiagnosticTimeline": snapshots})
    return sessions, archives, malformed, dup


def write_activity_probes(paths, output):
    # Freeze every usable keyframe, independent of whether the user later made a gesture.
    # The probe carries original solver geometry only; no future labels enter its inputs.
    if any(Path(path).resolve() == Path(output).resolve() for path in paths):
        raise ValueError("The activity probe ZIP must have a different path from all source recordings.")
    sessions, _, _, _ = read_recordings(paths, include_probe_frames=True)
    with ZipFile(output, "w", compression=ZIP_DEFLATED) as archive:
        with archive.open("layout-decisions.jsonl", "w") as writer:
            for session, record in sorted(sessions.items()):
                for tick, frame in sorted(record.probe_frames.items()):
                    if (frame.get("Truncated") is not False or not frame.get("Displays")
                            or not frame.get("Time") or not any(
                                w.get("Manageable") and w.get("Foreground") and not w.get("LocalSurface")
                                for w in frame.get("Windows") or [])):
                        continue
                    observation = {"Version": 9, "Session": session, "RequestId": activity_probe_id(tick),
                                   "Trigger": ACTIVITY_PROBE_TRIGGER, "Before": frame, "Targets": [], "Layers": [],
                                   "Groups": [], "Outcome": "offline-probe", "ApplyNotes": [], "FullDesktop": False}
                    writer.write((json.dumps(observation, separators=(",", ":")) + "\n").encode())


def analyze(paths, baseline_paths=(), candidate_paths=()):
    sessions, archives, malformed, dup = read_recordings(paths)
    reports = {key: record.report() for key, record in sorted(sessions.items())}
    if baseline_paths or candidate_paths:
        baseline = merge_replays(baseline_paths)
        candidate = merge_replays(candidate_paths)
        has_probes = any(row["Trigger"] == ACTIVITY_PROBE_TRIGGER
                         for predictions in (baseline, candidate) for row in predictions.values())
        for key, report in reports.items():
            score_correction_replays(key, report, baseline, candidate)
            score_missed_replays(key, report, baseline, candidate)
            if has_probes:
                score_missed_replays(key, report, baseline, candidate, probe=True)
    return {"ReportVersion": 2, "Archives": archives, "DuplicateRowsSkipped": dup, "InvalidRows": malformed,
            "Sessions": reports}


def read_replay(path):
    rows, summaries, duplicates = {}, [], []
    with open(path, encoding="utf-8-sig") as reader:
        for line, raw in enumerate(reader, 1):
            if not raw.lstrip().startswith("{"):
                continue
            item = json.loads(raw)
            if item.get("RequestId") is None:
                if "RecordedManualGestures" in item:
                    summaries.append(item)
                continue
            if not item.get("Session") or not isinstance(item.get("assumeResizable"), bool):
                raise ValueError(f"{path}:{line}: rerun LayoutModel with Session and assumeResizable in each request")
            # Legacy stdout called geometric distance "regret". The recorded release is only one observed layout.
            for current, legacy in (("HorizontalReleaseDistance", "PostArrangeRegret"),
                                    ("VerticalReleaseDistance", "PostArrangeVerticalError")):
                if current not in item:
                    item[current] = item.get(legacy)
            key = (item["Session"], item["RequestId"], item["assumeResizable"])
            if key in rows:
                duplicates.append(key)
            else:
                rows[key] = item
    if not rows:
        raise ValueError(f"{path}: no layout requests to compare")
    return rows, summaries, duplicates



def merge_replays(paths):
    output, ambiguous = {}, set()
    fields = ("InputFingerprint", "BeforeTime", "Trigger", "Truncated", "ProjectedWindows", "CandidateGroups")
    for path in paths:
        rows, _, duplicates = read_replay(path)
        ambiguous.update(duplicates)
        for key, row in rows.items():
            prediction = {field: row.get(field) for field in fields}
            if key in output and output[key] != prediction:
                ambiguous.add(key)
            else:
                output[key] = prediction
    return {key: row for key, row in output.items() if key not in ambiguous}


def score_correction_replays(session, report, baseline, candidate):
    for entry in report["InterventionAssessment"]["LinkedCorrectionAxes"]:
        request, window = entry["RequestId"], entry["WindowId"]
        plan = report["Plans"][request]
        value, tol, axis = entry["ReleasedValue"], entry["Tolerance"], entry["Axis"]
        for assume in (False, True):
            key = (session, request, assume)
            old, new = baseline.get(key), candidate.get(key)
            diagnosis = "unknown-replay-input"
            if (old and new and old.get("InputFingerprint") and old["InputFingerprint"] == new.get("InputFingerprint")
                    and plan["Time"] and all(r.get("BeforeTime") and stamp(r["BeforeTime"]) == stamp(plan["Time"])
                    and r.get("Truncated") is False and r["Trigger"] == plan["Trigger"] for r in (old, new))):
                target = next((w["Rect"] for w in new.get("ProjectedWindows") or [] if w["Id"] == window), None)
                if target:
                    diagnosis = ("selected-near-release" if abs(target[axis] - value) <= tol else
                                 axis_diagnosis(new.get("CandidateGroups"), window, axis, value, tol))
            entry.setdefault("ReplaySearch", {})["resizable" if assume else "fixed"] = diagnosis


def score_missed_replays(session, report, baseline, candidate, probe=False):
    missed = report["MissedAssistance"]
    prefix = "ProbeReplay" if probe else "Replay"
    totals = defaultdict(Counter)
    pairs = defaultdict(lambda: defaultdict(list))
    for entry in missed["Gestures"]:
        if not entry.get("Axes") or probe and not entry.get("ProbeRequestId"):
            continue
        request = entry["ProbeRequestId"] if probe else entry["RequestId"]
        window = entry["WindowId"]
        entry[prefix] = {}
        for assume in (False, True):
            name = "resizable" if assume else "fixed"
            key = (session, request, assume)
            old, new = baseline.get(key), candidate.get(key)
            plan = report["Plans"].get(request)
            expected_time = stamp(entry["EvidenceTime"]) if probe else stamp(plan["Time"]) if plan else None
            expected_trigger = ACTIVITY_PROBE_TRIGGER if probe else plan["Trigger"] if plan else None
            status = ("unknown-no-request" if not request else
                      "unknown-missing-or-ambiguous-replay" if not old or not new else
                      "unknown-replay-input" if any(
                          not row.get("InputFingerprint") or not row.get("BeforeTime")
                          or row.get("Truncated") is not False or row.get("Trigger") != expected_trigger
                          or stamp(row["BeforeTime"]) != expected_time for row in (old, new))
                      or old["InputFingerprint"] != new["InputFingerprint"] else "paired")
            result = entry[prefix][name] = {"Status": status}
            if status == "paired":
                positions = [[w for w in row.get("ProjectedWindows") or [] if w["Id"] == window]
                             for row in (old, new)]
                if any(len(matches) != 1 or not rect(matches[0].get("Rect"))
                       or rect(matches[0].get("Before")) != entry["Keep"] for matches in positions):
                    result["Status"] = "unknown-replay-window"
                else:
                    result["BaselineTarget"], result["CandidateTarget"] = (rect(matches[0]["Rect"]) for matches in positions)
                    for axis in entry["Axes"]:
                        dim = axis["Dimension"]
                        before, after = (axis_error(result[target], entry["ManualRelease"], entry["WorkArea"], dim)
                                         for target in ("BaselineTarget", "CandidateTarget"))
                        result.setdefault("Axes", {})[dim] = scores = {"Keep": axis["KeepError"], "Baseline": before,
                            "Candidate": after, "CandidateCloserToReleaseThanKeep": after < axis["KeepError"],
                            "CandidateCloserToReleaseThanBaseline": after < before}
                        field, idx = axis_field(dim)
                        result.setdefault("CandidateSearch", {})[dim] = ("selected-near-release" if abs(result["CandidateTarget"][idx] -
                            entry["ManualRelease"][idx]) <= 4 else axis_diagnosis(new.get("CandidateGroups"), window, field,
                            entry["ManualRelease"][idx], 4))
                        pairs[name][dim].append(scores)
            totals[name][result["Status"]] += 1
    missed[prefix + "Coverage"] = {name: dict(counts) for name, counts in totals.items()}
    missed[prefix + "PairedDimensions"] = {
        name: {dimension: {"Count": len(scores), "MeanErrors": {key: round(sum(s[key] for s in scores) / len(scores), 6)
                for key in ("Keep", "Baseline", "Candidate")}, "CandidateVsKeep": comparison_counts(scores, "Keep"),
                "CandidateVsBaseline": comparison_counts(scores, "Baseline")}
               for dimension, scores in axes.items()}
        for name, axes in pairs.items()}


def comparison_counts(scores, baseline):
    return dict(Counter("closer" if s["Candidate"] < s[baseline] else
                        "farther" if s["Candidate"] > s[baseline] else "same" for s in scores))

def replay_signature(row):
    return (sorted((move["Id"], rect(move["Before"]), rect(move["After"])) for move in row.get("Moves", [])),
            tuple(sorted(row.get("Selected") or [])), row.get("Layers"), row.get("MinimizeTarget"))


def metric_delta(before, after, field):
    if before.get(field) is not None and after.get(field) is not None:
        return round(after[field] - before[field], 6)
    return None


def evaluate_reference_pair(baseline, candidate, same_input):
    if not same_input:
        return {"Status": "input-mismatch", "Subjects": []}
    if baseline.get("Truncated") or candidate.get("Truncated"):
        return {"Status": "truncated", "Subjects": []}
    measurements = [row.get("EvaluationEffects") for row in (baseline, candidate)]
    if any(side is None for side in measurements):
        return {"Status": "evaluation-missing", "Subjects": []}

    indices = []
    for side in measurements:
        index = defaultdict(list)
        for item in side:
            if item.get("Rule") and item.get("SubjectId") is not None:
                index[item["Rule"], item["SubjectId"]].append(item)
        indices.append(index)
    old, new = indices
    exclusions = Counter()
    subjects = []
    for rule, subject in sorted(old.keys() | new.keys()):
        key = rule, subject
        if len(old[key]) > 1 or len(new[key]) > 1:
            exclusions["duplicate-subject"] += 1
            continue
        if not old[key] or not new[key]:
            exclusions["missing-or-changed-subject"] += 1
            continue
        a, b = old[key][0], new[key][0]
        if any(data.get(field) is None for data in (a, b)
               for field in ("BeforeVisible", "AfterVisible", "LostVisible")):
            exclusions["null-measurement"] += 1
            continue
        if a["BeforeVisible"] != b["BeforeVisible"] or a.get("MaxLoss") != b.get("MaxLoss"):
            exclusions["different-observation-basis"] += 1
            continue
        subjects.append({"Rule": rule, "SubjectId": subject, "BeforeVisible": a["BeforeVisible"],
                         "MaxLoss": a.get("MaxLoss"), "BaselineAfterVisible": a["AfterVisible"],
                         "CandidateAfterVisible": b["AfterVisible"], "BaselineLostVisible": a["LostVisible"],
                         "CandidateLostVisible": b["LostVisible"]})
    return {"Status": "jointly-observable" if subjects else "no-joint-subjects",
            "ExcludedSubjects": dict(exclusions), "Subjects": subjects}


def reference_metrics(cohorts):
    # Each cohort represents one request; one request can have several observed reference windows.
    samples = [subject for request in cohorts for subject in request]
    result = {"JointlyObservableRequests": len(cohorts), "JointlyObservableSubjects": len(samples)}
    for side in ("Baseline", "Candidate"):
        field = side + "LostVisible"
        subject_losses = sum(subject[field] > 0 for subject in samples)
        request_losses = sum(any(subject[field] > 0 for subject in request) for request in cohorts)
        result[side] = {
            "RequestsWithLoss": request_losses,
            "PercentOfJointlyObservableRequestsWithLoss": percent(request_losses, len(cohorts)),
            "SubjectsWithLoss": subject_losses,
            "PercentOfJointlyObservableSubjectsWithLoss": percent(subject_losses, len(samples)),
            "SumLostVisible": round(sum(subject[field] for subject in samples), 6) if samples else None,
        }
    return result


def summarize_reference_evaluation(entries, include_window_ids=False):
    observations = [row.get("ReferenceEvaluation") or {"Status": row["Status"], "Subjects": []} for row in entries]
    jointly_observed = [o["Subjects"] for o in observations if o["Subjects"]]
    by_rule, by_rule_subject = defaultdict(list), defaultdict(list)
    for subjects in jointly_observed:
        request_rules = defaultdict(list)
        for subject in subjects:
            rule = subject["Rule"]
            request_rules[rule].append(subject)
            if include_window_ids:
                by_rule_subject[rule, subject["SubjectId"]].append([subject])
        for rule, selected in request_rules.items():
            by_rule[rule].append(selected)
    excluded = Counter()
    for observation in observations:
        excluded.update(observation.get("ExcludedSubjects", {}))
    return {
        **reference_metrics(jointly_observed),
        "RequestCoverage": field_counts(observations, "Status"),
        "ExcludedSubjects": dict(excluded),
        "ByRule": {rule: reference_metrics(cohort) for rule, cohort in sorted(by_rule.items())},
        **({"ByRuleAndSubject": [{"Rule": key[0], "SubjectId": key[1], **reference_metrics(cohort)}
                                 for key, cohort in sorted(by_rule_subject.items())]} if include_window_ids else {}),
    }


def compare(baseline, candidate):
    old, old_summaries, old_duplicates = read_replay(baseline)
    new, new_summaries, new_duplicates = read_replay(candidate)
    sessions = {}
    duplicates = set(old_duplicates) | set(new_duplicates)
    for session, request, resizable in sorted(old.keys() | new.keys()):
        result = sessions.setdefault(session, {"Assumptions": {}})
        entries = result["Assumptions"].setdefault("resizable" if resizable else "fixed", [])
        key = session, request, resizable
        a, b = old.get(key), new.get(key)
        if a is None or b is None:
            entries.append({"RequestId": request, "Status": "candidate-only" if a is None else "baseline-only"})
            continue
        same_input = all(a.get(k) == b.get(k) for k in ("InputFingerprint", "BeforeTime", "Trigger", "Truncated", "RecordedManualFollowups",
                         "RecordedUndoOfThisRequest", "ObservedReleaseDetails", "GestureTiming"))
        status = ("duplicate-request" if key in duplicates else "input-mismatch" if not same_input
                  else "unknown-replay-input" if not a.get("InputFingerprint") else "paired")
        evaluation = (evaluate_reference_pair(a, b, True) if status == "paired"
                      else {"Status": status, "Subjects": []})
        horizontal = metric_delta(a, b, "HorizontalReleaseDistance")
        vertical = metric_delta(a, b, "VerticalReleaseDistance")
        protected_a, protected_b = (row.get("ProtectedRegionsPreserved") for row in (a,b))
        effects_a, effects_b = (row.get("SceneEffects") for row in (a,b))
        subject_key = lambda effect: (effect.get("Rule"), effect.get("SubjectId"))
        previous_effects = {subject_key(effect): effect for effect in effects_a or []}
        comparable = status == "paired" and a.get("Truncated") is False
        harmed = []
        for effect in effects_b or []:
            before, after, limit = (effect.get(k) for k in ("BeforeVisible", "AfterVisible", "MaxLoss"))
            if before is None or after is None:
                continue
            # LostVisible measures old exposed pixels lost, even when exposure is gained elsewhere.
            lost = effect.get("LostVisible")
            if lost is None:
                lost = round(before - after, 6)
            old_effect = previous_effects.get(subject_key(effect), {})
            if effect.get("SubjectId") is None or old_effect.get("BeforeVisible") != before or not comparable:
                old_effect = {}
            old_after = old_effect.get("AfterVisible")
            relative = round(old_after-after, 6) if old_after is not None else None
            extra_loss = round(lost - old_effect["LostVisible"], 6) if old_effect.get("LostVisible") is not None else None
            if lost > 0 or (relative is not None and relative > 0) or (extra_loss is not None and extra_loss > 0):
                harmed.append({"Rule": effect.get("Rule"), "SubjectId": effect.get("SubjectId"),
                               "VisibilityLossFromRecordedDesktop": lost, "VisibilityLossFromBaseline": relative,
                               "AdditionalLossOfOriginalExposure": extra_loss,
                               "LossLimitExceeded": lost > limit + 0.000001 if limit is not None else None})
        changed = replay_signature(a) != replay_signature(b)
        harm = []
        if comparable and protected_a is True and protected_b is False:
            harm.append("protected-region-regression")
        if any(subject["CandidateLostVisible"] > subject["BaselineLostVisible"]
               for subject in evaluation["Subjects"]):
            harm.append("reference-exposure-regression")
        moves_a = {m["Id"]: rect(m["After"]) for m in a.get("Moves") or []}
        moves_b = {m["Id"]: rect(m["After"]) for m in b.get("Moves") or []}
        changed_targets = [{"Id": i, "BaselineTarget": moves_a.get(i), "CandidateTarget": moves_b.get(i)}
                           for i in sorted(moves_a.keys() | moves_b.keys()) if moves_a.get(i) != moves_b.get(i)]
        candidate_subjects = {e.get("SubjectId") for e in effects_b or [] if e.get("AfterVisible") is not None}
        unmatched_measurements = sorted({e["SubjectId"] for e in effects_a or []
                                         if e.get("SubjectId") is not None and e.get("AfterVisible") is not None
                                         and e["SubjectId"] not in candidate_subjects})
        entries.append({"RequestId": request, "Trigger": b.get("Trigger"), "Status": status,
                        "Truncated": b.get("Truncated"), "OutcomeChanged": changed, "ChangedTargets": changed_targets,
                        "LayerOrderChanged": a.get("Layers") != b.get("Layers")
                        if a.get("Layers") is not None and b.get("Layers") is not None else None,
                        "HorizontalReleaseDistanceDelta": horizontal if comparable else None,
                        "VerticalReleaseDistanceDelta": vertical if comparable else None,
                        "ProtectedRegions": [protected_a, protected_b], "Harm": harm, "ReferenceVisibilityLosses": harmed,
                        "RecordedManualFollowups": b.get("RecordedManualFollowups"),
                        "RecordedUndoOfThisRequest": b.get("RecordedUndoOfThisRequest"),
                        "BaselineSceneEffects": effects_a, "CandidateSceneEffects": effects_b,
                        "ReferenceEvaluation": evaluation,
                        "BaselineSubjectsMissingCandidateMeasurement": unmatched_measurements,
                        "BaselineSelected": a.get("Selected"), "CandidateSelected": b.get("Selected"),
                        "BaselineMoveCount": len(a.get("Moves") or []), "CandidateMoveCount": len(b.get("Moves") or [])})
    # Both runs project alternatives on the same historical actions. Count each original request only once.
    for session, result in sessions.items():
        observed = {}
        for view in result["Assumptions"].values():
            for row in view:
                if row["Status"] == "paired":
                    observed[row["RequestId"]] = row
        followups = [r["RecordedManualFollowups"] for r in observed.values() if r["RecordedManualFollowups"] is not None]
        result["ObservedOnRecordedDesktop"] = {
            "PairedRequests": len(observed), "RequestsWithFollowupCount": len(followups),
            "RequestsWithManualFollowups": sum(n > 0 for n in followups) if followups else None,
            "LinkedManualFollowups": sum(followups) if followups else None,
            "ConfirmedUndoRequests": sum(r["RecordedUndoOfThisRequest"] is True for r in observed.values()),
        }
    evaluation_entries = defaultdict(list)
    for result in sessions.values():
        for name, entries in result["Assumptions"].items():
            evaluation_entries[name].extend(entries)
            paired = [r for r in entries if r["Status"] == "paired"]
            summary = {"Pairing": field_counts(entries, "Status"),
                       "ChangedOutcomes": sum(r["OutcomeChanged"] for r in paired),
                       "ReferenceEvaluation": summarize_reference_evaluation(entries, include_window_ids=True),
                       "Harm": dict(Counter(h for r in paired for h in r["Harm"])),
                       "SceneRules": dict(Counter(e["Rule"] for r in paired for e in r["CandidateSceneEffects"] or [])),
                       "ReferenceVisibilityLosses": sum(bool(r["ReferenceVisibilityLosses"]) for r in paired),
                       "RequestsMissingCandidateReferenceMeasurement": sum(
                           bool(r["BaselineSubjectsMissingCandidateMeasurement"]) for r in paired),
                       "ReferenceLossLimitExceeded": sum(any(e["LossLimitExceeded"] is True for e in r["ReferenceVisibilityLosses"])
                                                         for r in paired)}
            for field in ("HorizontalReleaseDistanceDelta", "VerticalReleaseDistanceDelta"):
                values = [r[field] for r in paired if r[field] is not None]
                summary[field] = {"Count": len(values), "Mean": round(sum(values)/len(values), 6) if values else None,
                                  "Closer": sum(v < 0 for v in values), "Farther": sum(v > 0 for v in values),
                                  "Unchanged": sum(v == 0 for v in values)}
            result["Assumptions"][name] = {"Summary": summary, "Requests": entries}
    missing_assumptions = sorted((session, request) for session, request in
        {(session, request) for session, request, _ in old.keys() | new.keys()}
        if any((session, request, assume) not in old and (session, request, assume) not in new for assume in (False, True)))
    invalid = bool(old_duplicates or new_duplicates or missing_assumptions) or any(
        r["Status"] != "paired" for result in sessions.values() for view in result["Assumptions"].values() for r in view["Requests"])
    return {"ReportVersion": 2, "CompletePairing": not invalid,
            "BaselineRecordedEffort": old_summaries, "CandidateRecordedEffort": new_summaries,
            "DuplicateBaselineKeys": old_duplicates, "DuplicateCandidateKeys": new_duplicates,
            "RequestsMissingAnAssumptionOnBothSides": missing_assumptions,
            "ReferenceEvaluationByAssumption": {name: summarize_reference_evaluation(entries)
                                                for name, entries in evaluation_entries.items()},
            "Sessions": sessions}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    collect = commands.add_parser("analyze", help="report on one or more recording ZIPs")
    collect.add_argument("archives", nargs="+")
    collect.add_argument("--baseline-replay", action="append", default=[], help="request-time LayoutModel stdout")
    collect.add_argument("--candidate-replay", action="append", default=[], help="matching candidate stdout")
    probes = commands.add_parser("probes", help="freeze complete activity keyframes into an offline solver input ZIP")
    probes.add_argument("archives", nargs="+")
    probes.add_argument("-o", "--output", required=True, help="write an offline solver input ZIP to this file")
    check = commands.add_parser("compare", help="pair baseline and candidate LayoutModel replay stdout")
    check.add_argument("baseline")
    check.add_argument("candidate")
    for command in (collect, check):
        command.add_argument("-o", "--output", help="write JSON to this file instead of stdout")
    args = parser.parse_args()
    try:
        if args.command == "probes":
            write_activity_probes(args.archives, args.output)
            return 0
        result = (analyze(args.archives, args.baseline_replay, args.candidate_replay)
                  if args.command == "analyze" else compare(args.baseline, args.candidate))
        if args.output:
            with open(args.output, "w", encoding="utf-8") as writer:
                json.dump(result, writer, ensure_ascii=False, indent=2, allow_nan=False)
                writer.write("\n")
        else:
            json.dump(result, sys.stdout, ensure_ascii=False, indent=2, allow_nan=False)
            sys.stdout.write("\n")
        return 0 if result.get("CompletePairing", True) else 1
    except (OSError, ValueError, KeyError, BadZipFile) as exc:
        parser.exit(2, f"{exc}\n")


if __name__ == "__main__":
    sys.exit(main())
