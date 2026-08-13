using System;
using System.Configuration;
using System.IO;
using System.Reflection;

namespace BetterJoyForCemu {
    // Real assembly entry point (see BetterJoy.csproj's StartupObject). This has to run before
    // Program is ever touched: Program.useHidHide is a static field initializer that reads
    // ConfigurationManager.AppSettings, and static field initializers run before Main's body
    // when Main lives on that same class - too late to redirect the config path from there.
    internal static class EntryPoint {
        [STAThread]
        static void Main(string[] args) {
            RedirectConfigToAppData();
            Program.Main(args);
        }

        private static void RedirectConfigToAppData() {
            string userConfigPath = Path.Combine(AppPaths.DataDir, "BetterJoyForCemu.exe.config");

            if (!File.Exists(userConfigPath)) {
                string bundledConfigPath = Assembly.GetExecutingAssembly().Location + ".config";
                File.Copy(bundledConfigPath, userConfigPath);
            }

            MigrateHidGuardianSettings(userConfigPath);

            AppDomain.CurrentDomain.SetData("APP_CONFIG_FILE", userConfigPath);
        }

        // One-off migration for users upgrading from before the HidGuardian -> HidHide switch:
        // their AppData config predates the UseHidHide key (fresh installs get it from the
        // bundled template above and skip this entirely). Preserves their old UseHIDG value as
        // the new key's default rather than silently resetting it, then drops the settings that
        // no longer mean anything under HidHide.
        private static void MigrateHidGuardianSettings(string userConfigPath) {
            var fileMap = new ExeConfigurationFileMap { ExeConfigFilename = userConfigPath };
            Configuration config = ConfigurationManager.OpenMappedExeConfiguration(fileMap, ConfigurationUserLevel.None);
            KeyValueConfigurationCollection settings = config.AppSettings.Settings;

            if (settings["UseHidHide"] != null)
                return;

            string legacyValue = settings["UseHIDG"] != null ? settings["UseHIDG"].Value : "false";
            settings.Add("UseHidHide", legacyValue);
            settings.Remove("UseHIDG");
            settings.Remove("PurgeWhitelist");
            settings.Remove("PurgeAffectedDevices");

            config.Save(ConfigurationSaveMode.Modified);
        }
    }
}
