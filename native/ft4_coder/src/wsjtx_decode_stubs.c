/* Stub wsjtx_lib decode callback; decoder.f90 calls this when a message is found. */
void wsjtx_decoded_(int *nutc, int *snr, float *dt, int *freq, char *decoded, int len)
{
  (void)nutc;
  (void)snr;
  (void)dt;
  (void)freq;
  (void)decoded;
  (void)len;
}
