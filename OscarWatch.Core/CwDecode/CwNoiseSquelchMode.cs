namespace OscarWatch.Core.CwDecode;

/// <summary>How aggressively the decoder hides short, unsure letters from noise.</summary>
public enum CwNoiseSquelchMode
{
    Off = 0,
    Normal = 1,
    Strict = 2,
}
