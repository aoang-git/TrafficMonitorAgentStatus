#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <sddl.h>
#include <shellapi.h>
#include <string>
#include <vector>

std::wstring PipeName() {
    HANDLE token; if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &token)) return {};
    BYTE data[512]; DWORD n; BOOL ok = GetTokenInformation(token, TokenUser, data, sizeof(data), &n); CloseHandle(token);
    if (!ok) return {};
    LPWSTR sid; if (!ConvertSidToStringSidW(reinterpret_cast<TOKEN_USER*>(data)->User.Sid, &sid)) return {};
    DWORD session; ProcessIdToSessionId(GetCurrentProcessId(), &session);
    auto result = L"\\\\.\\pipe\\TMAS-v1-" + std::wstring(sid) + L"-" + std::to_wstring(session); LocalFree(sid); return result;
}

bool Transfer(HANDLE pipe, void* bytes, DWORD size, bool write, ULONGLONG deadline) {
    auto p = static_cast<BYTE*>(bytes);
    while (size) {
        OVERLAPPED ov{}; ov.hEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr); if (!ov.hEvent) return false;
        DWORD count = 0;
        BOOL ok = write ? WriteFile(pipe, p, size, &count, &ov) : ReadFile(pipe, p, size, &count, &ov);
        if (!ok && GetLastError() == ERROR_IO_PENDING) {
            auto now = GetTickCount64(); DWORD budget = now < deadline ? static_cast<DWORD>(deadline - now) : 0;
            ok = GetOverlappedResultEx(pipe, &ov, &count, budget, FALSE);
            if (!ok) { CancelIoEx(pipe, &ov); GetOverlappedResult(pipe, &ov, &count, TRUE); }
        }
        CloseHandle(ov.hEvent);
        if (!ok || count == 0) return false;
        p += count; size -= count;
    }
    return true;
}

void StartBridge() {
    wchar_t path[32768]; DWORD n = GetModuleFileNameW(nullptr, path, 32768); if (!n || n == 32768) return;
    std::wstring exe(path); exe.resize(exe.find_last_of(L"\\/") + 1); exe += L"AgentStatusBridge.exe";
    std::wstring command = L"\"" + exe + L"\" serve";
    STARTUPINFOW si{}; si.cb = sizeof(si); PROCESS_INFORMATION pi{};
    if (CreateProcessW(exe.c_str(), command.data(), nullptr, nullptr, FALSE, CREATE_NO_WINDOW, nullptr, nullptr, &si, &pi)) { CloseHandle(pi.hThread); CloseHandle(pi.hProcess); }
}

int WINAPI wWinMain(HINSTANCE, HINSTANCE, LPWSTR, int) {
    bool strict = false;
    try {
        int argc; auto argv = CommandLineToArgvW(GetCommandLineW(), &argc);
        std::string source = "generic", event;
        for (int i = 1; i < argc; ++i) {
            if (std::wstring(argv[i]) == L"--strict") strict = true;
            if ((std::wstring(argv[i]) == L"--source" || std::wstring(argv[i]) == L"--event") && i + 1 < argc) {
                bool isSource = std::wstring(argv[i]) == L"--source";
                std::wstring value(argv[++i]); std::string safe;
                for (auto c : value) { if (!((c >= L'a' && c <= L'z') || (c >= L'A' && c <= L'Z') || c == L'-' || c == L'_')) { LocalFree(argv); return strict ? 1 : 0; } safe += static_cast<char>(c); }
                (isSource ? source : event) = safe;
            }
        }
        LocalFree(argv);
        std::string raw; char chunk[4096]; DWORD n;
        while (ReadFile(GetStdHandle(STD_INPUT_HANDLE), chunk, sizeof(chunk), &n, nullptr) && n) { raw.append(chunk, n); if (raw.size() > 60000) return strict ? 1 : 0; }
        if (raw.empty()) return strict ? 1 : 0;
        std::string payload = "{\"op\":\"hook\",\"source\":\"" + source + "\",\"event_override\":\"" + event + "\",\"raw\":" + raw + "}";
        auto name = PipeName(); if (name.empty()) return strict ? 1 : 0;
        HANDLE pipe = CreateFileW(name.c_str(), GENERIC_READ | GENERIC_WRITE, 0, nullptr, OPEN_EXISTING, FILE_FLAG_OVERLAPPED | SECURITY_SQOS_PRESENT | SECURITY_IDENTIFICATION, nullptr);
        if (pipe == INVALID_HANDLE_VALUE) {
            StartBridge(); auto end = GetTickCount64() + 1500;
            do { WaitNamedPipeW(name.c_str(), 30); Sleep(10); pipe = CreateFileW(name.c_str(), GENERIC_READ | GENERIC_WRITE, 0, nullptr, OPEN_EXISTING, FILE_FLAG_OVERLAPPED | SECURITY_SQOS_PRESENT | SECURITY_IDENTIFICATION, nullptr); } while (pipe == INVALID_HANDLE_VALUE && GetTickCount64() < end);
        }
        if (pipe == INVALID_HANDLE_VALUE) return strict ? 1 : 0;
        auto deadline = GetTickCount64() + 500;
        DWORD length = static_cast<DWORD>(payload.size());
        bool ok = Transfer(pipe, &length, 4, true, deadline) && Transfer(pipe, payload.data(), length, true, deadline);
        DWORD replyLength = 0;
        ok = ok && Transfer(pipe, &replyLength, 4, false, deadline) && replyLength > 0 && replyLength < 4096;
        std::string reply(ok ? replyLength : 0, '\0');
        ok = ok && Transfer(pipe, reply.data(), replyLength, false, deadline) && reply.find("\"ok\":true") != std::string::npos;
        CloseHandle(pipe);
        return strict && !ok ? 1 : 0;
    } catch (...) { return strict ? 1 : 0; }
}
