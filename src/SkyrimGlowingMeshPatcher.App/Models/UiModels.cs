using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using SkyrimGlowingMeshPatcher.Core.Models;

namespace SkyrimGlowingMeshPatcher.App.Models;

public sealed class ShapeScanItemViewModel
{
    public required string ShapeName { get; init; }

    public required string KindText { get; init; }

    public required string ValueText { get; init; }

    public required string DecisionText { get; init; }

    public required string ReasonSummary { get; init; }
}

public sealed class FileScanItemViewModel : ObservableObject
{
    private bool isSelected = true;

    public event EventHandler? SelectionChanged;

    public required FileScanResult SourceResult { get; init; }

    public required string DisplayPath { get; init; }

    public required string Summary { get; init; }

    public required string PatchCandidateCountText { get; init; }

    public bool IsExpanded { get; init; } = true;

    public required IReadOnlyList<ShapeScanItemViewModel> Shapes { get; init; }

    public int PatchCandidateCount => Shapes.Count;

    public bool IsSelected
    {
        get => isSelected;
        set
        {
            if (SetProperty(ref isSelected, value))
            {
                OnPropertyChanged(nameof(SelectionStateText));
                SelectionChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public string SelectionStateText => IsSelected ? "Selected" : "Skipped";
}

public sealed class ModScanGroupViewModel : ObservableObject
{
    private bool isSelected = true;
    private bool suppressSelectionSync;

    public ModScanGroupViewModel(string modName, IEnumerable<FileScanItemViewModel> files)
    {
        ModName = modName;
        Files = new ObservableCollection<FileScanItemViewModel>(files);

        foreach (var file in Files)
        {
            file.SelectionChanged += HandleFileSelectionChanged;
        }

        isSelected = Files.Count == 0 || Files.All(static file => file.IsSelected);
    }

    public event EventHandler? SelectionChanged;

    public string ModName { get; }

    public ObservableCollection<FileScanItemViewModel> Files { get; }

    public bool IsSelected
    {
        get => isSelected;
        set
        {
            if (!SetProperty(ref isSelected, value))
            {
                return;
            }

            suppressSelectionSync = true;
            try
            {
                foreach (var file in Files)
                {
                    file.IsSelected = value;
                }
            }
            finally
            {
                suppressSelectionSync = false;
            }

            NotifySummaryChanged();
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public int SelectedFileCount => Files.Count(static file => file.IsSelected);

    public int SelectedShapeCount => Files.Where(static file => file.IsSelected).Sum(static file => file.PatchCandidateCount);

    public int ShapeCount => Files.Sum(static file => file.PatchCandidateCount);

    public string Summary => $"{SelectedFileCount}/{Files.Count} file(s) selected, {SelectedShapeCount}/{ShapeCount} shape(s)";

    public string StatusText => $"{Files.Count} file(s)";

    private void HandleFileSelectionChanged(object? sender, EventArgs e)
    {
        if (suppressSelectionSync)
        {
            return;
        }

        var allSelected = Files.Count == 0 || Files.All(static file => file.IsSelected);
        if (isSelected != allSelected)
        {
            isSelected = allSelected;
            OnPropertyChanged(nameof(IsSelected));
        }

        NotifySummaryChanged();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void NotifySummaryChanged()
    {
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(StatusText));
    }
}
