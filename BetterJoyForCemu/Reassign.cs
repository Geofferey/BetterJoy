using System;
using System.Collections.Generic;
using System.ComponentModel;
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
        // detect a press is to poll the already-connected Joycons' own current button state
        // ourselves. Only meaningful in local/hardware-owning mode - Program.mgr is null in
        // remote mode (the service owns the hardware), so this silently does nothing there and
        // the right-click list remains the only way to assign a controller button.
        private Timer joyPoll;
        private readonly Dictionary<Joycon, bool[]> joyPrevButtons = new Dictionary<Joycon, bool[]>();

        public Reassign() {
            InitializeComponent();

            foreach (int i in Enum.GetValues(typeof(Joycon.Button))) {
                ToolStripMenuItem temp = new ToolStripMenuItem(Enum.GetName(typeof(Joycon.Button), i));
                temp.Tag = i;
                menu_joy_buttons.Items.Add(temp);
            }

            menu_joy_buttons.ItemClicked += Menu_joy_buttons_ItemClicked;

            foreach (SplitButton c in new SplitButton[] { btn_capture, btn_home, btn_sl_l, btn_sl_r, btn_sr_l, btn_sr_r, btn_shake, btn_reset_mouse, btn_active_gyro }) {
                c.Tag = c.Name.Substring(4);
                GetPrettyName(c);

                tip_reassign.SetToolTip(c, "Left-click to detect input.\r\nMiddle-click to clear to default.\r\nRight-click to see more options.");
                c.MouseDown += Remap;
                c.Menu = menu_joy_buttons;
                c.TextAlign = System.Drawing.ContentAlignment.MiddleLeft;
            }
        }

        private void Menu_joy_buttons_ItemClicked(object sender, ToolStripItemClickedEventArgs e) {
            Control c = sender as Control;

            ToolStripItem clickedItem = e.ClickedItem;

            SplitButton caller = (SplitButton)c.Tag;
            Config.SetValue((string)caller.Tag, "joy_" + (clickedItem.Tag));
            GetPrettyName(caller);
        }

        private void Remap(object sender, MouseEventArgs e) {
            SplitButton c = sender as SplitButton;
            switch (e.Button) {
                case MouseButtons.Left:
                    c.Text = "...";
                    curAssignment = c;
                    break;
                case MouseButtons.Middle:
                    Config.SetValue((string)c.Tag, Config.GetDefaultValue((string)c.Tag));
                    GetPrettyName(c);
                    break;
                case MouseButtons.Right:
                    break;
            }
        }

        private void Reassign_Load(object sender, EventArgs e) {
            keyboard = WindowsInput.Capture.Global.KeyboardAsync();
            keyboard.KeyEvent += Keyboard_KeyEvent;
            mouse = WindowsInput.Capture.Global.MouseAsync();
            mouse.MouseEvent += Mouse_MouseEvent;

            joyPoll = new Timer { Interval = 30 };
            joyPoll.Tick += JoyPoll_Tick;
            joyPoll.Start();
        }

        // Polls every connected Joycon's current button state and edge-detects a rising press
        // ourselves (a freshly-seen Joycon's baseline is recorded without triggering, so a
        // button already held before this dialog opened - or before that controller connected -
        // never gets mistaken for a new press). Runs continuously, independent of curAssignment,
        // same as the keyboard/mouse hooks only actually acting on a press while curAssignment
        // is set.
        private void JoyPoll_Tick(object sender, EventArgs e) {
            if (Program.mgr == null)
                return;

            int buttonCount = Enum.GetValues(typeof(Joycon.Button)).Length;
            foreach (Joycon jc in Program.mgr.j) {
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

                    if (curAssignment != null && now && !wasDown) {
                        Config.SetValue((string)curAssignment.Tag, "joy_" + bi);
                        GetPrettyName(curAssignment);
                        curAssignment = null;
                    }
                }
            }
        }

        private void Mouse_MouseEvent(object sender, WindowsInput.Events.Sources.EventSourceEventArgs<WindowsInput.Events.Sources.MouseEvent> e) {
            if (curAssignment != null && e.Data.ButtonDown != null) {
                Config.SetValue((string)curAssignment.Tag, "mse_" + ((int)e.Data.ButtonDown.Button));
                AsyncPrettyName(curAssignment);
                curAssignment = null;
                e.Next_Hook_Enabled = false;
            }
        }

        private void Keyboard_KeyEvent(object sender, WindowsInput.Events.Sources.EventSourceEventArgs<WindowsInput.Events.Sources.KeyboardEvent> e) {
            if (curAssignment != null && e.Data.KeyDown != null) {
                Config.SetValue((string)curAssignment.Tag, "key_" + ((int)e.Data.KeyDown.Key));
                AsyncPrettyName(curAssignment);
                curAssignment = null;
                e.Next_Hook_Enabled = false;
            }
        }

        private void Reassign_FormClosing(object sender, FormClosingEventArgs e) {
            keyboard.Dispose();
            mouse.Dispose();
            joyPoll.Stop();
            joyPoll.Dispose();
        }

        private void AsyncPrettyName(Control c) {
            if (InvokeRequired) {
                this.Invoke(new Action<Control>(AsyncPrettyName), new object[] { c });
                return;
            }
            GetPrettyName(c);
        }

        private void GetPrettyName(Control c) {
            string val;
            switch (val = Config.Value((string)c.Tag)) {
                case "0":
                    if (c == btn_home)
                        c.Text = "Guide";
                    else
                        c.Text = "";
                    break;
                default:
                    Type t = val.StartsWith("joy_") ? typeof(Joycon.Button) : (val.StartsWith("key_") ? typeof(WindowsInput.Events.KeyCode) : typeof(WindowsInput.Events.ButtonCode));
                    c.Text = Enum.GetName(t, Int32.Parse(val.Substring(4)));
                    break;
            }
        }

        private void btn_apply_Click(object sender, EventArgs e) {
            Config.Save();
        }

        private void btn_close_Click(object sender, EventArgs e) {
            btn_apply_Click(sender, e);
            Close();
        }
    }
}
