using HarmonyLib;
using Model;
using Model.AI;

namespace Conductor
{
    // The Auto Engineer's own station stops (the game's "AI passenger stop" setting) stop a train
    // wherever it thinks right, which is short of the platform when the train is backing up. Conductor
    // stops its trains itself, so for engines it's running the Auto Engineer sees no station ahead.
    [HarmonyPatch(typeof(AutoEngineerPassengerStopper), "UpdateFor")]
    internal static class NoGameStationStops
    {
        private static readonly AccessTools.FieldRef<AutoEngineerComponentBase, BaseLocomotive> Locomotive =
            AccessTools.FieldRefAccess<AutoEngineerComponentBase, BaseLocomotive>("_locomotive");

        private static void Prefix(AutoEngineerPassengerStopper __instance, object[] __args)
        {
            BaseLocomotive loco = Locomotive(__instance);
            if (loco == null)
                return;
            var plan = new Plan(loco);
            if (!plan.Running || !plan.Enabled)
                return;
            // No station ahead and none underneath, as when the game has nothing to stop for.
            __args[0] = null;
            __args[1] = null;
        }
    }
}
