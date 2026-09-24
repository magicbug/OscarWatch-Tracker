#include "oscarwatch_ft8.h"

#include <math.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#ifdef _WIN32
#include <windows.h>
#else
#include <pthread.h>
#endif

#include <common/common.h>
#include <common/monitor.h>
#include <ft8/constants.h>
#include <ft8/decode.h>
#include <ft8/encode.h>
#include <ft8/message.h>

#define FT8_SYMBOL_BT 2.0f
#define FT4_SYMBOL_BT 1.0f
#define GFSK_CONST_K 5.336446f

#define CALLSIGN_HASHTABLE_SIZE 256
#define kMin_score 10
#define kMax_candidates 60
#define kLDPC_iterations_fast 10
#define kLDPC_iterations 25
#define kFreq_osr 2
#define kTime_osr 2

static struct
{
    char callsign[12];
    uint32_t hash;
} callsign_hashtable[CALLSIGN_HASHTABLE_SIZE];

static int callsign_hashtable_size;

#ifdef _WIN32
static CRITICAL_SECTION g_hash_lock;
static LONG g_hash_lock_ready;
static void hash_lock_ensure(void)
{
    if (InterlockedCompareExchange(&g_hash_lock_ready, 1, 0) == 0)
        InitializeCriticalSection(&g_hash_lock);
}
static void hash_lock(void)
{
    hash_lock_ensure();
    EnterCriticalSection(&g_hash_lock);
}
static void hash_unlock(void)
{
    LeaveCriticalSection(&g_hash_lock);
}
#else
static pthread_mutex_t g_hash_lock = PTHREAD_MUTEX_INITIALIZER;
static void hash_lock(void)
{
    pthread_mutex_lock(&g_hash_lock);
}
static void hash_unlock(void)
{
    pthread_mutex_unlock(&g_hash_lock);
}
#endif

static void hashtable_init(void)
{
    callsign_hashtable_size = 0;
    memset(callsign_hashtable, 0, sizeof(callsign_hashtable));
}

static void hashtable_add(const char* callsign, uint32_t hash)
{
    hash_lock();
    uint16_t hash10 = (hash >> 12) & 0x3FFu;
    int idx_hash = (hash10 * 23) % CALLSIGN_HASHTABLE_SIZE;
    while (callsign_hashtable[idx_hash].callsign[0] != '\0')
    {
        if (((callsign_hashtable[idx_hash].hash & 0x3FFFFFu) == hash)
            && (0 == strcmp(callsign_hashtable[idx_hash].callsign, callsign)))
        {
            callsign_hashtable[idx_hash].hash &= 0x3FFFFFu;
            hash_unlock();
            return;
        }
        idx_hash = (idx_hash + 1) % CALLSIGN_HASHTABLE_SIZE;
    }
    callsign_hashtable_size++;
    strncpy(callsign_hashtable[idx_hash].callsign, callsign, 11);
    callsign_hashtable[idx_hash].callsign[11] = '\0';
    callsign_hashtable[idx_hash].hash = hash;
    hash_unlock();
}

static bool hashtable_lookup(ftx_callsign_hash_type_t hash_type, uint32_t hash, char* callsign)
{
    hash_lock();
    uint8_t hash_shift = (hash_type == FTX_CALLSIGN_HASH_10_BITS) ? 12
        : (hash_type == FTX_CALLSIGN_HASH_12_BITS ? 10 : 0);
    uint16_t hash10 = (hash >> (12 - hash_shift)) & 0x3FFu;
    int idx_hash = (hash10 * 23) % CALLSIGN_HASHTABLE_SIZE;
    while (callsign_hashtable[idx_hash].callsign[0] != '\0')
    {
        if (((callsign_hashtable[idx_hash].hash & 0x3FFFFFu) >> hash_shift) == hash)
        {
            strcpy(callsign, callsign_hashtable[idx_hash].callsign);
            hash_unlock();
            return true;
        }
        idx_hash = (idx_hash + 1) % CALLSIGN_HASHTABLE_SIZE;
    }
    callsign[0] = '\0';
    hash_unlock();
    return false;
}

static ftx_callsign_hash_interface_t hash_if = {
    .lookup_hash = hashtable_lookup,
    .save_hash = hashtable_add
};

static int hashtable_ready;

static void ensure_hashtable(void)
{
    hash_lock();
    if (!hashtable_ready)
    {
        hashtable_init();
        hashtable_ready = 1;
    }
    hash_unlock();
}

typedef struct
{
    int ready;
    int sample_rate;
    int is_ft4;
    float f_min;
    float f_max;
    monitor_t mon;
} ow_monitor_cache_t;

#ifdef _WIN32
static __declspec(thread) ow_monitor_cache_t g_mon_cache;
#else
static __thread ow_monitor_cache_t g_mon_cache;
#endif

static monitor_t* acquire_monitor(int sample_rate, int is_ft4, float f_min_hz, float f_max_hz)
{
    ftx_protocol_t protocol = is_ft4 ? FTX_PROTOCOL_FT4 : FTX_PROTOCOL_FT8;
    if (g_mon_cache.ready
        && g_mon_cache.sample_rate == sample_rate
        && g_mon_cache.is_ft4 == is_ft4
        && g_mon_cache.f_min == f_min_hz
        && g_mon_cache.f_max == f_max_hz)
    {
        monitor_reset(&g_mon_cache.mon);
        if (g_mon_cache.mon.last_frame && g_mon_cache.mon.nfft > 0)
            memset(g_mon_cache.mon.last_frame, 0, (size_t)g_mon_cache.mon.nfft * sizeof(float));
        return &g_mon_cache.mon;
    }

    if (g_mon_cache.ready)
    {
        monitor_free(&g_mon_cache.mon);
        g_mon_cache.ready = 0;
    }

    monitor_config_t mon_cfg = {
        .f_min = f_min_hz,
        .f_max = f_max_hz,
        .sample_rate = sample_rate,
        .time_osr = kTime_osr,
        .freq_osr = kFreq_osr,
        .protocol = protocol
    };
    monitor_init(&g_mon_cache.mon, &mon_cfg);
    g_mon_cache.sample_rate = sample_rate;
    g_mon_cache.is_ft4 = is_ft4;
    g_mon_cache.f_min = f_min_hz;
    g_mon_cache.f_max = f_max_hz;
    g_mon_cache.ready = 1;
    return &g_mon_cache.mon;
}

static void gfsk_pulse(int n_spsym, float symbol_bt, float* pulse)
{
    for (int i = 0; i < 3 * n_spsym; ++i)
    {
        float t = i / (float)n_spsym - 1.5f;
        float arg1 = GFSK_CONST_K * symbol_bt * (t + 0.5f);
        float arg2 = GFSK_CONST_K * symbol_bt * (t - 0.5f);
        pulse[i] = (erff(arg1) - erff(arg2)) / 2;
    }
}

static void synth_gfsk(
    const uint8_t* symbols,
    int n_sym,
    float f0,
    float symbol_bt,
    float symbol_period,
    int signal_rate,
    float* signal)
{
    int n_spsym = (int)(0.5f + signal_rate * symbol_period);
    int n_wave = n_sym * n_spsym;
    float hmod = 1.0f;
    float dphi_peak = 2 * (float)M_PI * hmod / n_spsym;

    float* dphi = (float*)calloc((size_t)(n_wave + 2 * n_spsym), sizeof(float));
    float* pulse = (float*)malloc((size_t)(3 * n_spsym) * sizeof(float));
    if (!dphi || !pulse)
    {
        free(dphi);
        free(pulse);
        return;
    }

    for (int i = 0; i < n_wave + 2 * n_spsym; ++i)
        dphi[i] = 2 * (float)M_PI * f0 / signal_rate;

    gfsk_pulse(n_spsym, symbol_bt, pulse);

    for (int i = 0; i < n_sym; ++i)
    {
        int ib = i * n_spsym;
        for (int j = 0; j < 3 * n_spsym; ++j)
            dphi[j + ib] += dphi_peak * symbols[i] * pulse[j];
    }

    for (int j = 0; j < 2 * n_spsym; ++j)
    {
        dphi[j] += dphi_peak * pulse[j + n_spsym] * symbols[0];
        dphi[j + n_sym * n_spsym] += dphi_peak * pulse[j] * symbols[n_sym - 1];
    }

    float phi = 0;
    for (int k = 0; k < n_wave; ++k)
    {
        signal[k] = sinf(phi);
        phi = fmodf(phi + dphi[k + n_spsym], 2 * (float)M_PI);
    }

    int n_ramp = n_spsym / 8;
    for (int i = 0; i < n_ramp; ++i)
    {
        float env = (1 - cosf(2 * (float)M_PI * i / (2 * n_ramp))) / 2;
        signal[i] *= env;
        signal[n_wave - 1 - i] *= env;
    }

    free(dphi);
    free(pulse);
}

OW_FT8_API void ow_ft8_remember_callsign(const char* callsign)
{
    ensure_hashtable();
    if (callsign == NULL || callsign[0] == '\0')
        return;
    /* Hash is computed when encoding/decoding; store with a placeholder hash via encode path. */
    ftx_message_t msg;
    char buf[64];
    snprintf(buf, sizeof(buf), "CQ %s", callsign);
    ftx_message_encode(&msg, &hash_if, buf);
}

OW_FT8_API void ow_ft8_clear_callsigns(void)
{
    hash_lock();
    hashtable_init();
    hashtable_ready = 1;
    hash_unlock();
}

OW_FT8_API int ow_ft8_encode_pcm(
    const char* message_text,
    float freq_hz,
    int is_ft4,
    float* out_samples,
    int out_capacity,
    int sample_rate,
    int* out_count)
{
    ensure_hashtable();
    if (!message_text || !out_samples || !out_count || sample_rate <= 0 || out_capacity <= 0)
        return -1;

    ftx_message_t msg;
    ftx_message_rc_t rc = ftx_message_encode(&msg, &hash_if, message_text);
    if (rc != FTX_MESSAGE_RC_OK)
        return -2;

    int num_tones = is_ft4 ? FT4_NN : FT8_NN;
    float symbol_period = is_ft4 ? FT4_SYMBOL_PERIOD : FT8_SYMBOL_PERIOD;
    float symbol_bt = is_ft4 ? FT4_SYMBOL_BT : FT8_SYMBOL_BT;
    float slot_time = is_ft4 ? FT4_SLOT_TIME : FT8_SLOT_TIME;

    uint8_t* tones = (uint8_t*)malloc((size_t)num_tones);
    if (!tones)
        return -3;

    if (is_ft4)
        ft4_encode(msg.payload, tones);
    else
        ft8_encode(msg.payload, tones);

    int num_samples = (int)(0.5f + num_tones * symbol_period * sample_rate);
    int slot_samples = (int)(0.5f + slot_time * sample_rate);
    /* FT4's candidate search only covers early time offsets. A long centred
     * silence pad pushes the Costas symbols outside that window, so keep a
     * short lead-in and put the remaining silence after the waveform. */
    int num_silence_head = (int)(0.5f + 0.5f * sample_rate); /* 0.5 s */
    if (num_silence_head + num_samples > slot_samples)
        num_silence_head = 0;
    int num_silence_tail = slot_samples - num_silence_head - num_samples;
    if (num_silence_tail < 0)
        num_silence_tail = 0;
    int num_total = num_silence_head + num_samples + num_silence_tail;
    if (num_total > out_capacity)
    {
        free(tones);
        return -4;
    }

    memset(out_samples, 0, (size_t)num_total * sizeof(float));
    synth_gfsk(tones, num_tones, freq_hz, symbol_bt, symbol_period, sample_rate, out_samples + num_silence_head);
    free(tones);

    *out_count = num_total;
    return 0;
}

OW_FT8_API int ow_ft8_decode_pcm(
    const float* samples,
    int num_samples,
    int sample_rate,
    int is_ft4,
    float f_min_hz,
    float f_max_hz,
    ow_ft8_decode_t* out_decodes,
    int out_capacity)
{
    ensure_hashtable();
    if (!samples || num_samples <= 0 || sample_rate <= 0 || !out_decodes || out_capacity <= 0)
        return -1;

    if (!(f_max_hz > f_min_hz + 99.0f) || f_min_hz < 50.0f || f_max_hz > 3500.0f)
    {
        f_min_hz = 200.0f;
        f_max_hz = 2800.0f;
    }

    ftx_protocol_t protocol = is_ft4 ? FTX_PROTOCOL_FT4 : FTX_PROTOCOL_FT8;
    (void)protocol;
    monitor_t* mon = acquire_monitor(sample_rate, is_ft4, f_min_hz, f_max_hz);

    const int block_size = mon->block_size;
    int pos = 0;
    while (pos + block_size <= num_samples)
    {
        monitor_process(mon, samples + pos);
        pos += block_size;
    }

    ftx_candidate_t candidate_list[kMax_candidates];
    int num_candidates = ftx_find_candidates(&mon->wf, kMax_candidates, candidate_list, kMin_score);

    int num_decoded = 0;
    ftx_message_t decoded[OW_FT8_MAX_DECODES];
    ftx_message_t* decoded_hashtable[OW_FT8_MAX_DECODES];
    for (int i = 0; i < OW_FT8_MAX_DECODES; ++i)
        decoded_hashtable[i] = NULL;

    for (int idx = 0; idx < num_candidates && num_decoded < out_capacity; ++idx)
    {
        const ftx_candidate_t* cand = &candidate_list[idx];
        float freq_hz = (mon->min_bin + cand->freq_offset + (float)cand->freq_sub / mon->wf.freq_osr) / mon->symbol_period;
        float time_sec = (cand->time_offset + (float)cand->time_sub / mon->wf.time_osr) * mon->symbol_period;

        ftx_message_t message;
        ftx_decode_status_t status;
        /* Sparse satellite slots rarely need full LDPC; try a short pass first. */
        if (!ftx_decode_candidate(&mon->wf, cand, kLDPC_iterations_fast, &message, &status)
            && !ftx_decode_candidate(&mon->wf, cand, kLDPC_iterations, &message, &status))
        {
            continue;
        }

        int idx_hash = message.hash % OW_FT8_MAX_DECODES;
        bool found_empty_slot = false;
        bool found_duplicate = false;
        do
        {
            if (decoded_hashtable[idx_hash] == NULL)
                found_empty_slot = true;
            else if ((decoded_hashtable[idx_hash]->hash == message.hash)
                && (0 == memcmp(decoded_hashtable[idx_hash]->payload, message.payload, sizeof(message.payload))))
                found_duplicate = true;
            else
                idx_hash = (idx_hash + 1) % OW_FT8_MAX_DECODES;
        } while (!found_empty_slot && !found_duplicate);

        if (!found_empty_slot)
            continue;

        memcpy(&decoded[idx_hash], &message, sizeof(message));
        decoded_hashtable[idx_hash] = &decoded[idx_hash];

        char text[FTX_MAX_MESSAGE_LENGTH];
        ftx_message_offsets_t offsets;
        ftx_message_rc_t unpack_status = ftx_message_decode(&message, &hash_if, text, &offsets);
        if (unpack_status != FTX_MESSAGE_RC_OK)
            snprintf(text, sizeof(text), "ERR%d", (int)unpack_status);

        ow_ft8_decode_t* out = &out_decodes[num_decoded++];
        out->freq_hz = freq_hz;
        out->time_sec = time_sec;
        out->snr = cand->score * 0.5f;
        strncpy(out->text, text, OW_FT8_MAX_MESSAGE_LEN - 1);
        out->text[OW_FT8_MAX_MESSAGE_LEN - 1] = '\0';
    }

    return num_decoded;
}
