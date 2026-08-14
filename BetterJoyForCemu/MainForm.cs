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
        private Timer countDown;
        private int count;
        private Timer clickTimer;

        // When a Windows Service already owns the hardware (see ServiceControlProtocol/
        // HeadlessJoyconHost), this GUI never runs its own HID/ViGEm pipeline at all - it just
        // shows live status pushed over ServiceControlClient and forwards a handful of commands
        // (rumble test/join-split/calibration) instead of acting on a live Joycon directly.
        // Decided once in MainForm_Load; not re-evaluated mid-session.
        private bool isRemoteMode = false;
        private ServiceControlClient serviceClient;

        public enum NonOriginalController : int {
            Disabled = 0,
            DefaultCalibration = 1,
            ControllerCalibration = 2,
        }

        public MainForm() {
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

            //list all options
            string[] myConfigs = ConfigurationManager.AppSettings.AllKeys;
            Size childSize = new Size(150, 20);
            for (int i = 0; i != myConfigs.Length; i++) {
                settingsTable.RowCount++;
                settingsTable.Controls.Add(new Label() { Text = myConfigs[i], TextAlign = ContentAlignment.BottomLeft, AutoEllipsis = true, Size = childSize }, 0, i);

                var value = ConfigurationManager.AppSettings[myConfigs[i]];
                Control childControl;
                if (value == "true" || value == "false") {
                    childControl = new CheckBox() { Checked = Boolean.Parse(value), Size = childSize };
                } else {
                    childControl = new TextBox() { Text = value, Size = childSize };
                }

                childControl.MouseClick += cbBox_Changed;
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
            if (AppPaths.ServiceModeEnabled && IsBetterJoyServiceRunning()) {
                serviceClient = new ServiceControlClient();
                isRemoteMode = serviceClient.Connect();
            }

            if (isRemoteMode) {
                WireServiceClientEvents();

                // Add Controllers/blacklist (_3rdPartyControllers dialog) reads/edits these two
                // in-memory lists - normally populated by Program.Start()'s GUI branch, which
                // never runs here. Load them the same way headless mode does, so the dialog
                // isn't working from an empty list.
                _3rdPartyControllers.LoadIntoProgramLists();

                AppendTextBox("Connected to the BetterJoy service - it owns the controllers; this window shows live status only.\r\n");
                serviceClient.RequestSnapshot();
            } else {
                Program.Start();
            }

            console.Visible = !Boolean.Parse(ConfigurationManager.AppSettings["HideStatus"]);
            if (!console.Visible) {
                // Close up the gap console leaves behind by pulling the settings gear/version
                // label up into its row instead of leaving them down where console used to end.
                // console.Top itself is the stable anchor here - it doesn't change when Visible
                // is toggled off. The form is AutoSize/GrowAndShrink, so it naturally shrinks to
                // fit afterward - and grows back to fit rightPanel when settings gets toggled
                // open later, since that's sized independently of this.
                btn_settings.Top = console.Top;
                version_lbl.Top = console.Top;
            }

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
                Environment.Exit(0);
            }
        }

        private void linkLabel1_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e) {
            donationLink.LinkVisited = true;
            System.Diagnostics.Process.Start("http://paypal.me/DavidKhachaturov/5");
        }

        public void AppendTextBox(string value) { // https://stackoverflow.com/questions/519233/writing-to-a-textbox-from-another-thread
            if (InvokeRequired) {
                this.Invoke(new Action<string>(AppendTextBox), new object[] { value });
                return;
            }
            console.AppendText(value);
        }

        // GUI mode always has an interactive desktop of its own, so these just call
        // WindowsInput.Simulate directly - same as this code always has.
        public void SimulateKeyClick(int keyCode) {
            WindowsInput.Simulate.Events().Click((WindowsInput.Events.KeyCode)keyCode).Invoke();
        }

        public void SimulateKeyHold(int keyCode) {
            WindowsInput.Simulate.Events().Hold((WindowsInput.Events.KeyCode)keyCode).Invoke();
        }

        public void SimulateKeyRelease(int keyCode) {
            WindowsInput.Simulate.Events().Release((WindowsInput.Events.KeyCode)keyCode).Invoke();
        }

        public void SimulateButtonClick(int buttonCode) {
            WindowsInput.Simulate.Events().Click((WindowsInput.Events.ButtonCode)buttonCode).Invoke();
        }

        public void SimulateButtonHold(int buttonCode) {
            WindowsInput.Simulate.Events().Hold((WindowsInput.Events.ButtonCode)buttonCode).Invoke();
        }

        public void SimulateButtonRelease(int buttonCode) {
            WindowsInput.Simulate.Events().Release((WindowsInput.Events.ButtonCode)buttonCode).Invoke();
        }

        public void SimulateMoveTo(int x, int y) {
            WindowsInput.Simulate.Events().MoveTo(x, y).Invoke();
        }

        public void SimulateMoveBy(int dx, int dy) {
            WindowsInput.Simulate.Events().MoveBy(dx, dy).Invoke();
        }

        public void SimulateMoveToScreenCenter() {
            WindowsInput.Simulate.Events().MoveTo(Screen.PrimaryScreen.Bounds.Width / 2, Screen.PrimaryScreen.Bounds.Height / 2).Invoke();
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

        // All ServiceControlClient events fire from a background read thread - each handler
        // marshals onto the UI thread itself (RenderSnapshot does its own Invoke check; the
        // rest are simple enough to wrap inline here).
        private void WireServiceClientEvents() {
            serviceClient.SnapshotReceived += RenderSnapshot;

            serviceClient.CalibrationStarted += padId => this.Invoke(new MethodInvoker(delegate {
                console.Text = "Calibrating via service - hold the controller flat...";
            }));

            serviceClient.CalibrationComplete += padId => this.Invoke(new MethodInvoker(delegate {
                console.Text += "\r\nCalibration completed!!!\r\n";
                RestoreCalibrateIcon();
            }));

            serviceClient.CalibrationFailed += padId => this.Invoke(new MethodInvoker(delegate {
                console.Text += "\r\nCalibration failed - only one controller may be connected while calibrating.\r\n";
                RestoreCalibrateIcon();
            }));

            serviceClient.Disconnected += () => this.Invoke(new MethodInvoker(delegate {
                AppendTextBox("Lost connection to the BetterJoy service.\r\n");
            }));
        }

        // Full re-render from a snapshot rather than incremental diffing against previous state
        // - simpler and self-healing, and snapshots only arrive when something actually changed
        // (see HeadlessJoyconHost.BroadcastSnapshot), not continuously.
        private void RenderSnapshot(List<ControllerRecord> records) {
            if (InvokeRequired) {
                this.Invoke(new Action<List<ControllerRecord>>(RenderSnapshot), new object[] { records });
                return;
            }

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

        // Left click on any controller (Pro or Joycon) opens Map Buttons; right click on a
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

        // Left-click disambiguation between "map buttons" (single click) and "calibrate"
        // (double click), only relevant when AllowCalibration is on - otherwise every left
        // click opens Map Buttons immediately, same as before this existed. A single click is
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
            string tip = isPro ? "Left-click to map buttons" : "Right-click to split / left-click to map buttons";
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
            this.Invoke(new MethodInvoker(delegate {
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
            }));
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

            this.Invoke(new MethodInvoker(delegate {
                AssignJoyconToSlot(joycon, icon);
            }));
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
            this.Invoke(new MethodInvoker(delegate {
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
            }));
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

                            if (v.out_xbox != null) {
                                v.out_xbox.Disconnect();
                                v.out_xbox = null;
                            }

                            if (v.out_ds4 != null) {
                                v.out_ds4.Disconnect();
                                v.out_ds4 = null;
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

            for (int row = 0; row < ConfigurationManager.AppSettings.AllKeys.Length; row++) {
                var valCtl = settingsTable.GetControlFromPosition(1, row);
                var KeyCtl = settingsTable.GetControlFromPosition(0, row).Text;

                if (valCtl.GetType() == typeof(CheckBox) && settings[KeyCtl] != null) {
                    settings[KeyCtl].Value = ((CheckBox)valCtl).Checked.ToString().ToLower();
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
                } else if (valCtl.GetType() == typeof(TextBox) && settings[KeyCtl] != null) {
                    settings[KeyCtl].Value = ((TextBox)valCtl).Text.ToLower();
                }

                if (KeyCtl == "HomeLEDOn") {
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
        private void StartCalibrate(object sender, EventArgs e) {
            if (calibrationInProgress) {
                RestoreCalibrateIcon();
                return;
            }
            if (Program.mgr.j.Count == 0) {
                this.console.Text = "Please connect a single pro controller.";
                RestoreCalibrateIcon();
                return;
            }
            if (Program.mgr.j.Count > 1) {
                this.console.Text = "Please calibrate one controller at a time (disconnect others).";
                RestoreCalibrateIcon();
                return;
            }
            calibrationInProgress = true;
            countDown = new Timer();
            this.count = 4;
            this.CountDown(null, null);
            countDown.Tick += new EventHandler(CountDown);
            countDown.Interval = 1000;
            countDown.Enabled = true;
        }

        private void StartGetData() {
            CalibrationState.ClearSamples();
            countDown = new Timer();
            this.count = 3;
            CalibrationState.Calibrating = true;
            countDown.Tick += new EventHandler(CalcData);
            countDown.Interval = 1000;
            countDown.Enabled = true;
        }

        private void btn_reassign_open_Click(object sender, EventArgs e) {
            Reassign mapForm = new Reassign();
            mapForm.ShowDialog();
        }

        private void CountDown(object sender, EventArgs e) {
            if (this.count == 0) {
                this.console.Text = "Calibrating...";
                countDown.Stop();
                this.StartGetData();
            } else {
                this.console.Text = "Plese keep the controller flat." + "\r\n";
                this.console.Text += "Calibration will start in " + this.count + " seconds.";
                this.count--;
            }
        }
        private void CalcData(object sender, EventArgs e) {
            if (this.count == 0) {
                countDown.Stop();
                CalibrationState.Calibrating = false;
                CalibrationState.FinishCalibration(Program.mgr.j.First().serial_number);
                this.console.Text += "Calibration completed!!!" + "\r\n";
                Program.mgr.j.First().getActiveData();
                calibrationInProgress = false;
                RestoreCalibrateIcon();
            } else {
                this.count--;
            }

        }
    }
}
