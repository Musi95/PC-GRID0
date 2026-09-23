using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace GRID0Setup;

public sealed class MainForm : Form
{
    // GRID0 ZeroTier network id. Joining is not enough on its own:
    // each member still needs authorizing on the network unless the
    // network is set to auto-authorize.
    private const string NetworkId = "8bd5124fd68185ec";

    // Official ZeroTier Windows installer. Update this when ZeroTier ships
    // a new release.
    // Pattern: https://download.zerotier.com/RELEASES/<version>/dist/ZeroTier%20One.msi
    private const string ZeroTierMsiUrl = "https://download.zerotier.com/RELEASES/1.16.2/dist/ZeroTier%20One.msi";

    private readonly StatusDot _dot;
    private readonly Label _statusLabel;
    private readonly Label _detailLabel;
    private readonly ProgressBar _progress;
    private readonly TextBox _logBox;
    private readonly Button _actionButton;

    private bool _running;
    private CancellationTokenSource? _monitorCts;

    public MainForm()
    {
        Text = "GRID0 Setup";
        ClientSize = new Size(480, 500);
        MinimumSize = new Size(440, 460);
        StartPosition = FormStartPosition.CenterScreen;

        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

        var banner = new PictureBox
        {
            Dock = DockStyle.Top,
            Height = 100,
            BackColor = Color.Black,
            SizeMode = PictureBoxSizeMode.Zoom,
            Image = LoadBanner(),
        };

        _dot = new StatusDot { Location = new Point(16, 118) };
        _statusLabel = new Label
        {
            Text = "Starting...",
            Font = new Font("Segoe UI", 13, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(44, 116),
        };
        _detailLabel = new Label
        {
            Text = "",
            AutoSize = true,
            MaximumSize = new Size(448, 60),
            Location = new Point(16, 148),
        };

        _progress = new ProgressBar
        {
            Location = new Point(16, 216),
            Size = new Size(448, 18),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            Style = ProgressBarStyle.Marquee,
            Visible = false,
        };

        var logLabel = new Label
        {
            Text = "Log:",
            AutoSize = true,
            Location = new Point(16, 244),
        };
        _logBox = new TextBox
        {
            Location = new Point(16, 266),
            Size = new Size(448, 168),
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
        };

        _actionButton = new Button
        {
            Text = "Retry",
            Size = new Size(92, 30),
            Location = new Point(372, 448),
            Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
            Enabled = false,
        };
        _actionButton.Click += async (_, _) => await RetryAsync();

        Controls.AddRange(new Control[] { banner, _dot, _statusLabel, _detailLabel, _progress, logLabel, _logBox, _actionButton });

        Shown += async (_, _) => await RunFlowAsync();
        FormClosing += (_, _) => _monitorCts?.Cancel();
    }

    // The GRID0 banner, embedded in the exe so the single-file build
    // needs nothing next to it.
    private static Image? LoadBanner()
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            var name = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("banner.png", StringComparison.OrdinalIgnoreCase));
            if (name is null) return null;
            using var s = asm.GetManifestResourceStream(name);
            if (s is null) return null;
            using var tmp = new Bitmap(s);
            return new Bitmap(tmp);
        }
        catch { return null; }
    }

    // A real painted dot instead of a text glyph, so it stays a crisp
    // circle at any DPI.
    private sealed class StatusDot : Control
    {
        private Color _color = Color.Gray;

        public StatusDot()
        {
            Size = new Size(20, 20);
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
        }

        public Color DotColor
        {
            get => _color;
            set { _color = value; Invalidate(); }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var brush = new SolidBrush(_color);
            e.Graphics.FillEllipse(brush, 2, 2, Width - 4, Height - 4);
        }
    }

    private async Task RetryAsync()
    {
        // Stop the background monitor, then run the full flow again. The
        // button is only enabled while no setup is running, so this never
        // races an in-flight RunFlowAsync.
        _monitorCts?.Cancel();
        await RunFlowAsync();
    }

    private async Task RunFlowAsync()
    {
        if (_running) return;
        _running = true;
        _actionButton.Enabled = false;
        _actionButton.Text = "Retry";
        _progress.Visible = true;
        _monitorCts?.Cancel();
        _monitorCts?.Dispose();
        _monitorCts = new CancellationTokenSource();
        var ct = _monitorCts.Token;

        try
        {
            SetStatus(Color.Gray, "Checking for ZeroTier...");
            if (!await IsZeroTierHealthyAsync())
            {
                if (FindCli() is not null)
                    Log("Found a ZeroTier install, but it is not responding. Repairing...");
                SetStatus(Color.Gray, "Installing ZeroTier...");
                var msi = await DownloadMsiAsync();
                Log("Running the ZeroTier installer silently...");
                var (icode, _, ierr) = await RunAsync("msiexec.exe", "/i \"" + msi + "\" /quiet /norestart", 300000);
                if (icode != 0)
                    throw new Exception("The ZeroTier installer exited with code " + icode + "." +
                        (string.IsNullOrWhiteSpace(ierr) ? "" : " " + ierr.Trim()));
                Log("Installer finished. Waiting for the ZeroTier service...");
                if (!await WaitForCliAsync())
                    throw new Exception("ZeroTier installed, but its service did not start.");
                Log("ZeroTier is running.");
            }
            else
            {
                Log("ZeroTier is already installed.");
            }

            if (!await WaitForNodeOnlineAsync())
                throw new Exception("ZeroTier did not come online. Turn your WiFi on and check your internet connection, then press Retry.");

            SetStatus(Color.Gray, "Joining the GRID0 network...");
            var (jcode, jout, jerr) = await CliAsync("join " + NetworkId);
            var jmsg = string.IsNullOrWhiteSpace(jout) ? jerr.Trim() : jout.Trim();
            if (jmsg.Length > 0) Log(jmsg);
            if (jcode != 0)
                throw new Exception("Could not join the network. " + jmsg);

            if (!await WaitForFirstConnectionAsync(ct))
            {
                SetStatus(Color.Orange, "Still not connected",
                    "Timed out waiting for the network. Press Retry to try again.");
                return;
            }

            // Connected. Keep watching in the background so a later
            // disconnect or network leave updates the status instead of
            // leaving a stale green dot behind.
            _ = MonitorNetworkAsync(ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            SetStatus(Color.Red, "Something went wrong", ex.Message);
            Log("ERROR: " + ex.Message);
        }
        finally
        {
            _progress.Visible = false;
            _actionButton.Enabled = true;
            _running = false;
        }
    }

    // Waits up to ~3 minutes for the first GRID0 connection. Returns true
    // once connected, false on timeout.
    private async Task<bool> WaitForFirstConnectionAsync(CancellationToken ct)
    {
        string? lastSeen = null;
        for (var i = 0; i < 60; i++)
        {
            var (cliOk, online, hasInternet, status, ips) = await GetStateAsync();

            if (!hasInternet)
            {
                // WiFi off, airplane mode, cable unplugged: ZeroTier's own
                // "online" flag and listnetworks lag behind here, so we
                // check reachability ourselves and say so plainly.
                if (lastSeen != "NOINET")
                {
                    lastSeen = "NOINET";
                    SetStatus(Color.Red, "No internet connection",
                        "Turn your WiFi on. The app will continue automatically.");
                    Log("No internet connection.");
                }
            }
            else if (!cliOk || !online)
            {
                // The node cannot reach ZeroTier's roots. listnetworks may
                // still report a cached "OK" here, so never trust it alone.
                if (lastSeen != "OFFLINE")
                {
                    lastSeen = "OFFLINE";
                    SetStatus(Color.Red, "ZeroTier is offline",
                        "The ZeroTier node cannot reach ZeroTier's servers. Check your connection.");
                    Log("ZeroTier node is offline.");
                }
            }
            else if (status is null)
            {
                if (lastSeen != "REJOIN")
                {
                    lastSeen = "REJOIN";
                    Log("Not on the GRID0 network, joining...");
                }
                SetStatus(Color.Gray, "Joining the GRID0 network...");
                await CliAsync("join " + NetworkId);
            }
            else
            {
                if (status != lastSeen)
                {
                    lastSeen = status;
                    Log("Network status: " + status);
                }
                UpdateNetworkStatus(status, ips);

                if (status == "OK")
                {
                    Log("Connected to the GRID0 network" + (ips.Length > 0 ? " (" + ips + ")" : "") + ".");
                    return true;
                }
            }

            await Task.Delay(3000, ct);
        }

        return false;
    }

    // Keeps watching after the first connection so a later disconnect,
    // network leave, or de-authorization updates the status instead of
    // leaving a stale green dot behind. Runs until cancelled.
    private async Task MonitorNetworkAsync(CancellationToken ct)
    {
        var lastSeen = "OK";
        var nullStreak = 0;
        try
        {
            while (true)
            {
                await Task.Delay(3000, ct);
                var (cliOk, online, hasInternet, status, ips) = await GetStateAsync();

                if (!hasInternet)
                {
                    nullStreak = 0;
                    if (lastSeen != "NOINET")
                    {
                        lastSeen = "NOINET";
                        SetStatus(Color.Red, "No internet connection",
                            "Turn your WiFi on. The app will reconnect automatically.");
                        Log("Lost connection: no internet.");
                    }
                }
                else if (!cliOk || !online)
                {
                    nullStreak = 0;
                    if (lastSeen != "OFFLINE")
                    {
                        lastSeen = "OFFLINE";
                        SetStatus(Color.Red, "ZeroTier is offline",
                            "The ZeroTier node cannot reach ZeroTier's servers. Check your connection.");
                        Log("Lost connection: ZeroTier node is offline.");
                    }
                }
                else if (status is null)
                {
                    // The network entry vanished: the user left GRID0 (or it
                    // was removed). Debounce a couple of polls so a transient
                    // blip does not flash the state, then show disconnected
                    // and offer Reconnect. No silent auto-rejoin here:
                    // fighting the user's own leave is hostile UX.
                    nullStreak++;
                    if (nullStreak >= 2 && lastSeen != "DISCONNECTED")
                    {
                        lastSeen = "DISCONNECTED";
                        SetStatus(Color.Gray, "Disconnected from GRID0",
                            "You left the GRID0 network. Press Reconnect to join again.");
                        _actionButton.Text = "Reconnect";
                        Log("Disconnected from the GRID0 network.");
                    }
                }
                else
                {
                    nullStreak = 0;
                    if (lastSeen == "DISCONNECTED") _actionButton.Text = "Retry";
                    if (status != lastSeen)
                    {
                        lastSeen = status;
                        Log("Network status: " + status);
                    }
                    UpdateNetworkStatus(status, ips);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            SetStatus(Color.Red, "Something went wrong", ex.Message);
            Log("ERROR: " + ex.Message);
        }
    }

    private void UpdateNetworkStatus(string status, string ips)
    {
        if (status == "OK")
            SetStatus(Color.Green, "Connected to GRID0",
                "Network " + NetworkId + (ips.Length > 0 ? "\nYour address: " + ips : ""));
        else if (status == "ACCESS_DENIED")
            SetStatus(Color.Orange, "Waiting for authorization",
                "You joined the network. An admin still needs to approve your device.");
        else
            SetStatus(Color.Gray, "Joining the GRID0 network...", "Status: " + status);
    }

    // Quick ground-truth check for internet access. ZeroTier's own
    // "online" flag and listnetworks both lag behind reality (a cached
    // "OK" can linger for a while after WiFi drops), so we verify
    // reachability ourselves instead of trusting them alone.
    private static async Task<bool> HasInternetAsync()
    {
        try
        {
            using var client = new TcpClient();
            var connected = client.ConnectAsync("1.1.1.1", 443);
            var winner = await Task.WhenAny(connected, Task.Delay(3000));
            if (winner != connected) return false;
            try { await connected; }
            catch { return false; }
            return client.Connected;
        }
        catch
        {
            return false;
        }
    }

    // Reads the node's online state, our own internet reachability, and
    // the network's membership state. listnetworks alone is not enough:
    // its "OK" is the cached membership/config state and stays "OK" even
    // when the node itself is offline.
    private static async Task<(bool CliOk, bool NodeOnline, bool HasInternet, string? NetStatus, string Addresses)> GetStateAsync()
    {
        try
        {
            var infoTask = CliAsync("-j info");
            var netsTask = CliAsync("-j listnetworks");
            var internetTask = HasInternetAsync();
            await Task.WhenAll(infoTask, netsTask, internetTask);

            var cliOk = false;
            var online = false;

            var (icode, iout, _) = infoTask.Result;
            if (icode == 0)
            {
                cliOk = true;
                try
                {
                    using var doc = JsonDocument.Parse(iout);
                    online = doc.RootElement.TryGetProperty("online", out var o) && o.GetBoolean();
                }
                catch (JsonException) { }
            }

            string? status = null;
            var ips = "";
            var (lcode, lout, _) = netsTask.Result;
            if (lcode == 0)
            {
                cliOk = true;
                try
                {
                    using var doc = JsonDocument.Parse(lout);
                    foreach (var net in doc.RootElement.EnumerateArray())
                    {
                        if (net.GetProperty("id").GetString() != NetworkId) continue;
                        status = net.GetProperty("status").GetString();
                        if (net.TryGetProperty("assignedAddresses", out var addrs))
                            ips = string.Join(", ", addrs.EnumerateArray()
                                .Select(a => a.GetString())
                                .Where(s => !string.IsNullOrEmpty(s)));
                        break;
                    }
                }
                catch (JsonException) { }
            }

            return (cliOk, online, internetTask.Result, status, ips);
        }
        catch
        {
            return (false, false, false, null, "");
        }
    }

    private static async Task<bool> WaitForCliAsync()
    {
        for (var i = 0; i < 30; i++)
        {
            if (FindCli() is not null)
            {
                var (code, _, _) = await CliAsync("info");
                if (code == 0) return true;
            }
            await Task.Delay(3000);
        }
        return false;
    }

    private async Task<bool> WaitForNodeOnlineAsync()
    {
        for (var i = 0; i < 20; i++)
        {
            var (cliOk, online, hasInternet, _, _) = await GetStateAsync();
            if (cliOk && online && hasInternet) return true;
            if (cliOk && !hasInternet)
                SetStatus(Color.Red, "No internet connection",
                    "Turn your WiFi on. The app will continue automatically.");
            else
                SetStatus(Color.Gray, "Waiting for the ZeroTier service...");
            await Task.Delay(3000);
        }
        return false;
    }

    private async Task<string> DownloadMsiAsync()
    {
        var dest = Path.Combine(Path.GetTempPath(), "ZeroTierOne.msi");
        Log("Downloading the ZeroTier installer...");
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        using var resp = await http.GetAsync(ZeroTierMsiUrl, HttpCompletionOption.ResponseHeadersRead);
        resp.EnsureSuccessStatusCode();
        await using var net = await resp.Content.ReadAsStreamAsync();
        await using var fs = File.Create(dest);
        var buffer = new byte[81920];
        int n;
        while ((n = await net.ReadAsync(buffer)) > 0)
            await fs.WriteAsync(buffer.AsMemory(0, n));
        Log("Download complete.");
        return dest;
    }

    // A leftover zerotier-cli.bat from a broken or partial install is not
    // enough: the CLI must actually answer. Otherwise a dead install would
    // make the wizard skip the install step entirely.
    private static async Task<bool> IsZeroTierHealthyAsync()
    {
        if (FindCli() is null) return false;
        try
        {
            var (code, _, _) = await CliAsync("info");
            return code == 0;
        }
        catch
        {
            return false;
        }
    }

    private static string? FindCli()
    {
        var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var candidate = Path.Combine(pf86, "ZeroTier", "One", "zerotier-cli.bat");
        if (File.Exists(candidate)) return candidate;

        var pf64 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        candidate = Path.Combine(pf64, "ZeroTier", "One", "zerotier-cli.bat");
        if (File.Exists(candidate)) return candidate;

        return null;
    }

    private static Task<(int ExitCode, string StdOut, string StdErr)> CliAsync(string args)
    {
        var cli = FindCli();
        if (cli is null) throw new InvalidOperationException("ZeroTier is not installed.");
        // Run the .bat through cmd with the whole command quoted.
        return RunAsync("cmd.exe", "/c \"\"" + cli + "\" " + args + "\"");
    }

    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunAsync(string fileName, string args, int timeoutMs = 30000)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = args,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi);
        if (p is null) throw new Exception("Could not start " + fileName + ".");
        var outTask = p.StandardOutput.ReadToEndAsync();
        var errTask = p.StandardError.ReadToEndAsync();
        var exited = await Task.Run(() => p.WaitForExit(timeoutMs));
        if (!exited)
        {
            try { p.Kill(); } catch { }
            return (-1, "", "timed out");
        }
        return (p.ExitCode, await outTask, await errTask);
    }

    private void SetStatus(Color color, string text, string detail = "")
    {
        if (InvokeRequired) { Invoke(() => SetStatus(color, text, detail)); return; }
        _dot.DotColor = color;
        _statusLabel.Text = text;
        _detailLabel.Text = detail;
    }

    private void Log(string message)
    {
        if (InvokeRequired) { Invoke(() => Log(message)); return; }
        _logBox.AppendText("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + message + Environment.NewLine);
    }
}
