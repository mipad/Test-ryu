package org.ryujinx.android

import android.view.InputDevice
import android.view.KeyEvent
import android.view.MotionEvent
import org.ryujinx.android.viewmodels.QuickSettings
import kotlin.math.abs

class PhysicalControllerManager(val activity: MainActivity) {
    private var controllerId: Int = -1

    // Trigger-Entprellung (analog → digital)
    private var leftTriggerPressed = false
    private var rightTriggerPressed = false
    private val pressThreshold = 0.65f
    private val releaseThreshold = 0.45f

    fun onKeyEvent(event: KeyEvent): Boolean {
        // Make sure we are connected
        if (controllerId == -1) {
            controllerId = RyujinxNative.jnaInstance.inputConnectGamepad(0)
        }

        val id = getGamePadButtonInputId(event.keyCode)
        if (id != GamePadButtonInputId.None) {
            val isNotFallback = (event.flags and KeyEvent.FLAG_FALLBACK) == 0
            if (isNotFallback) {
                when (event.action) {
                    KeyEvent.ACTION_UP -> {
                        RyujinxNative.jnaInstance.inputSetButtonReleased(id.ordinal, controllerId)
                    }

                    KeyEvent.ACTION_DOWN -> {
                        RyujinxNative.jnaInstance.inputSetButtonPressed(id.ordinal, controllerId)
                    }
                }
                return true
            } else if (!isNotFallback) {
                return true
            }
        }

        return false
    }

    fun onMotionEvent(ev: MotionEvent) {
        if (ev.action != MotionEvent.ACTION_MOVE) return

        if (controllerId == -1) {
            controllerId = RyujinxNative.jnaInstance.inputConnectGamepad(0)
        }

        val device = ev.device
        val source = InputDevice.SOURCE_JOYSTICK

        fun hasAxis(axis: Int): Boolean =
            device?.getMotionRange(axis, source) != null

        fun axisValue(axis: Int): Float = ev.getAxisValue(axis)

        // --- Sticks (rechts bevorzugt RX/RY, Fallback Z/RZ) ---
        val rightXaxis = if (hasAxis(MotionEvent.AXIS_RX)) MotionEvent.AXIS_RX else MotionEvent.AXIS_Z
        val rightYaxis = if (hasAxis(MotionEvent.AXIS_RY)) MotionEvent.AXIS_RY else MotionEvent.AXIS_RZ

        val leftStickX = if (hasAxis(MotionEvent.AXIS_X)) axisValue(MotionEvent.AXIS_X) else 0f
        val leftStickY = if (hasAxis(MotionEvent.AXIS_Y)) axisValue(MotionEvent.AXIS_Y) else 0f
        val rightStickX = if (hasAxis(rightXaxis)) axisValue(rightXaxis) else 0f
        val rightStickY = if (hasAxis(rightYaxis)) axisValue(rightYaxis) else 0f

        RyujinxNative.jnaInstance.inputSetStickAxis(
            1,
            leftStickX,
            -leftStickY,
            controllerId
        )
        RyujinxNative.jnaInstance.inputSetStickAxis(
            2,
            rightStickX,
            -rightStickY,
            controllerId
        )

        // --- Trigger lesen (mit Fallbacks) ---
        // Bevorzugt: LTRIGGER/RTRIGGER, dann BRAKE/GAS.
        // Wenn der rechte Stick RX/RY nutzt (Standard bei Xbox), sind Z/RZ frei -> als weiterer Fallback verwenden.
        // Nutzt der Stick Z/RZ, werden diese NICHT für Trigger verwendet (um Konflikte zu vermeiden).
        val rightStickUsesZ = (rightXaxis == MotionEvent.AXIS_Z)
        val rightStickUsesRZ = (rightYaxis == MotionEvent.AXIS_RZ)

        val rawLT = when {
            hasAxis(MotionEvent.AXIS_LTRIGGER) -> axisValue(MotionEvent.AXIS_LTRIGGER)
            hasAxis(MotionEvent.AXIS_BRAKE) -> axisValue(MotionEvent.AXIS_BRAKE)
            !rightStickUsesZ && hasAxis(MotionEvent.AXIS_Z) -> axisValue(MotionEvent.AXIS_Z)
            else -> 0f
        }
        val rawRT = when {
            hasAxis(MotionEvent.AXIS_RTRIGGER) -> axisValue(MotionEvent.AXIS_RTRIGGER)
            hasAxis(MotionEvent.AXIS_GAS) -> axisValue(MotionEvent.AXIS_GAS)
            !rightStickUsesRZ && hasAxis(MotionEvent.AXIS_RZ) -> axisValue(MotionEvent.AXIS_RZ)
            else -> 0f
        }

        // Einige Pads liefern leichte Offsets – normalisieren
        val lt = if (abs(rawLT) < 0.02f) 0f else rawLT.coerceIn(0f, 1f)
        val rt = if (abs(rawRT) < 0.02f) 0f else rawRT.coerceIn(0f, 1f)

        // Analog → digital mit Hysterese
        if (!leftTriggerPressed && lt >= pressThreshold) {
            leftTriggerPressed = true
            RyujinxNative.jnaInstance.inputSetButtonPressed(GamePadButtonInputId.LeftTrigger.ordinal, controllerId)
        } else if (leftTriggerPressed && lt <= releaseThreshold) {
            leftTriggerPressed = false
            RyujinxNative.jnaInstance.inputSetButtonReleased(GamePadButtonInputId.LeftTrigger.ordinal, controllerId)
        }

        if (!rightTriggerPressed && rt >= pressThreshold) {
            rightTriggerPressed = true
            RyujinxNative.jnaInstance.inputSetButtonPressed(GamePadButtonInputId.RightTrigger.ordinal, controllerId)
        } else if (rightTriggerPressed && rt <= releaseThreshold) {
            rightTriggerPressed = false
            RyujinxNative.jnaInstance.inputSetButtonReleased(GamePadButtonInputId.RightTrigger.ordinal, controllerId)
        }

        // --- DPAD als HAT (wie gehabt) ---
        device?.apply {
            if (sources and InputDevice.SOURCE_DPAD != InputDevice.SOURCE_DPAD) {
                val dPadHor = ev.getAxisValue(MotionEvent.AXIS_HAT_X)
                val dPadVert = ev.getAxisValue(MotionEvent.AXIS_HAT_Y)

                if (dPadVert == 0.0f) {
                    RyujinxNative.jnaInstance.inputSetButtonReleased(
                        GamePadButtonInputId.DpadUp.ordinal,
                        controllerId
                    )
                    RyujinxNative.jnaInstance.inputSetButtonReleased(
                        GamePadButtonInputId.DpadDown.ordinal,
                        controllerId
                    )
                }
                if (dPadHor == 0.0f) {
                    RyujinxNative.jnaInstance.inputSetButtonReleased(
                        GamePadButtonInputId.DpadLeft.ordinal,
                        controllerId
                    )
                    RyujinxNative.jnaInstance.inputSetButtonReleased(
                        GamePadButtonInputId.DpadRight.ordinal,
                        controllerId
                    )
                }

                if (dPadVert < 0.0f) {
                    RyujinxNative.jnaInstance.inputSetButtonPressed(
                        GamePadButtonInputId.DpadUp.ordinal,
                        controllerId
                    )
                    RyujinxNative.jnaInstance.inputSetButtonReleased(
                        GamePadButtonInputId.DpadDown.ordinal,
                        controllerId
                    )
                }
                if (dPadHor < 0.0f) {
                    RyujinxNative.jnaInstance.inputSetButtonPressed(
                        GamePadButtonInputId.DpadLeft.ordinal,
                        controllerId
                    )
                    RyujinxNative.jnaInstance.inputSetButtonReleased(
                        GamePadButtonInputId.DpadRight.ordinal,
                        controllerId
                    )
                }

                if (dPadVert > 0.0f) {
                    RyujinxNative.jnaInstance.inputSetButtonReleased(
                        GamePadButtonInputId.DpadUp.ordinal,
                        controllerId
                    )
                    RyujinxNative.jnaInstance.inputSetButtonPressed(
                        GamePadButtonInputId.DpadDown.ordinal,
                        controllerId
                    )
                }
                if (dPadHor > 0.0f) {
                    RyujinxNative.jnaInstance.inputSetButtonReleased(
                        GamePadButtonInputId.DpadLeft.ordinal,
                        controllerId
                    )
                    RyujinxNative.jnaInstance.inputSetButtonPressed(
                        GamePadButtonInputId.DpadRight.ordinal,
                        controllerId
                    )
                }
            }
        }
    }

    fun connect(): Int {
        controllerId = RyujinxNative.jnaInstance.inputConnectGamepad(0)
        return controllerId
    }

    fun disconnect() {
        // Falls ein Trigger beim Disconnect "hing", sicherheitshalber releasen
        if (leftTriggerPressed) {
            leftTriggerPressed = false
            RyujinxNative.jnaInstance.inputSetButtonReleased(GamePadButtonInputId.LeftTrigger.ordinal, controllerId)
        }
        if (rightTriggerPressed) {
            rightTriggerPressed = false
            RyujinxNative.jnaInstance.inputSetButtonReleased(GamePadButtonInputId.RightTrigger.ordinal, controllerId)
        }
        controllerId = -1
    }

    private fun getGamePadButtonInputId(keycode: Int): GamePadButtonInputId {
        val quickSettings = QuickSettings(activity)
        return when (keycode) {
            // ABXY (Switch/Xbox Layout)
            KeyEvent.KEYCODE_BUTTON_A -> if (!quickSettings.useSwitchLayout) GamePadButtonInputId.A else GamePadButtonInputId.B
            KeyEvent.KEYCODE_BUTTON_B -> if (!quickSettings.useSwitchLayout) GamePadButtonInputId.B else GamePadButtonInputId.A
            KeyEvent.KEYCODE_BUTTON_X -> if (!quickSettings.useSwitchLayout) GamePadButtonInputId.X else GamePadButtonInputId.Y
            KeyEvent.KEYCODE_BUTTON_Y -> if (!quickSettings.useSwitchLayout) GamePadButtonInputId.Y else GamePadButtonInputId.X

            // Shoulder & Trigger (falls ein Pad sie doch als Keys sendet)
            KeyEvent.KEYCODE_BUTTON_L1 -> GamePadButtonInputId.LeftShoulder
            KeyEvent.KEYCODE_BUTTON_L2 -> GamePadButtonInputId.LeftTrigger
            KeyEvent.KEYCODE_BUTTON_R1 -> GamePadButtonInputId.RightShoulder
            KeyEvent.KEYCODE_BUTTON_R2 -> GamePadButtonInputId.RightTrigger

            // L3 / R3
            KeyEvent.KEYCODE_BUTTON_THUMBL -> GamePadButtonInputId.LeftStick
            KeyEvent.KEYCODE_BUTTON_THUMBR -> GamePadButtonInputId.RightStick
            KeyEvent.KEYCODE_BUTTON_11 -> GamePadButtonInputId.LeftStick
            KeyEvent.KEYCODE_BUTTON_12 -> GamePadButtonInputId.RightStick

            // D-Pad
            KeyEvent.KEYCODE_DPAD_UP -> GamePadButtonInputId.DpadUp
            KeyEvent.KEYCODE_DPAD_DOWN -> GamePadButtonInputId.DpadDown
            KeyEvent.KEYCODE_DPAD_LEFT -> GamePadButtonInputId.DpadLeft
            KeyEvent.KEYCODE_DPAD_RIGHT -> GamePadButtonInputId.DpadRight

            // Plus/Minus
            KeyEvent.KEYCODE_BUTTON_START -> GamePadButtonInputId.Plus
            KeyEvent.KEYCODE_BUTTON_SELECT -> GamePadButtonInputId.Minus

            else -> GamePadButtonInputId.None
        }
    }
}
