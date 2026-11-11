using Tatehama.Shukkindai.Client.Core;

namespace Tatehama.Shukkindai.Client;

public sealed class MainForm : Form
{
    private static readonly TimeSpan SignInTimeout = TimeSpan.FromMinutes(5);

    private readonly SessionManager _sessionManager;
    private readonly Label _statusLabel = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
    private readonly Button _signInButton = new() { Text = "ログイン", AutoSize = true };
    private readonly Button _cancelButton = new() { Text = "キャンセル", AutoSize = true };
    private readonly Button _signOutButton = new() { Text = "ログアウト", AutoSize = true };
    private CancellationTokenSource? _signInCancellation;

    public MainForm(SessionManager sessionManager)
    {
        _sessionManager = sessionManager;

        Text = "出勤台クライアント";
        ClientSize = new Size(480, 140);
        MinimumSize = new Size(400, 180);
        StartPosition = FormStartPosition.CenterScreen;

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            Controls = { _signInButton, _cancelButton, _signOutButton },
        };
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(16),
            ColumnCount = 1,
            RowCount = 2,
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(_statusLabel, 0, 0);
        layout.Controls.Add(buttons, 0, 1);
        Controls.Add(layout);

        _signInButton.Click += OnSignInClick;
        _cancelButton.Click += (_, _) => _signInCancellation?.Cancel();
        _signOutButton.Click += OnSignOutClick;
        Load += OnFormLoad;
        FormClosed += OnFormClosed;
        _sessionManager.StatusChanged += OnStatusChanged;

        ApplyStatus(_sessionManager.Status);
    }

    private void OnStatusChanged(SessionStatus status)
    {
        // SessionManager のスレッド(タイマー・スレッドプール)から呼ばれる
        if (IsDisposed || !IsHandleCreated)
        {
            return;
        }

        try
        {
            BeginInvoke(() => ApplyStatus(status));
        }
        catch (Exception e) when (e is ObjectDisposedException or InvalidOperationException)
        {
            // フォームが閉じられた後
        }
    }

    private void ApplyStatus(SessionStatus status)
    {
        _statusLabel.Text = StatusText.Format(status, TimeZoneInfo.Local);
        var signingIn = status.State == SessionState.SigningIn;
        var browserWaiting = _signInCancellation is not null;
        _signInButton.Enabled = !signingIn && status.State != SessionState.SignedIn;
        _cancelButton.Enabled = browserWaiting;
        _signOutButton.Enabled = !signingIn && (status.State is SessionState.SignedIn or SessionState.Error);
    }

    private async void OnFormLoad(object? sender, EventArgs e)
    {
        try
        {
            await _sessionManager.Start(CancellationToken.None);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"起動時のログイン復元に失敗しました。{ex.Message}", "出勤台",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private async void OnSignInClick(object? sender, EventArgs e)
    {
        using var cancellation = new CancellationTokenSource(SignInTimeout);
        _signInCancellation = cancellation;
        try
        {
            ApplyStatus(_sessionManager.Status);
            await _sessionManager.SignIn(cancellation.Token);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"ログインに失敗しました。{ex.Message}", "出勤台",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            _signInCancellation = null;
            ApplyStatus(_sessionManager.Status);
        }
    }

    private async void OnSignOutClick(object? sender, EventArgs e)
    {
        _signOutButton.Enabled = false;
        try
        {
            await _sessionManager.SignOut(CancellationToken.None);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"ログアウトに失敗しました。{ex.Message}", "出勤台",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            ApplyStatus(_sessionManager.Status);
        }
    }

    private void OnFormClosed(object? sender, FormClosedEventArgs e)
    {
        _sessionManager.StatusChanged -= OnStatusChanged;
        _signInCancellation?.Cancel();
        _sessionManager.Dispose();
    }
}
