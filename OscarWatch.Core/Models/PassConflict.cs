namespace OscarWatch.Core.Models;

public sealed record PassConflict(PassInfo PassA, PassInfo PassB, TimeSpan OverlapDuration);
