// Azeroth Survivors process-local timing hook. No system DLL code is patched.
// Build DLL: cl /nologo /O2 /W4 /EHsc /MT /LD SpeedHook.cpp /link /OUT:SpeedHook.dll
// Build tests: cl /nologo /O2 /W4 /EHsc /MT /DAZS_SELF_TEST SpeedHook.cpp /Fe:SpeedHookTests.exe
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <cstdint>
#include <cstddef>
#include <cwchar>
#include <cstring>
#include <initializer_list>
#include <limits>

namespace {
constexpr wchar_t kTargetName[] = L"AzerothSurvivors.exe";
constexpr DWORD kMaxPathChars = 32768;
constexpr DWORD kMagic = 0x415A5350;
constexpr DWORD kVersion = 3;

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
static_assert(sizeof(ControlBlock) == 65592, "IPC size mismatch");
static_assert(offsetof(ControlBlock, heartbeat) == 32, "IPC heartbeat mismatch");
static_assert(offsetof(ControlBlock, hookCalls) == 40, "IPC counter mismatch");
static_assert(offsetof(ControlBlock, expectedPathChars) == 48, "IPC path length mismatch");
static_assert(offsetof(ControlBlock, expectedPath) == 56, "IPC path mismatch");

// Both buffers must be NUL-terminated within kMaxPathChars. The IPC path is a
// fixed-size UTF-16 array, with a separately supplied count excluding the NUL.
// Normal drive-rooted and UNC paths are supported; device and relative paths
// are intentionally excluded. No user name or game install folder is fixed.
bool NormalAbsolutePath(const wchar_t* path, size_t length) {
    if (!path || length == 0 || length >= kMaxPathChars) return false;
    size_t firstComponent = 0;
    const bool drive = length >= 4 &&
        ((path[0] >= L'A' && path[0] <= L'Z') || (path[0] >= L'a' && path[0] <= L'z')) &&
        path[1] == L':' && path[2] == L'\\';
    const bool unc = length >= 5 && path[0] == L'\\' && path[1] == L'\\';
    if (drive) firstComponent = 3;
    else if (unc) firstComponent = 2;
    else return false;
    size_t componentStart = firstComponent;
    size_t componentCount = 0;
    for (size_t i = firstComponent; i <= length; ++i) {
        if (i == length || path[i] == L'\\') {
            const size_t componentLength = i - componentStart;
            if (componentLength == 0 ||
                (componentLength == 1 && path[componentStart] == L'.') ||
                (componentLength == 2 && path[componentStart] == L'.' && path[componentStart + 1] == L'.') ||
                path[i - 1] == L'.' || path[i - 1] == L' ') return false;
            ++componentCount;
            componentStart = i + 1;
        } else {
            const wchar_t c = path[i];
            if (c < 32 || c == L'/' || c == L':' || c == L'<' || c == L'>' ||
                c == L'"' || c == L'|' || c == L'?' || c == L'*') return false;
        }
    }
    // A UNC path needs a server, a share, and a file below that share.
    return !unc || componentCount >= 3;
}

bool HasTargetBasename(const wchar_t* path, size_t length) {
    if (!NormalAbsolutePath(path, length)) return false;
    size_t nameStart = length;
    while (nameStart > 0 && path[nameStart - 1] != L'\\') --nameStart;
    const size_t nameLength = length - nameStart;
    constexpr size_t targetLength = ARRAYSIZE(kTargetName) - 1;
    return nameLength == targetLength &&
        CompareStringOrdinal(path + nameStart, static_cast<int>(nameLength),
            kTargetName, static_cast<int>(targetLength), TRUE) == CSTR_EQUAL;
}

bool PortableTargetMatches(const wchar_t* actualPath, const wchar_t* expectedPath, DWORD expectedChars) {
    if (!actualPath || !expectedPath || expectedChars == 0 || expectedChars >= kMaxPathChars) return false;
    const size_t actualChars = wcsnlen_s(actualPath, kMaxPathChars);
    const size_t boundedExpectedChars = wcsnlen_s(expectedPath, kMaxPathChars);
    if (actualChars == 0 || actualChars >= kMaxPathChars ||
        boundedExpectedChars != expectedChars || !HasTargetBasename(actualPath, actualChars) ||
        !HasTargetBasename(expectedPath, boundedExpectedChars)) return false;
    return actualChars == expectedChars &&
        CompareStringOrdinal(actualPath, static_cast<int>(actualChars),
            expectedPath, static_cast<int>(expectedChars), TRUE) == CSTR_EQUAL;
}

using QpcFunction = BOOL (WINAPI*)(LARGE_INTEGER*);
QpcFunction realQpc = nullptr;
SRWLOCK clockLock = SRWLOCK_INIT;
LONGLONG realAnchor = 0;
LONGLONG virtualAnchor = 0;
LONGLONG lastVirtual = 0;
LONG speedPercent = 100;
ControlBlock* control = nullptr;

LONG ReadLong(volatile LONG* value) {
    return InterlockedCompareExchange(value, 0, 0);
}
LONG64 ReadLong64(volatile LONG64* value) {
    return InterlockedCompareExchange64(value, 0, 0);
}
bool SupportedSpeed(LONG percent) {
    return percent == 100 || percent == 150 || percent == 200 || percent == 300 || percent == 500;
}
bool FreshHeartbeat(ULONGLONG now, ULONGLONG heartbeat) {
    return heartbeat != 0 && heartbeat <= now && now - heartbeat <= 2000;
}

// Called only with the exclusive clock lock held. Split the multiplication to
// avoid overflowing the intermediate QPC delta, even for long-running games.
LONGLONG ClockAt(LONGLONG realNow) {
    const LONGLONG max = (std::numeric_limits<LONGLONG>::max)();
    const LONGLONG delta = realNow >= realAnchor ? realNow - realAnchor : 0;
    const LONGLONG whole = delta / 100;
    const LONGLONG tail = (delta % 100) * speedPercent / 100;
    LONGLONG value = max;
    if (whole <= (max - tail) / speedPercent) {
        const LONGLONG scaled = whole * speedPercent + tail;
        if (virtualAnchor <= max - scaled) value = virtualAnchor + scaled;
    }
    if (value < lastVirtual) value = lastVirtual;
    lastVirtual = value;
    return value;
}

BOOL WINAPI HookQpc(LARGE_INTEGER* counter) {
    // A NULL pointer is invalid for the Windows API; forward its behavior.
    if (!counter) return realQpc(counter);
    AcquireSRWLockExclusive(&clockLock);
    LARGE_INTEGER realNow{};
    const BOOL result = realQpc(&realNow);
    if (result) counter->QuadPart = ClockAt(realNow.QuadPart);
    ReleaseSRWLockExclusive(&clockLock);
    if (control) InterlockedIncrement64(&control->hookCalls);
    return result;
}

bool ChangeSpeed(LONG percent) {
    if (!SupportedSpeed(percent)) percent = 100;
    AcquireSRWLockExclusive(&clockLock);
    LARGE_INTEGER now{};
    const BOOL ok = realQpc(&now);
    if (ok) {
        virtualAnchor = ClockAt(now.QuadPart);
        realAnchor = now.QuadPart;
        speedPercent = percent;
    }
    ReleaseSRWLockExclusive(&clockLock);
    return ok != FALSE;
}

void PublishError(DWORD error) {
    if (!control) return;
    InterlockedExchange(&control->appliedPercent, 100);
    InterlockedExchange(&control->error, static_cast<LONG>(error));
    InterlockedExchange(&control->state, 3);
}

// Find only the main executable's import-by-name slot. Never enumerate, open,
// or alter another process, and never overwrite kernel32/ntdll function bytes.
void** FindQpcSlot(DWORD& error) {
    auto* base = reinterpret_cast<BYTE*>(GetModuleHandleW(nullptr));
    auto* dos = reinterpret_cast<IMAGE_DOS_HEADER*>(base);
    if (!base || dos->e_magic != IMAGE_DOS_SIGNATURE || dos->e_lfanew <= 0) {
        error = ERROR_BAD_EXE_FORMAT; return nullptr;
    }
    auto* nt = reinterpret_cast<IMAGE_NT_HEADERS64*>(base + dos->e_lfanew);
    if (nt->Signature != IMAGE_NT_SIGNATURE || nt->FileHeader.Machine != IMAGE_FILE_MACHINE_AMD64 ||
        nt->OptionalHeader.Magic != IMAGE_NT_OPTIONAL_HDR64_MAGIC) {
        error = ERROR_BAD_EXE_FORMAT; return nullptr;
    }
    const DWORD size = nt->OptionalHeader.SizeOfImage;
    const auto& imports = nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_IMPORT];
    if (!imports.VirtualAddress || imports.VirtualAddress >= size ||
        imports.Size < sizeof(IMAGE_IMPORT_DESCRIPTOR) || imports.Size > size - imports.VirtualAddress) {
        error = ERROR_PROC_NOT_FOUND; return nullptr;
    }
    auto* descriptors = reinterpret_cast<IMAGE_IMPORT_DESCRIPTOR*>(base + imports.VirtualAddress);
    const DWORD count = imports.Size / sizeof(IMAGE_IMPORT_DESCRIPTOR);
    for (DWORD i = 0; i < count && descriptors[i].Name; ++i) {
        const auto& desc = descriptors[i];
        if (!desc.OriginalFirstThunk || !desc.FirstThunk ||
            desc.OriginalFirstThunk >= size || desc.FirstThunk >= size) continue;
        auto* names = reinterpret_cast<IMAGE_THUNK_DATA64*>(base + desc.OriginalFirstThunk);
        auto* slots = reinterpret_cast<IMAGE_THUNK_DATA64*>(base + desc.FirstThunk);
        const DWORD maxNames = (size - desc.OriginalFirstThunk) / sizeof(IMAGE_THUNK_DATA64);
        const DWORD maxSlots = (size - desc.FirstThunk) / sizeof(IMAGE_THUNK_DATA64);
        for (DWORD j = 0; j < maxNames && j < maxSlots && names[j].u1.AddressOfData; ++j) {
            const ULONGLONG rva = names[j].u1.AddressOfData;
            if (IMAGE_SNAP_BY_ORDINAL64(rva) || rva >= size || size - rva < sizeof(IMAGE_IMPORT_BY_NAME)) continue;
            auto* name = reinterpret_cast<IMAGE_IMPORT_BY_NAME*>(base + static_cast<SIZE_T>(rva));
            const SIZE_T remaining = size - static_cast<SIZE_T>(rva) - offsetof(IMAGE_IMPORT_BY_NAME, Name);
            if (remaining >= sizeof("QueryPerformanceCounter") &&
                std::memcmp(name->Name, "QueryPerformanceCounter", sizeof("QueryPerformanceCounter")) == 0) {
                return reinterpret_cast<void**>(&slots[j].u1.Function);
            }
        }
    }
    error = ERROR_PROC_NOT_FOUND;
    return nullptr;
}

bool InstallHook(DWORD& error) {
    void** slot = FindQpcSlot(error);
    if (!slot) return false;
    if (reinterpret_cast<ULONG_PTR>(slot) % sizeof(void*) != 0) {
        error = ERROR_INVALID_ADDRESS; return false;
    }
    auto expected = reinterpret_cast<void*>(GetProcAddress(GetModuleHandleW(L"kernel32.dll"), "QueryPerformanceCounter"));
    if (!expected || *slot != expected) {
        // Refuse to chain an unknown hook.
        error = ERROR_INVALID_FUNCTION; return false;
    }
    realQpc = reinterpret_cast<QpcFunction>(expected);
    LARGE_INTEGER now{};
    if (!realQpc(&now)) { error = ERROR_GEN_FAILURE; return false; }
    realAnchor = virtualAnchor = lastVirtual = now.QuadPart;
    speedPercent = 100;
    DWORD oldProtection = 0;
    if (!VirtualProtect(slot, sizeof(void*), PAGE_READWRITE, &oldProtection)) {
        error = GetLastError(); return false;
    }
    void* observed = InterlockedCompareExchangePointer(slot, reinterpret_cast<void*>(&HookQpc), expected);
    DWORD ignored = 0;
    const BOOL restored = VirtualProtect(slot, sizeof(void*), oldProtection, &ignored);
    if (observed != expected) { error = ERROR_BUSY; return false; }
    if (!restored) { error = GetLastError(); return false; }
    return true;
}

#ifndef AZS_SELF_TEST
DWORD WINAPI Worker(void*) {
    wchar_t actualPath[kMaxPathChars]{};
    const DWORD actualChars = GetModuleFileNameW(nullptr, actualPath, ARRAYSIZE(actualPath));
    if (actualChars == 0 || actualChars >= kMaxPathChars ||
        !HasTargetBasename(actualPath, actualChars)) return ERROR_ACCESS_DENIED;
    wchar_t name[100]{};
    swprintf_s(name, L"Local\\AzerothSurvivorsSpeed_%lu", GetCurrentProcessId());
    const HANDLE mapping = OpenFileMappingW(FILE_MAP_ALL_ACCESS, FALSE, name);
    if (!mapping) return GetLastError();
    control = static_cast<ControlBlock*>(MapViewOfFile(mapping, FILE_MAP_ALL_ACCESS, 0, 0, sizeof(ControlBlock)));
    if (!control) { const DWORD error = GetLastError(); CloseHandle(mapping); return error; }
    if (control->magic != kMagic || control->version != kVersion) {
        PublishError(ERROR_REVISION_MISMATCH); return ERROR_REVISION_MISMATCH;
    }
    if (!PortableTargetMatches(actualPath, control->expectedPath, control->expectedPathChars)) {
        PublishError(ERROR_ACCESS_DENIED); return ERROR_ACCESS_DENIED;
    }
    DWORD error = 0;
    if (!InstallHook(error)) { PublishError(error); return error; }
    InterlockedExchange(&control->appliedPercent, 100);
    InterlockedExchange(&control->error, 0);
    InterlockedExchange(&control->state, 1);
    // Keep mapping, DLL, and hook resident until process exit. A controller can
    // reattach by reopening this PID-specific mapping and updating heartbeat.
    for (;;) {
        const auto heartbeat = static_cast<ULONGLONG>(ReadLong64(&control->heartbeat));
        const bool fresh = FreshHeartbeat(GetTickCount64(), heartbeat) && ReadLong(&control->controllerPid) != 0;
        LONG wanted = fresh ? ReadLong(&control->requestedPercent) : 100;
        if (!SupportedSpeed(wanted)) wanted = 100;
        if (wanted != speedPercent && !ChangeSpeed(wanted)) {
            ChangeSpeed(100);
            PublishError(ERROR_GEN_FAILURE);
            return ERROR_GEN_FAILURE;
        }
        InterlockedExchange(&control->appliedPercent, wanted);
        InterlockedExchange(&control->state, fresh ? 1 : 2);
        Sleep(25);
    }
}
#endif
} // namespace

#ifndef AZS_SELF_TEST
BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID) {
    (void)instance;
    if (reason == DLL_PROCESS_ATTACH) {
        // Keep thread notifications enabled for the statically linked CRT.
        const HANDLE thread = CreateThread(nullptr, 0, Worker, nullptr, 0, nullptr);
        if (!thread) return FALSE;
        CloseHandle(thread);
    }
    return TRUE;
}
#else
#include <cstdio>
#include <cmath>
namespace {
LONGLONG fakeTicks = 0;
BOOL WINAPI FakeQpc(LARGE_INTEGER* out) { out->QuadPart = fakeTicks; return TRUE; }
void ResetFake(LONGLONG ticks = 1000000) {
    realQpc = FakeQpc;
    fakeTicks = ticks;
    realAnchor = virtualAnchor = lastVirtual = ticks;
    speedPercent = 100;
}
int failures = 0;
void Check(bool ok, const char* description) {
    std::printf("%s %s\n", ok ? "PASS" : "FAIL", description);
    if (!ok) ++failures;
}
void TestPortableGuards() {
    const wchar_t* asciiPath = L"C:\\Games\\AzerothSurvivors.exe";
    const wchar_t* spacedPath = L"D:\\Game Library\\Azeroth Survivors\\AzerothSurvivors.exe";
    const wchar_t* unicodePath = L"E:\\Games\\\u6e38\u620f \u00d6\\AzerothSurvivors.exe";
    const wchar_t* unicodeCasePath = L"e:\\games\\\u6e38\u620f \u00f6\\azerothsurvivors.EXE";
    const wchar_t* userPathOne = L"C:\\Users\\PlayerOne\\Games\\AzerothSurvivors.exe";
    const wchar_t* userPathTwo = L"F:\\Users\\PlayerTwo\\Downloads\\AzerothSurvivors.exe";
    const wchar_t* uncPath = L"\\\\fileserver\\Game Library\\AzerothSurvivors.exe";
    for (const wchar_t* path : {asciiPath, spacedPath, unicodePath, userPathOne, userPathTwo, uncPath}) {
        Check(PortableTargetMatches(path, path, static_cast<DWORD>(wcslen(path))),
            "portable guard accepts matching normal absolute target path");
    }
    Check(PortableTargetMatches(unicodePath, unicodeCasePath, static_cast<DWORD>(wcslen(unicodeCasePath))),
        "portable guard compares Unicode and ASCII casing ordinally");
    Check(!PortableTargetMatches(asciiPath, L"D:\\Games\\AzerothSurvivors.exe", static_cast<DWORD>(wcslen(asciiPath))),
        "portable guard rejects a different drive");
    Check(!PortableTargetMatches(userPathOne, userPathTwo, static_cast<DWORD>(wcslen(userPathTwo))),
        "portable guard rejects a different selected user path");
    const wchar_t* otherFolder = L"C:\\Other Games\\AzerothSurvivors.exe";
    Check(!PortableTargetMatches(asciiPath, otherFolder, static_cast<DWORD>(wcslen(otherFolder))),
        "portable guard rejects an unselected target location");
    for (const wchar_t* bad : {
        L"C:\\Games\\OtherGame.exe", L"C:\\Games\\AzerothSurvivors.exe.bak",
        L"AzerothSurvivors.exe", L"Games\\AzerothSurvivors.exe", L"C:AzerothSurvivors.exe",
        L"\\Games\\AzerothSurvivors.exe", L"\\\\?\\C:\\Games\\AzerothSurvivors.exe",
        L"\\\\.\\C:\\Games\\AzerothSurvivors.exe", L"C:\\Games\\..\\AzerothSurvivors.exe",
        L"C:\\Games\\.\\AzerothSurvivors.exe", L"C:\\Games\\\\AzerothSurvivors.exe",
        L"C:/Games/AzerothSurvivors.exe", L"\\\\fileserver\\AzerothSurvivors.exe"}) {
        Check(!PortableTargetMatches(bad, bad, static_cast<DWORD>(wcslen(bad))),
            "portable guard rejects wrong executable or non-normal path");
    }
    Check(!PortableTargetMatches(asciiPath, L"", 0), "portable guard rejects empty expected path");
    Check(!PortableTargetMatches(L"", asciiPath, static_cast<DWORD>(wcslen(asciiPath))),
        "portable guard rejects empty actual path");
    Check(!PortableTargetMatches(asciiPath, asciiPath, static_cast<DWORD>(wcslen(asciiPath) - 1)),
        "portable guard rejects a mismatched path count");
    Check(!PortableTargetMatches(asciiPath, asciiPath, kMaxPathChars),
        "portable guard rejects oversized path count");
    wchar_t unterminated[kMaxPathChars];
    for (auto& c : unterminated) c = L'X';
    Check(!PortableTargetMatches(asciiPath, unterminated, kMaxPathChars - 1),
        "portable guard rejects nonterminated expected path");
    Check(!PortableTargetMatches(unterminated, asciiPath, static_cast<DWORD>(wcslen(asciiPath))),
        "portable guard rejects nonterminated actual path");
}
}
int main() {
    TestPortableGuards();
    for (LONG percent : {100, 150, 200, 300, 500}) {
        ResetFake();
        Check(ChangeSpeed(percent), "set supported multiplier");
        fakeTicks += 100000;
        LARGE_INTEGER value{};
        HookQpc(&value);
        Check(value.QuadPart == 1000000 + 1000 * percent, "exact deterministic timing ratio");
        const LONGLONG before = value.QuadPart;
        Check(ChangeSpeed(100), "restore normal multiplier");
        HookQpc(&value);
        Check(value.QuadPart == before, "speed change preserves time continuity");
        fakeTicks += 1000;
        HookQpc(&value);
        Check(value.QuadPart == before + 1000, "restored clock advances at 1x");
    }
    ResetFake();
    LARGE_INTEGER value{};
    fakeTicks += 1000; HookQpc(&value);
    const LONGLONG last = value.QuadPart;
    fakeTicks -= 500; HookQpc(&value);
    Check(value.QuadPart == last, "backward source sample remains monotonic");
    ChangeSpeed(42);
    Check(speedPercent == 100, "unsupported multiplier falls back to 1x");
    Check(FreshHeartbeat(5000, 3000), "heartbeat valid at timeout boundary");
    Check(!FreshHeartbeat(5001, 3000), "expired heartbeat rejected");
    Check(!FreshHeartbeat(5000, 0), "zero heartbeat rejected");
    Check(!FreshHeartbeat(5000, 5001), "future heartbeat rejected");
    ResetFake();
    ChangeSpeed(300); fakeTicks += 10000;
    HookQpc(&value);
    const LONGLONG beforeExpiry = value.QuadPart;
    if (!FreshHeartbeat(5001, 3000)) ChangeSpeed(100);
    fakeTicks += 1000; HookQpc(&value);
    Check(speedPercent == 100 && value.QuadPart == beforeExpiry + 1000, "expired heartbeat resets to continuous 1x");
    // Check real performance-counter ratios as well, without hooking a process.
    realQpc = QueryPerformanceCounter;
    LARGE_INTEGER frequency{};
    QueryPerformanceFrequency(&frequency);
    for (LONG percent : {100, 150, 200, 300, 500}) {
        LARGE_INTEGER start{}; realQpc(&start);
        realAnchor = virtualAnchor = lastVirtual = start.QuadPart;
        speedPercent = percent;
        LARGE_INTEGER virtualStart{}, virtualEnd{}, realStart{}, realEnd{};
        HookQpc(&virtualStart); realQpc(&realStart);
        Sleep(120);
        realQpc(&realEnd); HookQpc(&virtualEnd);
        const double ratio = double(virtualEnd.QuadPart - virtualStart.QuadPart) / double(realEnd.QuadPart - realStart.QuadPart);
        std::printf("Measured %ld%%: %.5fx\n", percent, ratio);
        Check(std::abs(ratio - percent / 100.0) < 0.03, "live QPC ratio within tolerance");
    }

    // Integration test: install the actual PE64 IAT hook into this dedicated
    // test executable only. The saved realQpc function pointer bypasses its IAT;
    // direct QueryPerformanceCounter calls below go through the patched slot.
    ControlBlock testControl{};
    control = &testControl;
    DWORD installError = 0;
    const bool installed = InstallHook(installError);
    std::printf("IAT installation Win32 error: %lu\n", installError);
    Check(installed, "install QPC hook into this test executable's IAT");
    if (installed) {
        for (LONG percent : {100, 150, 200, 300, 500}) {
            LARGE_INTEGER before{}, after{}, realStart{}, realEnd{}, virtualStart{}, virtualEnd{};
            QueryPerformanceCounter(&before);
            Check(ChangeSpeed(percent), "apply multiplier to installed IAT hook");
            QueryPerformanceCounter(&after);
            Check(after.QuadPart >= before.QuadPart &&
                  after.QuadPart - before.QuadPart < frequency.QuadPart / 10,
                  "IAT hook multiplier transition stays continuous");
            QueryPerformanceCounter(&virtualStart); realQpc(&realStart);
            Sleep(120);
            realQpc(&realEnd); QueryPerformanceCounter(&virtualEnd);
            const double ratio = double(virtualEnd.QuadPart - virtualStart.QuadPart) /
                                 double(realEnd.QuadPart - realStart.QuadPart);
            std::printf("Actual imported QPC %ld%%: %.5fx\n", percent, ratio);
            Check(std::abs(ratio - percent / 100.0) < 0.03, "installed IAT hook ratio within tolerance");
        }
        LARGE_INTEGER beforeReset{}, afterReset{};
        QueryPerformanceCounter(&beforeReset);
        if (!FreshHeartbeat(5001, 3000)) ChangeSpeed(100);
        QueryPerformanceCounter(&afterReset);
        Check(afterReset.QuadPart >= beforeReset.QuadPart &&
              afterReset.QuadPart - beforeReset.QuadPart < frequency.QuadPart / 10,
              "installed IAT hook watchdog reset stays continuous");
        LARGE_INTEGER realStart{}, realEnd{}, virtualStart{}, virtualEnd{};
        QueryPerformanceCounter(&virtualStart); realQpc(&realStart);
        Sleep(120);
        realQpc(&realEnd); QueryPerformanceCounter(&virtualEnd);
        const double ratio = double(virtualEnd.QuadPart - virtualStart.QuadPart) /
                             double(realEnd.QuadPart - realStart.QuadPart);
        Check(std::abs(ratio - 1.0) < 0.03, "installed IAT hook reset restores measured 1x");
        Check(ReadLong64(&testControl.hookCalls) > 0, "imported QPC calls reached actual hook");
    }
    return failures ? 1 : 0;
}
#endif
