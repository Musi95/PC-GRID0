using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
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

    private readonly Label _dot;
    private readonly Label _statusLabel;
    private readonly Label _detailLabel;
    private readonly ProgressBar _progress;
    private readonly TextBox _logBox;
    private readonly Button _actionButton;

    private bool _running;

    public MainForm()
    {
        Text = "GRID0 Setup";
        ClientSize = new Size(460, 372);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;

        var title = new Label
        {
            Text = "GRID0",
            Font = new Font("Segoe UI", 22, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(16, 10),
        };
        var subtitle = new Label
        {
            Text = "Installs ZeroTier and joins the GRID0 network automatically.",
            AutoSize = true,
            Location = new Point(18, 50),
        };

        // Status indicator: colored dot + status text.
        _dot = new Label
        {
            Text = "\u25CF",
            Font = new Font("Segoe UI", 26, FontStyle.Regular),
            AutoSize = true,
            Location = new Point(14, 74),
            ForeColor = Color.Gray,
        };
        _statusLabel = new Label
        {
            Text = "Starting...",
            Font = new Font("Segoe UI", 13, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(52, 84),
        };
        _detailLabel = new Label
        {
            Text = "",
            AutoSize = true,
            MaximumSize = new Size(424, 40),
            Location = new Point(18, 122),
        };

        _progress = new ProgressBar
        {
            Location = new Point(18, 168),
            Size = new Size(424, 16),
            Style = ProgressBarStyle.Marquee,
            Visible = false,
        };

        _logBox = new TextBox
        {
            Location = new Point(18, 192),
            Size = new Size(424, 124),
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
        };

        _actionButton = new Button
        {
            Text = "Retry",
            Location = new Point(342, 326),
            Size = new Size(100, 32),
            Enabled = false,
        };
        _actionButton.Click += async (_, _) => await RunFlowAsync();

        Controls.AddRange(new Control[] { title, subtitle, _dot, _statusLabel, _detailLabel, _progress, _logBox, _actionButton });

        Shown += async (_, _) => await RunFlowAsync();
    }

    private async Task RunFlowAsync()
    {
        if (_running) return;
        _running = true;
        _actionButton.Enabled = false;
        _progress.Visible = true;

        try
        {
            SetStatus(Color.Gray, "Checking for ZeroTier...");
            if (FindCli() is null)
            {
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
                throw new Exception("ZeroTier is installed but the node is offline. Check your internet connection.");

            SetStatus(Color.Gray, "Joining the GRID0 network...");
            var (jcode, jout, jerr) = await CliAsync("join " + NetworkId);
            var jmsg = string.IsNullOrWhiteSpace(jout) ? jerr.Trim() : jout.Trim();
            if (jmsg.Length > 0) Log(jmsg);
            if (jcode != 0)
                throw new Exception("Could not join the network. " + jmsg);

            await PollNetworkAsync();
        }
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

    private async Task PollNetworkAsync()
    {
        string? lastSeen = null;
        for (var i = 0; i < 60; i++)
        {
            var (code, stdout, _) = await CliAsync("-j listnetworks");
            if (code == 0)
            {
                try
                {
                    using var doc = JsonDocument.Parse(stdout);
                    foreach (var net in doc.RootElement.EnumerateArray())
                    {
                        if (net.GetProperty("id").GetString() != NetworkId) continue;

                        var status = net.GetProperty("status").GetString() ?? "UNKNOWN";
                        if (status != lastSeen)
                        {
                            Log("Network status: " + status);
                            lastSeen = status;
                        }

                        if (status == "OK")
                        {
                            var ips = "";
                            if (net.TryGetProperty("assignedAddresses", out var addrs))
                                ips = string.Join(", ", addrs.EnumerateArray()
                                    .Select(a => a.GetString())
                                    .Where(s => !string.IsNullOrEmpty(s)));
                            SetStatus(Color.Green, "Connected to GRID0",
                                "Network " + NetworkId + (ips.Length > 0 ? "\nYour address: " + ips : ""));
                            Log("Connected to the GRID0 network" + (ips.Length > 0 ? " (" + ips + ")" : "") + ".");
                            return;
                        }

                        if (status == "ACCESS_DENIED")
                            SetStatus(Color.Orange, "Waiting for authorization",
                                "You joined the network. An admin still needs to approve your device.");
                        else
                            SetStatus(Color.Gray, "Joining the GRID0 network...", "Status: " + status);

                        break;
                    }
                }
                catch (JsonException) { /* bad read, try again next poll */ }
            }
            await Task.Delay(3000);
        }

        SetStatus(Color.Orange, "Still not connected",
            "Timed out waiting for the network. Press Retry to try again.");
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

    private static async Task<bool> WaitForNodeOnlineAsync()
    {
        for (var i = 0; i < 20; i++)
        {
            var (code, stdout, _) = await CliAsync("-j info");
            if (code == 0)
            {
                try
                {
                    using var doc = JsonDocument.Parse(stdout);
                    if (doc.RootElement.TryGetProperty("online", out var online) && online.GetBoolean())
                        return true;
                }
                catch (JsonException) { }
            }
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
        _dot.ForeColor = color;
        _statusLabel.Text = text;
        _detailLabel.Text = detail;
    }

    private void Log(string message)
    {
        if (InvokeRequired) { Invoke(() => Log(message)); return; }
        _logBox.AppendText("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + message + Environment.NewLine);
    }
}
