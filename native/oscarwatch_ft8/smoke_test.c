#include <stdio.h>
#include <stdlib.h>
#include "oscarwatch_ft8.h"

int main(void)
{
    float* buf = malloc(12000 * 8 * sizeof(float));
    int count = 0;
    int rc = ow_ft8_encode_pcm("CQ MM9SQL IO85", 1200.f, 1, buf, 12000 * 8, 12000, &count);
    printf("encode rc=%d count=%d\n", rc, count);
    ow_ft8_decode_t out[20];
    int n = ow_ft8_decode_pcm(buf, count, 12000, 1, 200.f, 2800.f, out, 20);
    printf("decode n=%d\n", n);
    for (int i = 0; i < n; i++)
        printf("  %.0fHz %.1fdB [%s]\n", out[i].freq_hz, out[i].snr, out[i].text);
    free(buf);
    return n > 0 ? 0 : 1;
}
