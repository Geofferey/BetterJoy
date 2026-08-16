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
            var desktopInput = new DesktopInputBackend();
            var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            try {
                pipe.Connect(5000);
            } catch {
                desktopInput.Dispose();
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
                        Execute(msg, desktopInput);
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
            desktopInput.Dispose();
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

        private static void Execute(InputMessage msg, DesktopInputBackend desktopInput) {
            switch (msg.Type) {
                case InputMessageType.SimulateKeyClick:
                    desktopInput.KeyClick(msg.A);
                    break;
                case InputMessageType.SimulateKeyHold:
                    desktopInput.KeyHold(msg.A);
                    break;
                case InputMessageType.SimulateKeyRelease:
                    desktopInput.KeyRelease(msg.A);
                    break;
                case InputMessageType.SimulateButtonClick:
                    desktopInput.ButtonClick(msg.A);
                    break;
                case InputMessageType.SimulateButtonHold:
                    desktopInput.ButtonHold(msg.A);
                    break;
                case InputMessageType.SimulateButtonRelease:
                    desktopInput.ButtonRelease(msg.A);
                    break;
                case InputMessageType.SimulateMoveTo:
                    desktopInput.MoveTo(msg.A, msg.B);
                    break;
                case InputMessageType.SimulateMoveBy:
                    desktopInput.MoveBy(msg.A, msg.B);
                    break;
                case InputMessageType.SimulateCursorMoveBy:
                    desktopInput.CursorMoveBy(msg.A, msg.B);
                    break;
                case InputMessageType.SimulateMoveToScreenCenter:
                    desktopInput.MoveToScreenCenter();
                    break;
                case InputMessageType.SimulateScroll:
                    desktopInput.Scroll(msg.A != 0);
                    break;
            }
        }
    }
}
