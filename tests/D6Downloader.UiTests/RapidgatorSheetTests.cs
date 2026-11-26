using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using D6Downloader.Downloads;
using D6Downloader.Hosters.Rapidgator;
using D6Downloader.Sheets;
using D6Downloader.Tests;
using Xunit;

namespace D6Downloader.UiTests;

public class RapidgatorSheetTests
{
    private static RapidgatorSheet Open(Ui ui)
    {
        Ui.Click(ui.Window, "RapidgatorButton");
        return ui.Sheet<RapidgatorSheet>();
    }

    private static async Task Submit(RapidgatorSheet sheet)
    {
        Ui.Pump();
        Ui.Click(sheet, "SubmitButton");
        await sheet.Pending;
        Ui.Pump();
    }

    private static void Fill(RapidgatorSheet sheet, string password = FakeRapidgator.Password)
    {
        Ui.Find<TextBox>(sheet, "EmailBox").Text = FakeRapidgator.Login;
        Ui.Find<TextBox>(sheet, "PasswordBox").Text = password;
    }

    private static string Status(RapidgatorSheet sheet) => Ui.Find<TextBlock>(sheet, "StatusText").Text ?? "";

    private static bool Visible(RapidgatorSheet sheet, string name) => Ui.Find<Control>(sheet, name).IsVisible;

    [AvaloniaFact]
    public async Task A_wrong_password_shows_the_error_and_is_not_retried()
    {
        using var ui = new Ui();
        var sheet = Open(ui);
        Assert.True(Visible(sheet, "LoginPanel"));
        Assert.False(Ui.Find<Button>(sheet, "SubmitButton").IsEnabled);

        Fill(sheet, "falsch");
        await Submit(sheet);

        Assert.StartsWith(Loc.T("Error.Rapidgator.BadCredentials"), Status(sheet));
        Assert.True(Visible(sheet, "LoginPanel"));
        Assert.False(Visible(sheet, "SignOutButton"));
        Assert.Equal(1, ui.Rapidgator.Logins);
        Assert.False(ui.Downloads.Settings.Rapidgator.HasCredential);
    }

    [AvaloniaFact]
    public async Task Sign_in_shows_premium_and_traffic_and_sign_out_forgets_everything()
    {
        using var ui = new Ui();
        var sheet = Open(ui);
        Fill(sheet);

        await Submit(sheet);

        Assert.True(Visible(sheet, "AccountPanel"));
        Assert.Contains(FakeRapidgator.Login, Ui.Find<TextBlock>(sheet, "SignedInText").Text);
        Assert.Contains("30", Ui.Find<TextBlock>(sheet, "PremiumText").Text); // 1893456000 = Anfang 2030
        Assert.Contains("MB", Ui.Find<TextBlock>(sheet, "TrafficText").Text);
        Assert.True(string.IsNullOrEmpty(Ui.Find<TextBox>(sheet, "PasswordBox").Text));
        Assert.True(ui.Downloads.Settings.Rapidgator.Enabled);

        Ui.Click(sheet, "SignOutButton");

        Assert.True(Visible(sheet, "LoginPanel"));
        Assert.Null(ui.Downloads.Settings.Rapidgator.ProtectedPassword);
        Assert.Null(ui.Downloads.Settings.Rapidgator.ProtectedToken);
        Assert.Equal(RapidgatorState.LoggedOut, ui.Downloads.Rapidgator.State);
    }

    [AvaloniaFact]
    public async Task Two_factor_asks_for_the_code_and_offers_to_restart_failed_downloads()
    {
        using var ui = new Ui();
        ui.Rapidgator.TwoFactor = true;
        var failed = new DownloadItem(FakeRapidgator.FileUrl, "Pack.part1.rar", ui.Dir.Path);
        ui.Downloads.Add(new[] { failed }, start: false);
        failed.Status = DownloadStatus.Failed;
        var sheet = Open(ui);
        Fill(sheet);

        await Submit(sheet);

        Assert.True(Visible(sheet, "CodePanel"));
        Assert.False(Visible(sheet, "LoginPanel"));
        Assert.False(Visible(sheet, "StatusText"));
        Assert.True(Visible(sheet, "CancelCodeButton"));

        Ui.Find<TextBox>(sheet, "CodeBox").Text = "000000";
        await Submit(sheet);
        Assert.StartsWith(Loc.T("Error.Rapidgator.TwoFactorWrong"), Status(sheet));
        Assert.True(Visible(sheet, "CodePanel"));

        Ui.Find<TextBox>(sheet, "CodeBox").Text = FakeRapidgator.Code;
        await Submit(sheet);

        Assert.True(Visible(sheet, "AccountPanel"));
        var retry = Ui.Find<Button>(sheet, "RetryButton");
        Assert.Equal(Loc.Plural("Rapidgator.Retry", 1), retry.Content);
        Ui.Click(retry);
        Assert.NotEqual(DownloadStatus.Paused, failed.Status);
        Assert.False(retry.IsVisible);
    }

    [AvaloniaFact]
    public async Task The_add_sheet_hands_rapidgator_links_to_the_account_and_shows_no_access()
    {
        using var ui = new Ui();
        await ui.Downloads.Rapidgator.LoginAsync(FakeRapidgator.Login, FakeRapidgator.Password, null, default);
        Ui.Pump();
        string noAccess = "https://rapidgator.net/file/" + new string('a', 32) + "/noaccess.bin.html";

        ui.Window.OpenAdd(FakeRapidgator.FileUrl + "\n" + noAccess);
        var sheet = ui.Sheet<AddSheet>();
        var candidates = sheet.Rows.OfType<AddCandidate>().ToList();
        Assert.Equal(2, candidates.Count);
        Assert.All(candidates, c => Assert.Equal(RapidgatorProvider.ProviderId, c.ServiceId));
        await Ui.WaitUntil(() => candidates.All(c => c.Check is CheckStatus.Online or CheckStatus.NoAccess), "Online-Check");
        Assert.Contains(candidates, c => c.Check == CheckStatus.NoAccess);

        var add = Ui.Find<Button>(sheet, "AddButton");
        await Ui.WaitUntil(() => add.IsEffectivelyEnabled, "Knopf");
        Ui.Click(add);

        Assert.All(ui.Downloads.Items, i => Assert.Equal(RapidgatorProvider.ProviderId, i.ViaService));
    }
}
