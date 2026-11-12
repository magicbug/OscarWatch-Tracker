using OscarWatch.Recording;

namespace OscarWatch.Sstv;

/// <summary>PortAudio receive-only capture for OscarWatch SSTV.</summary>
public sealed class SstvAudioService() : ReceiveAudioCapture("SSTV");
