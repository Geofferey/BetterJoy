using System;
using System.Numerics;

namespace BetterJoyForCemu {
    // Gravity tracking and world-space gyro mapping adapted from Julian "Jibb" Smart's
    // MIT-licensed GamepadMotionHelpers reference implementation:
    // https://github.com/JibbSmart/GamepadMotionHelpers
    //
    // The important separation is that accelerometer fusion estimates which way is down, while
    // only calibrated gyro angular velocity creates pointer motion. Gravity defines which gyro
    // rotations are horizontal and vertical in the player's frame; it is never added to cursor
    // velocity. An imperfect gravity estimate can therefore change the mapping, but cannot
    // manufacture mouse movement while the gyro is stationary.
    internal sealed class GyroMousePlayerSpace {
        private const float DegreesToRadians = (float)(Math.PI / 180.0);
        // GamepadMotionHelpers gravity-correction defaults (version 10).
        private const float ShakinessMinThreshold = 0.01f;
        private const float ShakinessMaxThreshold = 0.4f;
        private const float StillCorrectionSpeed = 1.0f;
        private const float ShakyCorrectionSpeed = 0.1f;
        private const float GyroCorrectionFactor = 0.1f;
        private const float GyroCorrectionMinThreshold = 0.05f;
        private const float GyroCorrectionMaxThreshold = 0.25f;
        private const float MinimumCorrectionSpeed = 0.01f;
        private const float ShortSteadinessHalfTime = 0.25f;
        // GamepadMotionHelpers' default guard around the world-space pitch singularity. When the
        // controller's local pitch axis is nearly vertical, projecting it onto the horizontal
        // plane becomes ill-conditioned, so its contribution is faded instead of amplified.
        private const float WorldPitchSideReductionThreshold = 0.125f;
        // Accelerometer direction is a useful gravity reference at rest, but sustained rotation
        // around an off-centre IMU produces centripetal acceleration proportional to yaw squared.
        // That makes the apparent tilt point the same way for left and right turns, matching the
        // observed one-way mouse-Y crawl. Reduce accelerometer influence only when motion is both
        // sustained-yaw-speed and strongly yaw-dominant. Keep a non-zero correction floor: fully
        // removing the gravity anchor lets small gyro/reference-axis disagreement turn into a
        // large periodic Y oscillation over repeated rotations.
        private const float YawTrustReductionStartRate = 2.0f; // degrees/sec
        private const float YawTrustReductionFullRate = 10.0f; // degrees/sec
        private const float YawDominanceStart = 0.80f;
        private const float YawDominanceFull = 0.95f;
        private const float MinimumYawGravityTrust = 0.25f;
        private const float TrustReductionHalfTime = 0.05f;
        private const float TrustRecoveryHalfTime = 0.35f;
        // Residual leakage that points the same way for both yaw signs cannot be represented by
        // a conventional signed cross-axis coefficient. Learn pitch / |yaw| instead. The narrow
        // purity gate keeps ordinary diagonals and corner-to-corner gestures out of the learner;
        // the cap is a safety bound, not a controller-specific correction value.
        private const float EvenYawLeakPurityFull = 0.08f;
        private const float EvenYawLeakPurityZero = 0.20f;
        private const float MaximumEvenYawLeakRatio = 0.15f;
        private const float EvenYawLeakAttackHalfTime = 0.04f;
        private const float EvenYawLeakReleaseHalfTime = 0.50f;

        private Vector3 gravity;
        private Vector3 smoothedAccel;
        private float shakiness;
        private bool gravityInitialized;
        private float gravityCorrectionTrust = 1.0f;
        private float yawDominance;
        private float gravityErrorDegrees;
        private float evenYawLeakRatio;
        private float evenYawLeakCorrection;

        public float GravityCorrectionTrust => gravityCorrectionTrust;
        public float YawDominance => yawDominance;
        public float GravityErrorDegrees => gravityErrorDegrees;
        public float EvenYawLeakRatio => evenYawLeakRatio;
        public float EvenYawLeakCorrection => evenYawLeakCorrection;

        public void Reset() {
            gravity = Vector3.Zero;
            smoothedAccel = Vector3.Zero;
            shakiness = 0.0f;
            gravityInitialized = false;
            gravityCorrectionTrust = 1.0f;
            yawDominance = 0.0f;
            gravityErrorDegrees = 0.0f;
            evenYawLeakRatio = 0.0f;
            evenYawLeakCorrection = 0.0f;
        }

        public void Update(Vector3 gyroDegPerSec, Vector3 accel, float deltaTime) {
            float accelMagnitude = accel.Length();
            Vector3 rotationRadians = gyroDegPerSec * DegreesToRadians;
            float angularSpeed = rotationRadians.Length();

            // BetterJoy has already transformed and calibrated this sample into the controller's
            // active coordinate basis. Seed gravity from it immediately instead of spending the
            // reference implementation's first second easing a zero vector toward "down". This
            // removes the slow correction users could feel after attach or a Joy-Con layout
            // change; the guarded fusion below still handles subsequent accumulated error.
            if (!gravityInitialized && accelMagnitude > 0.0f) {
                gravity = -accel / accelMagnitude;
                smoothedAccel = accel;
                shakiness = 0.0f;
                gravityInitialized = true;
                gravityCorrectionTrust = 1.0f;
                yawDominance = 0.0f;
                gravityErrorDegrees = 0.0f;
                evenYawLeakCorrection = 0.0f;
                return;
            }

            // Gravity is world-fixed, so in controller-local coordinates it rotates opposite the
            // controller. Doing this from gyro preserves immediate response while accelerometer
            // correction below only reins in accumulated tilt error.
            gravity = RotateByInverseLocalMotion(gravity, rotationRadians, deltaTime);

            if (accelMagnitude <= 0.0f)
                return;

            smoothedAccel = RotateByInverseLocalMotion(smoothedAccel, rotationRadians, deltaTime);
            float smoothFactor = (float)Math.Pow(2.0, -deltaTime / ShortSteadinessHalfTime);
            shakiness *= smoothFactor;
            shakiness = Math.Max(shakiness, (accel - smoothedAccel).Length());
            smoothedAccel = Vector3.Lerp(accel, smoothedAccel, smoothFactor);

            // BetterJoy's accelerometer reports apparent gravity upward at rest, so the physical
            // down/gravity vector is its negation. Its calibrated nominal magnitude is 1 g.
            Vector3 targetGravity = -Vector3.Normalize(accel);
            Vector3 gravityError = targetGravity - gravity;
            float errorLength = gravityError.Length();
            Vector3 gravityDirection = gravity.LengthSquared() > 0.0f
                ? Vector3.Normalize(gravity)
                : targetGravity;
            float angularSpeedDegrees = gyroDegPerSec.Length();
            float yawSpeed = Math.Abs(Vector3.Dot(gravityDirection, gyroDegPerSec));
            yawDominance = angularSpeedDegrees > 1e-5f
                ? Clamp01(yawSpeed / angularSpeedDegrees)
                : 0.0f;

            // Both factors use magnitudes deliberately. A centripetal/rectification error is
            // even in yaw direction, so turning left and right must produce identical trust.
            float yawSpeedConfidence = SmoothStep(Clamp01(
                (yawSpeed - YawTrustReductionStartRate) /
                (YawTrustReductionFullRate - YawTrustReductionStartRate)));
            float yawDominanceConfidence = SmoothStep(Clamp01(
                (yawDominance - YawDominanceStart) /
                (YawDominanceFull - YawDominanceStart)));
            float targetTrust = 1.0f - yawSpeedConfidence * yawDominanceConfidence *
                                (1.0f - MinimumYawGravityTrust);
            float trustHalfTime = targetTrust < gravityCorrectionTrust
                ? TrustReductionHalfTime
                : TrustRecoveryHalfTime;
            float trustBlend = 1.0f - (float)Math.Pow(2.0, -deltaTime / trustHalfTime);
            gravityCorrectionTrust += (targetTrust - gravityCorrectionTrust) * trustBlend;

            float gravityDot = Clamp(Vector3.Dot(gravityDirection, targetGravity), -1.0f, 1.0f);
            gravityErrorDegrees = (float)(Math.Acos(gravityDot) / DegreesToRadians);
            if (errorLength <= 0.0f)
                return;

            float correctionSpeed = StillCorrectionSpeed +
                (ShakyCorrectionSpeed - StillCorrectionSpeed) *
                Clamp01((shakiness - ShakinessMinThreshold) /
                        (ShakinessMaxThreshold - ShakinessMinThreshold));

            float gyroCorrectionLimit = Math.Max(angularSpeed * GyroCorrectionFactor,
                                                  MinimumCorrectionSpeed);
            if (correctionSpeed > gyroCorrectionLimit) {
                float closeEnoughFactor = Clamp01((errorLength - GyroCorrectionMinThreshold) /
                                                  (GyroCorrectionMaxThreshold -
                                                   GyroCorrectionMinThreshold));
                correctionSpeed = gyroCorrectionLimit +
                    (correctionSpeed - gyroCorrectionLimit) * closeEnoughFactor;
            }

            correctionSpeed *= gravityCorrectionTrust;
            if (correctionSpeed <= 0.0f)
                return;

            Vector3 correction = gravityError / errorLength * correctionSpeed * deltaTime;
            gravity = correction.LengthSquared() < gravityError.LengthSquared()
                ? gravity + correction
                : targetGravity;
        }

        public void Map(Vector3 gyroDegPerSec, float deltaTime, out float yawRate,
                        out float pitchRate, out float rollRadians) {
            // Normalize here as JoyShockMapper does. Fusion deliberately lets the gravity vector
            // converge smoothly, so its length is not guaranteed to remain exactly one; using
            // the unnormalized vector would make cursor gain vary during that convergence.
            Vector3 gravityDirection = gravity.LengthSquared() > 0.0f
                ? Vector3.Normalize(gravity)
                : new Vector3(0.0f, -1.0f, 0.0f);

            // Match GamepadMotionHelpers::CalculateWorldSpaceGyro. Horizontal motion is rotation
            // around gravity. Vertical motion uses the controller's LOCAL pitch axis (+X)
            // projected onto the plane perpendicular to gravity. This projection is important:
            // choosing an arbitrary horizontal axis such as Z x gravity follows wrist roll past
            // the side-on pose and can redirect compound roll/yaw motion into one-way mouse Y.
            yawRate = Vector3.Dot(gravityDirection, gyroDegPerSec);

            float gravityAlongLocalPitch = gravityDirection.X;
            Vector3 worldPitchAxis = new Vector3(1.0f, 0.0f, 0.0f) -
                                     gravityDirection * gravityAlongLocalPitch;
            float pitchAxisLengthSquared = worldPitchAxis.LengthSquared();
            if (pitchAxisLengthSquared > 0.0f) {
                worldPitchAxis /= (float)Math.Sqrt(pitchAxisLengthSquared);

                float flatness = Math.Abs(gravityDirection.Y);
                float upness = Math.Abs(gravityDirection.Z);
                float sideReduction = Clamp01(
                    (Math.Max(flatness, upness) - WorldPitchSideReductionThreshold) /
                    WorldPitchSideReductionThreshold);
                pitchRate = sideReduction * Vector3.Dot(worldPitchAxis, gyroDegPerSec);
            } else {
                // Local pitch is exactly parallel to gravity, so world-relative pitch is
                // undefined. The side reduction above smoothly approaches this zero.
                pitchRate = 0.0f;
            }

            ApplyEvenYawLeakCorrection(ref pitchRate, yawRate, deltaTime);

            // Diagnostic only: zero while the canonical controller frame is flat.
            rollRadians = (float)Math.Atan2(gravityDirection.X, -gravityDirection.Y);
        }

        private void ApplyEvenYawLeakCorrection(ref float pitchRate, float yawRate,
                                                float deltaTime) {
            float absoluteYaw = Math.Abs(yawRate);
            float absolutePitch = Math.Abs(pitchRate);
            float yawSpeedConfidence = SmoothStep(Clamp01(
                (absoluteYaw - YawTrustReductionStartRate) /
                (YawTrustReductionFullRate - YawTrustReductionStartRate)));
            float yawDominanceConfidence = SmoothStep(Clamp01(
                (yawDominance - YawDominanceStart) /
                (YawDominanceFull - YawDominanceStart)));
            float pitchToYaw = absoluteYaw > 1e-5f
                ? absolutePitch / absoluteYaw
                : float.MaxValue;
            float purityConfidence = 1.0f - SmoothStep(Clamp01(
                (pitchToYaw - EvenYawLeakPurityFull) /
                (EvenYawLeakPurityZero - EvenYawLeakPurityFull)));
            float learningConfidence = yawSpeedConfidence * yawDominanceConfidence *
                                       purityConfidence;

            if (learningConfidence > 0.0f && absoluteYaw > 1e-5f) {
                // |yaw| is deliberate: a real even-order leak retains its pitch sign when the
                // user reverses yaw. A signed divisor was the reason the first adaptive attempt
                // learned mutually incompatible coefficients for left and right turns.
                float observedRatio = Clamp(pitchRate / absoluteYaw,
                                            -MaximumEvenYawLeakRatio,
                                            MaximumEvenYawLeakRatio);
                float attackBlend = 1.0f -
                    (float)Math.Pow(2.0, -deltaTime / EvenYawLeakAttackHalfTime);
                evenYawLeakRatio += (observedRatio - evenYawLeakRatio) *
                                    attackBlend * learningConfidence;
            } else {
                float releaseBlend = 1.0f -
                    (float)Math.Pow(2.0, -deltaTime / EvenYawLeakReleaseHalfTime);
                evenYawLeakRatio += (0.0f - evenYawLeakRatio) * releaseBlend;
            }

            evenYawLeakCorrection = evenYawLeakRatio * absoluteYaw * learningConfidence;
            pitchRate -= evenYawLeakCorrection;
        }

        private static Vector3 RotateByInverseLocalMotion(Vector3 value,
                                                           Vector3 rotationRadiansPerSecond,
                                                           float deltaTime) {
            float angularSpeed = rotationRadiansPerSecond.Length();
            if (angularSpeed <= 1e-8f || value.LengthSquared() <= 0.0f)
                return value;

            Vector3 axis = rotationRadiansPerSecond / angularSpeed;
            float angle = angularSpeed * deltaTime;
            float cos = (float)Math.Cos(angle);
            float sin = (float)Math.Sin(angle);
            return value * cos - Vector3.Cross(axis, value) * sin +
                   axis * Vector3.Dot(axis, value) * (1.0f - cos);
        }

        private static float Clamp01(float value) {
            return Math.Max(0.0f, Math.Min(1.0f, value));
        }

        private static float Clamp(float value, float minimum, float maximum) {
            return Math.Max(minimum, Math.Min(maximum, value));
        }

        private static float SmoothStep(float value) {
            return value * value * (3.0f - 2.0f * value);
        }
    }
}
