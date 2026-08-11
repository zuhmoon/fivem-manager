using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;   // OpenFolderDialog

namespace FiveMManager;

public partial class MainWindow : Window
{
    Config _cfg = Core.Load();
    readonly ObservableCollection<AccountRow> _accounts = new();
    readonly ObservableCollection<Server> _servers = new();

    CancellationTokenSource? _joinCts;   // live boot-delay countdown, if any
    string? _pendingUri;

    public class AccountRow
    {
        public required Account Src { get; init; }
        public string Name => Src.Name;
        public string LastUsedText => string.IsNullOrEmpty(Src.LastUsed) ? "never used" : $"last used {Src.LastUsed}";
        public Visibility ActiveDot { get; init; }
    }

    public MainWindow()
    {
        InitializeComponent();
        AccountList.ItemsSource = _accounts;
        ServerList.ItemsSource = _servers;
        RefreshAll();
        Status("Ready.");
        _ = CheckUpdates(silent: true);
    }

    // ---- shell ----
    void Status(string msg, bool error = false)
    {
        StatusText.Text = msg;
        StatusText.Foreground = (Brush)FindResource(error ? "Danger" : "Muted");
    }

    // The snapshot/restore copies are the reason the old build froze: they ran on the UI thread.
    // Off-thread + a disabled shell + an indeterminate bar is the whole fix.
    async Task<bool> RunAsync(string busyMsg, string failPrefix, Action work)
    {
        Tabs.IsEnabled = false;
        Busy.Visibility = Visibility.Visible;
        Status(busyMsg);
        try { await Task.Run(work); return true; }
        catch (Exception ex) { Status(failPrefix + ex.Message, true); return false; }
        finally { Tabs.IsEnabled = true; Busy.Visibility = Visibility.Collapsed; }
    }

    void RefreshAll()
    {
        PathBox.Text = _cfg.FiveMAppPath;
        LinkedBox.Text = string.Join(Environment.NewLine, _cfg.LinkedPaths);
        DelayBox.Text = _cfg.BootDelaySeconds.ToString();
        VersionText.Text = $"Version {Core.CurrentVersion}";
        RefreshAccounts();
        _servers.Clear();
        foreach (var s in _cfg.Servers) _servers.Add(s);
    }

    void RefreshAccounts()
    {
        var keep = SelectedAccount?.Name;
        _accounts.Clear();
        foreach (var a in _cfg.Accounts)
            _accounts.Add(new AccountRow { Src = a, ActiveDot = a.Name == _cfg.ActiveAccount ? Visibility.Visible : Visibility.Collapsed });
        if (keep is not null) AccountList.SelectedItem = _accounts.FirstOrDefault(r => r.Name == keep);
        ActiveAccountText.Text = string.IsNullOrEmpty(_cfg.ActiveAccount) ? "no account linked" : "active: " + _cfg.ActiveAccount;
    }

    Account? SelectedAccount => (AccountList.SelectedItem as AccountRow)?.Src;
    static string Now() => DateTime.Now.ToString("yyyy-MM-dd HH:mm");

    bool GuardFiveMClosed()
    {
        if (!Core.IsFiveMRunning()) return true;
        Status("Close FiveM first — its account files are in use.", true);
        return false;
    }

    static bool Confirm(string text, string title) =>
        MessageBox.Show(text, title, MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;

    // ---- accounts ----
    async void AddAccount_Click(object sender, RoutedEventArgs e)
    {
        var name = NewAccName.Text.Trim();
        if (name.Length == 0) { Status("Type a name for the account first.", true); NewAccName.Focus(); return; }
        if (_cfg.Accounts.Any(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
        { Status($"An account named '{name}' already exists.", true); return; }
        if (!GuardFiveMClosed()) return;

        if (!await RunAsync($"Saving '{name}'…", "Snapshot failed: ", () => Core.SnapshotAccount(_cfg, name))) return;
        _cfg.Accounts.Add(new Account { Name = name, LastUsed = Now() });
        if (string.IsNullOrEmpty(_cfg.ActiveAccount)) _cfg.ActiveAccount = name;
        Core.Save(_cfg);
        NewAccName.Clear();
        RefreshAccounts();
        Status($"Saved '{name}'.");
    }

    async void Switch_Click(object sender, RoutedEventArgs e)
    {
        var a = SelectedAccount;
        if (a is null) { Status("Select an account first.", true); return; }
        if (a.Name == _cfg.ActiveAccount) { Status($"'{a.Name}' is already active."); return; }
        if (!GuardFiveMClosed()) return;

        var outgoing = _cfg.ActiveAccount;
        var ok = await RunAsync($"Switching to '{a.Name}'…", "Switch failed: ", () =>
        {
            // keep the outgoing account fresh, then load the selected one
            if (!string.IsNullOrEmpty(outgoing) && _cfg.Accounts.Any(x => x.Name == outgoing))
                Core.SnapshotAccount(_cfg, outgoing);
            Core.RestoreAccount(_cfg, a.Name);
        });
        if (!ok) return;

        _cfg.ActiveAccount = a.Name; a.LastUsed = Now();
        Core.Save(_cfg); RefreshAccounts();
        Status($"Now using '{a.Name}'.");
    }

    async void UpdateAccount_Click(object sender, RoutedEventArgs e)
    {
        var a = SelectedAccount;
        if (a is null) { Status("Select an account first.", true); return; }
        if (!GuardFiveMClosed()) return;

        if (!await RunAsync($"Updating '{a.Name}'…", "Update failed: ", () => Core.SnapshotAccount(_cfg, a.Name))) return;
        a.LastUsed = Now(); Core.Save(_cfg); RefreshAccounts();
        Status($"'{a.Name}' now matches your current login.");
    }

    async void DeleteAccount_Click(object sender, RoutedEventArgs e)
    {
        var a = SelectedAccount;
        if (a is null) { Status("Select an account first.", true); return; }
        if (!Confirm($"Delete the saved account '{a.Name}'?\n\nThis only removes the saved copy — your live FiveM login is untouched.", "Delete account")) return;

        if (!await RunAsync($"Deleting '{a.Name}'…", "Delete failed: ", () => Core.DeleteProfile(a.Name))) return;
        _cfg.Accounts.Remove(a);
        if (_cfg.ActiveAccount == a.Name) _cfg.ActiveAccount = "";
        Core.Save(_cfg); RefreshAccounts();
        Status($"Deleted '{a.Name}'.");
    }

    async void Unlink_Click(object sender, RoutedEventArgs e)
    {
        if (!Confirm("Delete the local Rockstar link files?\n\nFiveM will ask you to sign in again on next launch.", "Unlink Rockstar")) return;
        if (!GuardFiveMClosed()) return;

        if (!await RunAsync("Unlinking…", "Unlink failed: ", () => Core.Unlink(_cfg))) return;
        _cfg.ActiveAccount = ""; Core.Save(_cfg); RefreshAccounts();
        Status("Unlinked. Launch FiveM and sign in with the account you want linked.");
    }

    // ---- servers ----
    void ServerList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ServerList.SelectedItem is not Server s) return;
        SrvName.Text = s.Name; SrvLink.Text = s.Link; SrvPure.SelectedIndex = s.Pure; SrvBuild.Text = s.Build;
    }

    void AddServer_Click(object sender, RoutedEventArgs e)
    {
        var name = SrvName.Text.Trim(); var link = SrvLink.Text.Trim();
        if (name.Length == 0 || link.Length == 0) { Status("A server needs both a name and a join link.", true); return; }

        var s = new Server { Name = name, Link = link, Pure = SrvPure.SelectedIndex, Build = SrvBuild.Text.Trim() };
        _cfg.Servers.Add(s); Core.Save(_cfg); _servers.Add(s);
        SrvName.Clear(); SrvLink.Clear(); SrvPure.SelectedIndex = 0; SrvBuild.Clear();
        Status($"Added '{s.Name}'.");
    }

    void UpdateServer_Click(object sender, RoutedEventArgs e)
    {
        if (ServerList.SelectedItem is not Server s) { Status("Select a server first.", true); return; }
        var name = SrvName.Text.Trim(); var link = SrvLink.Text.Trim();
        if (name.Length == 0 || link.Length == 0) { Status("A server needs both a name and a join link.", true); return; }

        s.Name = name; s.Link = link; s.Pure = SrvPure.SelectedIndex; s.Build = SrvBuild.Text.Trim();
        Core.Save(_cfg);
        ServerList.Items.Refresh();   // Server has no INotifyPropertyChanged; one line beats four properties of boilerplate
        Status($"Updated '{s.Name}'.");
    }

    void DeleteServer_Click(object sender, RoutedEventArgs e)
    {
        if (ServerList.SelectedItem is not Server s) { Status("Select a server first.", true); return; }
        if (!Confirm($"Delete '{s.Name}' from the list?", "Delete server")) return;
        _cfg.Servers.Remove(s); _servers.Remove(s); Core.Save(_cfg);
        Status($"Deleted '{s.Name}'.");
    }

    async void Launch_Click(object sender, RoutedEventArgs e)
    {
        if (ServerList.SelectedItem is not Server s) { Status("Select a server first.", true); return; }
        if (s.Link.Trim().Length == 0) { Status($"'{s.Name}' has no join link.", true); return; }

        string? uri;
        try { uri = Core.Launch(s, _cfg.FiveMAppPath); }
        catch (Exception ex) { Status("Launch failed: " + ex.Message, true); return; }

        if (uri is null) { Status($"Handed '{s.Name}' to FiveM."); return; }
        await Countdown(uri, s.Name);   // build/pure path: FiveM opens first, join fires after it boots
    }

    async Task Countdown(string uri, string name)
    {
        _joinCts?.Cancel();
        var cts = _joinCts = new CancellationTokenSource();
        _pendingUri = uri;
        JoinPanel.Visibility = Visibility.Visible;
        try
        {
            for (var left = Math.Max(0, _cfg.BootDelaySeconds); left > 0; left--)
            {
                Status($"FiveM is starting — joining '{name}' in {left}s");
                await Task.Delay(1000, cts.Token);
            }
            Core.Connect(uri);
            Status($"Joining '{name}'…");
        }
        catch (OperationCanceledException) { /* joined early or cancelled; the handler set the status */ }
        finally
        {
            if (_joinCts == cts) { JoinPanel.Visibility = Visibility.Collapsed; _joinCts = null; _pendingUri = null; cts.Dispose(); }
        }
    }

    void JoinNow_Click(object sender, RoutedEventArgs e)
    {
        var uri = _pendingUri;
        _joinCts?.Cancel();
        if (uri is null) return;
        try { Core.Connect(uri); Status("Joining…"); }
        catch (Exception ex) { Status("Join failed: " + ex.Message, true); }
    }

    void CancelJoin_Click(object sender, RoutedEventArgs e)
    {
        _joinCts?.Cancel();
        Status("Join cancelled — FiveM is still open, join from the server list in-game.");
    }

    // ---- settings ----
    void Browse_Click(object sender, RoutedEventArgs e)
    {
        var d = new OpenFolderDialog { Title = "Select FiveM.app folder", InitialDirectory = PathBox.Text };
        if (d.ShowDialog() == true) PathBox.Text = d.FolderName;
    }

    void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        var path = PathBox.Text.Trim();
        if (path.Length == 0) { Status("FiveM.app folder can't be blank.", true); return; }
        if (!int.TryParse(DelayBox.Text.Trim(), out var delay) || delay < 0) { Status("Connect delay must be a whole number of seconds.", true); return; }

        _cfg.FiveMAppPath = path;
        _cfg.LinkedPaths = LinkedBox.Text.Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
        _cfg.BootDelaySeconds = delay;
        Core.Save(_cfg);
        var missing = !Directory.Exists(path);
        Status(missing ? "Settings saved — but that folder doesn't exist yet." : "Settings saved.", missing);
    }

    // Silent on startup (download only, Velopack applies it on next launch — the Spotify behaviour);
    // loud from the button, where the user is waiting for an answer and can be asked to restart.
    async Task CheckUpdates(bool silent)
    {
        var mgr = Core.Updater();
        if (mgr is null)
        {
            if (!silent) Status("This copy wasn't installed by the installer, so it can't self-update.", true);
            return;
        }
        try
        {
            if (!silent) Status("Checking for updates…");
            var info = await mgr.CheckForUpdatesAsync();
            if (info is null) { if (!silent) Status($"You're on the latest version ({Core.CurrentVersion})."); return; }

            var version = info.TargetFullRelease.Version;
            Status($"Downloading {version}…");
            await mgr.DownloadUpdatesAsync(info);

            if (silent) { Status($"Version {version} downloaded — it installs next time you start the app."); return; }
            if (Confirm($"Version {version} is ready to install.\n\nRestart now to apply it?", "Update available"))
                mgr.ApplyUpdatesAndRestart(info);
            else
                Status($"Version {version} will install next time you start the app.");
        }
        catch (Exception ex)
        {
            if (!silent) Status("Update check failed: " + ex.Message, true);
        }
    }

    async void CheckUpdates_Click(object sender, RoutedEventArgs e) => await CheckUpdates(silent: false);

    void StartMenu_Click(object sender, RoutedEventArgs e)
    {
        try { Core.CreateStartMenuShortcut(); Status("Added to the Start menu — search for 'FiveM Manager'."); }
        catch (Exception ex) { Status("Couldn't create the shortcut: " + ex.Message, true); }
    }

    async void ClearCache_Click(object sender, RoutedEventArgs e)
    {
        if (!Confirm($"Delete these folders from {_cfg.FiveMAppPath}\\data?\n\n    {string.Join("\n    ", Core.CacheFolders)}\n\n" +
                     "Servers re-download their resources on your next join, and UI settings a script saved locally reset.\n\n" +
                     "Your Rockstar link (game-storage) and saved accounts are not touched.", "Clear cache")) return;
        if (!GuardFiveMClosed()) return;

        long freed = 0;
        var failed = new List<string>();
        var ok = await RunAsync("Clearing cache…", "Clear cache failed: ", () =>
        {
            var r = Core.ClearCache(_cfg.FiveMAppPath);
            freed = r.Freed; failed = r.Failed;
        });
        if (!ok) return;

        Status(failed.Count == 0
            ? (freed == 0 ? "Nothing to clear — cache folders are already gone." : $"Cleared {Size(freed)}.")
            : $"Cleared {Size(freed)}, but {string.Join(" and ", failed)} wouldn't delete — something still has the files open.",
            failed.Count > 0);
    }

    static string Size(long b) =>
        b >= 1L << 30 ? $"{b / (double)(1L << 30):0.#} GB" :
        b >= 1L << 20 ? $"{b / (double)(1L << 20):0.#} MB" : $"{b / 1024.0:0.#} KB";
}
