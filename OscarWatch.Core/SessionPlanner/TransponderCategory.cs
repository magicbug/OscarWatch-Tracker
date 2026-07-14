namespace OscarWatch.Core.SessionPlanner;

/// <summary>
/// Categorises a satellite's transponder type for quality scoring.
/// </summary>
public enum TransponderCategory
{
    /// <summary>Linear transponder — factor 1.0.</summary>
    Linear,

    /// <summary>FM transponder — factor 0.6.</summary>
    Fm,

    /// <summary>Both linear and FM modes available — factor 0.8.</summary>
    Mixed,

    /// <summary>No transponder data in catalogue — factor 0.7.</summary>
    Unknown
}
