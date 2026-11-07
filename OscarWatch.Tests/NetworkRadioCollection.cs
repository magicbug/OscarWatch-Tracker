namespace OscarWatch.Tests;

/// <summary>
/// Tests that talk to a loopback fake radio over TCP or UDP. They rely on the fake replying within the
/// driver's reply timeouts, so they run on their own instead of alongside the CPU-heavy suite.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class NetworkRadioCollection
{
    public const string Name = "NetworkRadio";
}
