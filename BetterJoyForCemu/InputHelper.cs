using System.IO;
using System.IO.Pipes;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace BetterJoyForCemu {
    // Runs as a separate BetterJoyForCemu.exe -inputhelper <pipeName> process, launched by
    // BetterJoyService (via SessionLauncher) into whichever session is currently active. Its only
    // job is the desktop-bound half of keyboard/mouse remap that Session 0 can't do itself:
    // capture global key/mouse events and forward them to the service, and execute Simulate
    // commands the service sends back. No config/decision logic lives here at all - see
    // HeadlessJoyconHost (the other end of the pipe) and Program.OnKeyDown/OnKeyUp/
    // OnMouseButtonDown/OnMouseButtonUp for that.
    internal static class InputHelper {
        public static void Run(string pipeName) {
            var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            try {
                pipe.Connect(5000);
            } catch {
                return; // service isn't listening (torn down/superseded before we connected)
            }

            var reader = new BinaryReader(pipe);
            var writer = new BinaryWriter(pipe);
            var writeLock = new object();

            var keyboard = WindowsInput.Capture.Global.KeyboardAsync();
            keyboard.KeyEvent += (sender, e) => {
                if (e.Data.KeyDown != null) SendSafe(pipe, writer, writeLock, InputMessageType.KeyDown, (int)e.Data.KeyDown.Key);
                if (e.Data.KeyUp != null) SendSafe(pipe, writer, writeLock, InputMessageType.KeyUp, (int)e.Data.KeyUp.Key);
            };

            var mouse = WindowsInput.Capture.Global.MouseAsync();
            mouse.MouseEvent += (sender, e) => {
                if (e.Data.ButtonDown != null) SendSafe(pipe, writer, writeLock, InputMessageType.MouseButtonDown, (int)e.Data.ButtonDown.Button);
                if (e.Data.ButtonUp != null) SendSafe(pipe, writer, writeLock, InputMessageType.MouseButtonUp, (int)e.Data.ButtonUp.Button);
            };

            // Global hooks need a message pump on this thread to deliver events at all - there's
            // no visible window (nothing is ever Show()n), just the pump itself. Exits as soon as
            // the pipe drops, which happens whenever the service tears this session's connection
            // down (session change, service stop, etc.) - see HeadlessJoyconHost.StartNewHelperSession.
            var context = new ApplicationContext();

            // Must be created on this thread, before Application.Run starts pumping - it's what
            // lets the read-loop task below (a different thread) safely call back onto this one.
            // context.ExitThread() called directly from that background thread doesn't reliably
            // stop the message loop: the PostQuitMessage it triggers targets whichever thread
            // calls it, not necessarily the one actually running Application.Run, so the process
            // (and its global keyboard/mouse hooks) could stay alive indefinitely after the pipe
            // dropped - accumulating an orphaned helper on every session change.
            var syncContext = new WindowsFormsSynchronizationContext();

            Task.Run(() => {
                try {
                    while (pipe.IsConnected) {
                        InputMessage msg = InputMessage.ReadFrom(reader);
                        Execute(msg);
                    }
                } catch {
                    // pipe closed/service gone - fall through and stop the message pump below
                } finally {
                    syncContext.Post(_ => context.ExitThread(), null);
                }
            });

            Application.Run(context);

            keyboard.Dispose();
            mouse.Dispose();
            try { pipe.Dispose(); } catch { }
        }

        private static void SendSafe(NamedPipeClientStream pipe, BinaryWriter writer, object writeLock, InputMessageType type, int code) {
            lock (writeLock) {
                if (!pipe.IsConnected)
                    return;

                try {
                    new InputMessage { Type = type, A = code }.WriteTo(writer);
                    writer.Flush();
                } catch {
                    // best-effort - a mid-write disconnect just drops this one event
                }
            }
        }

        private static void Execute(InputMessage msg) {
            switch (msg.Type) {
                case InputMessageType.SimulateKeyClick:
                    WindowsInput.Simulate.Events().Click((WindowsInput.Events.KeyCode)msg.A).Invoke();
                    break;
                case InputMessageType.SimulateKeyHold:
                    WindowsInput.Simulate.Events().Hold((WindowsInput.Events.KeyCode)msg.A).Invoke();
                    break;
                case InputMessageType.SimulateKeyRelease:
                    WindowsInput.Simulate.Events().Release((WindowsInput.Events.KeyCode)msg.A).Invoke();
                    break;
                case InputMessageType.SimulateButtonClick:
                    WindowsInput.Simulate.Events().Click((WindowsInput.Events.ButtonCode)msg.A).Invoke();
                    break;
                case InputMessageType.SimulateButtonHold:
                    WindowsInput.Simulate.Events().Hold((WindowsInput.Events.ButtonCode)msg.A).Invoke();
                    break;
                case InputMessageType.SimulateButtonRelease:
                    WindowsInput.Simulate.Events().Release((WindowsInput.Events.ButtonCode)msg.A).Invoke();
                    break;
                case InputMessageType.SimulateMoveTo:
                    WindowsInput.Simulate.Events().MoveTo(msg.A, msg.B).Invoke();
                    break;
                case InputMessageType.SimulateMoveBy:
                    WindowsInput.Simulate.Events().MoveBy(msg.A, msg.B).Invoke();
                    break;
                case InputMessageType.SimulateMoveToScreenCenter:
                    WindowsInput.Simulate.Events().MoveTo(Screen.PrimaryScreen.Bounds.Width / 2, Screen.PrimaryScreen.Bounds.Height / 2).Invoke();
                    break;
                case InputMessageType.SimulateScroll:
                    WindowsInput.Simulate.Events().Scroll(WindowsInput.Events.ButtonCode.VScroll, msg.A != 0 ? WindowsInput.Events.ButtonScrollDirection.Forwards : WindowsInput.Events.ButtonScrollDirection.Backwards).Invoke();
                    break;
            }
        }
    }
}
