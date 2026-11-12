using OscarWatch.Recording;

namespace OscarWatch.Cw;

/// <summary>PortAudio receive-only capture for the OscarWatch CW decoder.</summary>
public sealed class CwAudioService() : ReceiveAudioCapture("CW");
