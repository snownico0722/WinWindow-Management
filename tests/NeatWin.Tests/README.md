# NeatWin layout regression tests

This project carries the NeatWin automated tests that exercise code now hosted by
`NeatWin.Layout`. It keeps the original test cases and test names for the shared
layout engine, task model, recording/reference logic, automatic-layout scheduler,
undo safety, and window-layer protection.

Run on Windows with the .NET 10 SDK:

```powershell
dotnet test tests/NeatWin.Tests/NeatWin.Tests.csproj -c Release
```

The suite intentionally excludes tests coupled to the retired standalone NeatWin
window/recorder UI, its application manifest, or implementation classes that are
not part of the integrated layout assembly. Those are not silently replaced with
tests for different behavior.
