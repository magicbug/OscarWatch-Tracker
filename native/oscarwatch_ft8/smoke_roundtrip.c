#include <stdio.h>
#include <stdlib.h>
#include "oscarwatch_ft8.h"

static void roundtrip(const char* label, int is_ft4)
{
    int cap = 12000 * 16;
    float* buf = malloc((size_t)cap * sizeof(float));
    int count = 0;
    int rc = ow_ft8_encode_pcm("CQ MM9SQL IO85", 1200.f, is_ft4, buf, cap, 12000, &count);
    printf("%s encode rc=%d count=%d\n", label, rc, count);
    ow_ft8_decode_t out[20];
    int n = ow_ft8_decode_pcm(buf, count, 12000, is_ft4, 200.f, 2800.f, out, 20);
    printf("%s decode n=%d\n", label, n);
    for (int i = 0; i < n; i++)
        printf("  %.0fHz %.1fdB [%s]\n", out[i].freq_hz, out[i].snr, out[i].text);
    free(buf);
}

int main(void)
{
    roundtrip("ft8", 0);
    roundtrip("ft4", 1);
    return 0;
}
