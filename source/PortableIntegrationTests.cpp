// Production-DLL integration host. Build this dedicated test executable with
// the name AzerothSurvivors.exe, then pass the full production DLL path.
// It loads the DLL into itself only; no other process is opened or modified.
// Usage: AzerothSurvivors.exe <full-path-to-AzerothSpeedHook.dll> [--mismatch]
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <cstddef>
#include <cstdio>
#include <cwchar>
#include <cmath>
#include <initializer_list>

namespace {
constexpr DWORD kMagic = 0x415A5350;
constexpr DWORD kVersion = 3;
constexpr DWORD kMaxPathChars = 32768;
constexpr size_t kControlSize = 65592;

struct alignas(8) ControlBlock {
    DWORD magic;
    DWORD version;
    volatile LONG controllerPid;
    volatile LONG requestedPercent;
    volatile LONG appliedPercent;
    volatile LONG state;
    volatile LONG error;
    DWORD reserved;
    volatile LONG64 heartbeat;
    volatile LONG64 hookCalls;
    DWORD expectedPathChars;
    DWORD reservedPath;
    wchar_t expectedPath[kMaxPathChars];
};
static_assert(sizeof(ControlBlock) == kControlSize, "IPC size mismatch");
static_assert(offsetof(ControlBlock, heartbeat) == 32, "IPC heartbeat mismatch");
static_assert(offsetof(ControlBlock, hookCalls) == 40, "IPC counter mismatch");
static_assert(offsetof(ControlBlock, expectedPathChars) == 48, "IPC path length mismatch");
static_assert(offsetof(ControlBlock, expectedPath) == 56, "IPC path mismatch");

using QpcFunction = BOOL (WINAPI*)(LARGE_INTEGER*);
ControlBlock* control = nullptr;
QpcFunction realQpc = nullptr;
int failures = 0;

LONG ReadLong(volatile LONG* value) {
    return InterlockedCompareExchange(value, 0, 0);
}
LONG64 ReadLong64(volatile LONG64* value) {
    return InterlockedCompareExchange64(value, 0, 0);
}
void Heartbeat() {
    InterlockedExchange64(&control->heartbeat, static_cast<LONG64>(GetTickCount64()));
}
void Check(bool ok, const char* description) {
    std::printf("%s %s\n", ok ? "PASS" : "FAIL", description);
    if (!ok) ++failures;
}

bool WaitFor(LONG state, LONG percent, DWORD timeoutMs, bool refreshHeartbeat) {
    const ULONGLONG start = GetTickCount64();
    do {
        if (refreshHeartbeat) Heartbeat();
        if (ReadLong(&control->state) == state &&
            (percent < 0 || ReadLong(&control->appliedPercent) == percent)) return true;
        if (ReadLong(&control->state) == 3 && state != 3) return false;
        Sleep(10);
    } while (GetTickCount64() - start < timeoutMs);
    return false;
}

bool MeasureRatio(LONG percent) {
    LARGE_INTEGER virtualStart{}, virtualEnd{}, realStart{}, realEnd{};
    // These direct imported calls pass through the production DLL's patched
    // main-executable IAT slot. realQpc is the unmodified Windows export.
    const BOOL virtualStartOk = QueryPerformanceCounter(&virtualStart);
    const BOOL realStartOk = realQpc(&realStart);
    Sleep(120);
    const BOOL realEndOk = realQpc(&realEnd);
    const BOOL virtualEndOk = QueryPerformanceCounter(&virtualEnd);
    if (!virtualStartOk || !realStartOk || !realEndOk || !virtualEndOk ||
        realEnd.QuadPart <= realStart.QuadPart) return false;
    const double ratio = static_cast<double>(virtualEnd.QuadPart - virtualStart.QuadPart) /
                         static_cast<double>(realEnd.QuadPart - realStart.QuadPart);
    std::printf("Production DLL requested %ld%%: %.5fx\n", percent, ratio);
    return std::abs(ratio - percent / 100.0) < 0.03;
}

bool IsNormalAbsoluteArgument(const wchar_t* path) {
    const size_t length = wcsnlen_s(path, kMaxPathChars);
    return (length >= 3 && ((path[0] >= L'A' && path[0] <= L'Z') ||
        (path[0] >= L'a' && path[0] <= L'z')) && path[1] == L':' && path[2] == L'\\') ||
        (length >= 5 && path[0] == L'\\' && path[1] == L'\\' && path[2] != L'?' && path[2] != L'.');
}
} // namespace

int wmain(int argc, wchar_t** argv) {
    if (argc < 2 || argc > 3 || (argc == 3 && wcscmp(argv[2], L"--mismatch") != 0) ||
        !IsNormalAbsoluteArgument(argv[1])) {
        std::fprintf(stderr, "Usage: AzerothSurvivors.exe <full-production-DLL-path> [--mismatch]\n");
        return 2;
    }
    const bool mismatch = argc == 3;
    wchar_t actualPath[kMaxPathChars]{};
    const DWORD actualChars = GetModuleFileNameW(nullptr, actualPath, ARRAYSIZE(actualPath));
    if (actualChars == 0 || actualChars >= kMaxPathChars) {
        std::fprintf(stderr, "Could not read integration-host path: %lu\n", GetLastError());
        return 2;
    }
    const wchar_t* basename = wcsrchr(actualPath, L'\\');
    if (!basename || _wcsicmp(basename + 1, L"AzerothSurvivors.exe") != 0) {
        std::fprintf(stderr, "Build this isolated host with the name AzerothSurvivors.exe.\n");
        return 2;
    }
    realQpc = reinterpret_cast<QpcFunction>(GetProcAddress(GetModuleHandleW(L"kernel32.dll"), "QueryPerformanceCounter"));
    if (!realQpc) {
        std::fprintf(stderr, "Could not resolve original QPC export.\n");
        return 2;
    }

    wchar_t mappingName[100]{};
    swprintf_s(mappingName, L"Local\\AzerothSurvivorsSpeed_%lu", GetCurrentProcessId());
    const HANDLE mapping = CreateFileMappingW(INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE,
        0, static_cast<DWORD>(kControlSize), mappingName);
    const DWORD mappingError = GetLastError();
    if (!mapping || mappingError == ERROR_ALREADY_EXISTS) {
        std::fprintf(stderr, "Could not create unique integration mapping: %lu\n", mappingError);
        if (mapping) CloseHandle(mapping);
        return 2;
    }
    control = static_cast<ControlBlock*>(MapViewOfFile(mapping, FILE_MAP_ALL_ACCESS, 0, 0, kControlSize));
    if (!control) {
        std::fprintf(stderr, "Could not map integration control block: %lu\n", GetLastError());
        CloseHandle(mapping);
        return 2;
    }
    ZeroMemory(control, kControlSize);
    control->version = kVersion;
    control->controllerPid = static_cast<LONG>(GetCurrentProcessId());
    control->requestedPercent = 100;
    if (mismatch) {
        // A syntactically valid target name in an unselected directory. It need
        // not exist: the DLL compares it with its own real executable path.
        constexpr wchar_t otherPath[] = L"C:\\PortableIntegrationUnselected\\AzerothSurvivors.exe";
        wcscpy_s(control->expectedPath, otherPath);
        control->expectedPathChars = static_cast<DWORD>(ARRAYSIZE(otherPath) - 1);
    } else {
        wcscpy_s(control->expectedPath, actualPath);
        control->expectedPathChars = actualChars;
    }
    Heartbeat();
    // Publish the magic last after writing the remaining initialization data.
    InterlockedExchange(reinterpret_cast<volatile LONG*>(&control->magic), static_cast<LONG>(kMagic));

    const HMODULE module = LoadLibraryW(argv[1]);
    if (!module) {
        std::fprintf(stderr, "Could not load production DLL into test host: %lu\n", GetLastError());
        UnmapViewOfFile(control);
        CloseHandle(mapping);
        return 2;
    }
    if (mismatch) {
        Check(WaitFor(3, -1, 5000, true), "production DLL rejects mismatched selected path");
        Check(ReadLong(&control->error) == ERROR_ACCESS_DENIED, "mismatched path reports access denied");
        Check(ReadLong64(&control->hookCalls) == 0, "mismatched path produces no timing-hook calls");
        Check(MeasureRatio(100), "mismatched path leaves imported QPC at measured 1x");
        Check(ReadLong64(&control->hookCalls) == 0, "unmodified imported QPC never enters hook");
    } else {
        const bool ready = WaitFor(1, 100, 5000, true);
        Check(ready, "production DLL initializes at acknowledged 1x");
        if (!ready) std::fprintf(stderr, "Module state=%ld error=%ld\n", ReadLong(&control->state), ReadLong(&control->error));
        if (ready) {
            LARGE_INTEGER frequency{};
            QueryPerformanceFrequency(&frequency);
            for (LONG percent : {100, 150, 200, 300, 500}) {
                LARGE_INTEGER before{}, after{};
                QueryPerformanceCounter(&before);
                Heartbeat();
                InterlockedExchange(&control->requestedPercent, percent);
                const bool applied = WaitFor(1, percent, 1500, true);
                Check(applied, "production worker acknowledges requested speed");
                if (!applied) break;
                QueryPerformanceCounter(&after);
                Check(after.QuadPart >= before.QuadPart &&
                    after.QuadPart - before.QuadPart < frequency.QuadPart,
                    "production speed transition remains continuous");
                Heartbeat();
                Check(MeasureRatio(percent), "production DLL imported-QPC ratio within tolerance");
            }
            Check(ReadLong64(&control->hookCalls) > 0, "production hook received imported QPC calls");
            // Stop heartbeats while leaving requestedPercent unchanged at 5x;
            // only the real production watchdog can cause this reset to 1x.
            InterlockedExchange64(&control->heartbeat, 0);
            Check(WaitFor(2, 100, 3000, false), "production watchdog resets speed after zero heartbeat");
            Check(MeasureRatio(100), "production watchdog restores measured 1x");
        }
    }
    std::printf("Production integration mode %s: %s (%d failures)\n",
        mismatch ? "mismatch" : "matching path", failures ? "FAILED" : "PASSED", failures);
    // Deliberately keep the production DLL and IPC views resident. Its worker
    // and IAT hook remain active until this isolated test process exits.
    return failures ? 1 : 0;
}
