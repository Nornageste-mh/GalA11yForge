using System;
using System.IO;
using BepInEx;
using BepInEx.Unity.Mono;

namespace ForgeProbe.Be6
{
    [BepInPlugin("com.gala11y.forge.probe6", "ForgeProbeBe6", "0.0.1")]
    public class Plugin : BaseUnityPlugin
    {
        private void Awake()
        {
            try
            {
                var line = DateTimeOffset.Now.ToString("o") + " BE6_PLUGIN_AWAKE runtime=" +
                           Environment.Version + Environment.NewLine;
                File.AppendAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "forge-probe6.txt"), line);
                Logger.LogInfo("FORGE_PROBE_BE6_AWAKE");
            }
            catch (Exception ex)
            {
                Logger.LogError("FORGE_PROBE_BE6_FAIL " + ex);
            }
        }
    }
}
