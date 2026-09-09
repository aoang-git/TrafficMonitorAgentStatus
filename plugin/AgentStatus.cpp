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
std::wstring tooltip = L"无活动 Agent";

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
    LocalFree(sid); return result;
}

void OpenDetails(HWND parent) {
    wchar_t path[32768]; DWORD n = GetModuleFileNameW(moduleHandle, path, 32768);
    if (!n || n == 32768) return;
    std::wstring exe(path); exe.resize(exe.find_last_of(L"\\/") + 1);
    // Installer places the standalone bridge next to this plugin.
    exe += L"AgentStatus\\AgentStatusBridge.exe";
    if (reinterpret_cast<INT_PTR>(ShellExecuteW(parent, L"open", exe.c_str(), L"details", nullptr, SW_SHOWNORMAL)) <= 32)
        MessageBoxW(parent, L"未找到接收器。请使用安装脚本将插件和 AgentStatus 子目录一并安装。", L"Agent 状态", MB_OK | MB_ICONINFORMATION);
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
    const wchar_t* GetItemValueSampleText() const override { return L"Claudecode:调用工具"; }
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
            std::wstring next, tip = L"无活动 Agent";
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
                        if (magic == 0x53414d54 && tick <= now && now - tick < 3000) { next = text; tip = details; }
                        break;
                    }
                    UnmapViewOfFile(p);
                }
                CloseHandle(map);
            }
            AcquireSRWLockExclusive(&cacheLock);
            value.swap(next); tooltip.swap(tip);
            ReleaseSRWLockExclusive(&cacheLock);
        } catch (...) { /* Preserve last usable cache on allocation failure. */ }
    }
    const wchar_t* GetTooltipInfo() override { return Cached(true); }
    const wchar_t* GetInfo(PluginInfoIndex index) override {
        switch (index) {
        case TMI_NAME: return L"Agent 状态";
        case TMI_DESCRIPTION: return L"通过本机桥接显示 Agent 工作、工具调用与等待状态";
        case TMI_AUTHOR: return L"TrafficMonitorAgentStatus";
        case TMI_COPYRIGHT: return L"2026 TrafficMonitorAgentStatus contributors";
        case TMI_VERSION: return L"0.1.0";
        case TMI_URL: return L"";
        default: return L"";
        }
    }
    OptionReturn ShowOptionsDialog(void* parent) override { OpenDetails(static_cast<HWND>(parent)); return OR_OPTION_UNCHANGED; }
};
Plugin plugin;
}

extern "C" __declspec(dllexport) ITMPlugin* TMPluginGetInstance() { return &plugin; }
BOOL APIENTRY DllMain(HMODULE instance, DWORD reason, LPVOID) {
    if (reason == DLL_PROCESS_ATTACH) moduleHandle = instance;
    return TRUE;
}
