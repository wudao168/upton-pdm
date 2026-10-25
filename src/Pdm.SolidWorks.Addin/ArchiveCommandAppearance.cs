using System.Drawing;
using System.Windows.Forms;

namespace Upton.Pdm.SolidWorks;

internal static class ArchiveCommandAppearance
{
    internal static void Apply(Button button, Color? active = null)
    {
        var background = active ?? Color.FromArgb(246, 247, 249);
        var foreground = active.HasValue ? Color.White : Color.FromArgb(31, 49, 72);
        button.FlatStyle = FlatStyle.Flat;
        button.UseVisualStyleBackColor = false;
        button.FlatAppearance.BorderSize = 1;
        void Refresh()
        {
            button.BackColor = button.Enabled ? background : Color.FromArgb(242, 244, 247);
            button.ForeColor = button.Enabled ? foreground : Color.FromArgb(145, 155, 168);
            button.FlatAppearance.BorderColor = button.Enabled && active.HasValue ? background : Color.FromArgb(205, 210, 217);
            button.FlatAppearance.MouseOverBackColor = button.BackColor;
            button.FlatAppearance.MouseDownBackColor = button.BackColor;
        }
        button.EnabledChanged += (_, _) => Refresh();
        Refresh();
    }
}
