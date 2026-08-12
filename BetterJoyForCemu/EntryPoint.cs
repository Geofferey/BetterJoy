using System;
using System.IO;
using System.Reflection;

namespace BetterJoyForCemu {
    // Real assembly entry point (see BetterJoy.csproj's StartupObject). This has to run before
    // Program is ever touched: Program.useHIDG is a static field initializer that reads
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

            AppDomain.CurrentDomain.SetData("APP_CONFIG_FILE", userConfigPath);
        }
    }
}
