using System;
using System.IO;
using BepInEx;

namespace ForgeProbe.Be5
{
    [BepInPlugin("com.gala11y.forge.probe5", "ForgeProbeBe5", "0.0.1")]
    public class Plugin : BaseUnityPlugin
    {
        private void Awake()
        {
            try
            {
                var line = DateTimeOffset.Now.ToString("o") + " BE5_PLUGIN_AWAKE runtime=" +
                           Environment.Version + Environment.NewLine;
                File.AppendAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "forge-probe5.txt"), line);
                Logger.LogInfo("FORGE_PROBE_BE5_AWAKE");
            }
            catch (Exception ex)
            {
                Logger.LogError("FORGE_PROBE_BE5_FAIL " + ex);
            }
        }
    }
}
