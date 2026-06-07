#include "ft4_coder_api.h"

#include <stdio.h>
#include <string.h>

#define ENCODE_SAMPLES 241920

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

int main(void)
{
  unsigned char message[37];
  float tone = 1500.0f;
  float audio[ENCODE_SAMPLES];

  memset(message, (unsigned char)' ', sizeof(message));
  memcpy(message, "CQ TESTCALL FN03", 16);

  encode_ft4(message, &tone, audio);

  float peak = sample_peak(audio, ENCODE_SAMPLES);
  if (peak < 0.01f) {
    fprintf(stderr, "FAIL: encoded waveform peak=%f\n", peak);
    return 1;
  }

  printf("PASS: encode_ft4 peak=%f\n", peak);
  return 0;
}
