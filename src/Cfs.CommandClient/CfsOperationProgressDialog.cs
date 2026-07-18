internal sealed class CfsOperationProgressDialog : Form
{
    private readonly Func<Task<CfsCommandClient.ClientResponse>> _start;
    private readonly Func<Task<CfsCommandClient.ClientResponse>> _poll;
    private readonly Func<Task<CfsCommandClient.ClientResponse>> _cancel;
    private readonly Label _phase = new() { AutoSize = true, Text = "Starting CFS operation…" };
    private readonly Label _item = new() { AutoEllipsis = true, Width = 440, Text = "Waiting for the broker" };
    private readonly ProgressBar _progress = new() { Width = 440, Style = ProgressBarStyle.Marquee, MarqueeAnimationSpeed = 25 };
    private readonly Button _cancelButton = new() { Text = "Cancel", AutoSize = true };
    private bool _closingAfterResult;

    public CfsOperationProgressDialog(
        string title,
        Func<Task<CfsCommandClient.ClientResponse>> start,
        Func<Task<CfsCommandClient.ClientResponse>> poll,
        Func<Task<CfsCommandClient.ClientResponse>> cancel)
    {
        _start = start;
        _poll = poll;
        _cancel = cancel;
        Text = title;
        Width = 500;
        Height = 190;
        MinimumSize = new Size(500, 190);
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, AutoSize = true };
        buttons.Controls.Add(_cancelButton);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), RowCount = 4, ColumnCount = 1 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(_phase, 0, 0);
        layout.Controls.Add(_item, 0, 1);
        layout.Controls.Add(_progress, 0, 2);
        layout.Controls.Add(buttons, 0, 3);
        Controls.Add(layout);

        Shown += async (_, _) => await RunOperationAsync();
        _cancelButton.Click += async (_, _) => await RequestCancellationAsync();
        FormClosing += (_, e) =>
        {
            if (!_closingAfterResult && Result is null)
            {
                e.Cancel = true;
                _ = RequestCancellationAsync();
            }
        };
    }

    public CfsCommandClient.ClientResponse? Result { get; private set; }

    private async Task RunOperationAsync()
    {
        try
        {
            var operation = _start();
            while (!operation.IsCompleted)
            {
                await Task.WhenAny(operation, Task.Delay(350));
                if (operation.IsCompleted) break;
                try { Apply(await _poll()); }
                catch { /* A transient poll failure does not cancel broker-owned work. */ }
            }
            Result = await operation;
            Apply(Result);
        }
        catch (Exception ex)
        {
            Result = new CfsCommandClient.ClientResponse(2, false, "CFS_E_BROKER_TIMEOUT", ex.Message);
        }
        finally
        {
            _closingAfterResult = true;
            Close();
        }
    }

    private async Task RequestCancellationAsync()
    {
        if (!_cancelButton.Enabled || Result is not null) return;
        _cancelButton.Enabled = false;
        _phase.Text = "Cancelling…";
        try
        {
            var response = await _cancel();
            if (!response.Success) _cancelButton.Enabled = true;
        }
        catch
        {
            _cancelButton.Enabled = true;
            _phase.Text = "Cancellation could not be delivered; operation is still running.";
        }
    }

    private void Apply(CfsCommandClient.ClientResponse response)
    {
        _phase.Text = response.OperationPhase ?? response.Message ?? response.OperationState ?? "Working…";
        _item.Text = response.CurrentItem ?? FormatCounts(response);
        _cancelButton.Enabled = response.CanCancel;
        if (response.Percent is double percent)
        {
            _progress.Style = ProgressBarStyle.Continuous;
            _progress.Value = Math.Clamp((int)Math.Round(percent), 0, 100);
        }
        else
        {
            _progress.Style = ProgressBarStyle.Marquee;
            _progress.MarqueeAnimationSpeed = 25;
        }
    }

    private static string FormatCounts(CfsCommandClient.ClientResponse response)
    {
        if (response.TotalBytes is long totalBytes)
            return $"{response.CompletedBytes:N0} of {totalBytes:N0} bytes";
        if (response.TotalItems is long totalItems)
            return $"{response.CompletedItems:N0} of {totalItems:N0} items";
        return "Calculating progress…";
    }
}
