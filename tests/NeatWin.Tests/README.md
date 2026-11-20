# NeatWin layout regression tests

This project carries the NeatWin automated tests that exercise code now hosted by
`NeatWin.Layout`.

It preserves 21 original test classes in full and retains another 24 applicable
regression cases from three original suites whose remaining cases target removed
legacy planner APIs. The suite covers layout planning, task/occlusion models,
automatic-layout scheduling, follow-hand behavior, video-aspect correction,
recording/reference logic, undo safety, and window-layer protection.

Run on Windows with the .NET 10 SDK:

```powershell
dotnet test tests/NeatWin.Tests/NeatWin.Tests.csproj -c Release
```

Tests tied exclusively to the retired standalone NeatWin window/recorder UI,
application manifest, or superseded planner implementations are excluded. The
integrated app's native and WPF behavior is covered by the packaged smoke tests.
