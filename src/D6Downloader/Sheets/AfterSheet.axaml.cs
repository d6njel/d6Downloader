using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using D6Downloader.Downloads;
using D6Downloader.Power;

namespace D6Downloader.Sheets;

/// <summary>„Nach Abschluss“ festlegen: welche Aktion, und wann (alle fertig oder ein bestimmter Download).</summary>
public partial class AfterSheet : UserControl
{
    private const string WhenAll = "all", WhenOne = "one";

    private readonly MainWindow _host;
    private readonly List<DownloadItem> _targets;

    public AfterSheet() => throw new NotSupportedException();

    private AfterSheet(MainWindow host, DownloadItem? preselected)
    {
        InitializeComponent();
        _host = host;
        var downloads = host.Downloads;
        var plan = downloads.AfterCompletion;

        // Auswahl: alles, was noch nicht fertig ist.
        _targets = downloads.Items.Where(i => i.Status != DownloadStatus.Completed).ToList();
        string? target = plan.TargetId is not null && _targets.Any(i => i.Id == plan.TargetId) ? plan.TargetId
            : preselected is not null && _targets.Contains(preselected) ? preselected.Id
            : _targets.FirstOrDefault()?.Id;
        bool specific = (plan.IsArmed && plan.TargetId is not null) || preselected is not null;

        var whens = new List<Choice> { new(Loc.T("After.WhenAll"), WhenAll) };
        if (_targets.Count > 0) whens.Add(new(Loc.T("After.WhenOne"), WhenOne));
        Choices.Fill(WhenBox, whens, specific && _targets.Count > 0 ? WhenOne : WhenAll);
        WhenBox.SelectionChanged += (_, _) => UpdateTargetRow();
        Choices.Fill(TargetBox, _targets.Select(i => new Choice(i.FileName, i.Id)), target);
        Choices.Fill(ActionBox, new[] { new Choice(Loc.T("After.DoNothing"), nameof(PowerAction.None)) }, null);
        UpdateTargetRow();
        Loaded = LoadActionsAsync(plan);
    }

    /// <summary>Fertig, sobald die möglichen Aktionen abgefragt sind.</summary>
    internal new Task Loaded { get; }

    /// <summary>Was das System kann, erfährt man erst nach einigen Sekunden: nie auf dem UI-Thread fragen.</summary>
    private async Task LoadActionsAsync(AfterCompletion plan)
    {
        var all = new[] { PowerAction.Shutdown, PowerAction.Hibernate, PowerAction.Sleep };
        var actions = plan.Actions;
        var available = await Task.Run(() => all.Where(actions.IsAvailable).ToList());

        var choices = new List<Choice> { new(Loc.T("After.DoNothing"), nameof(PowerAction.None)) };
        choices.AddRange(available.Select(a => new Choice(PowerText.Name(a), a.ToString())));
        Choices.Fill(ActionBox, choices, plan.Kind.ToString());
        if (!available.Contains(PowerAction.Hibernate))
        {
            NoteText.Text = Loc.T("After.NoHibernate");
            NoteText.IsVisible = true;
        }
        ApplyButton.IsEnabled = true;
    }

    public static void Open(MainWindow host, DownloadItem? preselected = null) =>
        host.ShowSheet(new AfterSheet(host, preselected));

    private void UpdateTargetRow()
    {
        bool one = WhenBox.Value() == WhenOne;
        TargetLabel.IsVisible = one;
        TargetBox.IsVisible = one;
    }

    private void Save_Click(object? sender, RoutedEventArgs e)
    {
        var plan = _host.Downloads.AfterCompletion;
        var action = Enum.Parse<PowerAction>(ActionBox.Value() ?? nameof(PowerAction.None));
        if (action == PowerAction.None)
        {
            plan.Disarm();
        }
        else if (WhenBox.Value() == WhenOne)
        {
            var item = _targets.FirstOrDefault(i => i.Id == TargetBox.Value());
            if (item is null)
            {
                ErrorText.Text = Loc.T("After.ChooseDownload");
                ErrorText.IsVisible = true;
                return;
            }
            plan.Arm(action, item.Id, item.FileName);
        }
        else
        {
            plan.Arm(action);
        }
        _host.CloseSheet();
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => _host.CloseSheet();
}
