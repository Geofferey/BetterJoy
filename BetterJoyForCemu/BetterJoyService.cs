using System.Diagnostics;
using System.ServiceProcess;

namespace BetterJoyForCemu {
    // Hosts the exact same core pipeline (Program.Start/Stop) as GUI mode, just wired to a
    // HeadlessJoyconHost instead of a MainForm - see EntryPoint.cs for the "-service" switch
    // that runs this instead of the normal WinForms path via ServiceBase.Run(new
    // BetterJoyService()). A session helper handles input hooks and ordinary desktop fallback
    // after login; before login, the service writes controller mouse output directly through
    // FakerInput's virtual HID device (see HeadlessJoyconHost/DesktopInputBackend).
    public class BetterJoyService : ServiceBase {
        private HeadlessJoyconHost host;

        public BetterJoyService() {
            ServiceName = "BetterJoy";
            CanHandleSessionChangeEvent = true;
        }

        protected override void OnStart(string[] args) {
            host = new HeadlessJoyconHost();
            Program.SetHost(host);
            Program.Start();

            LaunchInputHelper();
            host.StartControlServer();
            host.StartConfigWatcher();
        }

        // Program.Stop() already guards the specific failures we know about (e.g. disconnecting
        // a ViGEm target that was never actually plugged in), but an unhandled exception here
        // would otherwise propagate back to the SCM as a failed/hung stop (see
        // ServiceBase.DeferredStop) instead of the service just stopping - belt and suspenders,
        // matching MainForm.ExitApplication()'s same try/catch around GUI shutdown.
        protected override void OnStop() {
            try {
                Program.Stop();
            } catch { } finally {
                if (host != null)
                    host.StopInputRouting();
            }
        }

        // Fires on logon/unlock/console-connect (a session becoming the active one) - relaunch
        // the helper there. We don't bother explicitly killing the old helper on logoff/
        // disconnect: relaunching here always tears down the previous pipe first (see
        // HeadlessJoyconHost.StartNewHelperSession), and the orphaned helper notices its pipe
        // drop and exits itself (see InputHelper.Run), so there's nothing extra to do on the
        // logoff-side events.
        protected override void OnSessionChange(SessionChangeDescription changeDescription) {
            base.OnSessionChange(changeDescription);

            if (changeDescription.Reason == SessionChangeReason.SessionLogon ||
                changeDescription.Reason == SessionChangeReason.ConsoleConnect ||
                changeDescription.Reason == SessionChangeReason.SessionUnlock) {
                LaunchInputHelper();
            }
        }

        private void LaunchInputHelper() {
            if (host == null)
                return;

            string pipeName = host.StartNewHelperSession();
            string commandLine = "\"" + Process.GetCurrentProcess().MainModule.FileName + "\" -inputhelper " + pipeName;

            if (!SessionLauncher.TryLaunchInActiveSession(commandLine, out int processId)) {
                host.AppendTextBox("Could not launch the keyboard/mouse remap helper right now - no interactive session may be available yet. It will retry on the next session change.");
            }
        }
    }
}
