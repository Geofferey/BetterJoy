using System.Collections.Generic;
using System.IO;

namespace BetterJoyForCemu {
    // Message types for the long-lived GUI<->service control pipe (see HeadlessJoyconHost's
    // server half and MainForm/ServiceControlClient's client half) - separate from
    // InputIpcProtocol's service<->input-helper pipe, which only ever carries keyboard/mouse
    // events. This one lets a GUI that has deferred hardware ownership to a running service
    // still show live controller status and trigger rumble test/join-split/calibration.
    public enum ControlMessageType : byte {
        // Service -> GUI
        ControllerSnapshot = 1,
        CalibrationStarted = 2,
        CalibrationComplete = 3,
        CalibrationFailed = 4,
        CalibrationStep = 5,

        // GUI -> service
        RequestSnapshot = 10,
        TestRumble = 11,
        JoinOrSplit = 12,
        StartCalibration = 13,
        CalibrationReady = 14, // user clicked "OK" on the current step - see CalibrationStep
    }

    public enum ControllerKind : byte {
        Left = 0,
        Right = 1,
        Pro = 2,
        Snes = 3,
        N64 = 4,
    }

    // One connected controller's display-relevant state - deliberately not a live Joycon
    // reference, since the GUI process may not have one at all when the service owns the
    // hardware. Battery -1 means unknown; OtherPadId -1 means unpaired.
    public struct ControllerRecord {
        public byte PadId;
        public ControllerKind Kind;
        public sbyte Battery;
        public sbyte OtherPadId;

        public void WriteTo(BinaryWriter writer) {
            writer.Write(PadId);
            writer.Write((byte)Kind);
            writer.Write(Battery);
            writer.Write(OtherPadId);
        }

        public static ControllerRecord ReadFrom(BinaryReader reader) {
            return new ControllerRecord {
                PadId = reader.ReadByte(),
                Kind = (ControllerKind)reader.ReadByte(),
                Battery = reader.ReadSByte(),
                OtherPadId = reader.ReadSByte(),
            };
        }
    }

    // Start/Done: no time limit at all - the GUI shows a button and the service just waits
    // (CalibrationReady) for the user to click it, however long that takes. Countdown is the one
    // exception (gyro's actual hold-still measurement): the user can't be clicking anything on
    // the PC while proving the controller is sitting untouched, so that phase runs on a fixed
    // server-side timer instead, and Count carries its remaining whole seconds purely for
    // display - the GUI ticks its own once-per-second cosmetic countdown from it rather than the
    // service pushing a message every second.
    public enum CalibStepUiMode : byte { Start, Done, Countdown }

    // One step/phase transition of a calibration run in progress - see HeadlessJoyconHost's
    // StartCalibration (the source of truth for step names/instruction text) and MainForm's
    // CalibrationDialog (the passive renderer of whatever arrives here).
    public struct CalibrationStepInfo {
        public int PadId;
        public int StepNumber;
        public int TotalSteps;
        public string StepName;
        public string Instruction;
        public CalibStepUiMode UiMode;
        public int Count; // only meaningful when UiMode == Countdown
    }

    public static class ServiceControlIpc {
        // Fixed and well-known (unlike the per-session input-helper pipe) - the GUI needs to
        // find this without being told a name, since it isn't the one that launched the service.
        public const string PipeName = "BetterJoyServiceControl";

        public static void WriteSnapshot(BinaryWriter writer, List<ControllerRecord> records) {
            writer.Write((byte)ControlMessageType.ControllerSnapshot);
            writer.Write((byte)records.Count);
            foreach (ControllerRecord record in records)
                record.WriteTo(writer);
            writer.Flush();
        }

        public static List<ControllerRecord> ReadSnapshot(BinaryReader reader) {
            byte count = reader.ReadByte();
            var records = new List<ControllerRecord>(count);
            for (int i = 0; i < count; i++)
                records.Add(ControllerRecord.ReadFrom(reader));
            return records;
        }

        public static void WritePadIdMessage(BinaryWriter writer, ControlMessageType type, int padId) {
            writer.Write((byte)type);
            writer.Write((byte)padId);
            writer.Flush();
        }

        public static void WriteSimple(BinaryWriter writer, ControlMessageType type) {
            writer.Write((byte)type);
            writer.Flush();
        }

        public static void WriteCalibrationStep(BinaryWriter writer, CalibrationStepInfo step) {
            writer.Write((byte)ControlMessageType.CalibrationStep);
            writer.Write((byte)step.PadId);
            writer.Write((byte)step.StepNumber);
            writer.Write((byte)step.TotalSteps);
            writer.Write(step.StepName);
            writer.Write(step.Instruction);
            writer.Write((byte)step.UiMode);
            writer.Write((byte)step.Count);
            writer.Flush();
        }

        public static CalibrationStepInfo ReadCalibrationStep(BinaryReader reader) {
            return new CalibrationStepInfo {
                PadId = reader.ReadByte(),
                StepNumber = reader.ReadByte(),
                TotalSteps = reader.ReadByte(),
                StepName = reader.ReadString(),
                Instruction = reader.ReadString(),
                UiMode = (CalibStepUiMode)reader.ReadByte(),
                Count = reader.ReadByte(),
            };
        }
    }
}
