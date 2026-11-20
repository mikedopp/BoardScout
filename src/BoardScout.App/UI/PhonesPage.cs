using BoardScout.Services;

namespace BoardScout.UI;

/// <summary>
/// The Phones screen: what is inside each Android phone, tablet, or TV that adb can reach, as a card per device
/// with the same plates and meters as the header. BoardScout talks to a device only when Read phones is pressed
/// here, once per press, and only reads.
/// </summary>
internal sealed class PhonesPage : Panel
{
    private readonly CommandButton _read = new() { Kind = CommandKind.Primary, Glyph = CommandGlyph.Scan, Text = "&Read phones" };
    private readonly Label _intro = new();
    private readonly StatusLine _state = new();
    private readonly FlowLayoutPanel _cards = new();
    private readonly ToolTip _toolTip;
    private bool _reading;

    public PhonesPage(ToolTip toolTip)
    {
        _toolTip = toolTip;
        Dock = DockStyle.Fill;
        BackColor = AppTheme.Background;
        Tag = "background";

        _intro.Text = "What is inside each Android phone, tablet, or TV connected to this PC: chip, memory, storage, display, " +
                      "Android version, and battery. BoardScout asks a device only when you press Read phones, and only reads. " +
                      "No serial number, account, app list, or file is touched.";
        _intro.ForeColor = AppTheme.Muted;
        _intro.Tag = "muted";
        _intro.AutoSize = false;
        _state.Text = "Not read yet.";
        _state.ForeColor = AppTheme.Muted;
        _state.BackColor = AppTheme.Background;
        _state.Font = new Font("Segoe UI", 9f);
        _read.Click += async (_, _) => await ReadAsync();
        _toolTip.SetToolTip(_read, "Ask every connected and approved Android device what is inside it, once.");

        _cards.FlowDirection = FlowDirection.TopDown;
        _cards.WrapContents = false;
        _cards.AutoScroll = true;
        _cards.BackColor = AppTheme.Background;
        _cards.Tag = "background";
        AppTheme.UseDarkScrollbars(_cards);

        Controls.AddRange([_read, _intro, _state, _cards]);
        Resize += (_, _) => Arrange();
        Arrange();
    }

    private void Arrange()
    {
        var width = ClientSize.Width;
        if (width < 200) return;
        _read.Location = new Point(width - _read.Width - 4, 6);
        _intro.Bounds = new Rectangle(4, 4, Math.Max(100, _read.Left - 28), 38);
        _state.Bounds = new Rectangle(4, 48, width - 8, 22);
        _cards.Bounds = new Rectangle(0, 78, width, Math.Max(0, ClientSize.Height - 78));
        foreach (Control card in _cards.Controls) card.Width = Math.Max(300, _cards.ClientSize.Width - 6);
    }

    /// <summary>Reads every reachable device once and shows a card for each.</summary>
    public async Task ReadAsync()
    {
        if (_reading) return;
        _reading = true;
        _read.Enabled = false;
        _state.Tone = StatusTone.Busy;
        _state.Text = "Asking adb which devices are connected…";
        try
        {
            var result = await PhoneInventory.ReadAllAsync();
            Show(result);
        }
        catch (Exception ex)
        {
            _state.Tone = StatusTone.Bad;
            _state.Text = "The devices could not be read: " + ex.Message;
        }
        finally
        {
            _reading = false;
            _read.Enabled = true;
        }
    }

    public void Show(PhoneInventory.Result result)
    {
        _cards.SuspendLayout();
        foreach (Control old in _cards.Controls.Cast<Control>().ToList()) { _cards.Controls.Remove(old); old.Dispose(); }
        foreach (var phone in result.Phones) _cards.Controls.Add(new PhoneCard(phone, _toolTip));
        _cards.ResumeLayout();
        Arrange();

        var waiting = new List<string>();
        if (result.WaitingForApproval > 0)
            waiting.Add($"{result.WaitingForApproval} waiting for you to allow this PC on {(result.WaitingForApproval == 1 ? "its" : "their")} screen");
        if (result.Offline > 0) waiting.Add($"{result.Offline} known but not answering");
        var extra = waiting.Count > 0 ? " Also: " + string.Join("; ", waiting) + "." : "";

        if (result.Adb is null)
        {
            _state.Tone = StatusTone.Bad;
            _state.Text = "adb was not found. It comes with Google's Android platform-tools (developer.android.com/tools/releases/platform-tools); " +
                          "put its folder on PATH and press Read phones again.";
        }
        else if (result.Phones.Count == 0)
        {
            _state.Tone = StatusTone.Idle;
            _state.Text = "No device answered. On the phone turn on Developer options › USB debugging, plug it in, and allow this PC when it asks." + extra;
        }
        else
        {
            _state.Tone = StatusTone.Good;
            _state.Text = $"Read {result.Phones.Count} device{(result.Phones.Count == 1 ? "" : "s")} at {DateTime.Now:t}." + extra;
        }
    }

    /// <summary>One device: its name, a line about what it is, its plates, and its meters.</summary>
    private sealed class PhoneCard : GlassCard
    {
        private readonly HeaderLabel _title = new();
        private readonly HeaderLabel _about = new();
        private readonly SpecStrip _specs = new();
        private readonly List<GaugeTile> _tiles = [];

        public PhoneCard(PhoneFacts phone, ToolTip toolTip)
        {
            Height = 196;
            Margin = new Padding(0, 0, 0, 10);
            Padding = new Padding(0);

            var privacy = Privacy.Enabled;
            _title.Text = phone.Title(privacy);
            _title.Font = new Font("Segoe UI Semibold", 15f);
            _title.ForeColor = AppTheme.Text;
            var named = $"{phone.Maker} {phone.Model}".Trim();
            _about.Text = string.Join("  •  ", new[]
            {
                phone.Kind,
                named.Length > 0 && named != _title.Text ? named : null,
                phone.UptimeHours is { } hours ? hours >= 48 ? $"on for {hours / 24:0} days" : $"on for {hours:0} h" : null
            }.Where(part => part is not null));
            _about.Font = new Font("Segoe UI", 9.25f);
            _about.ForeColor = AppTheme.Muted;
            _specs.Set(PhoneInventory.Specs(phone));
            toolTip.SetToolTip(_specs, _specs.FullText);

            if (phone.HasBattery && phone.BatteryLevel is { } level)
            {
                var battery = new GaugeTile("Battery", GaugeKind.Disk) { ShowGraph = false };
                // A battery's lights run the other way from a drive's: full is the good end.
                battery.Invert = true;
                var notes = new[]
                {
                    phone.BatteryState,
                    phone.BatteryHealthPercent is { } health ? $"{health}% of new" : phone.BatteryCondition is { } condition ? $"condition {condition.ToLowerInvariant()}" : null,
                    phone.BatteryCycles is { } cycles ? $"{cycles} cycles" : null
                }.Where(part => part is not null);
                battery.Show("Battery", $"{level}", "%", level / 100f, string.Join(" · ", notes),
                    level <= 15 ? AppTheme.Warning : null);
                _tiles.Add(battery);
                if (phone.BatteryTempC is { } celsius)
                {
                    var temperature = new GaugeTile("Battery temperature", GaugeKind.Temperature) { ShowGraph = false };
                    temperature.Show("Battery temperature", $"{celsius:0}", "°C", (float)((celsius - 20) / 80),
                        celsius >= 45 ? "Hot for a battery" : celsius >= 38 ? "Warm" : "Normal", celsius >= 45 ? AppTheme.Warning : null);
                    _tiles.Add(temperature);
                }
            }
            if (phone.StorageBytes is { } size and > 0 && phone.StorageFreeBytes is { } free)
            {
                var used = Math.Clamp((size - free) / (double)size, 0, 1);
                var storage = new GaugeTile("Storage used", GaugeKind.Disk) { ShowGraph = false };
                storage.Show("Storage used", $"{used * 100:0}", "%", (float)used, $"{RigSpecs.Size(size - free)} of {RigSpecs.Size(size)}",
                    used >= 0.95 ? AppTheme.Critical : used >= 0.85 ? AppTheme.Warning : null);
                _tiles.Add(storage);
            }

            Controls.AddRange([_title, _about, _specs, .. _tiles]);
            Resize += (_, _) => Arrange();
            Arrange();
        }

        private void Arrange()
        {
            const int pad = 22;
            var inner = Width - pad * 2;
            if (inner < 100) return;
            _title.Bounds = new Rectangle(pad, 16, inner, 30);
            _about.Bounds = new Rectangle(pad, 46, inner, 20);
            _specs.Bounds = new Rectangle(pad, 72, inner, 30);
            const int gap = 10;
            var each = _tiles.Count == 0 ? 0 : Math.Min(330, (inner - gap * (_tiles.Count - 1)) / _tiles.Count);
            for (var i = 0; i < _tiles.Count; i++)
                _tiles[i].Bounds = new Rectangle(pad + i * (each + gap), 114, each, 64);
            Height = _tiles.Count == 0 ? 120 : 196;
        }
    }
}
