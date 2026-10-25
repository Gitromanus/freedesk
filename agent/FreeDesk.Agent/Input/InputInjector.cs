using System.Runtime.InteropServices;
using System.Text.Json;

namespace FreeDesk.Agent.Input;

/// <summary>Ввод удалённой стороны через SendInput. Координаты приходят нормализованными (0..1).</summary>
public sealed class InputInjector
{
    private const uint InputMouse = 0;
    private const uint InputKeyboard = 1;

    private const uint MouseMove = 0x0001;
    private const uint MouseAbsolute = 0x8000;
    private const uint MouseLeftDown = 0x0002;
    private const uint MouseLeftUp = 0x0004;
    private const uint MouseRightDown = 0x0008;
    private const uint MouseRightUp = 0x0010;
    private const uint MouseMiddleDown = 0x0020;
    private const uint MouseMiddleUp = 0x0040;
    private const uint MouseWheel = 0x0800;
    private const uint KeyUp = 0x0002;

    private readonly int _width;
    private readonly int _height;

    public InputInjector()
    {
        _width = Math.Max(1, GetSystemMetrics(0));
        _height = Math.Max(1, GetSystemMetrics(1));
    }

    public void Handle(string json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return;
        }

        using (document)
        {
            var root = document.RootElement;
            if (!root.TryGetProperty("t", out var typeElement))
            {
                return;
            }

            switch (typeElement.GetString())
            {
                case "m":
                    Move(root.GetProperty("x").GetDouble(), root.GetProperty("y").GetDouble());
                    break;

                case "mb":
                    Button(root.GetProperty("b").GetString(), root.GetProperty("d").GetBoolean());
                    break;

                case "w":
                    Wheel(root.GetProperty("d").GetInt32());
                    break;

                case "k":
                    Key((ushort)root.GetProperty("vk").GetInt32(), root.GetProperty("d").GetBoolean());
                    break;
            }
        }
    }

    private void Move(double x, double y)
    {
        var input = new Input
        {
            Type = InputMouse,
            Union = new InputUnion
            {
                Mouse = new MouseInput
                {
                    Dx = (int)Math.Round(Math.Clamp(x, 0, 1) * 65535),
                    Dy = (int)Math.Round(Math.Clamp(y, 0, 1) * 65535),
                    Flags = MouseMove | MouseAbsolute,
                },
            },
        };

        Send(new[] { input });

        if (_movesLogged++ < 3)
        {
            GetCursorPos(out var position);
            Log.Info($"мышь → {position.X},{position.Y} (экран {_width}x{_height})");
        }
    }

    private int _movesLogged;

    private void Button(string? button, bool down)
    {
        var flag = button switch
        {
            "right" => down ? MouseRightDown : MouseRightUp,
            "middle" => down ? MouseMiddleDown : MouseMiddleUp,
            _ => down ? MouseLeftDown : MouseLeftUp,
        };

        var input = new Input
        {
            Type = InputMouse,
            Union = new InputUnion { Mouse = new MouseInput { Flags = flag } },
        };

        Send(new[] { input });
    }

    private void Wheel(int delta)
    {
        var input = new Input
        {
            Type = InputMouse,
            Union = new InputUnion
            {
                Mouse = new MouseInput { MouseData = (uint)delta, Flags = MouseWheel },
            },
        };

        Send(new[] { input });
    }

    private void Key(ushort virtualKey, bool down)
    {
        var input = new Input
        {
            Type = InputKeyboard,
            Union = new InputUnion
            {
                Keyboard = new KeyboardInput
                {
                    VirtualKey = virtualKey,
                    Flags = down ? 0 : KeyUp,
                },
            },
        };

        Send(new[] { input });
    }

    private static void Send(Input[] inputs)
    {
        if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>()) == 0)
        {
            Log.Warn("SendInput не применился");
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputUnion Union;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MouseInput Mouse;
        [FieldOffset(0)] public KeyboardInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, Input[] inputs, int size);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point position);

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }
}
