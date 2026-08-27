using OscarWatch.Core.Models;

namespace OscarWatch.Core.Services;

public sealed class SatelliteDatabaseEditor : ISatelliteDatabaseEditor
{
    private readonly ISatelliteDatabaseService _database;
    private readonly string _bundledPath;

    public SatelliteDatabaseEditor(ISatelliteDatabaseService database, string bundledPath)
    {
        _database = database;
        _bundledPath = bundledPath;
        UserPath = SatelliteDatabasePaths.UserDatabasePath;
    }

    public string BundledPath => _bundledPath;
    public string UserPath { get; }
    public string ActivePath => _database.ActiveDatabasePath;
    public bool IsUsingUserDatabase => _database.IsUsingUserDatabase;

    public List<SatelliteRadioEntry> LoadForEditing()
    {
        var source = File.Exists(UserPath) ? UserPath : _bundledPath;
        return SatelliteDatabaseFile.Load(source)
            .Select(CloneEntry)
            .ToList();
    }

    public void Save(IReadOnlyList<SatelliteRadioEntry> entries)
    {
        var error = SatelliteDatabaseFile.ValidateEntries(entries);
        if (error is not null)
            throw new InvalidOperationException(error);

        var normalized = entries.Select(SatelliteDatabaseFile.NormalizeEntry).ToList();
        SatelliteDatabaseFile.Save(UserPath, normalized);
        _database.Reload();
    }

    public void ResetToBundled()
    {
        if (File.Exists(UserPath))
            File.Delete(UserPath);

        _database.Reload();
    }

    private static SatelliteRadioEntry CloneEntry(SatelliteRadioEntry source) =>
        new()
        {
            Name = source.Name,
            NoradId = source.NoradId,
            AlternativeNames = source.AlternativeNames?.ToList() ?? [],
            Modes = source.Modes.Select(CloneMode).ToList()
        };

    private static SatelliteTransponderMode CloneMode(SatelliteTransponderMode source) =>
        new()
        {
            Type = source.Type,
            DownlinkKHz = source.DownlinkKHz,
            UplinkKHz = source.UplinkKHz,
            DownlinkMode = source.DownlinkMode,
            UplinkMode = source.UplinkMode,
            Doppler = source.Doppler,
            CtcssHz = source.CtcssHz,
            CtcssArmHz = source.CtcssArmHz
        };
}
