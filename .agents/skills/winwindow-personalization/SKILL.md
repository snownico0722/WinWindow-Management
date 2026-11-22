---
name: winwindow-personalization
description: Analyze WinWindow recording ZIPs, identify recurring desktop behavior, and validate candidate placement, scene, or handoff rules with the WinWindow replay model.
---

# WinWindow personalization

## Read the desktop record

Read the export's `layout-guide.md` and inspect the active `compiled-rules.json` and `behavior-rules.json`. Analyze the long-running recordings as a whole, including quiet desktop periods between layout requests. Use short diagnostic snapshots to check a specific time or outcome. The standalone analysis requires Python 3.11+ and the standard library. Extract only the script to the working directory and pass the original ZIPs directly:

```text
python -c "from zipfile import ZipFile; z = ZipFile('Recorder.zip'); z.extract('analyze_recording.py', 'export')"
python export/analyze_recording.py analyze Recorder.zip OlderRecorder.zip -o desktop-profile.json
```

For an older export without the script, run the current repository copy at `.agents/skills/winwindow-personalization/analyze_recording.py`. Archives can overlap because export reads a rolling store. The report skips duplicate rows, groups retained data by recording session and keeps monotonic ticks and window IDs local to that session. Use the observed first and last timestamps and the stream/version inventory to check coverage. Truncated, missing or expired streams leave questions unanswered. Diagnostic snapshots can contain titles and handles in their raw timeline. The report uses the structured layout decisions from those snapshots and excludes that raw timeline.

- Read `Apps` for sampled app presence, event-based foreground time, pointer actions and observed exposure. `ActivityPresencePercent` uses the number of activity samples. `EventForegroundPercent` uses verified foreground time including window 0, the shell or untracked interfaces. `VisibleAtSamplePercent` uses samples with known exposure for that app, and the two mean exposure percentages describe the most visible sibling window in each sample. Sample percentages do not establish continuous visibility. Endpoint estimates use the largest recorded duration among sibling windows per sample and remain separate from event time. Pointer counts cover known windows only and omit partly overlapping intervals with inseparable counts.
- Check `Coverage`, including focus gaps, each observation status, missing geometry and known pointer time. Newer records preserve focus event intervals and boundary observations. Older endpoint estimates can miss quick switches. Read `Switches` and directional `CoUse` together. Co-use counts foreground time with a stable peer visible at both ends, and different pairs can overlap in time. This evidence says how windows were available during a visit. It cannot show where the user was looking.
- Join a session's window IDs to `Identities` for app keys, observed executable names and role hints. Use a unique recorded `AppKey` for placement rules. Exact app keys are specific to an installation. A missing or conflicting key leaves that window anonymous. An executable name is evidence for a behavior rule and can cover several window classes. Describe tasks from observed roles and sequences and ask for labels only when they change the decision.
- Check `CommonLocations`, co-use `CommonRelativePlacements`, and the contexts attached to edits for screen work area, DPI and visible fragments. Rectangles are screen pixels in `[x,y,width,height]`; work-area fractions use the same ordering. Use the width and height of the particular work area; a negative screen coordinate can be on a display. An edit's `AtRelease` and `Settled` frames can differ because WinWindow assisted after release. A raw event can be marked as automation, incomplete or superseded. Use `AfterRequest`, `PriorPlacement` and `Plans` within the same session to investigate the earlier attempt. Timing alone does not prove why the user moved a window.
- Join `Plans[].ObservedChangeEvents` and `SurfaceEvents[].ContextPressEvent` to `Interactions[].Sequence` within the same session. Inspect original titles, paths, click points, owner and foreground. Use `ReadingInput` to reproduce the local interruption cost. A nearby button event alone cannot attribute a move. Check snapshot coverage counters and disk drops before interpreting missing events.
- Check `SurfaceEvents` for local surface decisions when the export includes the stream. Join the child and host in the same session, then compare their app keys, bounds, area ratio, and focus sequence. A child suppressed locally and later released supplies both the interaction context and its transition to an ordinary task. Older exports leave this classification unknown.
- Use complete sessions or days for discovery and keep different complete sessions for validation. Count a burst of edits as one episode. Record counterexamples such as independent single-window work, a peer that happened to be visible, a display change, and windows regularly left available. Missing observation time stays unknown.

## Draft and replay placement rules

Use the schema and ranges in the current release's guide. Preserve the baseline rule document when testing one change. Infer side or overlap from repeated manual placement in equivalent task contexts. Infer width or height only from changes on that axis. A co-use pair should have repeated usage evidence and should not be activated by coexistence alone. Version 3 and later can use `OnlyWhenPaired` for an exact `Pairs` entry. Use role pairs only when current role evidence supports the same arrangement across apps.

A version 4 document can include `Scenes` alongside the placement priors. A scene adjusts a specific trigger context. Use the narrowest conditions the observations support, and order specific matches before broad ones. For example, the following cap protects an already visible reference when one recorded foreground app works beside it during an automatic workspace observation:

```json
{
  "Version": 4,
  "Scenes": [{
    "Id": "keep-reference-exposed",
    "Trigger": "workspace",
    "ForegroundApps": ["AAAAAAAAAAAAAAAAAAAAAAAA"],
    "ReferenceApp": "BBBBBBBBBBBBBBBBBBBBBBBB",
    "MinWindows": 2,
    "MinVisible": 0.2,
    "MaxVisibleLoss": 0.05
  }]
}
```

Replace the placeholder keys with identities observed together on the applicable display. The reference must uniquely match a background peer on the foreground monitor and meet `MinVisible`. The limits use fractions. `MaxVisibleLoss` bounds loss from the originally exposed region measured against the original reference window's area. `MinWindows` and `MaxWindows` count relevant windows on that monitor. `Trigger` can also be `gesture` or `explicit`; test each intended path independently. `Weights` can override `Information`, `Switching`, `Continuity`, `Reflow`, `Personal`, `Interruption` (Smart v2.4+), and `Intervention` (default 1, range 0–8 except intervention 0.125–8). `AllowResize` and `ReconsiderSettled` change the available behavior in the matching context. Make one evidence-backed change at a time. Treat release-specific fields absent from an older export as unknown.

Version 5 adds optional `FirstWidth` and `SecondWidth` in `Pairs` (0.18–0.88 of work-area width). Keep `FirstSide` at `either` when the evidence supports width alone. Compare a real opening or confirmed working pair with two apps that merely coexist, navigation through an app, and duplicate window identities. Freeze a candidate before evaluating an unseen session. A recording used to choose the width can demonstrate fit, but cannot establish generalization.

Inspect `OriginSide`, `SceneEffects`, `RepeatedReversal`, `ShadowRules` and `ShadowReplay` in the originating request. A learned width carries no evidence about the user's intended lane. Include a same-side opening with an empty opposite side, a naturally separate foreground, and an explicit split as controls. Count an actual manual return from its linked gesture. A candidate matching an endpoint in replay demonstrates a geometric prediction.

In placement model v2.3, a gesture can carry a pre-start `StartUsage` observation. Compare independently observed joint work, native text focus and playback within the same pair. A missing label leaves usage unknown. A repeatedly revealed cell can support a visibility preference independently of the side and size. Review the eligible region and each eligible axis separately when comparing alternatives.

### Tune a child surface

Version 4 also supports `SurfaceRules`. Each entry requires `Id`, the host's `HostApp` key, a `MaxAreaRatio` greater than zero and at most 1, measured as child window area divided by host window area, and an `Action` of `local` or `task`. Specify `ChildApp` when the recorded child class has its own identifiable app key. Use `local` for a short, recurring interaction that should stay attached to its host without triggering desktop reflow. Use `task` when the otherwise similar surface should enter normal layout handling. Treat recorded ownership and foreground handoffs as separate evidence about each surface's role.

Use a separate evidence row for every app and surface you tune.

| App and surface keys | Supporting episodes | Counterexample | Candidate and holdout result |
| --- | --- | --- | --- |
| Host `AppKey`, child `AppKey`, recorded role or executable | Session and tick, ownership or focus sequence, rectangles and DPI when available, child/host size and time to release | An independent task from the same app, with distinguishing geometry, ownership, key or handoff | Rule ID and action, why the key and size bounds fit this surface, held-out suppress/release and normal-task outcomes |

Use complete held-out surface episodes to verify both pathways and inspect the `suppress` and `release` decisions. The geometry replay begins with recorded layout requests. A local surface suppressed before request creation cannot be tested by changing the rule file in that replay alone. Verify scheduling with fresh recording or the application's runtime tests.

### Compare solver outcomes

From the WinWindow source checkout that implements the target rule version, run the actual model with both rule sets on the same held-out recording. The ZIP's active rules may have changed between requests. Omitting an override uses each request's saved rules or defaults. Supplying a baseline file and candidate file tests a specific before-and-after configuration. Both runs must use the same model build, archive and session selection.

```text
dotnet build tests/LayoutModel -c Release
dotnet run --project tests/LayoutModel -c Release --no-build -- Holdout.zip baseline.json SESSION evaluation.json > baseline-replay.txt
dotnet run --project tests/LayoutModel -c Release --no-build -- Holdout.zip candidate.json SESSION evaluation.json > candidate-replay.txt
python export/analyze_recording.py compare baseline-replay.txt candidate-replay.txt -o comparison.json
```

Set `SESSION` to `*` (quote it in a shell) if the whole archive is reserved for validation. Repeat for each holdout and its matching baseline. Use the same `evaluation.json` in both runs. It holds the version 4 scene criteria for the references you want to measure, such as the candidate's foreground keys, reference key and visibility floor. The evaluation file observes the two plans. Only the active rule file influences the solver.

The comparison pairs the two regular stdout files by session, request and resizability assumption. Use a model build that emits `Session`, `SceneEffects`, and `EvaluationEffects`. Check `CompletePairing` first. Use `ReferenceEvaluationByAssumption` for the shared reference cohort and the per-session breakdown for context. Joint measurements require the same rule, subject and starting observation in both runs. Each side uses those same denominators for requests and subjects with a loss. A request with several measured references counts once toward the request rate. Null measurements, truncated frames, and missing or changed subjects stay outside that denominator. Zero joint subjects leave the rates and sum unknown. If you omit evaluation for both runs, the comparison still shows active scene traces, which can cover different references and should not be used as a common denominator.

Compare outcome, actual target differences, horizontal and vertical correction error, protected-region retention, scene rule incidence and reference visibility. `LostVisible` measures the original exposure lost even when another part of the window becomes visible. Also look at no-match scenes, quiet controls, same-position switch controls, unlinked edits and undo. Report applicability and sample counts per session and both assumptions. Treat unexplained protected-region regression, exceeded reference loss, or worsened held-out corrections as a reason to narrow or drop the candidate. A replay measures a counterfactual geometry choice. It cannot establish a decrease in real edits or guarantee native placement.

### Evaluate assistance the solver did not provide

Read each session's `MissedAssistance` alongside its ordinary correction and undo counts. `no-system-placement` requires a recorded automatic workspace/opening request with no submitted moves, layer change or minimize and a verified frame before the drag. `no-recorded-request` means a fresh activity keyframe and continuous foreground evidence exist, but the retained log contains no eligible recent request. Authorization, scheduling, and the reason for an absent request may be unknown. A linked prior submission is handled as a correction even if it predates the 30-second `AfterRequest` window.

Compare a single window instance on the same display, work area and DPI. Verify a non-automated start and release, the confirmed manual endpoint, and continuous event-based foreground coverage connecting them with pre-start evidence. The analysis looks back 15 seconds and leaves a 250 ms margin before move/size start for clock alignment and observation latency. It treats focus switches (including switch-away-and-back), local-surface transitions and intervening geometry changes as controls. Older or stale coverage, interrupted verification and untracked focus stay unknown. Same-window continuity is evidence about the foreground task and cannot identify a change of document within that window.

A gesture that resizes an axis contributes evidence for size on that axis. Only a size-preserving translation contributes position evidence there. The endpoint at release labels that episode, including for a later assisted release. The settled frame can incorporate native assistance. Retained exports are deduplicated across files, and the same axis of a short, two-second trial (or a recorder-supplied trial ID) or of an already measured request counts once. The geometric test uses the first eligible end point for each such dimension. Meaningful changes must exceed the larger of four pixels or 1% of the applicable work-area dimension.

To attach replay outputs, keep the same pre-request frames and inputs across the two builds or configurations. Analyze the archives with the corresponding solver stdout files:

```text
python export/analyze_recording.py analyze Holdout.zip --baseline-replay baseline-replay.txt --candidate-replay candidate-replay.txt -o missed.json
```

Repeat each replay option when analyzing several exports together. Check `ReplayCoverage` for both resizability assumptions. `ReplayPairedDimensions` compares keep, baseline, and candidate error against the manual release on the eligible axes, normalized by the work-area dimension. The actual pre-request projection includes the geometry even when the solver chooses to stay put. A solver request made during or after the gesture is outside this comparison.

For `no-recorded-request` cases with a complete pre-start activity keyframe, an optional, separate geometry probe can compare hypothetical solver outputs. The probe archive freezes all usable activity keyframes without reading any gesture when choosing a frame to send to the solver. It preserves recorded window geometry, manageability and foreground state. The solver uses its ordinary workspace geometry path with default or supplied rules. Runtime authorization, input state, attention, local intent, and scheduler confidence are unknown. Keep the results in `ProbeReplayCoverage` and `ProbeReplayPairedDimensions`.

```text
python export/analyze_recording.py probes Holdout.zip -o activity-probes.zip
dotnet run --project tests/LayoutModel -c Release --no-build -- activity-probes.zip baseline.json '*' > baseline-probes.txt
dotnet run --project tests/LayoutModel -c Release --no-build -- activity-probes.zip candidate.json '*' > candidate-probes.txt
python export/analyze_recording.py analyze Holdout.zip --baseline-replay baseline-probes.txt --candidate-replay candidate-probes.txt -o probe-comparison.json
```

Keep the probe ZIP private alongside the source exports. Report counterexamples and unknown coverage alongside any improvements. If the archive contributed to designing the candidate, these comparisons measure fit. Only subsequent real use can measure saved drags, so `ObservedSavedDrags` stays null in offline results.

## Draft and replay behavior rules

Read complete visits using `Session + Segment + Tick`. An event with `Cause: winwindow` is an application action. An `observed` departure has unknown origin. Hidden or tray states alone do not establish closure. Require independently recurring visits, a specific following task and a measured delay. Count rapid returns, other tasks taking over, and the source remaining available as contrary evidence. A process that started before the helper visit contradicts `RequireNewProcess`. Favor minimizing when the source does background work. Consider a close rule only if repeated, sufficiently observed handoffs support closing and users rarely return quickly.

Draft `behavior-rules.json` using the v1 schema in the guide. Give each candidate a stable ID and a concise name. Evaluate on untouched visits in every held-out session:

```text
dotnet run --project tests/LayoutModel -c Release --no-build -- --behavior Holdout.zip behavior-rules.json SESSION
```

Report predicted actions, matching dismissals, rapid returns, actions that would go beyond observed behavior, and cases with incomplete follow-up. Insufficient evidence should leave the candidate unvalidated.

## Deliver and import

Return the rule JSON with an evidence summary, holdout results and any unknowns. If replay could not run, label the candidate as unvalidated and give the commands used or needed. Compare to the current release and use the same installation's app keys. Import `compiled-rules.json` under **Settings → Learn placement** and enable imported placement rules there. Import `behavior-rules.json` under **Settings → Learn behaviors** and enable each reviewed behavior rule individually. Use a WinWindow release supporting the rule file's version.
