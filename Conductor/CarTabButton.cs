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
            // The button on its own row and the status on the line below: a long status beside the
            // button squeezed it until the label ran over it.
            builder.HStack(row =>
            {
                row.AddButtonCompact("Conductor", () => ConductorWindow.Show(loco));
                row.Spacer();
            });
            builder.AddLabel(() =>
            {
                var plan = new Plan(loco);
                if (!plan.Running)
                    return plan.Enabled ? "" : "<color=#888888>Conductor off</color>";
                return plan.Enabled ? plan.Status : "<color=#E0A040>Conductor disabled</color>";
            }, UIPanelBuilder.Frequency.Periodic);
        }
    }
}
