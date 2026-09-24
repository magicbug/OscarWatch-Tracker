#ifndef OSCARWATCH_FT8_H
#define OSCARWATCH_FT8_H

#include <stdint.h>

#ifdef _WIN32
#  ifdef OSCARWATCH_FT8_EXPORTS
#    define OW_FT8_API __declspec(dllexport)
#  else
#    define OW_FT8_API __declspec(dllimport)
#  endif
#else
#  define OW_FT8_API __attribute__((visibility("default")))
#endif

#ifdef __cplusplus
extern "C" {
#endif

#define OW_FT8_MAX_MESSAGE_LEN 48
#define OW_FT8_MAX_DECODES 50

typedef struct
{
    float freq_hz;
    float time_sec;
    float snr;
    char text[OW_FT8_MAX_MESSAGE_LEN];
} ow_ft8_decode_t;

/// Encode a plain FT4/FT8 message text to a 12 kHz float PCM buffer (slot-length with silence padding).
/// @param message_text null-terminated message (e.g. "CQ MM9SQL IO85")
/// @param freq_hz tone base frequency (audio Hz)
/// @param is_ft4 non-zero for FT4, zero for FT8
/// @param out_samples caller-provided buffer; capacity must be at least sample_rate * slot_seconds
/// @param out_capacity number of floats in out_samples
/// @param sample_rate typically 12000
/// @param out_count written sample count
/// @return 0 on success, negative on error
OW_FT8_API int ow_ft8_encode_pcm(
    const char* message_text,
    float freq_hz,
    int is_ft4,
    float* out_samples,
    int out_capacity,
    int sample_rate,
    int* out_count);

/// Decode one slot of float PCM at the given sample rate.
/// @param samples mono float PCM for one slot (FT4 ≈ 7.5 s, FT8 ≈ 15 s)
/// @param num_samples length of samples
/// @param sample_rate sample rate in Hz
/// @param is_ft4 non-zero for FT4
/// @param f_min_hz lower audio search bound (Hz); invalid ranges fall back to 200–2800
/// @param f_max_hz upper audio search bound (Hz)
/// @param out_decodes output array
/// @param out_capacity max entries in out_decodes
/// @return number of decoded messages, or negative on error
OW_FT8_API int ow_ft8_decode_pcm(
    const float* samples,
    int num_samples,
    int sample_rate,
    int is_ft4,
    float f_min_hz,
    float f_max_hz,
    ow_ft8_decode_t* out_decodes,
    int out_capacity);

/// Remember a callsign for hash-table resolution during later decodes.
OW_FT8_API void ow_ft8_remember_callsign(const char* callsign);

/// Clear the callsign hash table.
OW_FT8_API void ow_ft8_clear_callsigns(void);

#ifdef __cplusplus
}
#endif

#endif /* OSCARWATCH_FT8_H */
