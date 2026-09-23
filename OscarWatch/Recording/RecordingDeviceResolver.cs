namespace OscarWatch.Recording;

/// <summary>
/// Resolves a saved recording device to a PortAudio index using durable name identity
/// (not a volatile enumeration index). Used after USB re-enumeration / reboot.
/// </summary>
internal static class RecordingDeviceResolver
{
    /// <summary>PortAudio <c>PaHostApiTypeId</c> values used when choosing a Windows host API.</summary>
    internal const int HostApiDirectSound = 1;

    internal const int HostApiMme = 2;
    internal const int HostApiAsio = 3;
    internal const int HostApiWdmks = 11;
    internal const int HostApiWasapi = 13;

    internal readonly record struct InputDeviceSnapshot(
        int Index,
        string RawName,
        double DefaultLowInputLatency,
        int MaxInputChannels,
        int HostApiType = 0);

    /// <summary>
    /// Returns the PortAudio device index to open, or -1 if no match.
    /// </summary>
    internal static int ResolveIndex(
        string? deviceId,
        string? deviceDisplayName,
        IReadOnlyList<InputDeviceSnapshot> inputs,
        bool preferLowLatencyShared = false)
    {
        if (inputs.Count == 0)
            return -1;

        var id = deviceId?.Trim() ?? "";
        var displayName = deviceDisplayName?.Trim() ?? "";
        var legacyIndex = TryParseLegacyIndex(id);

        if (legacyIndex is null && id.Length > 0)
        {
            var byRaw = FindBestByRawName(id, inputs, preferLowLatencyShared);
            if (byRaw >= 0)
                return byRaw;
        }

        if (displayName.Length > 0)
        {
            var byDisplay = FindBestByFormattedDisplayName(displayName, inputs, preferLowLatencyShared);
            if (byDisplay >= 0)
                return byDisplay;
        }

        // Legacy numeric id: only honour if the device at that index still matches the saved display name
        if (legacyIndex is { } index)
        {
            var atIndex = inputs.FirstOrDefault(d => d.Index == index && d.MaxInputChannels > 0);
            if (atIndex.MaxInputChannels > 0)
            {
                if (displayName.Length == 0)
                    return -1;

                var formattedAtIndex = RecordingDeviceNameFormatter.Format(atIndex.RawName);
                var formattedStored = RecordingDeviceNameFormatter.Format(displayName);
                if (formattedAtIndex.Equals(formattedStored, StringComparison.OrdinalIgnoreCase))
                    return index;

                // Index moved: try display name was already attempted above; also try raw match on id is N/A
            }
        }

        // Last resort for legacy: if we only have a numeric id and no display name, do not open by index alone
        return -1;
    }

    internal static bool IsLegacyNumericDeviceId(string? deviceId) =>
        TryParseLegacyIndex(deviceId?.Trim() ?? "") is not null;

    private static int? TryParseLegacyIndex(string id)
    {
        if (id.Length == 0)
            return null;
        // Pure decimal index only (legacy). Raw PortAudio names are never solely digits in practice,
        // but we treat any all-digit id as legacy so old settings migrate safely.
        for (var i = 0; i < id.Length; i++)
        {
            if (!char.IsAsciiDigit(id[i]))
                return null;
        }

        if (!int.TryParse(id, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var index))
            return null;
        return index;
    }

    private static int FindBestByRawName(
        string rawName,
        IReadOnlyList<InputDeviceSnapshot> inputs,
        bool preferLowLatencyShared)
    {
        var matches = inputs
            .Where(d => d.MaxInputChannels > 0
                        && d.RawName.Trim().Equals(rawName, StringComparison.OrdinalIgnoreCase));
        return Pick(matches, preferLowLatencyShared);
    }

    private static int FindBestByFormattedDisplayName(
        string displayName,
        IReadOnlyList<InputDeviceSnapshot> inputs,
        bool preferLowLatencyShared)
    {
        var formattedStored = RecordingDeviceNameFormatter.Format(displayName);
        if (formattedStored.Length == 0)
            return -1;

        // Pass recording prefers the higher-latency host API (MME or DirectSound) so a
        // Virtual Audio Cable stays shareable. WDM-KS is often the lowest latency and exclusive.
        // FT4 passes preferLowLatencyShared so transmit uses WASAPI instead.
        var matches = inputs
            .Where(d =>
            {
                if (d.MaxInputChannels <= 0)
                    return false;
                var formatted = RecordingDeviceNameFormatter.Format(d.RawName);
                return formatted.Equals(formattedStored, StringComparison.OrdinalIgnoreCase);
            });
        return Pick(matches, preferLowLatencyShared);
    }

    private static int Pick(IEnumerable<InputDeviceSnapshot> matches, bool preferLowLatencyShared)
    {
        var ordered = preferLowLatencyShared
            ? matches
                .OrderBy(SharedLatencyTier)
                .ThenBy(d => d.DefaultLowInputLatency)
                .ThenBy(d => d.Index)
            : matches
                .OrderByDescending(d => d.DefaultLowInputLatency)
                .ThenBy(d => d.Index);
        var best = ordered.FirstOrDefault();
        return best.MaxInputChannels > 0 ? best.Index : -1;
    }

    /// <summary>
    /// 0 = WASAPI (shared and low latency), 1 = other shareable APIs, 2 = exclusive (WDM-KS, ASIO).
    /// </summary>
    private static int SharedLatencyTier(InputDeviceSnapshot device)
    {
        if (device.HostApiType == HostApiWasapi)
            return 0;
        if (device.HostApiType is HostApiWdmks or HostApiAsio)
            return 2;
        return 1;
    }
}
