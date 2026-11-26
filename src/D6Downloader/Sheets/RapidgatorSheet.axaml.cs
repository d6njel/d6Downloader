using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using D6Downloader.Debrid;
using D6Downloader.Downloads;
using D6Downloader.Hosters.Rapidgator;

namespace D6Downloader.Sheets;

/// <summary>
/// Das Rapidgator-Konto: einmal anmelden (bei Bedarf mit 2FA-Code), danach Premium-Laufzeit und Traffic, Abmelden. Anmeldung und
/// Speicherung liegen ganz bei <see cref="RapidgatorSession"/>; Passwort und Code bleiben nicht in den Feldern stehen.
/// </summary>
public partial class RapidgatorSheet : UserControl
{
    private readonly MainWindow _host;
    private readonly DownloadManager _downloads;
    private readonly CancellationTokenSource _closed = new();
    private bool _busy;
    private bool _justSignedIn;
    private string? _message;
    private bool _messageIsError;

    public RapidgatorSheet() => throw new NotSupportedException();

    private RapidgatorSheet(MainWindow host)
    {
        InitializeComponent();
        _host = host;
        _downloads = host.Downloads;
        EmailBox.Text = _downloads.Rapidgator.Login ?? "";
        _downloads.RapidgatorChanged += OnChanged;
        DetachedFromVisualTree += (_, _) =>
        {
            _downloads.RapidgatorChanged -= OnChanged;
            _closed.Cancel();
        };
        if (State == RapidgatorState.LoginFailed && _downloads.Rapidgator.LoginError is { } error)
            SetMessage(ErrorText.Describe(new DebridException(error) { Service = RapidgatorProvider.ProviderId }), true);
        Render();
    }

    private RapidgatorState State => _downloads.Rapidgator.State;

    /// <summary>Der laufende Vorgang (Anmelden, Abfragen), für die Tests.</summary>
    internal Task Pending { get; private set; } = Task.CompletedTask;

    public static RapidgatorSheet Open(MainWindow host)
    {
        var sheet = new RapidgatorSheet(host);
        Control focus = sheet.State switch
        {
            RapidgatorState.CodeRequired => sheet.CodeBox,
            RapidgatorState.LoggedIn => sheet.CloseButton,
            _ => string.IsNullOrEmpty(sheet.EmailBox.Text) ? sheet.EmailBox : sheet.PasswordBox,
        };
        host.ShowSheet(sheet, focus);
        // Nach einem Neustart ist der Kontostand noch unbekannt.
        if (sheet.State == RapidgatorState.LoggedIn && sheet._downloads.Rapidgator.Account is null)
            sheet.Pending = sheet.RefreshAccountAsync();
        return sheet;
    }

    private void OnChanged(object? sender, EventArgs e) => Render();

    private void SetMessage(string? text, bool error)
    {
        _message = text;
        _messageIsError = error;
    }

    private void Render()
    {
        var session = _downloads.Rapidgator;
        var state = State;
        bool signedIn = state == RapidgatorState.LoggedIn;
        bool code = state == RapidgatorState.CodeRequired;
        bool form = state is RapidgatorState.LoggedOut or RapidgatorState.LoginFailed;
        bool stored = _downloads.Settings.Rapidgator.HasCredential;

        AccountPanel.IsVisible = signedIn;
        LoginPanel.IsVisible = form;
        CodePanel.IsVisible = code;
        CancelCodeButton.IsVisible = code && !stored; // eine gespeicherte Anmeldung wird nur über „Abmelden“ verworfen
        RefreshButton.IsVisible = signedIn;
        RefreshButton.IsEnabled = !_busy;
        SubmitButton.IsVisible = !signedIn;
        SubmitButton.Content = Loc.T(code ? "Rapidgator.SubmitCode" : "Rapidgator.SignIn");
        SignOutButton.IsVisible = stored;
        SignOutHint.IsVisible = stored;
        CloseButton.Classes.Set("accent", signedIn);

        if (signedIn)
        {
            SignedInText.Text = Loc.T("Rapidgator.SignedInAs", session.Login);
            var account = session.Account;
            PremiumText.Text = Premium(account);
            PremiumText.Classes.Set("error", account is { IsPremium: false });
            TrafficText.Text = Traffic(account);
            EnabledBox.IsChecked = _downloads.Settings.Rapidgator.Enabled;
        }
        if (code) CodePrompt.Text = Loc.T("Rapidgator.CodePrompt", session.Login);

        StatusText.Text = _message ?? "";
        StatusText.IsVisible = _message is not null;
        StatusText.Classes.Set("error", _messageIsError);

        int failed = signedIn && _justSignedIn ? FailedDownloads().Count : 0;
        RetryButton.IsVisible = failed > 0;
        if (failed > 0) RetryButton.Content = Loc.Plural("Rapidgator.Retry", failed);

        UpdateSubmit();
        _host.RefreshAccounts();
    }

    private static string Premium(DebridAccount? account)
    {
        if (account is null) return Loc.T("Rapidgator.Unknown");
        if (!account.IsPremium) return Loc.T("Rapidgator.PremiumNone");
        return account.PremiumUntil is { } until
            ? Loc.T("Rapidgator.PremiumUntil",
                until.LocalDateTime.ToString(Loc.T("Format.DatePattern"), System.Globalization.CultureInfo.InvariantCulture))
            : Loc.T("Rapidgator.PremiumActive");
    }

    private static string Traffic(DebridAccount? account) => account switch
    {
        { TrafficLeft: { } left, TrafficTotal: { } total } => Loc.T("Rapidgator.TrafficOf", Format.Bytes(left), Format.Bytes(total)),
        { TrafficLeft: { } left } => Loc.T("Rapidgator.TrafficLeft", Format.Bytes(left)),
        _ => Loc.T("Rapidgator.Unknown"),
    };

    private void UpdateSubmit()
    {
        bool complete = State == RapidgatorState.CodeRequired
            ? (CodeBox.Text ?? "").Trim().Length > 0
            : (EmailBox.Text ?? "").Trim().Length > 0 && (PasswordBox.Text ?? "").Length > 0;
        SubmitButton.IsEnabled = !_busy && complete;
    }

    private void Input_Changed(object? sender, TextChangedEventArgs e)
    {
        if (_downloads is not null) UpdateSubmit();
    }

    private async void Submit_Click(object? sender, RoutedEventArgs e)
    {
        if (_busy) return;
        Pending = SubmitAsync();
        await Pending;
    }

    /// <summary>Anmelden oder den Code nachreichen: genau eine Anfrage je Klick.</summary>
    internal async Task SubmitAsync()
    {
        var session = _downloads.Rapidgator;
        bool code = State == RapidgatorState.CodeRequired;
        string email = EmailBox.Text ?? "", password = PasswordBox.Text ?? "";
        string entered = (code ? CodeBox.Text : OptionalCodeBox.Text) ?? "";

        _busy = true;
        SetMessage(Loc.T("Rapidgator.SigningIn"), false);
        Render();
        try
        {
            if (code) await session.SubmitCodeAsync(entered, _closed.Token);
            else await session.LoginAsync(email, password, entered, _closed.Token);
            SetMessage(null, false);
            _justSignedIn = true;
            PasswordBox.Clear();
            CodeBox.Clear();
            OptionalCodeBox.Clear();
        }
        catch (OperationCanceledException)
        {
            return; // Sheet geschlossen
        }
        catch (Exception ex) // auch Unerwartetes nur anzeigen: Ein async-void-Aufrufer darf die App nicht beenden
        {
            // „Code verlangt“ ist kein Fehler: Das Sheet zeigt jetzt das Code-Feld.
            if (ex is DebridException { Error: DebridError.TwoFactorRequired }) SetMessage(null, false);
            else SetMessage(ErrorText.Describe(ex), true);
            CodeBox.Clear();
        }
        finally
        {
            _busy = false;
        }
        if (_closed.IsCancellationRequested) return;
        Render();
        if (State == RapidgatorState.CodeRequired) CodeBox.Focus();
    }

    private async void Refresh_Click(object? sender, RoutedEventArgs e)
    {
        if (_busy) return;
        Pending = RefreshAccountAsync();
        await Pending;
    }

    private async Task RefreshAccountAsync()
    {
        _busy = true;
        SetMessage(Loc.T("Rapidgator.Refreshing"), false);
        Render();
        try
        {
            await _downloads.RefreshRapidgatorAccountAsync(_closed.Token);
            SetMessage(null, false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            SetMessage(ErrorText.Describe(ex), true);
        }
        finally
        {
            _busy = false;
        }
        if (!_closed.IsCancellationRequested) Render();
    }

    private void CancelCode_Click(object? sender, RoutedEventArgs e)
    {
        _downloads.Rapidgator.CancelPendingLogin();
        SetMessage(null, false);
        PasswordBox.Clear();
        CodeBox.Clear();
        Render();
    }

    private void Enabled_Click(object? sender, RoutedEventArgs e)
    {
        _downloads.Settings.Rapidgator.Enabled = EnabledBox.IsChecked == true;
        _downloads.SaveSettings();
        _host.RefreshAccounts();
    }

    private void SignOut_Click(object? sender, RoutedEventArgs e)
    {
        _downloads.LogoutRapidgator();
        SetMessage(null, false);
        _justSignedIn = false;
        EmailBox.Clear();
        PasswordBox.Clear();
        CodeBox.Clear();
        OptionalCodeBox.Clear();
        Render();
    }

    private List<DownloadItem> FailedDownloads() => _downloads.Items
        .Where(i => i.Status == DownloadStatus.Failed
            && (i.ViaService == RapidgatorProvider.ProviderId || (i.ViaService is null && RapidgatorLinks.IsRapidgator(i.Url))))
        .ToList();

    private void Retry_Click(object? sender, RoutedEventArgs e)
    {
        _downloads.Start(FailedDownloads());
        _justSignedIn = false;
        Render();
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => _host.CloseSheet();
}
