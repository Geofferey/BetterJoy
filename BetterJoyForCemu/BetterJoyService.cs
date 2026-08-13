using System.ServiceProcess;

namespace BetterJoyForCemu {
    // Hosts the exact same core pipeline (Program.Start/Stop) as GUI mode, just wired to a
    // HeadlessJoyconHost instead of a MainForm - see EntryPoint.cs for the "-service" switch
    // that runs this instead of the normal WinForms path via ServiceBase.Run(new
    // BetterJoyService()). Session-change handling (relaunching the keyboard/mouse remap helper
    // into whichever session is active) is wired in SessionLauncher/Milestone 3.
    public class BetterJoyService : ServiceBase {
        public BetterJoyService() {
            ServiceName = "BetterJoy";
            CanHandleSessionChangeEvent = true;
        }

        protected override void OnStart(string[] args) {
            Program.SetHost(new HeadlessJoyconHost());
            Program.Start();
        }

        protected override void OnStop() {
            Program.Stop();
        }
    }
}
