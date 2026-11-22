using NeatWin.App;
using NeatWin.Core;

namespace NeatWin.Tests;

public sealed class AutomationSettingsTests
{
    [Fact]
    public void SettingsPreserveExistingGrantsAndRoundTripNewLightMode()
    {
        var dir = Path.Combine(Path.GetTempPath(), "NeatWin-mode-test-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "settings.json");
            File.WriteAllText(path, "{\"AutoTidyEnabled\":true,\"NeighborSnapDistance\":33}");
            var store = new SettingsStore(path);
            Assert.Equal(AutomaticLayoutMode.LightAssist, store.LoadAutomaticLayoutMode());
            Assert.Equal(AutomaticLayoutMode.Light, AutomaticLayoutPolicy.VisibleChoice(store.LoadAutomaticLayoutMode()));
            Assert.Null(AutomaticLayoutPolicy.Route(store.LoadAutomaticLayoutMode(), LayoutTrigger.NewTaskOpening));
            foreach (var mode in Enum.GetValues<AutomaticLayoutMode>())
            {
                store.SaveAutomaticLayoutMode(mode);
                var reloaded = new SettingsStore(path);
                Assert.Equal(mode, reloaded.LoadAutomaticLayoutMode());
                Assert.Equal(33, reloaded.LoadTidyOptions().NeighborSnapDistance);
            }
        }
        finally { Directory.Delete(dir, true); }
    }
}
