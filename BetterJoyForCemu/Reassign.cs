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
        ContextMenuStrip menu_gyro_activation = new ContextMenuStrip();

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
        private Button gameControllersButton;
        private Label profileStatusDot;
        private Label profileStatusLabel;
        private Label virtualControllerNameLabel;
        private Label virtualControllerDetailLabel;
        private readonly Dictionary<string, Panel> profilePages = new Dictionary<string, Panel>();
        private readonly Dictionary<string, Button> profileNavigationButtons = new Dictionary<string, Button>();
        private Panel profilePageHost;
        private Panel profileNavigationAccent;
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

        private ControllerProfileInfo SelectedProfile {
            get {
                if (controllerSelector == null)
                    return null;

                // ComboBox.SelectedItem internally indexes Items[SelectedIndex]. When the last
                // controller disappears, WinForms can briefly retain SelectedIndex == 0 after
                // the refresh has emptied Items, making that otherwise innocent property getter
                // throw ArgumentOutOfRangeException. Validate the pair explicitly before reading
                // the collection so the dialog transitions cleanly to its no-profile state.
                int selectedIndex = controllerSelector.SelectedIndex;
                if (selectedIndex < 0 || selectedIndex >= controllerSelector.Items.Count)
                    return null;

                ControllerProfileInfo selected =
                    controllerSelector.Items[selectedIndex] as ControllerProfileInfo;
                return selected;
            }
        }

        private string SelectedProfileId => SelectedProfile?.ProfileId;

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
            CreateDynamicProfileControls();
            BuildProfileInterface();

            foreach (int i in Enum.GetValues(typeof(Joycon.Button))) {
                ToolStripMenuItem temp = new ToolStripMenuItem(Enum.GetName(typeof(Joycon.Button), i));
                temp.Tag = i;
                menu_joy_buttons.Items.Add(temp);

                ToolStripMenuItem activationItem = new ToolStripMenuItem(Enum.GetName(typeof(Joycon.Button), i));
                activationItem.Tag = i;
                menu_gyro_activation.Items.Add(activationItem);
            }

            // Explicitly disabling a special mapping is different from middle-clicking it back
            // to its default: Capture and Re-Centre Gyro have nonzero defaults, but the user may
            // still want either feature completely unbound. Keep this action visually separated
            // and permanently last after every physical controller-button choice.
            menu_joy_buttons.Items.Add(new ToolStripSeparator());
            menu_joy_buttons.Items.Add(new ToolStripMenuItem("Disabled") { Tag = "0" });
            menu_gyro_activation.Items.Add(new ToolStripSeparator());
            menu_gyro_activation.Items.Add(new ToolStripMenuItem("Always On") { Tag = "always" });
            menu_gyro_activation.Items.Add(new ToolStripMenuItem("Disabled") { Tag = "0" });

            menu_joy_buttons.ItemClicked += Menu_joy_buttons_ItemClicked;
            menu_gyro_activation.ItemClicked += Menu_joy_buttons_ItemClicked;

            specialButtons = new List<SplitButton> { btn_capture, btn_home, btn_sl_l, btn_sl_r, btn_sr_l, btn_sr_r, btn_shake, btn_reset_mouse, btn_active_gyro };
            specialButtons.AddRange(gyroMouseButtons);
            specialButtons.AddRange(gyroStickActivationButtons);

            foreach (SplitButton c in specialButtons) {
                c.Tag = c == btn_active_gyro
                    ? "active_gyro_mouse"
                    : c.Name.Substring(4);
                GetPrettyName(c);

                c.MouseDown += Remap;
                c.Menu = IsGyroActivationKey((string)c.Tag)
                    ? menu_gyro_activation
                    : menu_joy_buttons;
                c.TextAlign = System.Drawing.ContentAlignment.MiddleLeft;
            }

            StyleAssignmentMenus();
            if (remoteRecords != null)
                SetRemoteProfiles(remoteRecords);
            RefreshControllerChoices();
        }

        // These controls are created in code because the profile window composes its pages at
        // runtime. Their mapping keys and event wiring are still shared with the original
        // Designer-owned buttons below, so page navigation never creates a second behavior path.
        private readonly List<SplitButton> gyroMouseButtons = new List<SplitButton>();
        private readonly List<SplitButton> gyroStickActivationButtons = new List<SplitButton>();
        private List<SplitButton> specialButtons;

        private static bool IsGyroActivationKey(string key) {
            return key == "active_gyro_mouse" ||
                   key == "active_gyro_left_stick" ||
                   key == "active_gyro_right_stick";
        }

        private static readonly Color ProfileBackground = Color.FromArgb(31, 32, 33);
        private static readonly Color ProfileSidebar = Color.FromArgb(26, 27, 28);
        private static readonly Color ProfileSurface = Color.FromArgb(45, 46, 48);
        private static readonly Color ProfileSurfaceHover = Color.FromArgb(55, 56, 58);
        private static readonly Color ProfileBorder = Color.FromArgb(68, 69, 72);
        private static readonly Color ProfileText = Color.FromArgb(244, 244, 244);
        private static readonly Color ProfileMuted = Color.FromArgb(184, 190, 199);
        private static readonly Color ProfileAccent = Color.FromArgb(255, 188, 21);
        private static readonly Color ProfileConnected = Color.FromArgb(62, 201, 116);

        private void CreateDynamicProfileControls() {
            var entries = new (string key, string label)[] {
                ("left_click", "Left Click"),
                ("right_click", "Right Click"),
                ("center_click", "Center Click"),
                ("scroll_up", "Scroll Up"),
                ("scroll_down", "Scroll Down"),
                ("clench_gyro", "Clench Gyro"),
            };
            foreach (var entry in entries) {
                var button = new SplitButton {
                    Name = "btn_" + entry.key,
                };
                gyroMouseButtons.Add(button);
            }

            var activationEntries = new (string key, string label)[] {
                ("active_gyro_left_stick", "Left Stick"),
                ("active_gyro_right_stick", "Right Stick"),
            };
            foreach (var entry in activationEntries) {
                var button = new SplitButton {
                    Name = "btn_" + entry.key,
                };
                gyroStickActivationButtons.Add(button);
            }

            gameControllersButton = new Button {
                Text = "Game Controllers...",
            };
            gameControllersButton.Click += GameControllersButton_Click;
            tip_reassign.SetToolTip(gameControllersButton,
                "Open the selected profile's virtual controller properties when connected.\r\n" +
                "Disconnected profiles open the standard Game Controllers list.");
        }

        private void BuildProfileInterface() {
            SuspendLayout();
            Controls.Clear();

            AutoScaleMode = AutoScaleMode.Dpi;
            BackColor = ProfileBackground;
            ForeColor = ProfileText;
            Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
            ClientSize = new Size(840, 680);
            MinimumSize = new Size(856, 719);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterParent;

            Panel header = BuildProfileHeader();
            Panel footer = BuildProfileFooter();
            Panel body = new Panel {
                Dock = DockStyle.Fill,
                BackColor = ProfileBackground,
            };
            Panel sidebar = BuildProfileSidebar();
            profilePageHost = new Panel {
                Dock = DockStyle.Fill,
                BackColor = ProfileBackground,
            };
            body.Controls.Add(profilePageHost);
            body.Controls.Add(sidebar);

            Controls.Add(body);
            Controls.Add(footer);
            Controls.Add(header);

            Panel bindingsPage = BuildBindingsPage();
            Panel gyroPage = BuildGyroPage();
            Panel virtualControllerPage = BuildVirtualControllerPage();
            profilePages.Add("bindings", bindingsPage);
            profilePages.Add("gyro", gyroPage);
            profilePages.Add("virtual", virtualControllerPage);
            profilePageHost.Controls.Add(bindingsPage);
            profilePageHost.Controls.Add(gyroPage);
            profilePageHost.Controls.Add(virtualControllerPage);
            ShowProfilePage("gyro");

            ResumeLayout(true);
        }

        private Panel BuildProfileHeader() {
            Panel header = new Panel {
                Dock = DockStyle.Top,
                Height = 66,
                BackColor = Color.FromArgb(38, 39, 41),
            };
            header.Paint += (sender, e) => {
                using (Pen pen = new Pen(ProfileBorder))
                    e.Graphics.DrawLine(pen, 0, header.Height - 1, header.Width, header.Height - 1);
            };

            header.Controls.Add(CreateLabel("Controller profile", 18, 25, ProfileText, false));
            controllerSelector = new ComboBox {
                DropDownStyle = ComboBoxStyle.DropDownList,
                FlatStyle = FlatStyle.Flat,
                BackColor = ProfileSurface,
                ForeColor = ProfileText,
                Font = new Font(Font.FontFamily, 9.25F),
                Location = new Point(140, 19),
                Size = new Size(535, 25),
            };
            controllerSelector.SelectedIndexChanged += ControllerSelector_SelectedIndexChanged;
            header.Controls.Add(controllerSelector);

            profileStatusDot = CreateLabel("●", 692, 23, ProfileConnected, true);
            profileStatusLabel = CreateLabel("Connected", 709, 25, ProfileConnected, false);
            header.Controls.Add(profileStatusDot);
            header.Controls.Add(profileStatusLabel);
            return header;
        }

        private Panel BuildProfileFooter() {
            Panel footer = new Panel {
                Dock = DockStyle.Bottom,
                Height = 54,
                BackColor = Color.FromArgb(37, 38, 40),
            };
            footer.Paint += (sender, e) => {
                using (Pen pen = new Pen(ProfileBorder))
                    e.Graphics.DrawLine(pen, 0, 0, footer.Width, 0);
            };

            StyleStandardButton(btn_close, false);
            btn_close.Text = "Close";
            btn_close.Size = new Size(84, 32);
            btn_close.Location = new Point(650, 12);

            StyleStandardButton(btn_apply, true);
            btn_apply.Size = new Size(84, 32);
            btn_apply.Location = new Point(744, 12);
            footer.Controls.Add(btn_close);
            footer.Controls.Add(btn_apply);
            return footer;
        }

        private Panel BuildProfileSidebar() {
            Panel sidebar = new Panel {
                Dock = DockStyle.Left,
                Width = 178,
                BackColor = ProfileSidebar,
            };
            sidebar.Paint += (sender, e) => {
                using (Pen pen = new Pen(ProfileBorder))
                    e.Graphics.DrawLine(pen, sidebar.Width - 1, 0, sidebar.Width - 1, sidebar.Height);
            };
            sidebar.Controls.Add(CreateLabel("PROFILE SETTINGS", 20, 25,
                Color.FromArgb(151, 174, 205), false, 8F));

            profileNavigationAccent = new Panel {
                BackColor = ProfileAccent,
                Location = new Point(10, 60),
                Size = new Size(3, 40),
            };
            sidebar.Controls.Add(profileNavigationAccent);
            sidebar.Controls.Add(CreateNavigationButton("Bindings", "bindings", 60));
            sidebar.Controls.Add(CreateNavigationButton("Gyro", "gyro", 104));
            sidebar.Controls.Add(CreateNavigationButton("Virtual controller", "virtual", 148));
            return sidebar;
        }

        private Button CreateNavigationButton(string text, string key, int top) {
            Button button = new Button {
                Text = text,
                Tag = key,
                Location = new Point(13, top),
                Size = new Size(153, 40),
                FlatStyle = FlatStyle.Flat,
                FlatAppearance = { BorderSize = 0, MouseOverBackColor = ProfileSurfaceHover },
                BackColor = ProfileSidebar,
                ForeColor = ProfileText,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(14, 0, 0, 0),
                Cursor = Cursors.Hand,
                Font = new Font(Font, FontStyle.Regular),
            };
            button.Click += (sender, e) => ShowProfilePage(key);
            profileNavigationButtons.Add(key, button);
            return button;
        }

        private void ShowProfilePage(string key) {
            foreach (KeyValuePair<string, Panel> page in profilePages)
                page.Value.Visible = page.Key == key;
            foreach (KeyValuePair<string, Button> navigation in profileNavigationButtons) {
                bool selected = navigation.Key == key;
                navigation.Value.BackColor = selected ? Color.FromArgb(58, 52, 34) : ProfileSidebar;
                if (navigation.Value.Font.Bold != selected) {
                    Font oldFont = navigation.Value.Font;
                    navigation.Value.Font = new Font(oldFont,
                        selected ? FontStyle.Bold : FontStyle.Regular);
                    oldFont.Dispose();
                }
                if (selected && profileNavigationAccent != null)
                    profileNavigationAccent.Top = navigation.Value.Top;
            }
            if (profilePages.TryGetValue(key, out Panel selectedPage))
                selectedPage.BringToFront();
        }

        private Panel CreateProfilePage(string title, string description) {
            Panel page = new Panel {
                Dock = DockStyle.Fill,
                BackColor = ProfileBackground,
                AutoScroll = true,
                Visible = false,
            };
            page.Controls.Add(CreateLabel(title, 24, 17, ProfileText, true, 15F));
            Label detail = CreateLabel(description, 24, 48, ProfileMuted, false, 9.25F);
            detail.AutoSize = false;
            detail.Size = new Size(570, 22);
            page.Controls.Add(detail);
            page.Controls.Add(CreateDivider(24, 78));
            return page;
        }

        private Panel BuildBindingsPage() {
            Panel page = CreateProfilePage("Bindings",
                "Choose the controller inputs used for system actions and Joy-Con rail buttons.");
            AddSectionHeading(page, "System controls", 96,
                "Common actions available from this controller profile.");
            AddMappingRow(page, lbl_capture, btn_capture, "Capture", 157, 24, 150, 430);
            AddMappingRow(page, lbl_home, btn_home, "Home / Guide", 196, 24, 150, 430);
            AddMappingRow(page, lbl_shake, btn_shake, "Shake input", 235, 24, 150, 430);

            page.Controls.Add(CreateDivider(24, 284));
            AddSectionHeading(page, "Joy-Con rail buttons", 301,
                "Independent mappings for the SL and SR buttons on each Joy-Con.");
            AddMappingRow(page, lbl_sl_l, btn_sl_l, "Left Joy-Con · SL", 362, 24, 145, 140);
            AddMappingRow(page, lbl_sl_r, btn_sl_r, "Right Joy-Con · SL", 362, 315, 440, 154);
            AddMappingRow(page, lbl_sr_l, btn_sr_l, "Left Joy-Con · SR", 403, 24, 145, 140);
            AddMappingRow(page, lbl_sr_r, btn_sr_r, "Right Joy-Con · SR", 403, 315, 440, 154);
            return page;
        }

        private Panel BuildGyroPage() {
            Panel page = CreateProfilePage("Gyro",
                "Choose where motion is sent and how each output activates.");
            AddSectionHeading(page, "Output activation", 86,
                "Set each output to always on, disabled, or activate it with a binding.");
            page.Controls.Add(CreateLabel("Output", 24, 137, ProfileMuted, false, 8.25F));
            page.Controls.Add(CreateLabel("Activation", 232, 137, ProfileMuted, false, 8.25F));
            AddMappingRow(page, lbl_activate_gyro, btn_active_gyro, "Mouse", 158, 24, 232, 362);
            AddMappingRow(page, null, gyroStickActivationButtons[0], "Left stick", 195, 24, 232, 362);
            AddMappingRow(page, null, gyroStickActivationButtons[1], "Right stick", 232, 24, 232, 362);

            page.Controls.Add(CreateDivider(24, 274));
            AddSectionHeading(page, "Orientation", 289,
                "Reset the current controller angle while gyro mouse is active.");
            AddMappingRow(page, lbl_reset_mouse, btn_reset_mouse, "Re-center gyro", 345, 24, 138, 456);

            page.Controls.Add(CreateDivider(24, 389));
            AddSectionHeading(page, "Mouse actions", 404,
                "Optional controller inputs available while gyro mouse is active.");
            string[] labels = { "Left click", "Right click", "Middle click", "Clench gyro", "Scroll up", "Scroll down" };
            for (int index = 0; index < gyroMouseButtons.Count; index++) {
                int column = index % 2;
                int row = index / 2;
                int labelX = column == 0 ? 24 : 323;
                int buttonX = column == 0 ? 114 : 423;
                AddMappingRow(page, null, gyroMouseButtons[index], labels[index],
                    456 + row * 34, labelX, buttonX, column == 0 ? 181 : 171);
            }
            page.AutoScrollMinSize = new Size(0, 555);
            return page;
        }

        private Panel BuildVirtualControllerPage() {
            Panel page = CreateProfilePage("Virtual controller",
                "Inspect and test the virtual gamepad connected to this profile.");
            AddSectionHeading(page, "Output device", 96,
                "Windows exposes connected profiles as virtual game controllers.");

            Panel deviceCard = new Panel {
                Location = new Point(24, 157),
                Size = new Size(570, 142),
                BackColor = Color.FromArgb(38, 39, 41),
            };
            deviceCard.Paint += (sender, e) => ControlPaint.DrawBorder(
                e.Graphics, deviceCard.ClientRectangle, ProfileBorder, ButtonBorderStyle.Solid);
            Label icon = CreateLabel("◎", 19, 22, ProfileAccent, true, 21F);
            virtualControllerNameLabel = CreateLabel("Virtual controller", 67, 22, ProfileText, true, 11F);
            virtualControllerDetailLabel = CreateLabel("Select a controller profile to view its output.",
                67, 51, ProfileMuted, false, 9F);
            virtualControllerDetailLabel.AutoSize = false;
            virtualControllerDetailLabel.Size = new Size(470, 42);
            StyleStandardButton(gameControllersButton, false);
            gameControllersButton.Location = new Point(67, 95);
            gameControllersButton.Size = new Size(210, 32);
            gameControllersButton.Text = "Open Game Controllers...";
            deviceCard.Controls.Add(icon);
            deviceCard.Controls.Add(virtualControllerNameLabel);
            deviceCard.Controls.Add(virtualControllerDetailLabel);
            deviceCard.Controls.Add(gameControllersButton);
            page.Controls.Add(deviceCard);

            page.Controls.Add(CreateDivider(24, 328));
            AddSectionHeading(page, "About testing", 345,
                "Connected profiles open the matching virtual controller's Properties dialog. " +
                "Disconnected profiles open the standard Windows Game Controllers list.");
            return page;
        }

        private void AddSectionHeading(Panel page, string title, int top, string description) {
            page.Controls.Add(CreateLabel(title, 24, top, ProfileText, true, 12F));
            Label help = CreateLabel(description, 24, top + 27, ProfileMuted, false, 9F);
            help.AutoSize = false;
            // A transparent WinForms Label still paints its full rectangular bounds. Keeping a
            // one-line helper at the old two-line height let that invisible lower half sit on
            // top of the selector below it, visibly shaving several pixels off the control.
            // Reserve the taller box only for copy that can actually wrap to a second line.
            help.Size = new Size(570, description.Length > 100 ? 40 : 22);
            page.Controls.Add(help);
        }

        private void AddMappingRow(Panel page, Label label, SplitButton button, string text,
                                   int top, int labelX, int buttonX, int buttonWidth) {
            if (label == null)
                label = new Label();
            label.Text = text;
            label.AutoSize = false;
            label.Location = new Point(labelX, top + 7);
            label.Size = new Size(Math.Max(80, buttonX - labelX - 12), 22);
            label.ForeColor = ProfileText;
            label.BackColor = Color.Transparent;
            label.TextAlign = ContentAlignment.MiddleLeft;
            page.Controls.Add(label);

            StyleMappingButton(button);
            button.Location = new Point(buttonX, top);
            button.Size = new Size(buttonWidth, 31);
            page.Controls.Add(button);
        }

        private Panel CreateDivider(int left, int top) {
            return new Panel {
                Location = new Point(left, top),
                Size = new Size(570, 1),
                BackColor = ProfileBorder,
            };
        }

        private Label CreateLabel(string text, int left, int top, Color color, bool bold,
                                  float size = 9F) {
            return new Label {
                AutoSize = true,
                Text = text,
                Location = new Point(left, top),
                ForeColor = color,
                BackColor = Color.Transparent,
                Font = new Font("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular),
            };
        }

        private void StyleMappingButton(SplitButton button) {
            button.UseVisualStyleBackColor = false;
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderColor = ProfileBorder;
            button.FlatAppearance.BorderSize = 1;
            button.FlatAppearance.MouseOverBackColor = ProfileSurfaceHover;
            button.BackColor = ProfileSurface;
            button.ForeColor = ProfileText;
            button.TextAlign = ContentAlignment.MiddleLeft;
            button.Padding = new Padding(8, 0, 0, 0);
            button.Font = new Font("Segoe UI", 9F);
            button.Cursor = Cursors.Hand;
        }

        private void StyleStandardButton(Button button, bool accent) {
            button.UseVisualStyleBackColor = false;
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderColor = accent ? ProfileAccent : ProfileBorder;
            button.FlatAppearance.BorderSize = 1;
            button.FlatAppearance.MouseOverBackColor = accent
                ? Color.FromArgb(255, 201, 65)
                : ProfileSurfaceHover;
            button.BackColor = accent ? ProfileAccent : ProfileSurface;
            button.ForeColor = accent ? Color.FromArgb(28, 28, 28) : ProfileText;
            button.Font = new Font("Segoe UI", 9F, accent ? FontStyle.Bold : FontStyle.Regular);
            button.Cursor = Cursors.Hand;
        }

        private void StyleAssignmentMenus() {
            foreach (ContextMenuStrip menu in new[] { menu_joy_buttons, menu_gyro_activation }) {
                menu.BackColor = ProfileSurface;
                menu.ForeColor = ProfileText;
                menu.ShowImageMargin = false;
                menu.Font = new Font("Segoe UI", 9F);
                foreach (ToolStripItem item in menu.Items) {
                    item.BackColor = ProfileSurface;
                    item.ForeColor = ProfileText;
                }
            }
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
                            IsConnected = true,
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
            List<ControllerProfileInfo> connectedChoices = serviceClient != null
                ? remoteProfiles.OrderByDescending(p => p.ConnectionSequence).ToList()
                : ControllerMappings.ConnectedProfiles(Program.mgr?.j);
            List<ControllerProfileInfo> choices =
                ControllerMappings.IncludeDisconnectedProfiles(connectedChoices);

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

            ControllerProfileInfo selected = SelectedProfile;
            bool hasController = selected != null && !String.IsNullOrEmpty(selected.ProfileId);
            foreach (SplitButton button in specialButtons) {
                button.Enabled = hasController;
                GetPrettyName(button);
            }
            btn_apply.Enabled = hasController;
            gameControllersButton.Enabled = hasController;
            UpdateProfilePresentation(selected);
        }

        private void UpdateProfilePresentation(ControllerProfileInfo selected) {
            if (selected == null) {
                SetProfileStatus("No profile", ProfileMuted);
                if (virtualControllerNameLabel != null)
                    virtualControllerNameLabel.Text = "No controller profile selected";
                if (virtualControllerDetailLabel != null)
                    virtualControllerDetailLabel.Text =
                        "Connect a controller or select a saved profile to inspect its output.";
                if (gameControllersButton != null)
                    gameControllersButton.Text = "Open Game Controllers...";
                return;
            }

            if (selected.IsConnected) {
                SetProfileStatus("Connected", ProfileConnected);
                if (virtualControllerNameLabel != null)
                    virtualControllerNameLabel.Text = ConfiguredVirtualControllerName();
                if (virtualControllerDetailLabel != null)
                    virtualControllerDetailLabel.Text =
                        "Connected to " + selected.DisplayName + ". Open Windows properties to test its inputs.";
                if (gameControllersButton != null)
                    gameControllersButton.Text = "Open controller properties...";
            } else {
                SetProfileStatus("Disconnected", ProfileMuted);
                if (virtualControllerNameLabel != null)
                    virtualControllerNameLabel.Text = "Virtual controller unavailable";
                if (virtualControllerDetailLabel != null)
                    virtualControllerDetailLabel.Text =
                        "This saved profile is offline. Its virtual controller will return when it reconnects.";
                if (gameControllersButton != null)
                    gameControllersButton.Text = "Open Game Controllers...";
            }
        }

        private void SetProfileStatus(string text, Color color) {
            if (profileStatusDot != null) {
                profileStatusDot.Text = "●";
                profileStatusDot.ForeColor = color;
            }
            if (profileStatusLabel != null) {
                profileStatusLabel.Text = text;
                profileStatusLabel.ForeColor = color;
            }
        }

        private static string ConfiguredVirtualControllerName() {
            bool showAsXbox;
            bool showAsDs4;
            Boolean.TryParse(ConfigurationManager.AppSettings["ShowAsXInput"], out showAsXbox);
            Boolean.TryParse(ConfigurationManager.AppSettings["ShowAsDS4"], out showAsDs4);
            if (showAsXbox)
                return "Xbox 360 virtual controller";
            if (showAsDs4)
                return "DualShock 4 virtual controller";
            return "Virtual controller output disabled";
        }

        private void GameControllersButton_Click(object sender, EventArgs e) {
            ControllerProfileInfo selected = SelectedProfile;
            if (selected == null)
                return;

            try {
                if (selected.IsConnected && OpenSelectedVirtualController(selected))
                    return;
                GameControllerControlPanel.OpenDefault();
            } catch (Exception ex) {
                MessageBox.Show(this,
                    "Windows could not open Game Controllers.\r\n\r\n" + ex.Message,
                    "Controller Profiles", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private bool OpenSelectedVirtualController(ControllerProfileInfo selected) {
            VirtualGameControllerType controllerType;
            int ordinal;

            if (serviceClient == null) {
                Joycon outputOwner = Program.mgr?.j.FirstOrDefault(jc =>
                    ControllerMappings.ProfileIdFor(jc) == selected.ProfileId &&
                    (jc.out_xbox != null || jc.out_ds4 != null));
                if (outputOwner == null)
                    return false;

                if (outputOwner.out_xbox != null) {
                    controllerType = VirtualGameControllerType.Xbox360;
                    ordinal = outputOwner.out_xbox.UserIndex;
                    if (ordinal < 0)
                        ordinal = LocalVirtualControllerOrdinal(selected.ProfileId, true);
                } else {
                    controllerType = VirtualGameControllerType.DualShock4;
                    ordinal = LocalVirtualControllerOrdinal(selected.ProfileId, false);
                }
            } else {
                bool hasXboxOutput;
                bool hasDs4Output;
                Boolean.TryParse(ConfigurationManager.AppSettings["ShowAsXInput"], out hasXboxOutput);
                Boolean.TryParse(ConfigurationManager.AppSettings["ShowAsDS4"], out hasDs4Output);
                if (!hasXboxOutput && !hasDs4Output)
                    return false;

                controllerType = hasXboxOutput
                    ? VirtualGameControllerType.Xbox360
                    : VirtualGameControllerType.DualShock4;
                ordinal = remoteProfiles
                    .OrderBy(profile => profile.ConnectionSequence)
                    .Select(profile => profile.ProfileId)
                    .ToList()
                    .FindIndex(profileId => profileId == selected.ProfileId);
            }

            return GameControllerControlPanel.OpenForVirtualController(
                controllerType, ordinal);
        }

        private static int LocalVirtualControllerOrdinal(string selectedProfileId,
                                                          bool xboxOutput) {
            if (Program.mgr == null)
                return -1;

            return Program.mgr.j
                .Where(jc => xboxOutput ? jc.out_xbox != null : jc.out_ds4 != null)
                .GroupBy(ControllerMappings.ProfileIdFor)
                .Select(group => group.OrderBy(jc => jc.virtualControllerSequence).First())
                .OrderBy(jc => jc.virtualControllerSequence)
                .Select(ControllerMappings.ProfileIdFor)
                .ToList()
                .FindIndex(profileId => profileId == selectedProfileId);
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

            // Gyro-mouse action buttons only ever check for a "joy_" binding at runtime (see
            // Joycon.SimulateGyroMouseButton/Scroll). A keyboard/mouse trigger would capture fine
            // here but then silently do nothing, so leave those actions uncaptured. Activation
            // mappings are not controller-only and may still use keyboard or mouse input.
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
            if (IsGyroActivationKey((string)c.Tag) && val == "always") {
                c.Text = "Always On";
                tip_reassign.SetToolTip(c,
                    "Always on.\r\n\r\nLeft-click to detect input.\r\n" +
                    "Middle-click to reset.\r\nRight-click for activation options.");
                return;
            }
            bool unassigned = val == "0";

            // A combo is "+"-joined parts (see Joycon.IsComboHeld) - a single-input bind is just
            // a one-part combo, so this handles both uniformly.
            bool disabledActivation = unassigned && IsGyroActivationKey((string)c.Tag);
            string description = disabledActivation
                ? "(disabled)"
                : (unassigned ? "(unassigned)" : String.Join("+", val.Split('+').Select(DescribeBindPart)));
            c.Text = disabledActivation
                ? "Disabled"
                : (unassigned ? ((c == btn_home) ? "Guide" : "") : description);

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
