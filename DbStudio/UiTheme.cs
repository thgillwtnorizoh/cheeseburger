using System.Runtime.InteropServices;

namespace Cheeseburger.DbStudio;

internal static class UiTheme
{
    public static readonly Color Window = Color.FromArgb(18, 20, 23);
    public static readonly Color Panel = Color.FromArgb(24, 27, 31);
    public static readonly Color PanelAlt = Color.FromArgb(29, 33, 38);
    public static readonly Color Header = Color.FromArgb(36, 41, 47);
    public static readonly Color Border = Color.FromArgb(48, 54, 61);
    public static readonly Color Text = Color.FromArgb(230, 232, 235);
    public static readonly Color Muted = Color.FromArgb(154, 162, 170);
    public static readonly Color Accent = Color.FromArgb(29, 111, 190);
    public static readonly Color AccentHover = Color.FromArgb(38, 128, 215);
    public static readonly Color Selection = Color.FromArgb(20, 82, 140);

    public static Button MakeButton(string text, EventHandler click)
    {
        var button = new Button
        {
            Text = text,
            AutoSize = true,
            Height = 30,
            FlatStyle = FlatStyle.Flat,
            BackColor = Header,
            ForeColor = Text,
            Font = new Font("Segoe UI", 9F),
            Padding = new Padding(10, 0, 10, 0),
            Margin = new Padding(0, 0, 6, 0),
            Cursor = Cursors.Hand,
            TabStop = false,
        };
        button.FlatAppearance.BorderColor = Border;
        button.FlatAppearance.MouseOverBackColor = AccentHover;
        button.FlatAppearance.MouseDownBackColor = Accent;
        button.Click += click;
        return button;
    }

    public static void TryEnableDarkTitleBar(Form form)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            var enabled = 1;
            if (DwmSetWindowAttribute(form.Handle, 20, ref enabled, sizeof(int)) != 0)
                DwmSetWindowAttribute(form.Handle, 19, ref enabled, sizeof(int));
        }
        catch
        {
            // Cosmetic only. Never let title-bar theming stop the app.
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int attributeValue, int attributeSize);
}
