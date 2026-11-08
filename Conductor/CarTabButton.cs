using HarmonyLib;
using Model;
using UI.Builder;
using UI.CarInspector;

namespace Conductor
{
    // A Conductor button and status line on an engine's Car tab.
    [HarmonyPatch(typeof(CarInspector), "PopulateCarPanel")]
    internal static class CarTabButton
    {
        private static void Postfix(UIPanelBuilder builder, Car ____car)
        {
            if (!(____car is BaseLocomotive loco) || loco.CarType.IsTender)
                return;
            builder.Spacer(6f);
            builder.HStack(row =>
            {
                row.AddButtonCompact("Conductor", () => ConductorWindow.Show(loco));
                row.Spacer(8f);
                row.AddLabel(() =>
                {
                    var plan = new Plan(loco);
                    if (!plan.Running)
                        return "";
                    return plan.Enabled ? plan.Status : "<color=#E0A040>Conductor disabled</color>";
                }, UIPanelBuilder.Frequency.Periodic);
            });
        }
    }
}
