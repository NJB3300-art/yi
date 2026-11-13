using System;
using System.Numerics;
using Windows.Gaming.Input;

namespace Facet3D
{
    /// <summary>Polls the first connected Xbox controller once per frame and tracks button edges.</summary>
    public sealed class GamepadInput
    {
        GamepadReading _cur, _prev;
        GamepadButtons _blocked;
        public bool Connected { get; private set; }

        public GamepadInput()
        {
            // Subscribing makes sure the Gamepads list is populated as controllers connect.
            Gamepad.GamepadAdded += (s, e) => { };
            Gamepad.GamepadRemoved += (s, e) => { };
        }

        public void Update()
        {
            _prev = _cur;
            var pads = Gamepad.Gamepads;
            if (pads.Count == 0)
            {
                Connected = false;
                _cur = new GamepadReading();
            }
            else
            {
                Connected = true;
                _cur = pads[0].GetCurrentReading();
            }
            // Unblock buttons once released.
            _blocked &= _cur.Buttons;
        }

        /// <summary>Ignore every button currently held until it is released (used after closing menus).</summary>
        public void Swallow() { _blocked = _cur.Buttons; _prev = _cur; }

        GamepadButtons Buttons { get { return _cur.Buttons & ~_blocked; } }

        public bool Down(GamepadButtons b) { return (Buttons & b) == b; }

        public bool Pressed(GamepadButtons b) { return (Buttons & b) == b && (_prev.Buttons & b) != b; }

        public bool Released(GamepadButtons b) { return (_cur.Buttons & b) != b && (_prev.Buttons & b) == b && (_blocked & b) == 0; }

        static Vector2 Dead(double x, double y)
        {
            var v = new Vector2((float)x, (float)y);
            float l = v.Length();
            const float dz = 0.18f;
            if (l < dz) return Vector2.Zero;
            float mag = Math.Min(1f, (l - dz) / (1f - dz));
            return v / l * mag;
        }

        public Vector2 LeftStick { get { return Dead(_cur.LeftThumbstickX, _cur.LeftThumbstickY); } }
        public Vector2 RightStick { get { return Dead(_cur.RightThumbstickX, _cur.RightThumbstickY); } }
        public float LeftTrigger { get { return _cur.LeftTrigger < 0.08 ? 0f : (float)_cur.LeftTrigger; } }
        public float RightTrigger { get { return _cur.RightTrigger < 0.08 ? 0f : (float)_cur.RightTrigger; } }

        public bool AnyActivity
        {
            get
            {
                return _cur.Buttons != GamepadButtons.None || LeftStick != Vector2.Zero || RightStick != Vector2.Zero
                    || LeftTrigger > 0 || RightTrigger > 0;
            }
        }
    }
}
