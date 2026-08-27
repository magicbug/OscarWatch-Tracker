using Avalonia.Controls;
using OscarWatch.Core.Models;
using OscarWatch.Core.Services;
using OscarWatch.ViewModels;

namespace OscarWatch.Views;

public sealed record AddSatellitePickResult(string Name, string? NoradId);

public static class AddSatelliteFromTleDialog
{
    public static async Task<AddSatellitePickResult?> TryPickAsync(
        Window owner,
        ITleService tleService,
        IEnumerable<SatelliteRadioEntry> existingEntries,
        CancellationToken cancellationToken = default)
    {
        await tleService.EnsureLoadedAsync(cancellationToken).ConfigureAwait(true);
        var vm = new AddSatelliteFromTleViewModel(
            tleService.Catalog,
            existingEntries,
            Localization.LocalizationService.Instance);
        var window = new AddSatelliteFromTleWindow { DataContext = vm };
        return await window.ShowDialog<AddSatellitePickResult?>(owner).ConfigureAwait(true);
    }
}
