using System;
using System.Drawing;
using System.Windows.Forms;

namespace BetterJoyForCemu {
    // Lightweight modeless status dialog shown for the duration of StartCalibrate's multi-step
    // flow (gyro, then each connected stick in turn) - built in code rather than with a
    // Designer.cs since it's a handful of labels with no complex layout. Owned/driven entirely
    // by MainForm's calibration state machine (the Set*/Show* methods are just dumb setters);
    // this class has no calibration logic of its own.
    //
    // Stick phases (center/rotate) have no time limit at all - the user clicks Start when
    // they're ready to begin and Done whenever they're actually finished, however long that
    // takes. Gyro is the one exception (the real hold-still measurement): the user can't be
    // clicking anything on the PC while proving the controller is sitting untouched, so that
    // phase alone runs on a countdown instead - see ShowCountdown.
    public class CalibrationDialog : Form {
        private Label stepLabel;
        private Label instructionLabel;
        private Label countLabel;
        private Button actionButton;

        public event Action ButtonClicked;

        public CalibrationDialog() {
            Text = "Calibrating";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MinimizeBox = false;
            MaximizeBox = false;
            ControlBox = false; // closed only by MainForm once the flow actually finishes - see FinishCalibrationFlow
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(360, 170);
            TopMost = true;

            stepLabel = new Label {
                Font = new Font(Font, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleCenter,
                Location = new Point(10, 12),
                Size = new Size(340, 24),
            };
            instructionLabel = new Label {
                TextAlign = ContentAlignment.MiddleCenter,
                Location = new Point(10, 50),
                Size = new Size(340, 60),
            };
            countLabel = new Label {
                Font = new Font(Font.FontFamily, 20, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleCenter,
                Location = new Point(10, 112),
                Size = new Size(340, 44),
                Visible = false,
            };
            actionButton = new Button {
                Location = new Point(110, 118),
                Size = new Size(140, 32),
                Visible = false,
            };
            actionButton.Click += (s, e) => ButtonClicked?.Invoke();

            Controls.Add(stepLabel);
            Controls.Add(instructionLabel);
            Controls.Add(countLabel);
            Controls.Add(actionButton);
        }

        public void SetStep(int stepNumber, int totalSteps, string stepName) {
            stepLabel.Text = String.Format("Step {0}/{1}: {2}", stepNumber, totalSteps, stepName);
        }

        public void SetInstruction(string instruction) {
            instructionLabel.Text = instruction;
        }

        public void ShowStartPrompt() {
            countLabel.Visible = false;
            actionButton.Text = "Start";
            actionButton.Visible = true;
        }

        public void ShowDonePrompt() {
            countLabel.Visible = false;
            actionButton.Text = "Done, I'm Finished";
            actionButton.Visible = true;
        }

        public void HidePrompt() {
            actionButton.Visible = false;
        }

        public void ShowCountdown(int count) {
            actionButton.Visible = false;
            countLabel.Visible = true;
            countLabel.Text = count > 0 ? count.ToString() : "";
        }

        public void ShowResult(string text) {
            actionButton.Visible = false;
            countLabel.Visible = false;
            instructionLabel.Text = text;
        }
    }
}
