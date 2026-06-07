#include "ft4_coder_api.h"
#include "wsjtx_params.h"

#include <math.h>
#include <stdio.h>
#include <string.h>
#include <time.h>

typedef size_t fortran_charlen_t;

#define FT4_DECODE_SAMPLES 290304
#define FT4_SS_FLOATS (184 * WSJTX_NSMAX)

static float g_ss[FT4_SS_FLOATS];
static short g_id2[WSJTX_NTMAX * WSJTX_RX_SAMPLE_RATE];

static Ft4DecodedMessageCallback g_ft4_message_callback;
static char g_ft4_callback_line[256];

void ft4_decode_set_callback(Ft4DecodedMessageCallback callback)
{
  g_ft4_message_callback = callback;
}

static void trim_trailing_spaces(char *text, size_t capacity)
{
  size_t len;
  if (!text || capacity == 0) {
    return;
  }
  len = strnlen(text, capacity);
  while (len > 0 && (text[len - 1] == ' ' || text[len - 1] == '\0')) {
    text[len - 1] = '\0';
    --len;
  }
}

static void format_decode_line(char *line, size_t line_size, int nutc, int snr, float dt, int freq,
                               const char *decoded)
{
  /* SkyRoof / OscarWatch Ft4DecodeLine.ParseMessageString layout */
  (void)nutc;
  snprintf(line, line_size, "%6.6d%4d%5.1f%5d ~  %s", nutc, snr, (double)dt, freq, decoded);
}

void wsjtx_decoded_(int *nutc, int *snr, float *dt, int *freq, char *decoded, fortran_charlen_t decoded_len)
{
  char message[64];
  size_t copy_len;

  (void)decoded_len;
  if (!nutc || !snr || !dt || !freq || !decoded || !g_ft4_message_callback) {
    return;
  }

  copy_len = decoded_len > 0 ? (size_t)decoded_len : 37;
  if (copy_len >= sizeof(message)) {
    copy_len = sizeof(message) - 1;
  }
  memcpy(message, decoded, copy_len);
  message[copy_len] = '\0';
  trim_trailing_spaces(message, sizeof(message));

  if (message[0] == '\0' || strstr(message, "DecodeFinished") != NULL) {
    return;
  }

  format_decode_line(g_ft4_callback_line, sizeof(g_ft4_callback_line), *nutc, *snr, *dt, *freq, message);
  g_ft4_message_callback(g_ft4_callback_line);
}

void multimode_decoder_(float *ss, short *id2, wsjtx_params_t *params, int *nfsample);

void ft4_run_decode(
  const float *audio_samples,
  Ft4QsoStage *qso_progress,
  int *nfqso,
  int *nfb,
  const unsigned char *mycall,
  const unsigned char *hiscall,
  Ft4DecodedMessageCallback callback)
{
  wsjtx_params_t params;
  int nfsample = 48000;
  int sample_count = FT4_DECODE_SAMPLES;
  time_t now;
  struct tm *utc;
  int i;

  if (!audio_samples || !callback) {
    return;
  }

  memset(&params, 0, sizeof(params));
  now = time(NULL);
  utc = gmtime(&now);
  if (utc) {
    params.nutc = utc->tm_hour * 10000 + utc->tm_min * 100 + utc->tm_sec;
  }

  params.newdat = true;
  params.ndiskdat = false;
  params.ntrperiod = 15;
  params.nmode = 5;
  params.ntxmode = 5;
  params.nQSOProgress = qso_progress ? (int)*qso_progress : (int)FT4_QSO_CALLING;
  params.nfqso = nfqso ? *nfqso : 1500;
  params.nfa = 200;
  params.nfb = nfb ? *nfb : 4000;
  params.nfSplit = 2700;
  params.ntol = 20;
  params.ndepth = 3;
  params.lft8apon = true;
  params.lapcqonly = false;
  params.ljt65apon = true;
  params.napwid = 75;
  params.nagain = false;
  params.nclearave = false;
  params.nexp_decode = 0;
  params.n2pass = 2;
  params.nranera = 6;
  params.naggressive = 0;
  params.nrobust = false;
  params.minw = 0;
  params.minSync = 0;
  params.emedelay = 0.0f;
  params.dttol = 3.0f;
  params.nlist = 0;
  params.npts8 = 74736;
  params.nzhsym = 79;
  params.nsubmode = 0;
  params.kin = 64800;

  if (mycall) {
    memcpy(params.mycall, mycall, 12);
  }
  if (hiscall) {
    memcpy(params.hiscall, hiscall, 12);
  }

  memset(g_id2, 0, sizeof(g_id2));
  for (i = 0; i < sample_count; ++i) {
    float sample = audio_samples[i];
    if (sample > 1.0f) {
      sample = 1.0f;
    } else if (sample < -1.0f) {
      sample = -1.0f;
    }
    g_id2[i] = (short)(sample * 32767.0f);
  }

  g_ft4_message_callback = callback;
  multimode_decoder_(g_ss, g_id2, &params, &nfsample);
  g_ft4_message_callback = NULL;
}
