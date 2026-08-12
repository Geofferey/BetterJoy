using System;
using System.IO;

namespace BetterJoyForCemu {
    // Program Files (where the app is installed) isn't writable without elevation, so all
    // runtime-generated state lives in the per-user AppData folder instead.
    internal static class AppPaths {
        public static readonly string DataDir;

        static AppPaths() {
            DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BetterJoy");
            Directory.CreateDirectory(DataDir);
        }
    }
}
