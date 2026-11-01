/*
 * GKMD native ABI
 * ===============
 *
 * GKMD 4.9.0 ships as a NativeAOT shared library:
 *
 *   Windows : GKMD.dll
 *   Linux   : libGKMD.so
 *
 * Link or load it with the platform's normal mechanism (LoadLibrary /
 * dlopen, or link against the import library / .so). All functions use the
 * C calling convention.
 *
 * Error handling
 * --------------
 * Functions returning int return 0 on success and -1 on failure.
 * Functions returning a pointer return NULL on failure.
 * After a failure, gkmd_last_error() describes the error on the calling
 * thread. The returned string is owned by the library and remains valid
 * until the next failing call on the same thread.
 *
 * Strings
 * -------
 * All strings are NUL-terminated UTF-8. The profile* functions write into
 * a caller buffer and return the number of bytes written (excluding the
 * NUL). Pass buffer = NULL / capacity = 0 to query the required capacity;
 * if the supplied capacity is too small the function returns the required
 * capacity and writes nothing.
 *
 * Threading
 * ---------
 * Output callbacks run on the controller's output thread. Keep them
 * short; they are invoked synchronously from the reader loop.
 */

#ifndef GKMD_H
#define GKMD_H

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

#if defined(_WIN32)
#  define GKMD_API __declspec(dllimport)
#else
#  define GKMD_API
#endif

#pragma pack(push, 8)

typedef struct GkmdAxisValue
{
    uint32_t axis;   /* HID usage address: (page << 8) | usage */
    float    value;  /* [0.0 .. 1.0] */
} GkmdAxisValue;

typedef struct GkmdGamepadState
{
    const GkmdAxisValue* axes;
    int32_t  axis_count;

    uint32_t buttons;             /* GKButton bitmask */
    uint8_t  hat;                 /* 0 = none, 1..8 = N, NE, E, SE, S, SW, W, NW */

    uint8_t  has_hat_degrees;
    float    hat_degrees;
    uint8_t  has_hat_hundredths;
    int32_t  hat_hundredths;
    uint8_t  has_hat_raw;
    uint16_t hat_raw;

    uint8_t  touchpad_finger0_active;
    uint16_t touchpad_finger0_x;
    uint16_t touchpad_finger0_y;
    uint8_t  touchpad_finger0_id;
    uint8_t  touchpad_finger1_active;
    uint16_t touchpad_finger1_x;
    uint16_t touchpad_finger1_y;
    uint8_t  touchpad_finger1_id;
    uint8_t  touchpad_packet_counter;

    int16_t  gyro_pitch;
    int16_t  gyro_yaw;
    int16_t  gyro_roll;
    int16_t  accel_x;
    int16_t  accel_y;
    int16_t  accel_z;
    uint32_t sensor_timestamp;

    float    accel_gx;
    float    accel_gy;
    float    accel_gz;
    float    gyro_dps_x;
    float    gyro_dps_y;
    float    gyro_dps_z;

    uint8_t  battery_level;
    uint8_t  battery_charging;
    uint8_t  battery_full;
    uint8_t  mic_muted;
    uint8_t  headphones_connected;
} GkmdGamepadState;

#pragma pack(pop)

typedef void (*GkmdOutputCallback)(
    uint32_t source,
    uint8_t  report_id,
    const uint8_t* data,
    int32_t  length,
    uint32_t seq_no,
    void*    user_data);

/* ── Version / diagnostics ──────────────────────────────────────────── */

GKMD_API const char* gkmd_version(void);
GKMD_API const char* gkmd_last_error(void);

/* ── Context ───────────────────────────────────────────────────────── */

GKMD_API void* gkmd_context_create(void);
GKMD_API void  gkmd_context_destroy(void* context);

GKMD_API int gkmd_context_is_backend_available(void);
GKMD_API int gkmd_context_install_backend(void);

GKMD_API int gkmd_context_load_default_profiles(void* context);
GKMD_API int gkmd_context_profile_count(void* context);

GKMD_API int gkmd_context_profile_id(void* context, int32_t index,
                                     uint8_t* buffer, int32_t capacity);
GKMD_API int gkmd_context_profile_name(void* context, int32_t index,
                                       uint8_t* buffer, int32_t capacity);
GKMD_API int gkmd_context_profile_vid(void* context, int32_t index);
GKMD_API int gkmd_context_profile_pid(void* context, int32_t index);
GKMD_API int gkmd_context_profile_is_deployable(void* context, int32_t index);

/* ── Controller ────────────────────────────────────────────────────── */

GKMD_API void* gkmd_context_create_controller(void* context,
                                              const char* profile_id);
GKMD_API void  gkmd_controller_destroy(void* controller);

GKMD_API int gkmd_controller_submit_gamepad(void* controller,
                                            const GkmdGamepadState* state);
GKMD_API int gkmd_controller_submit_raw(void* controller,
                                        const uint8_t* data, int32_t length);

GKMD_API int gkmd_controller_set_output_callback(void* controller,
                                                 GkmdOutputCallback callback,
                                                 void* user_data);
GKMD_API int gkmd_controller_clear_output_callback(void* controller);

#ifdef __cplusplus
}
#endif

#endif /* GKMD_H */
