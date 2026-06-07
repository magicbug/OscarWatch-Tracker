#ifndef WSJTX_PARAMS_H
#define WSJTX_PARAMS_H

#include <stdbool.h>
#include <stdint.h>

/* Must match paulh002/wsjtx_lib commons.h and lib/jt9com.f90 params_block.
 * Build with -fshort-logical so gfortran LOGICAL matches C bool size. */
#define WSJTX_NSMAX 6827
#define WSJTX_NTMAX (30 * 60)
#define WSJTX_RX_SAMPLE_RATE 12000

typedef struct wsjtx_params {
  int nutc;
  bool ndiskdat;
  int ntrperiod;
  int nQSOProgress;
  int nfqso;
  int nftx;
  bool newdat;
  int npts8;
  int nfa;
  int nfSplit;
  int nfb;
  int ntol;
  int kin;
  int nzhsym;
  int nsubmode;
  bool nagain;
  int ndepth;
  bool lft8apon;
  bool lapcqonly;
  bool ljt65apon;
  int napwid;
  int ntxmode;
  int nmode;
  int minw;
  bool nclearave;
  int minSync;
  float emedelay;
  float dttol;
  int nlist;
  int listutc[10];
  int n2pass;
  int nranera;
  int naggressive;
  bool nrobust;
  int nexp_decode;
  int max_drift;
  char datetime[20];
  char mycall[12];
  char mygrid[6];
  char hiscall[12];
  char hisgrid[6];
} wsjtx_params_t;

#endif
