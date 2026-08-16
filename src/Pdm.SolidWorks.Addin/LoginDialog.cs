using System;
using System.Drawing;
using System.Windows.Forms;

namespace Upton.Pdm.SolidWorks;

internal sealed class LoginDialog : Form
{
    private readonly TextBox username = new TextBox();
    private readonly TextBox password = new TextBox { UseSystemPasswordChar = true };
    private readonly CheckBox rememberCredentials = new CheckBox { Text = "记住用户名和密码", AutoSize = true, Checked = true };

    public LoginDialog()
    {
        Text = "登录 UPTON PDM";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(340, 212);
        Font = new Font("Microsoft YaHei UI", 9F);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(18),
            ColumnCount = 2,
            RowCount = 4
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(new Label { Text = "用户名", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        username.Dock = DockStyle.Fill;
        layout.Controls.Add(username, 1, 0);
        layout.Controls.Add(new Label { Text = "密码", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
        password.Dock = DockStyle.Fill;
        layout.Controls.Add(password, 1, 1);
        rememberCredentials.Anchor = AnchorStyles.Left;
        layout.Controls.Add(rememberCredentials, 1, 2);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        var login = new Button { Text = "登录", DialogResult = DialogResult.OK, Width = 82, Height = 30, BackColor = Color.FromArgb(47, 109, 224), ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
        login.FlatAppearance.BorderSize = 0;
        var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Width = 82, Height = 30 };
        buttons.Controls.Add(login);
        buttons.Controls.Add(cancel);
        layout.SetColumnSpan(buttons, 2);
        layout.Controls.Add(buttons, 0, 3);
        Controls.Add(layout);
        AcceptButton = login;
        CancelButton = cancel;

        if (RememberedCredentialsStore.TryLoad(out var rememberedUsername, out var rememberedPassword))
        {
            username.Text = rememberedUsername;
            password.Text = rememberedPassword;
            rememberCredentials.Checked = true;
        }
    }

    public string Username => username.Text.Trim();

    public string Password => password.Text;

    public bool RememberCredentials => rememberCredentials.Checked;
}
