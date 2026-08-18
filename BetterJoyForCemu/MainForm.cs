using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.Xbox360;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Configuration;
using System.Data;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.ServiceProcess;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Xml.Linq;

namespace BetterJoyForCemu {
    public partial class MainForm : Form, IJoyconHost {
        public bool allowCalibration = Boolean.Parse(ConfigurationManager.AppSettings["AllowCalibration"]);
        public List<Button> con, loc;
        private bool calibrationInProgress = false;
        private Joycon calibratingJoycon;
        private Timer countDown;
        private int count;
        private Timer clickTimer;
        private readonly DesktopInputBackend desktopInput;
        private readonly string[] displayedConfigKeys;

        // When a Windows Service already owns the hardware (see ServiceControlProtocol/
        // HeadlessJoyconHost), this GUI never runs its own HID/ViGEm pipeline at all - it just
        // shows live status pushed over ServiceControlClient and forwards a handful of commands
        // (rumble test/join-split/calibration) instead of acting on a live Joycon directly.
        // Decided once in MainForm_Load; not re-evaluated mid-session.
        private bool isRemoteMode = false;
        private ServiceControlClient serviceClient;
        private List<ControllerRecord> lastControllerSnapshot = new List<ControllerRecord>();

        public enum NonOriginalController : int {
            Disabled = 0,
            DefaultCalibration = 1,
            ControllerCalibration = 2,
        }

        public MainForm() {
            desktopInput = new DesktopInputBackend();
            clickTimer = new Timer { Interval = 250 };
            clickTimer.Tick += ClickTimer_Tick;

            InitializeComponent();

            // Read from the assembly instead of hardcoding a string here, so this can't drift
            // out of sync with AssemblyInfo.cs's version the way the old static Designer text did.
            version_lbl.Text = "v" + Assembly.GetExecutingAssembly().GetName().Version.ToString(3);

            if (AppPaths.ServiceModeEnabled) {
                btn_enableServiceMode.Text = "Config Synced with Service";
                btn_enableServiceMode.Enabled = false;
            }

            con = new List<Button> { con1, con2, con3, con4 };
            loc = new List<Button> { loc1, loc2, loc3, loc4 };

            // Wired once here (rather than per-connect in Program.cs) so empty slots stay
            // hoverable/clickable - they start with Tag == null and never get disabled, and
            // conBtnMouseClick/MouseEnter/MouseLeave branch on that to offer "add a controller"
            // instead of the connected-controller behavior. Uses MouseUp rather than MouseClick:
            // Button/ButtonBase only synthesizes the compound Click/MouseClick event for the
            // left mouse button, so a right-click handler on MouseClick silently never fires.
            // MouseDown/MouseUp aren't synthesized that way and reliably report e.Button for
            // any button.
            foreach (Button v in con) {
                v.MouseUp += new MouseEventHandler(conBtnMouseClick);
                v.MouseEnter += new EventHandler(conBtnMouseEnter);
                v.MouseLeave += new EventHandler(conBtnMouseLeave);
                SetEmptySlotTooltip(v);
            }

            // Gyro outputs are now selected independently in Controller Profiles. Keep the old
            // key in App.config only as a one-time compatibility hint for legacy mappings; it is
            // no longer a runtime setting and should not be editable here.
            displayedConfigKeys = ConfigurationManager.AppSettings.AllKeys
                .Where(key => key != "GyroToJoyOrMouse")
                .ToArray();
            Size childSize = new Size(150, 20);
            for (int i = 0; i != displayedConfigKeys.Length; i++) {
                settingsTable.RowCount++;
                settingsTable.Controls.Add(new Label() { Text = displayedConfigKeys[i], TextAlign = ContentAlignment.BottomLeft, AutoEllipsis = true, Size = childSize }, 0, i);

                var value = ConfigurationManager.AppSettings[displayedConfigKeys[i]];
                Control childControl;
                if (value == "true" || value == "false") {
                    // MouseClick is correct here - a click on a checkbox already IS the new
                    // value, nothing to wait for.
                    childControl = new CheckBox() { Checked = Boolean.Parse(value), Size = childSize };
                    childControl.MouseClick += cbBox_Changed;
                } else {
                    // Leave, not MouseClick - a text field's new value only exists once the user
                    // has actually finished typing it, not the instant they click into the box
                    // (which fires with whatever text was already sitting there, before any of
                    // the edit happens). MouseClick here meant typing a new value and tabbing/
                    // clicking away never saved it at all - cbBox_Changed only ever saw the old
                    // text, from that very first click.
                    childControl = new TextBox() { Text = value, Size = childSize };
                    childControl.Leave += cbBox_Changed;
                }

                settingsTable.Controls.Add(childControl, 1, i);
            }
        }

        private bool isExiting = false;

        private void HideToTray() {
            if (isExiting) return;
            this.WindowState = FormWindowState.Minimized;
            notifyIcon.Visible = true;
            notifyIcon.BalloonTipText = "Double click the tray icon to maximise!";
            notifyIcon.ShowBalloonTip(0);
            this.ShowInTaskbar = false;
            this.Hide();
        }

        private void ShowFromTray() {
            if (isExiting) return;
            this.Show();
            this.WindowState = FormWindowState.Normal;
            this.ShowInTaskbar = true;
            this.FormBorderStyle = FormBorderStyle.FixedSingle;
            this.Icon = Properties.Resources.betterjoyforcemu_icon;
            notifyIcon.Visible = false;
        }

        private void MainForm_Resize(object sender, EventArgs e) {
            if (isExiting) return;
            if (this.WindowState == FormWindowState.Minimized) {
                HideToTray();
            }
        }

        private void notifyIcon_MouseDoubleClick(object sender, MouseEventArgs e) {
            if (isExiting) return;
            ShowFromTray();
        }

        private void MainForm_Load(object sender, EventArgs e) {
            // Deferring hardware ownership to a running service is NOT conditional on config
            // sync being opted into - those are separate concerns (one prevents two processes
            // fighting over the same physical device and creating a duplicate virtual
            // controller; the other just controls whether settings changes here reach the
            // service). Gating this on AppPaths.ServiceModeEnabled meant a GUI that never had
            // "Sync Config with Service" clicked would always run its own pipeline even with the
            // service already running - exactly the duplicate-controller bug this exists to fix.
            bool serviceReportedRunning = IsBetterJoyServiceRunning();
            if (serviceReportedRunning) {
                serviceClient = new ServiceControlClient();
                isRemoteMode = TryConnectWithRetries(serviceClient);
            }

            if (isRemoteMode) {
                WireServiceClientEvents();

                // Config.Init() normally runs inside Program.Start() (see its comment there for
                // why) - remote mode never calls that, so without this, Config.variables stays
                // completely empty and every Config.Value(...) lookup returns "". Controller Profiles
                // (Reassign.GetPrettyName) doesn't guard against that, so it crashed with
                // ArgumentOutOfRangeException the moment anyone opened it in remote mode.
                Config.Init(CalibrationState.CaliData, CalibrationState.StickCaliData, CalibrationState.Stick2CaliData);

                // Add Controllers/blacklist (_3rdPartyControllers dialog) reads/edits these two
                // in-memory lists - normally populated by Program.Start()'s GUI branch, which
                // never runs here. Load them the same way headless mode does, so the dialog
                // isn't working from an empty list.
                _3rdPartyControllers.LoadIntoProgramLists();

                AppendTextBox("Connected to the BetterJoy service - it owns the controllers; this window shows live status only.\r\n");
                if (!AppPaths.ServiceModeEnabled)
                    AppendTextBox("Config isn't synced with the service yet - settings/remap changes made here won't reach it until you use \"Sync Config with Service\".\r\n");

                serviceClient.RequestSnapshot();
            } else if (serviceReportedRunning) {
                // The SCM says the service is Running, but its control pipe never answered
                // after retries - an ambiguous state (AV/firewall interference, pipe instance
                // exhaustion, some transient error), not evidence the service is actually down.
                // Falling back to a local pipeline here would risk exactly the duplicate-
                // controller conflict this whole mode exists to prevent, so refuse instead:
                // no local pipeline, no remote status - just wait for a restart of either side.
                AppendTextBox("The BetterJoy service appears to be running, but its status connection couldn't be reached. Not starting local controller support, to avoid conflicting with it - restart BetterJoy (or the service) to retry.\r\n");
                MessageBox.Show(
                    "The BetterJoy service appears to be running, but this window couldn't reach its status connection. " +
                    "To avoid creating a duplicate virtual controller, this window will not take over the controllers itself.\r\n\r\n" +
                    "Restart BetterJoy (or the service) to retry.",
                    "BetterJoy");
            } else {
                Program.Start();
            }

            console.Visible = !Boolean.Parse(ConfigurationManager.AppSettings["HideStatus"]);
            if (!console.Visible) {
                // Close up the gap console leaves behind by pulling the settings gear/version
                // label and profile button up into its row instead of leaving them down where
                // console used to end.
                // console.Top itself is the stable anchor here - it doesn't change when Visible
                // is toggled off. The form is AutoSize/GrowAndShrink, so it naturally shrinks to
                // fit afterward - and grows back to fit rightPanel when settings gets toggled
                // open later, since that's sized independently of this.
                btn_settings.Top = console.Top;
                version_lbl.Top = console.Top;
            }

            // Keep the two utility icons as one unit after the HideStatus layout adjustment (and
            // after WinForms DPI scaling). The Designer coordinates alone leave Profiles behind
            // at the old bottom row when the status console is hidden.
            btn_controllerProfiles.Location = new Point(
                btn_settings.Left - btn_controllerProfiles.Width - 6,
                btn_settings.Top);

            if (Boolean.Parse(ConfigurationManager.AppSettings["StartInTray"])) {
                HideToTray();
            } else {
                ShowFromTray();
            }
        }

        private void MainForm_FormClosing(object sender, FormClosingEventArgs e) {
            ExitApplication();
        }

        private void exitToolStripMenuItem_Click(object sender, EventArgs e) {
            ExitApplication();
        }

        // Single exit path, guarded against being entered twice (Close() re-enters via
        // MainForm_FormClosing, and Program.Stop()'s cleanup - disposing hooks, stopping the UDP
        // server, disconnecting Joycons - isn't safe to run twice). Termination itself must not
        // be skippable by a cleanup failure, so it happens in a finally rather than after Stop().
        private void ExitApplication() {
            if (isExiting) return;
            isExiting = true;

            notifyIcon.Visible = false; // remove the tray icon immediately so no further tray messages can reach it

            try {
                // In remote mode Program.Start() was never called - mgr/server are still null,
                // so there's nothing of ours to tear down here; the service keeps running
                // independently of this window closing, and the pipe closes with the process.
                if (!isRemoteMode)
                    Program.Stop();
            } catch { } finally {
                desktopInput.Dispose();
                Environment.Exit(0);
            }
        }

        private void linkLabel1_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e) {
            donationLink.LinkVisited = true;
            System.Diagnostics.Process.Start("http://paypal.me/DavidKhachaturov/5");
        }

        // AssignSlot/CollapseJoinedPair/HandleJoyconDropped are called from the scan thread
        // while it holds JoyconManager's scanLock (see Program.cs) - a synchronous this.Invoke
        // here would block the scan thread until the UI thread services it, but the UI thread's
        // own shutdown path (ExitApplication -> Program.Stop -> StopScanning) blocks trying to
        // ACQUIRE that same scanLock. If both happen at once, neither side can proceed: the UI
        // thread can't pump messages to service the Invoke while it's stuck in a plain lock
        // statement, and the scan thread can't release scanLock until Invoke returns - a
        // deadlock that meant closing the GUI could hang indefinitely instead of exiting.
        // BeginInvoke doesn't block the caller, breaking that cycle; the null/IsDisposed guards
        // handle the queued callback finally running after the form has already closed.
        private void SafeBeginInvoke(Action action) {
            if (IsDisposed)
                return;
            try {
                BeginInvoke(action);
            } catch (ObjectDisposedException) {
                // form closed between the check above and this call - nothing to update
            } catch (InvalidOperationException) {
                // handle not created / thread has no message loop - also fine to skip
            }
        }

        public void AppendTextBox(string value) { // https://stackoverflow.com/questions/519233/writing-to-a-textbox-from-another-thread
            if (InvokeRequired) {
                this.Invoke(new Action<string>(AppendTextBox), new object[] { value });
                return;
            }
            console.AppendText(value);
        }

        // GUI and service-helper modes share the same optional virtual-HID backend so elevated
        // windows behave identically regardless of which process currently owns the controllers.
        public void SimulateKeyClick(int keyCode) {
            desktopInput.KeyClick(keyCode);
        }

        public void SimulateKeyHold(int keyCode) {
            desktopInput.KeyHold(keyCode);
        }

        public void SimulateKeyRelease(int keyCode) {
            desktopInput.KeyRelease(keyCode);
        }

        public void SimulateButtonClick(int buttonCode) {
            desktopInput.ButtonClick(buttonCode);
        }

        public void SimulateButtonHold(int buttonCode) {
            desktopInput.ButtonHold(buttonCode);
        }

        public void SimulateButtonRelease(int buttonCode) {
            desktopInput.ButtonRelease(buttonCode);
        }

        public void SimulateMoveTo(int x, int y) {
            desktopInput.MoveTo(x, y);
        }

        public void SimulateMoveBy(int dx, int dy) {
            desktopInput.MoveBy(dx, dy);
        }

        public void SimulateCursorMoveBy(int dx, int dy) {
            desktopInput.CursorMoveBy(dx, dy);
        }

        public void SimulateWrappedCursorMoveBy(int dx, int dy) {
            desktopInput.WrappedCursorMoveBy(dx, dy);
        }

        public void SimulateMoveToScreenCenter() {
            desktopInput.MoveToScreenCenter();
        }

        public void SimulateScroll(bool up) {
            desktopInput.Scroll(up);
        }

        private static bool IsBetterJoyServiceRunning() {
            try {
                using (var sc = new ServiceController("BetterJoy")) {
                    return sc.Status == ServiceControllerStatus.Running;
                }
            } catch {
                return false; // not installed, access denied, etc. - fall back to running locally
            }
        }

        // A handful of quick retries covers the narrow legitimate race (the service was
        // reported Running an instant before its control pipe is actually ready to accept -
        // though in practice StartControlServer runs synchronously before OnStart even returns,
        // so this window is mostly theoretical) without turning a startup that's ultimately
        // going to fail into a long hang.
        private static bool TryConnectWithRetries(ServiceControlClient client, int attempts = 3, int perAttemptTimeoutMs = 1000, int delayBetweenAttemptsMs = 500) {
            for (int attempt = 0; attempt < attempts; attempt++) {
                if (client.Connect(perAttemptTimeoutMs))
                    return true;

                if (attempt < attempts - 1)
                    System.Threading.Thread.Sleep(delayBetweenAttemptsMs);
            }
            return false;
        }

        // All ServiceControlClient events fire from a background read thread - each handler
        // marshals onto the UI thread itself (RenderSnapshot does its own Invoke check; the
        // rest are simple enough to wrap inline here).
        private void WireServiceClientEvents() {
            serviceClient.SnapshotReceived += RenderSnapshot;

            serviceClient.CalibrationStarted += padId => this.Invoke(new MethodInvoker(delegate {
                console.Text = "Calibration started." + "\r\n";
                if (calibDialog == null) {
                    calibDialog = new CalibrationDialog();
                    calibDialog.ButtonClicked += OnRemoteCalibButtonClicked;
                    calibDialog.Show(this);
                }
            }));

            // Step name/instruction/UI mode all come from the service (HeadlessJoyconHost's
            // StartCalibration) - calibDialog here is a passive renderer, same as it is for the
            // local flow, just fed over the pipe instead of driven by a local state machine. The
            // service sends one message per second for the gyro countdown, so there's no need
            // for a local cosmetic timer the way an earlier version of this had - the displayed
            // number is always exactly what the service just said.
            serviceClient.CalibrationStep += step => this.Invoke(new MethodInvoker(delegate {
                if (calibDialog == null) {
                    calibDialog = new CalibrationDialog();
                    calibDialog.ButtonClicked += OnRemoteCalibButtonClicked;
                    calibDialog.Show(this);
                }
                calibDialog.SetStep(step.StepNumber, step.TotalSteps, step.StepName);
                calibDialog.SetInstruction(step.Instruction);
                remoteCalibPadId = step.PadId;

                switch (step.UiMode) {
                    case CalibStepUiMode.Start:
                        calibDialog.ShowStartPrompt();
                        break;
                    case CalibStepUiMode.Done:
                        calibDialog.ShowDonePrompt();
                        break;
                    case CalibStepUiMode.Countdown:
                        calibDialog.ShowCountdown(step.Count);
                        break;
                }
            }));

            serviceClient.CalibrationComplete += padId => this.Invoke(new MethodInvoker(delegate {
                console.Text += "\r\nCalibration completed!!!\r\n";
                RestoreCalibrateIcon();
                CloseRemoteCalibDialog("Calibration complete!");
            }));

            serviceClient.CalibrationFailed += padId => this.Invoke(new MethodInvoker(delegate {
                console.Text += "\r\nCalibration failed - was the controller disconnected?\r\n";
                RestoreCalibrateIcon();
                CloseRemoteCalibDialog("Failed - was the controller disconnected?");
            }));

            serviceClient.Disconnected += () => this.Invoke(new MethodInvoker(delegate {
                AppendTextBox("Lost connection to the BetterJoy service.\r\n");
                CloseRemoteCalibDialog("Lost connection to the service.");
            }));
        }

        private int remoteCalibPadId;

        // The dialog's one button always just reports "clicked" back to the service as
        // CalibrationReady, whether that means Start or Done - the service knows which one it
        // asked for (it's the only thing that can be pending), same as it's the sole source of
        // truth for what the button currently means (see the CalibrationStep handler above).
        private void OnRemoteCalibButtonClicked() {
            calibDialog.HidePrompt();
            serviceClient.CalibrationReady(remoteCalibPadId);
        }

        private void CloseRemoteCalibDialog(string message) {
            CalibrationDialog dialogToClose = calibDialog;
            calibDialog = null;
            if (dialogToClose == null)
                return;

            dialogToClose.ShowResult(message);
            Timer closeTimer = new Timer { Interval = 1500 };
            closeTimer.Tick += (s, e) => {
                closeTimer.Stop();
                closeTimer.Dispose();
                dialogToClose.Close();
            };
            closeTimer.Start();
        }

        // Full re-render from a snapshot rather than incremental diffing against previous state
        // - simpler and self-healing, and snapshots only arrive when something actually changed
        // (see HeadlessJoyconHost.BroadcastSnapshot), not continuously.
        private void RenderSnapshot(List<ControllerRecord> records) {
            if (InvokeRequired) {
                this.Invoke(new Action<List<ControllerRecord>>(RenderSnapshot), new object[] { records });
                return;
            }

            lastControllerSnapshot = records == null
                ? new List<ControllerRecord>()
                : new List<ControllerRecord>(records);

            foreach (Button b in con) {
                b.Tag = null;
                b.BackColor = Color.FromArgb(0x00, SystemColors.Control);
                b.BackgroundImage = Properties.Resources.cross;
                SetEmptySlotTooltip(b);
            }

            var handled = new HashSet<int>();
            int slotIndex = 0;

            foreach (ControllerRecord record in records) {
                if (handled.Contains(record.PadId) || slotIndex >= con.Count)
                    continue;

                Button button = con[slotIndex];
                bool isPair = record.OtherPadId >= 0;

                if (isPair) {
                    button.BackgroundImage = ComposeJoinedIcon(button.Width, button.Height);
                    SetConnectionTooltip(button, false);
                    handled.Add((byte)record.OtherPadId);
                } else {
                    button.BackgroundImage = IconFor(record);
                    SetConnectionTooltip(button, record.Kind == ControllerKind.Pro);
                }

                button.Tag = (int)record.PadId;
                button.BackColor = record.Battery >= 0 ? Joycon.GetBatteryColor(record.Battery) : Color.FromArgb(0x00, SystemColors.Control);

                // Mirrors AssignJoyconToSlot's loc-button wiring - unsubscribe first since this
                // whole method reruns on every snapshot push, unlike AssignJoyconToSlot which
                // only runs once per new connection.
                Button locButton = loc[slotIndex];
                locButton.Tag = button;
                locButton.Click -= locBtnClickAsync;
                locButton.Click += locBtnClickAsync;

                handled.Add(record.PadId);
                slotIndex++;
            }
        }

        private Bitmap IconFor(ControllerRecord record) {
            switch (record.Kind) {
                case ControllerKind.Pro: return Properties.Resources.pro;
                case ControllerKind.Snes: return Properties.Resources.snes;
                case ControllerKind.N64: return Properties.Resources.ultra;
                case ControllerKind.Left: return Properties.Resources.jc_left_s;
                default: return Properties.Resources.jc_right_s;
            }
        }

        private void StartRemoteCalibrate(Button button) {
            if (!(button.Tag is int)) {
                RestoreCalibrateIcon();
                return;
            }

            int padId = (int)button.Tag;
            console.Text = "Requesting calibration from service...";
            serviceClient.StartCalibration(padId);
            // calibrateIconButton keeps flashing until CalibrationComplete/Failed arrives (see
            // WireServiceClientEvents) - not cleared immediately here, unlike local mode.
        }

        bool toRumble = Boolean.Parse(ConfigurationManager.AppSettings["EnableRumble"]);
        bool showAsXInput = Boolean.Parse(ConfigurationManager.AppSettings["ShowAsXInput"]);
        bool showAsDS4 = Boolean.Parse(ConfigurationManager.AppSettings["ShowAsDS4"]);

        public async void locBtnClickAsync(object sender, EventArgs e) {
            Button bb = sender as Button;

            if (bb.Tag.GetType() == typeof(Button)) {
                Button button = bb.Tag as Button;

                if (isRemoteMode) {
                    if (button.Tag is int)
                        serviceClient.TestRumble((int)button.Tag);
                    return;
                }

                if (button.Tag.GetType() == typeof(Joycon)) {
                    Joycon v = (Joycon)button.Tag;
                    v.SetRumble(160.0f, 320.0f, 1.0f);
                    await Task.Delay(300);
                    v.SetRumble(160.0f, 320.0f, 0);
                }
            }
        }

        // Left click on any controller (Pro or Joycon) opens Controller Profiles; right click on a
        // Joycon joins/splits it instead (also triggered by double-clicking the stick in
        // hardware, via JoinOrSplitJoycon directly - see Joycon.cs). Left click on an empty
        // slot (Tag == null) opens Add Controllers instead.
        public void conBtnMouseClick(object sender, MouseEventArgs e) {
            Button button = sender as Button;
            if (button.Tag == null) {
                if (e.Button == MouseButtons.Left)
                    btn_open3rdP_Click(sender, e);
                return;
            }

            if (isRemoteMode) {
                if (!(button.Tag is int))
                    return;

                if (e.Button == MouseButtons.Right) {
                    serviceClient.JoinOrSplit((int)button.Tag);
                } else if (e.Button == MouseButtons.Left) {
                    if (allowCalibration) {
                        HandlePossibleDoubleClick(button);
                    } else {
                        btn_reassign_open_Click(sender, e);
                    }
                }
                return;
            }

            if (button.Tag.GetType() != typeof(Joycon))
                return;

            if (e.Button == MouseButtons.Right) {
                JoinOrSplitJoycon((Joycon)button.Tag);
            } else if (e.Button == MouseButtons.Left) {
                if (allowCalibration) {
                    HandlePossibleDoubleClick(button);
                } else {
                    btn_reassign_open_Click(sender, e);
                }
            }
        }

        // Left-click disambiguation between "controller profiles" (single click) and "calibrate"
        // (double click), only relevant when AllowCalibration is on - otherwise every left
        // click opens Controller Profiles immediately, same as before this existed. A single click is
        // held for clickTimer's interval to see if a second one follows on the same button
        // before committing to it - a plain WinForms DoubleClick event isn't usable here since
        // it fires in addition to, not instead of, the Click for the first press. While waiting,
        // and for the whole calibration process if a double click is confirmed, the button
        // flashes the calibrate icon - restored by StartCalibrate/CalcData once calibration
        // either fails to start or actually finishes (see RestoreCalibrateIcon call sites).
        private Button calibrateIconButton = null;
        private Image calibrateIconOriginalImage = null;

        private void HandlePossibleDoubleClick(Button button) {
            if (clickTimer.Enabled && calibrateIconButton == button) {
                clickTimer.Stop();
                if (isRemoteMode)
                    StartRemoteCalibrate(button);
                else
                    StartCalibrate(button, EventArgs.Empty);
            } else {
                clickTimer.Stop();
                RestoreCalibrateIcon();

                calibrateIconButton = button;
                calibrateIconOriginalImage = button.BackgroundImage;
                button.BackgroundImage = Properties.Resources.calibrate;

                clickTimer.Start();
            }
        }

        private void ClickTimer_Tick(object sender, EventArgs e) {
            clickTimer.Stop();
            if (calibrateIconButton != null) {
                Button button = calibrateIconButton;
                RestoreCalibrateIcon();
                btn_reassign_open_Click(button, EventArgs.Empty);
            }
        }

        private void RestoreCalibrateIcon() {
            if (calibrateIconButton != null)
                calibrateIconButton.BackgroundImage = calibrateIconOriginalImage;

            calibrateIconButton = null;
            calibrateIconOriginalImage = null;
        }

        // Empty slots swap their red X for a plus icon on hover, as a visual hint that
        // clicking opens Add Controllers (see conBtnMouseClick).
        public void conBtnMouseEnter(object sender, EventArgs e) {
            Button button = sender as Button;
            if (button.Tag == null)
                button.BackgroundImage = Properties.Resources.plus;
        }

        public void conBtnMouseLeave(object sender, EventArgs e) {
            Button button = sender as Button;
            if (button.Tag == null)
                button.BackgroundImage = Properties.Resources.cross;
        }

        public void SetConnectionTooltip(Button button, bool isPro) {
            string tip = isPro ? "Left-click to edit controller profile" : "Right-click to split / left-click to edit controller profile";
            if (allowCalibration)
                tip += ", double click to calibrate";
            btnTip.SetToolTip(button, tip);
        }

        public void SetEmptySlotTooltip(Button button) {
            btnTip.SetToolTip(button, "Add a controller");
        }

        // jc_left.png/jc_right.png are drawn as literal left/right halves of one combined-pair
        // silhouette (their flat edges meet in the middle), so cropping each to its actual
        // artwork (they're padded within a much larger transparent square canvas), scaling by
        // height only to keep proportions matching the other slot icons, and flushing each
        // half against the shared center seam recreates the combined shape within a single
        // slot - edges touching in the middle, matching margin on the outer edges - instead of
        // either spanning two slots or looking stretched/warped filling the box edge to edge.
        public Bitmap ComposeJoinedIcon(int width, int height) {
            Bitmap leftSource = Properties.Resources.jc_left;
            Bitmap rightSource = Properties.Resources.jc_right;
            Rectangle leftBounds = GetOpaqueBounds(leftSource);
            Rectangle rightBounds = GetOpaqueBounds(rightSource);

            const float fit = 0.58f; // leaves margin similar to the other slot icons, which
                                      // have padding baked into their own source canvas
            int halfWidth = width / 2;
            float targetHeight = height * fit;

            const int seamGap = 1; // small visible gap so the two halves read as distinct icons

            var composite = new Bitmap(width, height);
            using (Graphics g = Graphics.FromImage(composite)) {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                DrawHalfFlushToSeam(g, leftSource, leftBounds, 0, halfWidth, height, targetHeight, flushRight: true, seamGap: 0);
                DrawHalfFlushToSeam(g, rightSource, rightBounds, halfWidth, width - halfWidth, height, targetHeight, flushRight: false, seamGap: seamGap);
            }
            return composite;
        }

        private static void DrawHalfFlushToSeam(Graphics g, Bitmap source, Rectangle sourceBounds, int xOffset, int halfWidth, int height, float targetHeight, bool flushRight, int seamGap) {
            float scale = targetHeight / sourceBounds.Height;
            int destWidth = Math.Max(1, (int)(sourceBounds.Width * scale));
            int destHeight = Math.Max(1, (int)(sourceBounds.Height * scale));
            int destX = flushRight ? xOffset + halfWidth - destWidth : xOffset + seamGap;
            int destY = (height - destHeight) / 2;

            g.DrawImage(source, new Rectangle(destX, destY, destWidth, destHeight), sourceBounds, GraphicsUnit.Pixel);
        }

        // Scans a bitmap's alpha channel for the tightest rectangle containing its non-
        // transparent artwork, so ComposeJoinedIcon can crop out the surrounding padding
        // instead of relying on hardcoded pixel coordinates tied to one specific asset.
        private static Rectangle GetOpaqueBounds(Bitmap bitmap) {
            var data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            try {
                int stride = data.Stride;
                byte[] pixels = new byte[stride * bitmap.Height];
                System.Runtime.InteropServices.Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);

                int minX = bitmap.Width, minY = bitmap.Height, maxX = -1, maxY = -1;
                for (int y = 0; y < bitmap.Height; y++) {
                    for (int x = 0; x < bitmap.Width; x++) {
                        byte alpha = pixels[y * stride + x * 4 + 3];
                        if (alpha > 10) {
                            if (x < minX) minX = x;
                            if (x > maxX) maxX = x;
                            if (y < minY) minY = y;
                            if (y > maxY) maxY = y;
                        }
                    }
                }

                if (maxX < minX || maxY < minY)
                    return new Rectangle(0, 0, bitmap.Width, bitmap.Height);

                return Rectangle.FromLTRB(minX, minY, maxX + 1, maxY + 1);
            } finally {
                bitmap.UnlockBits(data);
            }
        }

        // Collapses a newly-joined Joycon pair from their two separate slots into one: the
        // left half's slot becomes the "primary" showing a composite icon for the pair, and
        // the right half's slot is freed back to fully empty (available for a new controller),
        // since the pair now acts as a single virtual controller and doesn't need two slots to
        // show that.
        public void CollapseJoinedPair(Joycon left, Joycon right) {
            SafeBeginInvoke(() => {
                Button primaryButton = con.Find(b => b.Tag == left);
                Button secondaryButton = con.Find(b => b.Tag == right);
                if (primaryButton == null || secondaryButton == null)
                    return;

                primaryButton.BackgroundImage = ComposeJoinedIcon(primaryButton.Width, primaryButton.Height);
                SetConnectionTooltip(primaryButton, false);

                secondaryButton.BackColor = Color.FromArgb(0x00, SystemColors.Control);
                secondaryButton.Tag = null;
                secondaryButton.BackgroundImage = Properties.Resources.cross;
                SetEmptySlotTooltip(secondaryButton);
            });
        }

        // IJoyconHost entry point for a brand new connection (Program.cs's
        // CheckForNewControllers) - unlike AssignJoyconToSlot below (used by callers already
        // running on the UI thread, like split/dropped-partner promotion), this is called from
        // the background scan thread, so it marshals onto the UI thread itself.
        public void AssignSlot(Joycon joycon) {
            Bitmap icon;
            if (joycon.isPro) icon = Properties.Resources.pro;
            else if (joycon.isSnes) icon = Properties.Resources.snes;
            else if (joycon.is64) icon = Properties.Resources.ultra;
            else icon = joycon.isLeft ? Properties.Resources.jc_left_s : Properties.Resources.jc_right_s;

            SafeBeginInvoke(() => {
                AssignJoyconToSlot(joycon, icon);
            });
        }

        // Finds an empty slot for a Joycon that doesn't currently have its own button - used
        // when splitting a collapsed pair back apart, or when the hidden half of a pair
        // survives its partner disconnecting. Mirrors the per-slot wiring Program.cs does for
        // a fresh connection. Returns false if all 4 slots are occupied.
        public bool AssignJoyconToSlot(Joycon jc, Bitmap icon) {
            int index = con.FindIndex(b => b.Tag == null);
            if (index == -1)
                return false;

            Button button = con[index];
            button.Tag = jc;
            button.BackgroundImage = icon;
            // Carry over the already-known battery color rather than leaving the freed
            // slot's default background - BatteryChanged() only reapplies it on the next
            // battery-level event, which may not come for a while.
            button.BackColor = jc.battery >= 0 ? Joycon.GetBatteryColor(jc.battery) : Color.FromArgb(0x00, SystemColors.Control);
            SetConnectionTooltip(button, jc.isPro);

            Button locButton = loc[index];
            locButton.Tag = button;
            locButton.Click += new EventHandler(locBtnClickAsync);

            return true;
        }

        // Called after Program.cs's CleanUp() detaches a dropped Joycon, to fix up whatever
        // slot(s) it and/or its (former) pair partner were occupying.
        public void HandleJoyconDropped(Joycon dropped, Joycon survivingPartner) {
            SafeBeginInvoke(() => {
                Button droppedButton = con.Find(b => b.Tag == dropped);

                if (droppedButton != null) {
                    // dropped was showing on its own slot - solo, Pro, or the primary half of a
                    // collapsed pair. Free that slot...
                    droppedButton.BackColor = Color.FromArgb(0x00, SystemColors.Control);
                    droppedButton.Tag = null;
                    droppedButton.BackgroundImage = Properties.Resources.cross;
                    SetEmptySlotTooltip(droppedButton);

                    // ...and if it was the primary half of a pair, the hidden secondary half
                    // needs a slot of its own now, since it was never given one.
                    if (survivingPartner != null) {
                        Bitmap soloIcon = survivingPartner.isLeft ? Properties.Resources.jc_left_s : Properties.Resources.jc_right_s;
                        if (!AssignJoyconToSlot(survivingPartner, soloIcon))
                            AppendTextBox("No free slot to show the split-off Joycon - reconnect it or free a slot.\r\n");
                    }
                } else if (survivingPartner != null) {
                    // dropped was the hidden secondary half of a collapsed pair - its partner
                    // (the still-connected primary) just needs to revert from the composite icon
                    // back to its own solo icon.
                    Button survivorButton = con.Find(b => b.Tag == survivingPartner);
                    if (survivorButton != null) {
                        survivorButton.BackgroundImage = survivingPartner.isLeft ? Properties.Resources.jc_left_s : Properties.Resources.jc_right_s;
                        SetConnectionTooltip(survivorButton, false);
                    }
                }
            });
        }

        // Matches the low-battery balloon tip previously inlined in Joycon.BatteryChanged() -
        // the caller (Joycon.cs) decides whether a notification is warranted (battery level,
        // not USB-powered); this just shows it. Not Invoke-wrapped, matching that prior
        // behavior - NotifyIcon operations aren't Control-handle-affine the way Buttons are.
        public void NotifyLowBattery(Joycon joycon) {
            string label = joycon.isPro ? "Pro Controller" : (joycon.isSnes ? "SNES Controller" : (joycon.is64 ? "N64 Controller" : (joycon.isLeft ? "Joycon Left" : "Joycon Right")));
            notifyIcon.Visible = true;
            notifyIcon.BalloonTipText = String.Format("Controller {0} ({1}) - low battery notification!", joycon.PadId, label);
            notifyIcon.ShowBalloonTip(0);
        }

        // Not Invoke-wrapped, matching the prior inline behavior in Joycon.BatteryChanged().
        public void UpdateBatteryColor(Joycon joycon) {
            foreach (Button v in con) {
                if (v.Tag == joycon) {
                    v.BackColor = Joycon.GetBatteryColor(joycon.battery);
                }
            }
        }

        // Called from the calibrating controller's own Poll thread (see Joycon.
        // DoThingsWithButtons) - PendingConfirmController already guarantees this only fires
        // while joycon is actually the one a prompt is showing for, but isRemoteMode/
        // calibratingJoycon are double-checked since this is a live Joycon reference that only
        // exists at all in local mode (remote mode has none - see HandleCalibrationConfirm on
        // HeadlessJoyconHost instead, which is what actually receives this call there).
        public void HandleCalibrationConfirm(Joycon joycon) {
            this.Invoke(new MethodInvoker(delegate {
                if (!isRemoteMode && calibrationInProgress && joycon == calibratingJoycon)
                    OnCalibButtonClicked();
            }));
        }

        public void JoinOrSplitJoycon(Joycon v) {
            if (v.other == null && !v.isPro) { // needs connecting to other joycon (so messy omg)
                bool succ = false;

                if (Program.mgr.j.Count == 1) { // when want to have a single joycon in vertical mode
                    v.other = v; // hacky; implement check in Joycon.cs to account for this
                    succ = true;
                } else {
                    foreach (Joycon jc in Program.mgr.j) {
                        if (!jc.isPro && jc.isLeft != v.isLeft && jc != v && jc.other == null) {
                            v.other = jc;
                            jc.other = v;

                            // Disconnect whichever controller was created later - see Joycon.
                            // virtualControllerSequence - not just whichever you happened to
                            // click. The older one is the one most likely already locked onto by
                            // a running game, so it's left completely untouched; the newer one is
                            // safe to actually disconnect (matching a real unplug - clean, no
                            // leftover state) and recreate later on split via ReenableViGEm.
                            Joycon loser = v.virtualControllerSequence > jc.virtualControllerSequence ? v : jc;
                            if (loser.out_xbox != null) {
                                loser.out_xbox.Disconnect();
                                loser.out_xbox = null;
                            }
                            if (loser.out_ds4 != null) {
                                loser.out_ds4.Disconnect();
                                loser.out_ds4 = null;
                            }

                            CollapseJoinedPair(v.isLeft ? v : jc, v.isLeft ? jc : v);

                            succ = true;
                            break;
                        }
                    }
                }

                if (succ && v.other == v) // self-pair (single joycon vertical mode) only -
                    foreach (Button b in con) // a real pair is already handled above
                        if (b.Tag == v)
                            b.BackgroundImage = v.isLeft ? Properties.Resources.jc_left : Properties.Resources.jc_right;
            } else if (v.other != null && !v.isPro) { // needs disconnecting from other joycon
                // Recreates whichever controller was actually disconnected on join (see above) -
                // a no-op for whichever one was never touched, since ReenableViGEm only acts
                // when out_xbox/out_ds4 is null.
                ReenableViGEm(v);
                ReenableViGEm(v.other);

                Joycon partner = v.other;
                bool wasRealPair = partner != v;

                Button button = con.Find(b => b.Tag == v);
                if (button != null) {
                    button.BackgroundImage = v.isLeft ? Properties.Resources.jc_left_s : Properties.Resources.jc_right_s;
                    SetConnectionTooltip(button, false);
                }

                v.other.other = null;
                v.other = null;

                if (wasRealPair) {
                    Bitmap soloIcon = partner.isLeft ? Properties.Resources.jc_left_s : Properties.Resources.jc_right_s;
                    if (!AssignJoyconToSlot(partner, soloIcon))
                        AppendTextBox("No free slot to show the split-off Joycon - reconnect it or free a slot.\r\n");
                }
            }
        }

        private void btn_open3rdP_Click(object sender, EventArgs e) {
            _3rdPartyControllers partyForm = new _3rdPartyControllers();
            partyForm.ShowDialog();
        }

        private void settingsApply_Click(object sender, EventArgs e) {
            var configFile = ConfigurationManager.OpenExeConfiguration(ConfigurationUserLevel.None);
            var settings = configFile.AppSettings.Settings;

            for (int row = 0; row < displayedConfigKeys.Length; row++) {
                var valCtl = settingsTable.GetControlFromPosition(1, row);
                var KeyCtl = settingsTable.GetControlFromPosition(0, row).Text;

                if (valCtl.GetType() == typeof(CheckBox) && settings[KeyCtl] != null) {
                    settings[KeyCtl].Value = ((CheckBox)valCtl).Checked.ToString().ToLower();
                } else if (valCtl.GetType() == typeof(ComboBox) && settings[KeyCtl] != null) {
                    settings[KeyCtl].Value = ((ComboBox)valCtl).SelectedItem.ToString();
                } else if (valCtl.GetType() == typeof(TextBox) && settings[KeyCtl] != null) {
                    settings[KeyCtl].Value = ((TextBox)valCtl).Text.ToLower();
                }
            }

            try {
                configFile.Save(ConfigurationSaveMode.Modified);
            } catch (ConfigurationErrorsException) {
                AppendTextBox("Error writing app settings.\r\n");
            }

            ConfigurationManager.AppSettings["AutoPowerOff"] = "false";  // Prevent joycons poweroff when applying settings
            Application.Restart();
            Environment.Exit(0);
        }

        // Copies the GUI's per-user config/calibration/controller lists to the shared location
        // (%ProgramData%\BetterJoy) a Windows Service uses (see AppPaths.EnableServiceMode),
        // and switches this and future GUI launches to read/write there too - otherwise settings
        // changed here would never be seen by a running service at all, since it runs as SYSTEM
        // and has its own separate profile.
        private void btn_enableServiceMode_Click(object sender, EventArgs e) {
            if (AppPaths.ServiceModeEnabled) {
                MessageBox.Show("Configuration is already shared with the Windows Service.", "BetterJoy");
                return;
            }

            DialogResult result = MessageBox.Show(
                "This copies your current settings, calibration data, and controller lists to a " +
                "shared location (%ProgramData%\\BetterJoy) so a Windows Service running BetterJoy " +
                "can use the same configuration. Continue?",
                "Sync Config with Service", MessageBoxButtons.YesNo, MessageBoxIcon.Question);

            if (result != DialogResult.Yes)
                return;

            try {
                AppPaths.EnableServiceMode();
                btn_enableServiceMode.Text = "Config Synced with Service";
                btn_enableServiceMode.Enabled = false;

                MessageBox.Show(
                    "Done - restart BetterJoy for this to take effect. If the Windows Service " +
                    "isn't installed yet, run this from an elevated PowerShell/cmd:\r\n\r\n" +
                    "sc create BetterJoy binPath= \"\\\"" + Application.ExecutablePath + "\\\" -service\" start= auto",
                    "BetterJoy");
            } catch (Exception ex) {
                MessageBox.Show("Failed to sync configuration: " + ex.Message, "BetterJoy");
            }
        }

        void ReenableViGEm(Joycon v) {
            if (showAsXInput && v.out_xbox == null) {
                v.out_xbox = new Controller.OutputControllerXbox360();

                if (toRumble)
                    v.out_xbox.FeedbackReceived += v.ReceiveRumble;
                v.out_xbox.Connect();
            }

            if (showAsDS4 && v.out_ds4 == null) {
                v.out_ds4 = new Controller.OutputControllerDualShock4();

                if (toRumble)
                    v.out_ds4.FeedbackReceived += v.Ds4_FeedbackReceived;
                v.out_ds4.Connect();
            }
        }

        private void btn_settings_Click(object sender, EventArgs e) {
            rightPanel.Visible = !rightPanel.Visible;
        }

        private void cbBox_Changed(object sender, EventArgs e) {
            var coord = settingsTable.GetPositionFromControl(sender as Control);

            var valCtl = settingsTable.GetControlFromPosition(coord.Column, coord.Row);
            var KeyCtl = settingsTable.GetControlFromPosition(coord.Column - 1, coord.Row).Text;

            try {
                var configFile = ConfigurationManager.OpenExeConfiguration(ConfigurationUserLevel.None);
                var settings = configFile.AppSettings.Settings;
                if (valCtl.GetType() == typeof(CheckBox) && settings[KeyCtl] != null) {
                    settings[KeyCtl].Value = ((CheckBox)valCtl).Checked.ToString().ToLower();
                } else if (valCtl.GetType() == typeof(ComboBox) && settings[KeyCtl] != null) {
                    settings[KeyCtl].Value = ((ComboBox)valCtl).SelectedItem.ToString();
                } else if (valCtl.GetType() == typeof(TextBox) && settings[KeyCtl] != null) {
                    settings[KeyCtl].Value = ((TextBox)valCtl).Text.ToLower();
                }

                // Program.mgr is null in remote mode (Program.Start() never ran there) - the
                // value is still saved to the config file either way (see above), so a synced
                // service picks it up for its own newly-connecting controllers via its file
                // watcher; there just isn't a live Joycon list here to apply it to immediately.
                if (KeyCtl == "HomeLEDOn" && Program.mgr != null) {
                    bool on = settings[KeyCtl].Value.ToLower() == "true";
                    foreach (Joycon j in Program.mgr.j) {
                        j.SetHomeLight(on);
                    }
                }

                configFile.Save(ConfigurationSaveMode.Modified);
                ConfigurationManager.RefreshSection(configFile.AppSettings.SectionInformation.Name);
            } catch (ConfigurationErrorsException) {
                AppendTextBox("Error writing app settings\r\n");
                Trace.WriteLine(String.Format("rw {0}, column {1}, {2}, {3}", coord.Row, coord.Column, sender.GetType(), KeyCtl));
            }
        }
        // The ordered set of steps a calibration run walks through, shown one at a time in
        // calibDialog - built per-run in StartCalibrate from the specific controller involved,
        // since a plain Joycon only ever has one physical stick to offer a step for, while a
        // Pro controller offers both.
        private enum CalibStepKind { Gyro, LeftStick, RightStick }
        // Target/Secondary are only meaningful for a stick step (unused/null-Target for Gyro,
        // which always targets calibratingJoycon's own IMU regardless of pairing). Target exists
        // because a Pro controller's two sticks live on ONE Joycon object (distinguished by
        // Secondary), but a joined pair's two sticks live on TWO SEPARATE Joycon objects - each
        // needs its own calibration pass against that specific instance, or only whichever half
        // happened to be clicked would ever actually get recalibrated.
        private struct CalibStep {
            public CalibStepKind Kind;
            public Joycon Target;
            public bool Secondary;
        }
        private List<CalibStep> calibSteps;
        private int calibStepIndex;

        // Gyro is a fixed countdown (see CalibrationDialog) - the other two are open-ended,
        // waiting for an explicit Start then an explicit Done click with no time limit.
        private enum CalibPhase { GyroCollect, StickCenter, StickRange }
        private CalibPhase calibPhase;
        private bool awaitingStart;

        private CalibrationDialog calibDialog;

        private void StartCalibrate(object sender, EventArgs e) {
            if (calibrationInProgress) {
                RestoreCalibrateIcon();
                return;
            }

            // The specific controller you double-clicked, not just "whatever's first in the
            // list" - CalibrationState scopes sample admission to this exact controller (see
            // CalibrationState.CalibratingController), so other controllers staying connected
            // and polling can no longer contaminate the buffers. That's what the old "exactly
            // one controller connected" restriction here was actually working around.
            Button button = sender as Button;
            calibratingJoycon = button?.Tag as Joycon;
            if (calibratingJoycon == null) {
                this.console.Text = "Please connect a controller.";
                RestoreCalibrateIcon();
                return;
            }

            calibrationInProgress = true;

            calibSteps = new List<CalibStep> { new CalibStep { Kind = CalibStepKind.Gyro } };

            bool isPair = calibratingJoycon.other != null && calibratingJoycon.other != calibratingJoycon;
            if (calibratingJoycon.isPro) {
                calibSteps.Add(new CalibStep { Kind = CalibStepKind.LeftStick, Target = calibratingJoycon, Secondary = false });
                calibSteps.Add(new CalibStep { Kind = CalibStepKind.RightStick, Target = calibratingJoycon, Secondary = true });
            } else if (isPair) {
                // A joined pair is two independent physical devices, not one device with two
                // sticks the way a Pro controller is - each side's stick lives on its own Joycon
                // object, so each gets its own step targeting that specific instance.
                Joycon leftJc = calibratingJoycon.isLeft ? calibratingJoycon : calibratingJoycon.other;
                Joycon rightJc = calibratingJoycon.isLeft ? calibratingJoycon.other : calibratingJoycon;
                calibSteps.Add(new CalibStep { Kind = CalibStepKind.LeftStick, Target = leftJc, Secondary = false });
                calibSteps.Add(new CalibStep { Kind = CalibStepKind.RightStick, Target = rightJc, Secondary = false });
            } else if (calibratingJoycon.isLeft) {
                calibSteps.Add(new CalibStep { Kind = CalibStepKind.LeftStick, Target = calibratingJoycon, Secondary = false });
            } else {
                calibSteps.Add(new CalibStep { Kind = CalibStepKind.RightStick, Target = calibratingJoycon, Secondary = false });
            }
            calibStepIndex = 0;

            calibDialog = new CalibrationDialog();
            calibDialog.ButtonClicked += OnCalibButtonClicked;
            calibDialog.Show(this);

            this.console.Text = "Calibration started." + "\r\n";
            BeginCurrentStep();
        }

        private static string StepDisplayName(CalibStepKind step) {
            switch (step) {
                case CalibStepKind.Gyro: return "Gyroscope";
                case CalibStepKind.LeftStick: return "Left Stick";
                case CalibStepKind.RightStick: return "Right Stick";
            }
            return "";
        }

        // The controller a prompt is currently showing for - calibratingJoycon for Gyro (always
        // its own IMU), but the current step's own Target for a stick phase, since a joined
        // pair's two steps target two different physical Joycon objects (see CalibStep).
        private Joycon CurrentPromptTarget() {
            CalibStep step = calibSteps[calibStepIndex];
            return step.Kind == CalibStepKind.Gyro ? calibratingJoycon : step.Target;
        }

        // Shows the step's first phase instruction with a Start prompt - nothing begins until
        // the user clicks it (OnCalibButtonClicked), so they have as long as they need to
        // actually read the instruction and get into position.
        private void BeginCurrentStep() {
            CalibStep step = calibSteps[calibStepIndex];
            calibDialog.SetStep(calibStepIndex + 1, calibSteps.Count, StepDisplayName(step.Kind));

            if (step.Kind == CalibStepKind.Gyro) {
                BeginPhase(CalibPhase.GyroCollect, "Place the controller on a flat, still surface.");
            } else {
                CalibrationState.ClearStickSamples();
                CalibrationState.StickCalibratingController = step.Target;
                CalibrationState.StickCalibrating = true;
                CalibrationState.CurrentStickTarget = step.Secondary
                    ? CalibrationState.StickTarget.Secondary
                    : CalibrationState.StickTarget.Primary;
                CalibrationState.CurrentStickPhase = CalibrationState.StickPhase.None;

                BeginPhase(CalibPhase.StickCenter, String.Format("Leave the {0} centered - don't touch it.", StepDisplayName(step.Kind).ToLower()));
            }
        }

        private void BeginPhase(CalibPhase phase, string instruction) {
            calibPhase = phase;
            awaitingStart = true;
            calibDialog.SetInstruction(instruction);
            ShowCalibStartPrompt();
        }

        // Wrap every prompt-showing call so CalibrationState.PendingConfirmController always
        // matches whatever the dialog is actually asking for - lets Joycon.DoThingsWithButtons
        // treat a face button on THIS controller as "confirm" while it's showing, without
        // needing to track dialog state itself. Uses CurrentPromptTarget, not calibratingJoycon
        // directly - for a joined pair's second stick step, the prompt is asking about the
        // PARTNER's stick, so the confirm button needs to be pressed on that physical Joycon.
        private void ShowCalibStartPrompt() {
            CalibrationState.PendingConfirmController = CurrentPromptTarget();
            calibDialog.ShowStartPrompt();
        }

        private void ShowCalibDonePrompt() {
            CalibrationState.PendingConfirmController = CurrentPromptTarget();
            calibDialog.ShowDonePrompt();
        }

        private void btn_reassign_open_Click(object sender, EventArgs e) {
            // isRemoteMode's serviceClient, not null - Reassign uses it to relay controller-
            // button presses for its "left-click then press" auto-detect, which otherwise has no
            // Joycon instances of its own to poll when the service owns the hardware.
            string preferredProfileId = null;
            Button sourceButton = sender as Button;
            if (sourceButton != null) {
                if (!isRemoteMode && sourceButton.Tag is Joycon)
                    preferredProfileId = ControllerMappings.ProfileIdFor((Joycon)sourceButton.Tag);
                else if (isRemoteMode && sourceButton.Tag is int) {
                    int padId = (int)sourceButton.Tag;
                    ControllerRecord record = lastControllerSnapshot.FirstOrDefault(r => r.PadId == padId);
                    preferredProfileId = record.ProfileId;
                }
            }

            Reassign mapForm = new Reassign(
                isRemoteMode ? serviceClient : null,
                isRemoteMode ? lastControllerSnapshot : null,
                preferredProfileId);
            mapForm.ShowDialog();
        }

        // Local mapping dialogs read the live Joycon objects directly. The headless host uses
        // this hook to push a fresh service snapshot after a USB handshake resolves the real
        // per-unit MAC; no additional main-window rendering is needed here.
        public void RefreshControllerState() { }

        // The dialog has one button whose meaning depends on where we are: first click in a
        // phase starts it (begins sample admission, and for gyro also starts its countdown);
        // for the two stick phases, a second click (Done) is what actually ends it - there's no
        // timer for those at all, the user decides when they're finished. Shared entry point for
        // both an actual mouse click on the dialog and a physical confirm button press (see
        // HandleCalibrationConfirm) - clearing PendingConfirmController here, not at each
        // prompt-hiding call site, means whichever trigger fires first is the one that counts.
        private void OnCalibButtonClicked() {
            CalibrationState.PendingConfirmController = null;

            if (awaitingStart) {
                awaitingStart = false;
                StartPhase();
            } else {
                FinishStickPhase();
            }
        }

        private void StartPhase() {
            switch (calibPhase) {
                case CalibPhase.GyroCollect:
                    CalibrationState.ClearSamples();
                    CalibrationState.CalibratingController = calibratingJoycon;
                    CalibrationState.Calibrating = true;
                    calibDialog.SetInstruction("Hold still...");
                    this.count = 3;
                    calibDialog.ShowCountdown(this.count);
                    countDown = new Timer();
                    countDown.Interval = 1000;
                    countDown.Tick += new EventHandler(GyroCollectTick);
                    countDown.Enabled = true;
                    break;

                case CalibPhase.StickCenter:
                    // No Done click here - centering is just a quick snapshot of rest position,
                    // not something that benefits from open-ended time the way rotating around
                    // the full range does. A brief automatic capture is enough, then straight
                    // into the (unlimited, Done-gated) rotate phase.
                    CalibrationState.CurrentStickPhase = CalibrationState.StickPhase.Center;
                    calibDialog.HidePrompt();
                    countDown = new Timer();
                    countDown.Interval = 1000;
                    countDown.Tick += new EventHandler(StickCenterCaptureTick);
                    countDown.Enabled = true;
                    break;
            }
        }

        // Gyro alone stays on a fixed countdown - the point of this measurement is proving the
        // controller sits genuinely untouched, so it can't reasonably end on a click the way the
        // stick phases do.
        private void GyroCollectTick(object sender, EventArgs e) {
            if (this.count > 0) {
                this.count--;
                calibDialog.ShowCountdown(this.count);
                return;
            }

            countDown.Stop();

            // calibratingJoycon captured once in StartCalibrate - a disconnect during the
            // collection window (the controller could legitimately be dropped mid-calibration)
            // must not leave calibrationInProgress/the icon/the dialog stuck, so this is a hard
            // requirement, not just tidiness.
            try {
                CalibrationState.FinishCalibration(calibratingJoycon.serial_number);
                calibratingJoycon.getActiveData();
                this.console.Text += "Gyro calibration completed!!!" + "\r\n";
                AdvanceStep();
            } catch {
                this.console.Text += "Calibration failed - was the controller disconnected?" + "\r\n";
                calibDialog.ShowResult("Failed - was the controller disconnected?");
                FinishCalibrationFlow();
            }
        }

        // One-shot: centering doesn't need a Done click, just a brief automatic capture, then
        // straight into the rotate phase (unlimited, Done-gated - no Start needed there either,
        // see the comment at its ShowDonePrompt call site).
        private void StickCenterCaptureTick(object sender, EventArgs e) {
            countDown.Stop();
            CalibrationState.CurrentStickPhase = CalibrationState.StickPhase.None;

            calibPhase = CalibPhase.StickRange;
            calibDialog.SetInstruction(String.Format("Now rotate the {0} in full circles out to its edges.", StepDisplayName(calibSteps[calibStepIndex].Kind).ToLower()));
            CalibrationState.CurrentStickPhase = CalibrationState.StickPhase.Range;
            ShowCalibDonePrompt();
        }

        // Center auto-advances on its own short timer (StickCenterCaptureTick), so this is only
        // ever reached via a Done click on the rotate phase.
        private void FinishStickPhase() {
            calibDialog.HidePrompt();
            CalibrationState.CurrentStickPhase = CalibrationState.StickPhase.None;

            CalibStep step = calibSteps[calibStepIndex];
            try {
                CalibrationState.FinishStickCalibration(step.Target.serial_number, step.Secondary);
                step.Target.getActiveStickData();
                this.console.Text += StepDisplayName(step.Kind) + " calibration completed!!!" + "\r\n";
                AdvanceStep();
            } catch {
                this.console.Text += "Stick calibration failed - was the controller disconnected?" + "\r\n";
                calibDialog.ShowResult("Failed - was the controller disconnected?");
                FinishCalibrationFlow();
            }
        }

        private void AdvanceStep() {
            calibStepIndex++;
            if (calibStepIndex >= calibSteps.Count) {
                calibDialog.ShowResult("Calibration complete!");
                this.console.Text += "Calibration completed!!!" + "\r\n";
                FinishCalibrationFlow();
            } else {
                BeginCurrentStep();
            }
        }

        // Shared end-of-flow cleanup, reached after the last step finishes (success or failure)
        // or as soon as any single step's own try/catch fails. Resets the stick flags here too,
        // not just inside FinishStickCalibration - a gyro-step failure never calls that method
        // at all, but a stick step earlier in the same run may have already turned
        // StickCalibrating/StickCalibratingController on. The dialog is closed on a short delay
        // (captured locally, not via the calibDialog field, in case a new run starts before the
        // delay elapses) so a completion/failure message is actually readable before it vanishes.
        private void FinishCalibrationFlow() {
            CalibrationState.Calibrating = false;
            CalibrationState.CalibratingController = null;
            CalibrationState.StickCalibrating = false;
            CalibrationState.StickCalibratingController = null;
            CalibrationState.PendingConfirmController = null;
            calibrationInProgress = false;
            calibratingJoycon = null;
            RestoreCalibrateIcon();

            CalibrationDialog dialogToClose = calibDialog;
            calibDialog = null;
            if (dialogToClose != null) {
                Timer closeTimer = new Timer { Interval = 1500 };
                closeTimer.Tick += (s, e) => {
                    closeTimer.Stop();
                    closeTimer.Dispose();
                    dialogToClose.Close();
                };
                closeTimer.Start();
            }
        }
    }
}
