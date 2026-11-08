using System;
using HarmonyLib;

namespace Conductor
{
    internal static class Patcher
    {
        private static Harmony _harmony;

        public static void Patch(string id)
        {
            _harmony = new Harmony(id);
            try
            {
                _harmony.PatchAll(typeof(Patcher).Assembly);
                ModEntry.Log?.Info("Conductor: added the Conductor button to engines' Car tab.");
            }
            catch (Exception ex)
            {
                // A game update renamed something: leave the game untouched.
                _harmony.UnpatchAll(id);
                _harmony = null;
                ModEntry.Log?.Error("Conductor: patching failed, no Conductor button: " + ex.Message);
            }
        }

        public static void Unpatch()
        {
            _harmony?.UnpatchAll(_harmony.Id);
            _harmony = null;
        }
    }
}
