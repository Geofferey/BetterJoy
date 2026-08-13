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
            MigratePassiveScanSettings(userConfigPath);
            AddMissingSettingsFromTemplate(userConfigPath);

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

        // One-off migration for users upgrading from before PassiveScan/StartInTray moved from
        // Config.cs's own flat settings file into App.config (so they show up in the settings
        // panel like everything else). Their AppData config predates both keys entirely - fresh
        // installs get them from the bundled template and skip this. Config.cs itself is left
        // untouched (still writes its own now-unused copies), so its value is read here directly
        // rather than through Config.Init(), which hasn't run yet at this point in startup.
        private static void MigratePassiveScanSettings(string userConfigPath) {
            var fileMap = new ExeConfigurationFileMap { ExeConfigFilename = userConfigPath };
            Configuration config = ConfigurationManager.OpenMappedExeConfiguration(fileMap, ConfigurationUserLevel.None);
            KeyValueConfigurationCollection settings = config.AppSettings.Settings;

            if (settings["PassiveScan"] != null)
                return;

            string legacySettingsPath = Path.Combine(AppPaths.DataDir, "settings");
            string legacyPassiveScan = ReadLegacySettingsValue(legacySettingsPath, "ProgressiveScan");
            string legacyStartInTray = ReadLegacySettingsValue(legacySettingsPath, "StartInTray");

            settings.Add("PassiveScan", legacyPassiveScan == "0" ? "false" : "true");
            settings.Add("StartInTray", legacyStartInTray == "1" ? "true" : "false");

            config.Save(ConfigurationSaveMode.Modified);
        }

        private static string ReadLegacySettingsValue(string path, string key) {
            if (!File.Exists(path))
                return null;

            foreach (string line in File.ReadAllLines(path)) {
                string[] parts = line.Split(new[] { ' ' }, 2);
                if (parts.Length == 2 && parts[0] == key)
                    return parts[1];
            }

            return null;
        }

        // Catch-all safety net, run after the migrations above: adds any key present in the
        // bundled template's App.config but missing from the user's copy, seeded with the
        // template's default. Existing users only ever get their AppData config seeded once (see
        // the copy-if-missing check above), so every plain new setting added after that point
        // would otherwise be missing entirely and crash the first thing that Boolean.Parses it -
        // exactly what happened when PassiveScan/StartInTray shipped without this. Doesn't handle
        // renames/value-preservation (that's what the bespoke migrations above are for) - just
        // makes sure a brand new key is never simply absent.
        private static void AddMissingSettingsFromTemplate(string userConfigPath) {
            string bundledConfigPath = Assembly.GetExecutingAssembly().Location + ".config";
            var bundledMap = new ExeConfigurationFileMap { ExeConfigFilename = bundledConfigPath };
            Configuration bundledConfig = ConfigurationManager.OpenMappedExeConfiguration(bundledMap, ConfigurationUserLevel.None);

            var userMap = new ExeConfigurationFileMap { ExeConfigFilename = userConfigPath };
            Configuration userConfig = ConfigurationManager.OpenMappedExeConfiguration(userMap, ConfigurationUserLevel.None);

            bool changed = false;
            foreach (KeyValueConfigurationElement bundledSetting in bundledConfig.AppSettings.Settings) {
                if (userConfig.AppSettings.Settings[bundledSetting.Key] == null) {
                    userConfig.AppSettings.Settings.Add(bundledSetting.Key, bundledSetting.Value);
                    changed = true;
                }
            }

            if (changed)
                userConfig.Save(ConfigurationSaveMode.Modified);
        }
    }
}
