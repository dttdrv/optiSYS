// optiSYS C ABI header for P/Invoke interop
// Generated from zig/src/c_api/bridge.zig

#ifndef OPTISYS_CORE_H
#define OPTISYS_CORE_H

#include <stdint.h>
#include <stdbool.h>

#ifdef OPTISYS_CORE_EXPORTS
#define OPTISYS_API __declspec(dllexport)
#else
#define OPTISYS_API __declspec(dllimport)
#endif

#ifdef __cplusplus
extern "C" {
#endif

typedef struct {
    int power_source;        // 0=Unknown, 1=AC, 2=Battery
    bool has_battery;
    uint8_t charge_percent;
    int drain_rate_milliwatts;
    int estimated_time_remaining_seconds;
} OptiSysBatteryInfo;

typedef struct {
    int64_t total_physical_bytes;
    int64_t available_physical_bytes;
    int64_t committed_bytes;
    int64_t standby_cache_bytes;
    int64_t modified_page_list_bytes;
} OptiSysMemoryInfo;

typedef struct {
    int process_id;
    char process_name[256];
    int64_t working_set_bytes;
    int64_t private_bytes;
    int priority_class;
    bool is_foreground;
    bool is_excluded;
} OptiSysProcessInfo;

// Power/battery
OPTISYS_API int optisys_power_init(void);
OPTISYS_API int optisys_power_snapshot(OptiSysBatteryInfo* info);
OPTISYS_API int optisys_power_source(void);

// Memory
OPTISYS_API int optisys_memory_init(void);
OPTISYS_API int optisys_memory_snapshot(OptiSysMemoryInfo* info);
OPTISYS_API int optisys_memory_optimize(int level, int excluded_count, int* excluded_pids);

// Process
OPTISYS_API int optisys_process_list(OptiSysProcessInfo* buffer, int buffer_size);
OPTISYS_API int64_t optisys_process_trim(int pid);

// Battery optimization
OPTISYS_API int optisys_set_eco_qos(int pid, bool enable);

// Shutdown
OPTISYS_API void optisys_shutdown(void);

#ifdef __cplusplus
}
#endif

#endif // OPTISYS_CORE_H
