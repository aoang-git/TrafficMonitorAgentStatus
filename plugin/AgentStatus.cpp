#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <sddl.h>
#include <shellapi.h>
#include <string>
#include <cstring>
#pragma warning(push)
#pragma warning(disable:4100)
#include "PluginInterface.h"
#pragma warning(pop)

namespace {
HMODULE moduleHandle;
SRWLOCK cacheLock = SRWLOCK_INIT;
std::wstring value;
std::wstring tooltip = L"显示桥接未连接";
SRWLOCK launchLock = SRWLOCK_INIT;
HANDLE pluginStopEvent = nullptr;
HANDLE bridgeProcess = nullptr;
ULONGLONG lastLaunch = 0;

std::wstring BridgePath() {
    wchar_t path[32768]; DWORD n = GetModuleFileNameW(moduleHandle, path, 32768);
    if (!n || n == 32768) return L"";
    std::wstring exe(path); exe.resize(exe.find_last_of(L"\\/") + 1);
    return exe + L"AgentStatus\\AgentStatusBridge.exe";
}

// Called by normal host callbacks, never under DllMain's loader lock.
// Only two read-only lifetime handles are inherited; no waiting or network I/O.
void EnsureBridge() {
    if (!TryAcquireSRWLockExclusive(&launchLock)) return;
    try {
        if (bridgeProcess) {
            if (WaitForSingleObject(bridgeProcess, 0) == WAIT_TIMEOUT) { ReleaseSRWLockExclusive(&launchLock); return; }
            CloseHandle(bridgeProcess); bridgeProcess = nullptr;
        }
        auto now = GetTickCount64();
        if (lastLaunch && now - lastLaunch < 5000) { ReleaseSRWLockExclusive(&launchLock); return; }
        lastLaunch = now;
        auto exe = BridgePath();
        if (!exe.empty() && GetFileAttributesW(exe.c_str()) != INVALID_FILE_ATTRIBUTES) {
            if (!pluginStopEvent) pluginStopEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
            HANDLE host = OpenProcess(SYNCHRONIZE, TRUE, GetCurrentProcessId()), stop = nullptr;
            if (host && pluginStopEvent && DuplicateHandle(GetCurrentProcess(), pluginStopEvent, GetCurrentProcess(), &stop, SYNCHRONIZE, TRUE, 0)) {
                SIZE_T bytes = 0;
                InitializeProcThreadAttributeList(nullptr, 1, 0, &bytes);
                auto attributes = static_cast<LPPROC_THREAD_ATTRIBUTE_LIST>(HeapAlloc(GetProcessHeap(), 0, bytes));
                if (attributes) {
                    if (InitializeProcThreadAttributeList(attributes, 1, 0, &bytes)) {
                        HANDLE handles[] = { host, stop };
                        if (UpdateProcThreadAttribute(attributes, 0, PROC_THREAD_ATTRIBUTE_HANDLE_LIST, handles, sizeof(handles), nullptr, nullptr)) {
                            STARTUPINFOEXW startup{}; startup.StartupInfo.cb = sizeof(startup);
                            startup.StartupInfo.dwFlags = STARTF_USESHOWWINDOW; startup.StartupInfo.wShowWindow = SW_HIDE;
                            startup.lpAttributeList = attributes;
                            PROCESS_INFORMATION process{};
                            auto command = L"\"" + exe + L"\" serve --host-process-handle " + std::to_wstring(reinterpret_cast<ULONG_PTR>(host))
                                + L" --plugin-stop-handle " + std::to_wstring(reinterpret_cast<ULONG_PTR>(stop));
                            auto directory = exe.substr(0, exe.find_last_of(L"\\/"));
                            if (CreateProcessW(exe.c_str(), command.data(), nullptr, nullptr, TRUE,
                                EXTENDED_STARTUPINFO_PRESENT | CREATE_NO_WINDOW, nullptr, directory.c_str(), &startup.StartupInfo, &process)) {
                                bridgeProcess = process.hProcess; CloseHandle(process.hThread);
                            }
                        }
                        DeleteProcThreadAttributeList(attributes);
                    }
                    HeapFree(GetProcessHeap(), 0, attributes);
                }
            }
            if (host) CloseHandle(host);
            if (stop) CloseHandle(stop);
        }
    } catch (...) { }
    ReleaseSRWLockExclusive(&launchLock);
}

std::wstring MappingName() {
    HANDLE token = nullptr;
    if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &token)) return L"";
    BYTE bytes[512]; DWORD required = 0;
    bool ok = GetTokenInformation(token, TokenUser, bytes, sizeof(bytes), &required) != 0;
    CloseHandle(token);
    if (!ok) return L"";
    LPWSTR sid = nullptr;
    if (!ConvertSidToStringSidW(reinterpret_cast<TOKEN_USER*>(bytes)->User.Sid, &sid)) return L"";
    DWORD session = 0; ProcessIdToSessionId(GetCurrentProcessId(), &session);
    std::wstring result = L"Local\\TMAS-v1-" + std::wstring(sid) + L"-" + std::to_wstring(session);
    LocalFree(sid);
    wchar_t instance[34];
    DWORD length = GetEnvironmentVariableW(L"TMAS_INSTANCE", instance, 34);
    if (length > 32) return L"";
    if (length) {
        for (DWORD i = 0; i < length; ++i)
            if (!((instance[i] >= L'0' && instance[i] <= L'9') || (instance[i] >= L'A' && instance[i] <= L'Z') || (instance[i] >= L'a' && instance[i] <= L'z') || instance[i] == L'-' || instance[i] == L'_')) return L"";
        result += L"-" + std::wstring(instance);
    }
    return result;
}

void OpenDetails(HWND parent) {
    auto exe = BridgePath(); if (exe.empty()) return;
    if (reinterpret_cast<INT_PTR>(ShellExecuteW(parent, L"open", exe.c_str(), L"details", nullptr, SW_SHOWNORMAL)) <= 32)
        MessageBoxW(parent, L"缺少连接组件，请将 AgentStatus.dll 和完整 AgentStatus 文件夹一起复制到插件目录。", L"Agent 状态", MB_OK | MB_ICONINFORMATION);
}

const wchar_t* Cached(bool tip) {
    // Each reader owns its returned text until its next call on the same thread.
    thread_local std::wstring textValue, textTip;
    auto& out = tip ? textTip : textValue;
    AcquireSRWLockShared(&cacheLock);
    try { out = tip ? tooltip : value; }
    catch (...) { ReleaseSRWLockShared(&cacheLock); return L"状态不可用"; }
    ReleaseSRWLockShared(&cacheLock); return out.c_str();
}

class Item final : public IPluginItem {
public:
    const wchar_t* GetItemName() const override { return L"Agent 状态"; }
    const wchar_t* GetItemId() const override { return L"TMASAgentStatusV1"; }
    const wchar_t* GetItemLableText() const override { return L""; }
    const wchar_t* GetItemValueText() const override { return Cached(false); }
    const wchar_t* GetItemValueSampleText() const override { return L"Claude:调用工具"; }
    int OnMouseEvent(MouseEventType type, int, int, void* window, int) override {
        if (type == MT_LCLICKED) { OpenDetails(static_cast<HWND>(window)); return 1; }
        return 0;
    }
};

class Plugin final : public ITMPlugin {
    Item item;
public:
    IPluginItem* GetItem(int index) override { return index == 0 ? &item : nullptr; }
    void DataRequired() override {
        try {
            std::wstring next, tip = L"正在自动连接状态服务";
            bool healthy = false;
            auto name = MappingName();
            HANDLE map = name.empty() ? nullptr : OpenFileMappingW(FILE_MAP_READ, FALSE, name.c_str());
            if (map) {
                auto p = static_cast<const BYTE*>(MapViewOfFile(map, FILE_MAP_READ, 0, 0, 32768));
                if (p) {
                    // No worker thread or waiting inside the host. Bounded attempts only.
                    for (int attempt = 0; attempt < 3; ++attempt) {
                        auto sequence = reinterpret_cast<volatile const LONG*>(p);
                        LONG before = *sequence; if (before & 1) continue;
                        MemoryBarrier();
                        DWORD magic; ULONGLONG tick;
                        wchar_t text[128], details[4096];
                        memcpy(&magic, p + 4, 4); memcpy(&tick, p + 8, 8);
                        memcpy(text, p + 16, sizeof(text)); memcpy(details, p + 272, sizeof(details));
                        MemoryBarrier();
                        if (before != *sequence) continue;
                        text[127] = 0; details[4095] = 0;
                        auto now = GetTickCount64();
                        if (magic == 0x53414d54 && tick <= now && now - tick < 3000) { next = text; tip = details; healthy = true; }
                        break;
                    }
                    UnmapViewOfFile(p);
                }
                CloseHandle(map);
            }
            AcquireSRWLockExclusive(&cacheLock);
            value.swap(next); tooltip.swap(tip);
            ReleaseSRWLockExclusive(&cacheLock);
            if (!healthy) EnsureBridge();
        } catch (...) { /* Preserve last usable cache on allocation failure. */ }
    }
    const wchar_t* GetTooltipInfo() override { return Cached(true); }
    const wchar_t* GetInfo(PluginInfoIndex index) override {
        switch (index) {
        case TMI_NAME: return L"Agent 状态";
        case TMI_DESCRIPTION: return L"订阅 Agent Status Runtime 显示本机 Agent 状态";
        case TMI_AUTHOR: return L"TrafficMonitorAgentStatus";
        case TMI_COPYRIGHT: return L"2026 TrafficMonitorAgentStatus contributors";
        case TMI_VERSION: return L"0.2.5";
        case TMI_URL: return L"";
        default: return L"";
        }
    }
    OptionReturn ShowOptionsDialog(void* parent) override { OpenDetails(static_cast<HWND>(parent)); return OR_OPTION_UNCHANGED; }
};
Plugin plugin;
}

extern "C" __declspec(dllexport) ITMPlugin* TMPluginGetInstance() { plugin.DataRequired(); return &plugin; }
BOOL APIENTRY DllMain(HMODULE instance, DWORD reason, LPVOID) {
    if (reason == DLL_PROCESS_ATTACH) moduleHandle = instance;
    if (reason == DLL_PROCESS_DETACH) {
        if (pluginStopEvent) { SetEvent(pluginStopEvent); CloseHandle(pluginStopEvent); }
        if (bridgeProcess) CloseHandle(bridgeProcess);
    }
    return TRUE;
}
