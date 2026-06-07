#ifndef FT4_CODER_API_H
#define FT4_CODER_API_H

#include <stddef.h>

#if defined(_WIN32)
  #if defined(FT4_CODER_BUILD)
    #define FT4_CODER_API __declspec(dllexport)
  #else
    #define FT4_CODER_API __declspec(dllimport)
  #endif
#else
  #define FT4_CODER_API __attribute__((visibility("default")))
#endif

#ifdef __cplusplus
extern "C" {
#endif

/* Matches SkyRoof NativeFT4Coder.QsoStage */
typedef enum Ft4QsoStage {
  FT4_QSO_CALLING = 0,
  FT4_QSO_REPLYING,
  FT4_QSO_REPORT,
  FT4_QSO_ROGER_REPORT,
  FT4_QSO_ROGERS,
  FT4_QSO_SIGNOFF
} Ft4QsoStage;

typedef void (*Ft4DecodedMessageCallback)(const char *message);

FT4_CODER_API void encode_ft4(
  unsigned char message[37],
  float *tx_audio_frequency,
  float audio_samples[241920]);

FT4_CODER_API void decode_ft4(
  float audio_samples[290304],
  Ft4QsoStage *qso_progress,
  int *nfqso,
  int *nfb,
  unsigned char mycall[12],
  unsigned char hiscall[12],
  Ft4DecodedMessageCallback callback);

#ifdef __cplusplus
}
#endif

#endif
