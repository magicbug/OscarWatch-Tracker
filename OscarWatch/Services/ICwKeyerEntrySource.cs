using OscarWatch.Core.Cw;

namespace OscarWatch.Services;

/// <summary>Logbook entry fields the CW keyer reads for macros and {SAVEQSO}.</summary>
public interface ICwKeyerEntrySource
{
    CwMacroContext BuildCwMacroContext();

    bool CanCommitQso { get; }

    Task CommitQsoFromCwAsync();
}
