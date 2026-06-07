#include "ft4_coder_api.h"

#include <stdio.h>
#include <string.h>

#define ENCODE_SAMPLES 241920
#define DECODE_SAMPLES 290304

static float sample_peak(const float *samples, int count)
{
  float peak = 0.0f;
  for (int i = 0; i < count; ++i) {
    float v = samples[i];
    if (v < 0.0f) {
      v = -v;
    }
    if (v > peak) {
      peak = v;
    }
  }
  return peak;
}

static int g_decode_count;

static void on_decoded(const char *message)
{
  if (message && message[0] != '\0') {
    ++g_decode_count;
    printf("DECODE: %s\n", message);
  }
}

int main(void)
{
  unsigned char message[37];
  float tone = 1500.0f;
  float audio[ENCODE_SAMPLES];
  float decode_window[DECODE_SAMPLES];
  unsigned char mycall[12];
  unsigned char hiscall[12];
  Ft4QsoStage stage = FT4_QSO_CALLING;
  int nfqso = 1500;
  int nfb = 4000;
  float peak;

  memset(message, (unsigned char)' ', sizeof(message));
  memcpy(message, "CQ TESTCALL FN03", 16);

  encode_ft4(message, &tone, audio);

  peak = sample_peak(audio, ENCODE_SAMPLES);
  if (peak < 0.01f) {
    fprintf(stderr, "FAIL: encoded waveform peak=%f\n", peak);
    return 1;
  }
  printf("PASS: encode_ft4 peak=%f\n", peak);

  memset(decode_window, 0, sizeof(decode_window));
  memcpy(decode_window, audio, ENCODE_SAMPLES * sizeof(float));

  memset(mycall, (unsigned char)' ', sizeof(mycall));
  memcpy(mycall, "TESTCALL  ", 10);
  memset(hiscall, (unsigned char)' ', sizeof(hiscall));

  g_decode_count = 0;
  decode_ft4(decode_window, &stage, &nfqso, &nfb, mycall, hiscall, on_decoded);

  if (g_decode_count < 1) {
    fprintf(stderr, "FAIL: decode_ft4 produced no callbacks\n");
    return 2;
  }

  printf("PASS: decode_ft4 callbacks=%d\n", g_decode_count);
  return 0;
}
