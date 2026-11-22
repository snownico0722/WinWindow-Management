#!/usr/bin/env python3
"""Summarize WinWindow recordings or compare two ordinary LayoutModel replay outputs."""

import argparse
from bisect import bisect_left
from collections import Counter, defaultdict
from datetime import datetime
import hashlib
import json
from pathlib import Path
import sys
from zipfile import BadZipFile, ZipFile


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
    return next((s for s in ("activity", "focus", "workspace", "plans", "gestures", "adjustments", "behavior", "surfaces")
                 if name.startswith(s + "-") and name.endswith(".jsonl")), None)


def stamp(value):
    return datetime.fromisoformat(value.replace("Z", "+00:00"))


class Recording:
    def __init__(self):
        self.counts = Counter()
        self.versions = defaultdict(set)
        self.times = []
        self.ids = defaultdict(lambda: defaultdict(set))
        self.activity = {}
        self.geometry = {}
        self.boundaries = {}
        self.behavior = {}
        self.surfaces = {}
        self.adjustments = {}
        self.gestures = {}
        self.dropped = 0
        self.plans = {}
        self.frame_counts = Counter()
        self.display_profiles = set()

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
        elif stream == "surfaces":
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
                self.plans[key] = {"Version": row["Version"], "Trigger": row["Trigger"],
                                   "Outcome": row.get("Outcome"), "ActualObserved": has_actual,
                                   "FullDesktop": row.get("FullDesktop"), "Truncated": before.get("Truncated"),
                                   "Time": before.get("Time"), "ForegroundId": active.get("Id"),
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
                       "Outcome", "DurationMilliseconds", "AfterRequest", "PriorPlacement")},
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
        return {"Streams": dict(self.counts), "Versions": {k: sorted(v) for k, v in self.versions.items()},
                "FirstTime": min(self.times, key=stamp, default=None), "LastTime": max(self.times, key=stamp, default=None),
                "Displays": self.display_profiles_report(),
                "FrameCounts": [{"Source": k[0], "Truncated": k[1], "Count": v} for k, v in sorted(self.frame_counts.items())],
                "Identities": {k: {"Identity": self.identity(k), **{f: sorted(vals) for f, vals in attrs.items()}}
                               for k, attrs in sorted(self.ids.items())},
                **use, "PlanTriggers": field_counts(self.plans.values(), "Trigger"),
                "Plans": self.plans, "AdjustmentEvidence": field_counts(self.adjustments.values(), "Evidence"),
                "Adjustments": sorted(self.adjustments.values(), key=lambda row: row["Time"] or ""),
                "Gestures": sorted(self.gestures.values(), key=lambda row: row["Tick"]), "DroppedGestureEvents": self.dropped,
                "BehaviorEvents": [self.behavior[key] for key in sorted(self.behavior)],
                "SurfaceActions": field_counts(self.surfaces.values(), "Action"),
                "SurfaceEvents": surface_events}

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


def analyze(paths):
    sessions = defaultdict(Recording)
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
    return {"ReportVersion": 1, "Archives": archives, "DuplicateRowsSkipped": dup, "InvalidRows": malformed,
            "Sessions": {key: record.report() for key,record in sorted(sessions.items())}}


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
            key = (item["Session"], item["RequestId"], item["assumeResizable"])
            if key in rows:
                duplicates.append(key)
            else:
                rows[key] = item
    if not rows:
        raise ValueError(f"{path}: no layout requests to compare")
    return rows, summaries, duplicates


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
        same_input = all(a.get(k) == b.get(k) for k in ("Trigger", "Truncated", "RecordedManualFollowups",
                         "RecordedUndoOfThisRequest", "ObservedReleaseDetails", "GestureTiming"))
        evaluation = ({"Status": "duplicate-request", "Subjects": []} if key in duplicates
                      else evaluate_reference_pair(a, b, same_input))
        horizontal = metric_delta(a,b,"PostArrangeRegret")
        vertical = metric_delta(a,b,"PostArrangeVerticalError")
        protected_a, protected_b = (row.get("ProtectedRegionsPreserved") for row in (a,b))
        effects_a, effects_b = (row.get("SceneEffects") for row in (a,b))
        subject_key = lambda effect: (effect.get("Rule"), effect.get("SubjectId"))
        previous_effects = {subject_key(effect): effect for effect in effects_a or []}
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
            if not old_effect and effect.get("SubjectId") is not None:
                old_effect = next((e for e in effects_a or [] if e.get("SubjectId") == effect["SubjectId"]), {})
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
        if protected_a is True and protected_b is False:
            harm.append("protected-region-regression")
        if horizontal is not None and horizontal > 0:
            harm.append("horizontal-regret-increased")
        if vertical is not None and vertical > 0:
            harm.append("vertical-error-increased")
        moves_a = {m["Id"]: rect(m["After"]) for m in a.get("Moves") or []}
        moves_b = {m["Id"]: rect(m["After"]) for m in b.get("Moves") or []}
        changed_targets = [{"Id": i, "BaselineTarget": moves_a.get(i), "CandidateTarget": moves_b.get(i)}
                           for i in sorted(moves_a.keys() | moves_b.keys()) if moves_a.get(i) != moves_b.get(i)]
        candidate_subjects = {e.get("SubjectId") for e in effects_b or [] if e.get("AfterVisible") is not None}
        unmatched_measurements = sorted({e["SubjectId"] for e in effects_a or []
                                         if e.get("SubjectId") is not None and e.get("AfterVisible") is not None
                                         and e["SubjectId"] not in candidate_subjects})
        entries.append({"RequestId": request, "Trigger": b.get("Trigger"), "Status": "paired" if same_input else "input-mismatch",
                        "Truncated": b.get("Truncated"), "OutcomeChanged": changed, "ChangedTargets": changed_targets,
                        "LayerOrderChanged": a.get("Layers") != b.get("Layers")
                        if a.get("Layers") is not None and b.get("Layers") is not None else None,
                        "HorizontalRegretDelta": horizontal, "VerticalErrorDelta": vertical,
                        "ProtectedRegions": [protected_a, protected_b], "Harm": harm, "ReferenceVisibilityLosses": harmed,
                        "RecordedManualFollowups": b.get("RecordedManualFollowups"),
                        "RecordedUndoOfThisRequest": b.get("RecordedUndoOfThisRequest"),
                        "BaselineSceneEffects": effects_a, "CandidateSceneEffects": effects_b,
                        "ReferenceEvaluation": evaluation,
                        "BaselineSubjectsMissingCandidateMeasurement": unmatched_measurements,
                        "BaselineSelected": a.get("Selected"), "CandidateSelected": b.get("Selected"),
                        "BaselineMoveCount": len(a.get("Moves") or []), "CandidateMoveCount": len(b.get("Moves") or [])})
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
            for field in ("HorizontalRegretDelta", "VerticalErrorDelta"):
                values = [r[field] for r in paired if r[field] is not None]
                summary[field] = {"Count": len(values), "Mean": round(sum(values)/len(values), 6) if values else None,
                                  "Improved": sum(v < 0 for v in values), "Regressed": sum(v > 0 for v in values)}
            result["Assumptions"][name] = {"Summary": summary, "Requests": entries}
    missing_assumptions = sorted((session, request) for session, request in
        {(session, request) for session, request, _ in old.keys() | new.keys()}
        if any((session, request, assume) not in old and (session, request, assume) not in new for assume in (False, True)))
    invalid = bool(old_duplicates or new_duplicates or missing_assumptions) or any(
        r["Status"] != "paired" for result in sessions.values() for view in result["Assumptions"].values() for r in view["Requests"])
    return {"ReportVersion": 1, "CompletePairing": not invalid,
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
    check = commands.add_parser("compare", help="pair baseline and candidate LayoutModel replay stdout")
    check.add_argument("baseline")
    check.add_argument("candidate")
    for command in (collect, check):
        command.add_argument("-o", "--output", help="write JSON to this file instead of stdout")
    args = parser.parse_args()
    try:
        result = analyze(args.archives) if args.command == "analyze" else compare(args.baseline, args.candidate)
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
