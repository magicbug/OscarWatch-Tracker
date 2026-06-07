#include "ft4_coder_api.h"

#include <fftw3.h>
#include <math.h>
#include <string.h>

typedef size_t fortran_charlen_t;

void genft4_(char *msg, int *ichk, char *msgsent, char ft4msgbits[],
             int itone[], fortran_charlen_t, fortran_charlen_t);

void gen_ft4wave_(int itone[], int *nsym, int *nsps, float *fsample, float *f0,
                  float xjunk[], float wave[], int *icmplx, int *nwave);

static void ft4_coder_init(void)
{
  fftwf_init_threads();
  fftwf_plan_with_nthreads(1);
}

#if defined(_WIN32)
  #include <windows.h>
  BOOL WINAPI DllMain(HINSTANCE inst, DWORD reason, LPVOID reserved)
  {
    (void)inst;
    (void)reserved;
    if (reason == DLL_PROCESS_ATTACH) {
      ft4_coder_init();
    }
    return TRUE;
  }
#else
  __attribute__((constructor))
  static void ft4_coder_ctor(void)
  {
    ft4_coder_init();
  }
#endif

void encode_ft4(unsigned char message[37], float *tx_audio_frequency, float audio_samples[241920])
{
  char msg[38];
  char sendmsg[38];
  char ft4msgbits[101];
  int itone[104];
  int ichk = 0;
  int nsym = 103;
  int nsps = 4 * 576;
  float fsample = 48000.0f;
  float f0 = tx_audio_frequency ? *tx_audio_frequency : 1500.0f;
  int nwave = (nsym + 2) * nsps;
  int icmplx = 0;

  memset(msg, 0, sizeof(msg));
  memcpy(msg, message, 37);
  genft4_(msg, &ichk, sendmsg, ft4msgbits, itone, (fortran_charlen_t)37, (fortran_charlen_t)37);
  gen_ft4wave_(itone, &nsym, &nsps, &fsample, &f0, audio_samples, audio_samples, &icmplx, &nwave);
}

void decode_ft4(
  float audio_samples[290304],
  Ft4QsoStage *qso_progress,
  int *nfqso,
  int *nfb,
  unsigned char mycall[12],
  unsigned char hiscall[12],
  Ft4DecodedMessageCallback callback)
{
  (void)audio_samples;
  (void)mycall;
  (void)hiscall;
  (void)callback;

  if (qso_progress) {
    *qso_progress = FT4_QSO_CALLING;
  }
  if (nfqso) {
    *nfqso = 1500;
  }
  if (nfb) {
    *nfb = 4000;
  }
}
