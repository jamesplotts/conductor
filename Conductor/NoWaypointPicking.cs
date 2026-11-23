using HarmonyLib;
using Model;
using UI.Common;
using UI.EngineControls;
using UI.Waypoints;
using UnityEngine.UI;

namespace Conductor
{
    // While Conductor is driving an engine, the Choose Waypoint button (and its key) would give the
    // Auto Engineer a waypoint of its own and Conductor would hand the train over. Grey the button out
    // and ignore the key for those engines; turn Conductor off (or Stop it) to pick a waypoint again.
    internal static class Conducted
    {
        public static bool Is(BaseLocomotive loco)
        {
            if (loco == null)
                return false;
            var plan = new Plan(loco);
            return plan.Enabled && plan.Running;
        }
    }

    [HarmonyPatch(typeof(AutoEngineerDestinationPicker), nameof(AutoEngineerDestinationPicker.StartPickingLocation))]
    internal static class NoWaypointPicking
    {
        private static bool Prefix(BaseLocomotive locomotive)
        {
            if (!Conducted.Is(locomotive))
                return true;
            Toast.Present($"Conductor is driving {locomotive.DisplayName}. Stop it in the Conductor window to set a waypoint.");
            return false;
        }
    }

    [HarmonyPatch(typeof(AutoEngineerWaypointControls), "UpdateControls")]
    internal static class GreyWaypointButton
    {
        private static readonly AccessTools.FieldRef<AutoEngineerWaypointControls, Button> SetButton =
            AccessTools.FieldRefAccess<AutoEngineerWaypointControls, Button>("setWaypointButton");
        private static readonly System.Func<EngineControlSetBase, BaseLocomotive> LocomotiveOf =
            AccessTools.MethodDelegate<System.Func<EngineControlSetBase, BaseLocomotive>>(AccessTools.PropertyGetter(typeof(EngineControlSetBase), "Locomotive"));

        private static void Postfix(AutoEngineerWaypointControls __instance)
        {
            Button button = SetButton(__instance);
            if (button == null)
                return;
            BaseLocomotive loco = LocomotiveOf(__instance);
            bool conducted = Conducted.Is(loco);
            if (button.interactable == conducted)
                button.interactable = !conducted;
        }
    }
}
