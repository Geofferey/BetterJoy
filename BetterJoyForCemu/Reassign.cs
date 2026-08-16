using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Configuration;
using System.Data;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace BetterJoyForCemu {
    public partial class Reassign : Form {
        private WindowsInput.Events.Sources.IKeyboardEventSource keyboard;
        private WindowsInput.Events.Sources.IMouseEventSource mouse;

        ContextMenuStrip menu_joy_buttons = new ContextMenuStrip();

        private Control curAssignment;

        // Controller buttons have no equivalent to WindowsInput.Capture.Global (there's no OS-
        // level hook for "a button was pressed on this specific HID device") - the only way to
        // detect a press is to poll a Joycon's own current button state directly. Locally
        // (serviceClient == null) that's this process's own Program.mgr.j, polled by joyPoll
        // below. In remote mode the service owns the hardware and this process has no Joycon
        // instances at all - HeadlessJoyconHost does the identical polling over there instead and
        // relays each transition over the control pipe (see ButtonTransition/StartButtonCapture),
        // landing in ServiceClient_ButtonTransition below. Either way, every actual press ends up
        // going through the shared HandleButtonTransition.
        private readonly ServiceControlClient serviceClient;
        private Timer joyPoll;
        private Timer controllerRefreshTimer;
        private readonly Dictionary<Joycon, bool[]> joyPrevButtons = new Dictionary<Joycon, bool[]>();
        private readonly List<ControllerProfileInfo> remoteProfiles = new List<ControllerProfileInfo>();
        private readonly string preferredProfileId;
        private ComboBox controllerSelector;
        private bool updatingControllerSelector;
        private bool initialControllerSelection = true;
        private long newestControllerSequence = -1;

        // Combo capture: hold down everything you want in the combo, then let go of all of it -
        // whatever got pressed while at least one member was held becomes the saved bind, joined
        // with "+" (see Joycon.IsComboHeld). comboMembers/comboHeldNow being non-null IS the
        // "currently combo-capturing" flag.
        private HashSet<string> comboMembers;
        private HashSet<string> comboHeldNow;
        private Timer comboTimeout;

        // These actions only accept controller inputs at runtime. Keyboard/mouse capture is
        // intentionally rejected for them below, just as before profiles were introduced.
        private static readonly HashSet<string> ControllerOnlyKeys = new HashSet<string> {
            "left_click", "right_click", "center_click", "scroll_up", "scroll_down",
            "clench_gyro"
        };

        private string SelectedProfileId {
            get {
                ControllerProfileInfo selected = controllerSelector?.SelectedItem as ControllerProfileInfo;
                return selected?.ProfileId;
            }
        }

        private string GetBindValue(string key) {
            return ControllerMappings.Value(SelectedProfileId, key);
        }

        private void SetBindValue(string key, string value) {
            if (!String.IsNullOrEmpty(SelectedProfileId))
                ControllerMappings.SetValue(SelectedProfileId, key, value);
        }

        // serviceClient: null when this process owns the hardware directly (local mode);
        // non-null when deferring to a running service (see MainForm.btn_reassign_open_Click) -
        // determines where controller-button auto-detect actually gets its presses from.
        public Reassign(ServiceControlClient serviceClient = null,
                        IEnumerable<ControllerRecord> remoteRecords = null,
                        string preferredProfileId = null) {
            this.serviceClient = serviceClient;
            this.preferredProfileId = preferredProfileId;
            InitializeComponent();
            MakeRoomForControllerSelector();
            AddGyroMouseButtons();

            foreach (int i in Enum.GetValues(typeof(Joycon.Button))) {
                ToolStripMenuItem temp = new ToolStripMenuItem(Enum.GetName(typeof(Joycon.Button), i));
                temp.Tag = i;
                menu_joy_buttons.Items.Add(temp);
            }

            // Explicitly disabling a special mapping is different from middle-clicking it back
            // to its default: Capture and Re-Centre Gyro have nonzero defaults, but the user may
            // still want either feature completely unbound. Keep this action visually separated
            // and permanently last after every physical controller-button choice.
            menu_joy_buttons.Items.Add(new ToolStripSeparator());
            menu_joy_buttons.Items.Add(new ToolStripMenuItem("Disabled") { Tag = "0" });

            menu_joy_buttons.ItemClicked += Menu_joy_buttons_ItemClicked;

            specialButtons = new List<SplitButton> { btn_capture, btn_home, btn_sl_l, btn_sl_r, btn_sr_l, btn_sr_r, btn_shake, btn_reset_mouse, btn_active_gyro };
            specialButtons.AddRange(gyroMouseButtons);

            foreach (SplitButton c in specialButtons) {
                c.Tag = c.Name.Substring(4);
                GetPrettyName(c);

                c.MouseDown += Remap;
                c.Menu = menu_joy_buttons;
                c.TextAlign = System.Drawing.ContentAlignment.MiddleLeft;
            }

            AddControllerSelector();
            if (remoteRecords != null)
                SetRemoteProfiles(remoteRecords);
            RefreshControllerChoices();
        }

        // Built in code, not the Designer, as a second column beside the existing one - avoids
        // hand-computing six more rows' worth of Designer.cs pixel coordinates in a single
        // column that would otherwise run the form well past its current height.
        private readonly List<SplitButton> gyroMouseButtons = new List<SplitButton>();
        private List<SplitButton> specialButtons;

        private const int ControllerSelectorHeight = 36;

        private void MakeRoomForControllerSelector() {
            foreach (Control control in Controls)
                control.Location = new Point(control.Left, control.Top + ControllerSelectorHeight);
            ClientSize = new Size(ClientSize.Width, ClientSize.Height + ControllerSelectorHeight);
        }

        private void AddControllerSelector() {
            var label = new Label {
                AutoSize = true,
                Location = new Point(15, 16),
                Text = "Controller",
            };
            controllerSelector = new ComboBox {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Location = new Point(105, 11),
                Size = new Size(375, 21),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            };
            controllerSelector.SelectedIndexChanged += ControllerSelector_SelectedIndexChanged;
            Controls.Add(label);
            Controls.Add(controllerSelector);
        }

        private void AddGyroMouseButtons() {
            // Short labels + a shared section header instead of repeating "(Gyro Mouse)" on each
            // one - the old per-row suffix made the label wider than the gap before the button,
            // which is what was overlapping/garbling the button text in this column.
            var entries = new (string key, string label)[] {
                ("left_click", "Left Click"),
                ("right_click", "Right Click"),
                ("center_click", "Center Click"),
                ("scroll_up", "Scroll Up"),
                ("scroll_down", "Scroll Down"),
                ("clench_gyro", "Clench Gyro"),
            };

            const int col2LabelX = 250;
            const int col2ButtonX = 350;
            const int buttonWidth = 130;
            // Lines up with the left column's row 2-6 rhythm (btn_home..btn_sr_r), leaving row 1's
            // slot free for the header below.
            const int entryStartY = 41 + ControllerSelectorHeight;
            const int rowSpacing = 29;

            var header = new Label {
                AutoSize = true,
                Location = new Point(col2LabelX, 17 + ControllerSelectorHeight),
                Text = "Gyro Mouse Only",
                Font = new Font(Font, FontStyle.Bold),
            };
            Controls.Add(header);

            for (int row = 0; row < entries.Length; row++) {
                int y = entryStartY + row * rowSpacing;

                var label = new Label {
                    AutoSize = true,
                    Location = new Point(col2LabelX, y + 5),
                    Text = entries[row].label,
                    TextAlign = ContentAlignment.TopCenter,
                };
                var button = new SplitButton {
                    Name = "btn_" + entries[row].key,
                    Location = new Point(col2ButtonX, y),
                    Size = new Size(buttonWidth, 23),
                    UseVisualStyleBackColor = true,
                };

                Controls.Add(label);
                Controls.Add(button);
                gyroMouseButtons.Add(button);
            }

            // Second column needs more width than the Designer-sized form has - height already
            // fits (this column has fewer rows than the first one).
            ClientSize = new Size(Math.Max(ClientSize.Width, col2ButtonX + buttonWidth + 15), ClientSize.Height);
        }

        private void SetRemoteProfiles(IEnumerable<ControllerRecord> records) {
            var byProfile = new Dictionary<string, ControllerProfileInfo>(StringComparer.Ordinal);
            if (records != null) {
                foreach (ControllerRecord record in records) {
                    if (String.IsNullOrEmpty(record.ProfileId))
                        continue;

                    ControllerProfileInfo existing;
                    if (!byProfile.TryGetValue(record.ProfileId, out existing) ||
                        record.ConnectionSequence > existing.ConnectionSequence) {
                        byProfile[record.ProfileId] = new ControllerProfileInfo {
                            ProfileId = record.ProfileId,
                            DisplayName = String.IsNullOrEmpty(record.ProfileName)
                                ? "Controller " + (record.PadId + 1)
                                : record.ProfileName,
                            ConnectionSequence = record.ConnectionSequence,
                        };
                    }
                }
            }

            remoteProfiles.Clear();
            remoteProfiles.AddRange(byProfile.Values.OrderByDescending(p => p.ConnectionSequence));
        }

        private void ServiceClient_SnapshotReceived(List<ControllerRecord> records) {
            if (InvokeRequired) {
                BeginInvoke(new Action<List<ControllerRecord>>(ServiceClient_SnapshotReceived), records);
                return;
            }

            SetRemoteProfiles(records);
            RefreshControllerChoices();
        }

        private void RefreshControllerChoices() {
            List<ControllerProfileInfo> choices = serviceClient != null
                ? remoteProfiles.OrderByDescending(p => p.ConnectionSequence).ToList()
                : ControllerMappings.ConnectedProfiles(Program.mgr?.j);

            string selectedId = SelectedProfileId;
            long newest = choices.Count == 0 ? -1 : choices.Max(p => p.ConnectionSequence);
            string targetId = selectedId;

            if (initialControllerSelection) {
                targetId = choices.Any(p => p.ProfileId == preferredProfileId)
                    ? preferredProfileId
                    : choices.FirstOrDefault()?.ProfileId;
            } else if (newest > newestControllerSequence) {
                // A genuinely new connection arrived while the dialog was open. Match the same
                // default as opening the dialog: the most recently connected logical controller.
                targetId = choices.FirstOrDefault()?.ProfileId;
            } else if (!choices.Any(p => p.ProfileId == selectedId)) {
                // Join/split changes the logical profile ID without creating a new Joycon.
                targetId = choices.FirstOrDefault()?.ProfileId;
            }

            bool changed = controllerSelector.Items.Count != choices.Count;
            if (!changed) {
                for (int i = 0; i < choices.Count; i++) {
                    ControllerProfileInfo current = controllerSelector.Items[i] as ControllerProfileInfo;
                    if (current == null || current.ProfileId != choices[i].ProfileId ||
                        current.DisplayName != choices[i].DisplayName) {
                        changed = true;
                        break;
                    }
                }
            }

            updatingControllerSelector = true;
            if (changed) {
                controllerSelector.Items.Clear();
                controllerSelector.Items.AddRange(choices.Cast<object>().ToArray());
            }

            ControllerProfileInfo target = controllerSelector.Items.Cast<ControllerProfileInfo>()
                .FirstOrDefault(p => p.ProfileId == targetId);
            controllerSelector.SelectedItem = target;
            if (target == null)
                controllerSelector.SelectedIndex = -1;
            updatingControllerSelector = false;

            newestControllerSequence = newest;
            initialControllerSelection = false;
            ApplySelectedController();
        }

        private void ControllerSelector_SelectedIndexChanged(object sender, EventArgs e) {
            if (!updatingControllerSelector)
                ApplySelectedController();
        }

        private void ApplySelectedController() {
            CancelComboCapture();
            joyPrevButtons.Clear();

            bool hasController = !String.IsNullOrEmpty(SelectedProfileId);
            foreach (SplitButton button in specialButtons) {
                button.Enabled = hasController;
                GetPrettyName(button);
            }
            btn_apply.Enabled = hasController;
        }

        private void Menu_joy_buttons_ItemClicked(object sender, ToolStripItemClickedEventArgs e) {
            Control c = sender as Control;

            ToolStripItem clickedItem = e.ClickedItem;

            SplitButton caller = (SplitButton)c.Tag;
            string value = clickedItem.Tag is int
                ? "joy_" + clickedItem.Tag
                : clickedItem.Tag as string;
            if (value == null)
                return;

            SetBindValue((string)caller.Tag, value);
            GetPrettyName(caller);
        }

        private void Remap(object sender, MouseEventArgs e) {
            SplitButton c = sender as SplitButton;
            switch (e.Button) {
                case MouseButtons.Left:
                    curAssignment = c;
                    StartComboCapture(c);
                    break;
                case MouseButtons.Middle:
                    CancelComboCapture();
                    SetBindValue((string)c.Tag, ControllerMappings.DefaultValue((string)c.Tag));
                    GetPrettyName(c);
                    break;
                case MouseButtons.Right:
                    break;
            }
        }

        private void StartComboCapture(SplitButton c) {
            comboMembers = new HashSet<string>();
            comboHeldNow = new HashSet<string>();
            c.Text = "Press combo...";

            // Button's own MouseDown handling grabs native Win32 mouse capture on press - normally
            // released on its matching MouseUp, but from here on WE'RE the ones deciding what
            // counts as input (via the global hook), not the button. Left stuck, that native
            // capture routes every subsequent left click system-wide back to this control (which
            // is what the focus rectangle sticking around was actually indicating - Windows only
            // force-releases stuck capture on a focus change, which is why Tab "fixed" it).
            // Releasing it explicitly, and taking focus off the button, avoids relying on that.
            c.Capture = false;
            ActiveControl = null;

            comboTimeout?.Stop();
            comboTimeout?.Dispose();
            comboTimeout = new Timer { Interval = 15000 };
            comboTimeout.Tick += (s, e) => CancelComboCapture();
            comboTimeout.Start();
        }

        // Safety net (e.g. the user changes their mind and walks away without releasing
        // whatever they were holding) as well as the explicit middle-click-to-reset path -
        // leaves whatever bind already existed untouched, just stops listening.
        private void CancelComboCapture() {
            if (comboMembers == null)
                return;

            comboTimeout?.Stop();
            comboTimeout?.Dispose();
            comboTimeout = null;

            Control target = curAssignment;
            curAssignment = null;
            comboMembers = null;
            comboHeldNow = null;

            if (target != null)
                GetPrettyName(target); // drop the "Press combo..." placeholder, restore the real value
        }

        // Called for every down/up transition seen while curAssignment is combo-capturing -
        // accumulates every input that gets pressed (comboMembers) and tracks which of those are
        // STILL held (comboHeldNow). Once everything that was pressed has been released again,
        // the accumulated set becomes the saved combo. downPart/upPart are mutually exclusive
        // per call, matching how the underlying key/mouse/controller sources report one
        // transition at a time. Always marshals onto the UI thread first - Keyboard_KeyEvent/
        // Mouse_MouseEvent fire from WindowsInput's background hook thread, and this both
        // touches Control.Text and mutates comboMembers/comboHeldNow/curAssignment, which
        // JoyPoll_Tick (a UI-thread Timer) also reads/writes.
        private void HandleComboInput(string downPart, string upPart) {
            if (InvokeRequired) {
                this.Invoke(new Action<string, string>(HandleComboInput), new object[] { downPart, upPart });
                return;
            }

            if (comboMembers == null)
                return; // capture was cancelled/finished between the event firing and this running

            if (downPart != null) {
                comboMembers.Add(downPart);
                comboHeldNow.Add(downPart);
                curAssignment.Text = "Press combo... (" + comboMembers.Count + ")";
            }

            if (upPart != null) {
                comboHeldNow.Remove(upPart);
                if (comboHeldNow.Count == 0 && comboMembers.Count > 0)
                    FinishComboCapture();
            }
        }

        private void FinishComboCapture() {
            comboTimeout?.Stop();
            comboTimeout?.Dispose();
            comboTimeout = null;

            string combo = String.Join("+", comboMembers.OrderBy(s => s, StringComparer.Ordinal));
            SetBindValue((string)curAssignment.Tag, combo);
            GetPrettyName(curAssignment);

            curAssignment = null;
            comboMembers = null;
            comboHeldNow = null;
        }

        private void Reassign_Load(object sender, EventArgs e) {
            keyboard = WindowsInput.Capture.Global.KeyboardAsync();
            keyboard.KeyEvent += Keyboard_KeyEvent;
            mouse = WindowsInput.Capture.Global.MouseAsync();
            mouse.MouseEvent += Mouse_MouseEvent;

            if (serviceClient != null) {
                serviceClient.ButtonTransition += ServiceClient_ButtonTransition;
                serviceClient.SnapshotReceived += ServiceClient_SnapshotReceived;
                serviceClient.StartButtonCapture();
                serviceClient.RequestSnapshot();
            } else {
                joyPoll = new Timer { Interval = 30 };
                joyPoll.Tick += JoyPoll_Tick;
                joyPoll.Start();

                controllerRefreshTimer = new Timer { Interval = 500 };
                controllerRefreshTimer.Tick += (s, args) => RefreshControllerChoices();
                controllerRefreshTimer.Start();
            }
        }

        // Remote-mode counterpart to JoyPoll_Tick below - HeadlessJoyconHost does the identical
        // polling against its own (non-null there) Program.mgr.j and pushes each transition over
        // the control pipe instead of acting on it directly. Fires on the pipe's background read
        // thread - marshal here before reading the selected ComboBox profile.
        private void ServiceClient_ButtonTransition(ButtonTransitionInfo info) {
            if (InvokeRequired) {
                BeginInvoke(new Action<ButtonTransitionInfo>(ServiceClient_ButtonTransition), info);
                return;
            }

            if (info.ProfileId != SelectedProfileId)
                return;
            HandleButtonTransition(info.ButtonIndex, info.IsDown);
        }

        // Polls every connected Joycon's current button state and edge-detects a rising press
        // ourselves (a freshly-seen Joycon's baseline is recorded without triggering, so a
        // button already held before this dialog opened - or before that controller connected -
        // never gets mistaken for a new press). Runs continuously, independent of curAssignment,
        // same as the keyboard/mouse hooks only actually acting on a press while curAssignment
        // is set. Local mode only (serviceClient == null) - see ServiceClient_ButtonTransition
        // for the remote equivalent.
        private void JoyPoll_Tick(object sender, EventArgs e) {
            if (Program.mgr == null)
                return;

            int buttonCount = Enum.GetValues(typeof(Joycon.Button)).Length;
            foreach (Joycon jc in Program.mgr.j) {
                // A joined pair's two halves each cross-reference the other's raw buttons into
                // their own buttons[] array (see Joycon.DoThingsWithButtons, the "other != null"
                // block) so that EITHER side alone already has a complete, correctly-labeled view
                // of the whole pair's buttons - the left's own DPAD_* stay real d-pad, and its
                // buttons[B]/[A]/[X]/[Y] mirror the right's real B/A/X/Y (and vice versa in the
                // other direction for HOME/PLUS). Polling both objects independently, as this loop
                // otherwise would, sees that same cross-referenced overlap as two separate
                // presses - physically pressing B alone reports as both the right instance's own
                // buttons[DPAD_DOWN] (which protocol-wise IS its B button) and the left instance's
                // buttons[B] (cross-referenced from the right), landing in a captured combo as
                // "DPAD_DOWN+B" for one single press. Skipping the right half here leaves the left
                // half as the one consistent, complete source per pair.
                if (jc.other != null && jc.other != jc && !jc.isLeft)
                    continue;
                if (ControllerMappings.ProfileIdFor(jc) != SelectedProfileId)
                    continue;

                if (!joyPrevButtons.TryGetValue(jc, out bool[] prev)) {
                    prev = new bool[buttonCount];
                    for (int bi = 0; bi < buttonCount; bi++)
                        prev[bi] = jc.GetButton((Joycon.Button)bi);
                    joyPrevButtons[jc] = prev;
                    continue;
                }

                for (int bi = 0; bi < buttonCount; bi++) {
                    bool now = jc.GetButton((Joycon.Button)bi);
                    bool wasDown = prev[bi];
                    prev[bi] = now;

                    if (now != wasDown)
                        HandleButtonTransition(bi, now);
                }
            }
        }

        // Shared by JoyPoll_Tick (local) and ServiceClient_ButtonTransition (remote) - both
        // reduce down to "button bi just went to state now," regardless of which process
        // actually polled the hardware to find that out.
        private void HandleButtonTransition(int bi, bool now) {
            if (InvokeRequired) {
                this.Invoke(new Action<int, bool>(HandleButtonTransition), new object[] { bi, now });
                return;
            }

            if (curAssignment == null)
                return;

            if (comboMembers != null) {
                HandleComboInput(now ? "joy_" + bi : null, now ? null : "joy_" + bi);
            } else if (now) {
                SetBindValue((string)curAssignment.Tag, "joy_" + bi);
                GetPrettyName(curAssignment);
                curAssignment = null;
            }
        }

        private void Mouse_MouseEvent(object sender, WindowsInput.Events.Sources.EventSourceEventArgs<WindowsInput.Events.Sources.MouseEvent> e) {
            if (curAssignment == null)
                return;

            if (comboMembers != null) {
                // Left click is how this box gets opened for capture in the first place - the
                // global hook sees every left click system-wide and can't tell "the click that
                // opened the box" apart from "a left click somewhere else" by timing alone. It
                // CAN tell them apart by position: only a click actually over the box being
                // assigned is treated as real input, so a bare click-and-release on the box still
                // works as a one-shot "bind this to left click" (matching the old single-input
                // behavior) while a left click anywhere else is ignored instead of restarting or
                // corrupting the capture.
                bool leftOverBox = curAssignment.RectangleToScreen(curAssignment.ClientRectangle).Contains(Cursor.Position);

                if (e.Data.ButtonDown != null && (e.Data.ButtonDown.Button != WindowsInput.Events.ButtonCode.Left || leftOverBox))
                    HandleComboInput("mse_" + (int)e.Data.ButtonDown.Button, null);
                if (e.Data.ButtonUp != null && (e.Data.ButtonUp.Button != WindowsInput.Events.ButtonCode.Left || leftOverBox))
                    HandleComboInput(null, "mse_" + (int)e.Data.ButtonUp.Button);
                e.Next_Hook_Enabled = false;
                return;
            }

            // Gyro-mouse buttons only ever check for a "joy_" binding at runtime (see Joycon.
            // SimulateGyroMouseButton/Scroll) - a keyboard/mouse trigger would capture fine here
            // but then silently do nothing, exactly the "looks bound but doesn't work" trap
            // GyroToJoyOrMouse just turned out to be. Left uncaptured so JoyPoll_Tick or the
            // right-click menu (both joy_-only) remain the way to actually assign these.
            if (e.Data.ButtonDown != null && !ControllerOnlyKeys.Contains((string)curAssignment.Tag)) {
                SetBindValue((string)curAssignment.Tag, "mse_" + ((int)e.Data.ButtonDown.Button));
                AsyncPrettyName(curAssignment);
                curAssignment = null;
                e.Next_Hook_Enabled = false;
            }
        }

        private void Keyboard_KeyEvent(object sender, WindowsInput.Events.Sources.EventSourceEventArgs<WindowsInput.Events.Sources.KeyboardEvent> e) {
            if (curAssignment == null)
                return;

            if (comboMembers != null) {
                if (e.Data.KeyDown != null)
                    HandleComboInput("key_" + (int)e.Data.KeyDown.Key, null);
                if (e.Data.KeyUp != null)
                    HandleComboInput(null, "key_" + (int)e.Data.KeyUp.Key);
                e.Next_Hook_Enabled = false;
                return;
            }

            // See the same guard in Mouse_MouseEvent above.
            if (e.Data.KeyDown != null && !ControllerOnlyKeys.Contains((string)curAssignment.Tag)) {
                SetBindValue((string)curAssignment.Tag, "key_" + ((int)e.Data.KeyDown.Key));
                AsyncPrettyName(curAssignment);
                curAssignment = null;
                e.Next_Hook_Enabled = false;
            }
        }

        private void Reassign_FormClosing(object sender, FormClosingEventArgs e) {
            keyboard?.Dispose();
            mouse?.Dispose();
            joyPoll?.Stop();
            joyPoll?.Dispose();
            controllerRefreshTimer?.Stop();
            controllerRefreshTimer?.Dispose();
            comboTimeout?.Stop();
            comboTimeout?.Dispose();

            if (serviceClient != null) {
                serviceClient.ButtonTransition -= ServiceClient_ButtonTransition;
                serviceClient.SnapshotReceived -= ServiceClient_SnapshotReceived;
                serviceClient.StopButtonCapture();
            }
        }

        private void AsyncPrettyName(Control c) {
            if (InvokeRequired) {
                this.Invoke(new Action<Control>(AsyncPrettyName), new object[] { c });
                return;
            }
            GetPrettyName(c);
        }

        private void GetPrettyName(Control c) {
            string val = GetBindValue((string)c.Tag);
            bool unassigned = val == "0";

            // A combo is "+"-joined parts (see Joycon.IsComboHeld) - a single-input bind is just
            // a one-part combo, so this handles both uniformly.
            string description = unassigned ? "(unassigned)" : String.Join("+", val.Split('+').Select(DescribeBindPart));
            c.Text = unassigned ? ((c == btn_home) ? "Guide" : "") : description;

            // Long combos can still run out of room on the button itself (see Reassign.Designer.cs
            // for the width these buttons get) - the tooltip always shows the full, untruncated
            // bind so it's never actually ambiguous what's assigned, just hover to check.
            tip_reassign.SetToolTip(c, description + "\r\n\r\nLeft-click to detect input.\r\nMiddle-click to clear to default.\r\nRight-click to see more options.");
        }

        private static string DescribeBindPart(string part) {
            Type t = part.StartsWith("joy_") ? typeof(Joycon.Button) : (part.StartsWith("key_") ? typeof(WindowsInput.Events.KeyCode) : typeof(WindowsInput.Events.ButtonCode));
            return Enum.GetName(t, Int32.Parse(part.Substring(4)));
        }

        private void btn_apply_Click(object sender, EventArgs e) {
            ControllerMappings.Save();
        }

        private void btn_close_Click(object sender, EventArgs e) {
            btn_apply_Click(sender, e);
            Close();
        }
    }
}
