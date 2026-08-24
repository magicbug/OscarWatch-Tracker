using System.Diagnostics;
using Avalonia.Controls;
using OscarWatch.Core.Models;
using OscarWatch.Core.Services;
using OscarWatch.Localization;
using OscarWatch.ViewModels;
using OscarWatch.Views;
using Serilog;

namespace OscarWatch.Services;

public static class HamsAtActivationCoordinator
{
    public static async Task PostAsync(
        Window owner,
        PassInfo pass,
        GroundStation observer,
        string defaultCallsign,
        HamsAtSettings hamsAtSettings,
        IHamsAtRovesService hamsAtService,
        ILocalizationService localization,
        string timeRangeLine,
        string detailsLine,
        FrequencyOverlayViewModel? frequencies,
        Action<string> setStatus,
        Func<Task>? refreshRovesAsync = null,
        ISatelliteDatabaseService? satelliteDatabase = null,
        IReadOnlyDictionary<string, SatelliteFrequencySelection>? frequencySelections = null,
        bool cwKeepSidebandDownlink = false)
    {
        if (string.IsNullOrWhiteSpace(hamsAtSettings.ApiKey))
        {
            setStatus(localization.Get("Pass.HamsAt.EnterApiKey"));
            return;
        }

        var hints = ResolveFrequencyHints(
            pass,
            frequencies,
            satelliteDatabase,
            frequencySelections,
            cwKeepSidebandDownlink);
        var dialog = new HamsAtActivationWindow(new HamsAtActivationViewModel(
            pass,
            observer,
            defaultCallsign,
            localization,
            timeRangeLine,
            detailsLine,
            hints));

        if (await dialog.ShowDialog<bool?>(owner).ConfigureAwait(true) != true)
            return;

        if (!dialog.TryBuildRequest(out var request) || request is null)
            return;

        HamsAtCreateAlertResult result;
        try
        {
            result = await hamsAtService.CreateAlertAsync(hamsAtSettings, request).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            var requestJson = HamsAtRovesService.SerializeCreateAlertPayload(request);
            Log.Warning(
                ex,
                "hams.at activation post failed for {Satellite} NORAD {Norad} callsign {Callsign}; request {RequestJson}",
                pass.SatelliteName,
                request.SatelliteNumber,
                request.Callsign,
                requestJson);
            setStatus(localization.Get("Pass.HamsAt.Failed", ex.Message));
            return;
        }

        if (!result.Ok)
        {
            Log.Warning(
                "hams.at activation post failed for {Satellite} NORAD {Norad} callsign {Callsign} mode {Mode} grids {Grids}: HTTP {StatusCode}; {Error}; request {RequestJson}",
                pass.SatelliteName,
                request.SatelliteNumber,
                request.Callsign,
                request.Mode,
                string.Join(", ", request.Grids),
                result.HttpStatusCode,
                result.ErrorMessage ?? localization.Get("Main.HamsAtRoves.LoadFailed"),
                result.RequestJson ?? HamsAtRovesService.SerializeCreateAlertPayload(request));
            setStatus(localization.Get(
                "Pass.HamsAt.Failed",
                result.ErrorMessage ?? localization.Get("Main.HamsAtRoves.LoadFailed")));
            return;
        }

        Log.Information(
            "hams.at activation posted for {Satellite} NORAD {Norad} callsign {Callsign} mode {Mode} grids {Grids}; alert {AlertUrl}",
            pass.SatelliteName,
            request.SatelliteNumber,
            request.Callsign,
            request.Mode,
            string.Join(", ", request.Grids),
            result.AlertUrl);

        setStatus(localization.Get("Pass.HamsAt.Posted"));

        if (refreshRovesAsync is not null)
            _ = refreshRovesAsync();

        if (dialog.OpenOnHamsAtAfterPost && !string.IsNullOrWhiteSpace(result.AlertUrl))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = result.AlertUrl,
                UseShellExecute = true
            });
        }
    }

    public static HamsAtActivationHints ResolveFrequencyHints(
        PassInfo pass,
        FrequencyOverlayViewModel? frequencies,
        ISatelliteDatabaseService? satelliteDatabase = null,
        IReadOnlyDictionary<string, SatelliteFrequencySelection>? frequencySelections = null,
        bool cwKeepSidebandDownlink = false)
    {
        SatelliteFrequencySelection? selection = null;
        if (frequencySelections is not null && satelliteDatabase is not null)
        {
            var entry = satelliteDatabase.TryGetEntry(pass.SatelliteName, pass.NoradId);
            if (entry is not null)
            {
                if (frequencySelections.TryGetValue(entry.Name, out var byName))
                    selection = byName;
                else if (frequencySelections.TryGetValue(pass.SatelliteName.Trim(), out var byPassName))
                    selection = byPassName;
            }
        }

        if (frequencies is not null
            && string.Equals(frequencies.SatelliteName, pass.SatelliteName, StringComparison.OrdinalIgnoreCase)
            && frequencies.SelectedMode is { } overlayMode)
        {
            return SatelliteDatabaseModePicker.ToActivationHints(
                overlayMode,
                selection,
                cwKeepSidebandDownlink);
        }

        if (satelliteDatabase is null)
            return HamsAtActivationHints.Empty;

        var databaseMode = SatelliteDatabaseModePicker.ResolveDefaultMode(
            satelliteDatabase,
            pass.SatelliteName,
            pass.NoradId,
            frequencySelections);
        return SatelliteDatabaseModePicker.ToActivationHints(
            databaseMode,
            selection,
            cwKeepSidebandDownlink);
    }
}
