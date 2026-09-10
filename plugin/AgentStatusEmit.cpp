#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <sddl.h>
#include <shellapi.h>
#include <string>
#include <vector>
#include <cstdio>

namespace {
constexpr ULONGLONG DiagnosticLimit = 1024 * 1024;

std::string NarrowAscii(const std::wstring& value) {
    std::string result;
    result.reserve(value.size());
    for (auto c : value) result += c >= 32 && c <= 126 ? static_cast<char>(c) : '?';
    return result;
}

void HookResponse() {
    const char response[] = "{}\n";
    DWORD written = 0;
    auto output = GetStdHandle(STD_OUTPUT_HANDLE);
    if (output && output != INVALID_HANDLE_VALUE) WriteFile(output, response, sizeof(response) - 1, &written, nullptr);
}

void Diagnostic(const std::string& source, const std::string& event, const std::wstring& sid,
    DWORD session, const char* stage, DWORD initialError, DWORD finalError, size_t bytes, ULONGLONG started,
    const char* encoding = "unknown") {
    wchar_t local[32768];
    auto count = GetEnvironmentVariableW(L"LOCALAPPDATA", local, static_cast<DWORD>(std::size(local)));
    if (!count || count >= std::size(local)) return;
    std::wstring directory(local, count); directory += L"\\TrafficMonitorAgentStatus";
    CreateDirectoryW(directory.c_str(), nullptr);
    auto path = directory + L"\\transport.log";
    HANDLE file = CreateFileW(path.c_str(), FILE_APPEND_DATA | FILE_READ_ATTRIBUTES,
        FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE) return;
    LARGE_INTEGER size{};
    if (GetFileSizeEx(file, &size) && size.QuadPart > DiagnosticLimit) {
        CloseHandle(file);
        file = CreateFileW(path.c_str(), GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
            nullptr, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (file == INVALID_HANDLE_VALUE) return;
    }
    SYSTEMTIME time{}; GetSystemTime(&time);
    auto sidText = NarrowAscii(sid);
    char line[1024];
    int length = std::snprintf(line, sizeof(line),
        "%04u-%02u-%02uT%02u:%02u:%02u.%03uZ source=%s event=%s pid=%lu session=%lu sid=%s stage=%s encoding=%s initial_error=%lu final_error=%lu bytes=%zu elapsed_ms=%llu\r\n",
        time.wYear, time.wMonth, time.wDay, time.wHour, time.wMinute, time.wSecond, time.wMilliseconds,
        source.c_str(), event.empty() ? "payload" : event.c_str(), GetCurrentProcessId(), session, sidText.c_str(),
        stage, encoding, initialError, finalError, bytes, GetTickCount64() - started);
    if (length > 0) {
        DWORD written = 0;
        WriteFile(file, line, static_cast<DWORD>(length < static_cast<int>(sizeof(line)) ? length : sizeof(line) - 1), &written, nullptr);
    }
    CloseHandle(file);
}

std::wstring PipeName(std::wstring& sidText, DWORD& session, DWORD& error) {
    HANDLE token = nullptr;
    if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &token)) { error = GetLastError(); return {}; }
    BYTE data[512]; DWORD n = 0; BOOL ok = GetTokenInformation(token, TokenUser, data, sizeof(data), &n); CloseHandle(token);
    if (!ok) { error = GetLastError(); return {}; }
    LPWSTR sid; if (!ConvertSidToStringSidW(reinterpret_cast<TOKEN_USER*>(data)->User.Sid, &sid)) return {};
    sidText = sid; LocalFree(sid);
    if (!ProcessIdToSessionId(GetCurrentProcessId(), &session)) { error = GetLastError(); return {}; }
    error = ERROR_SUCCESS;
    return L"\\\\.\\pipe\\TMAS-v1-" + sidText + L"-" + std::to_wstring(session);
}

bool Transfer(HANDLE pipe, void* bytes, DWORD size, bool write, ULONGLONG deadline, DWORD& error) {
    auto p = static_cast<BYTE*>(bytes);
    while (size) {
        OVERLAPPED ov{}; ov.hEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr); if (!ov.hEvent) { error = GetLastError(); return false; }
        DWORD count = 0;
        BOOL ok = write ? WriteFile(pipe, p, size, &count, &ov) : ReadFile(pipe, p, size, &count, &ov);
        if (!ok && GetLastError() == ERROR_IO_PENDING) {
            auto now = GetTickCount64(); DWORD budget = now < deadline ? static_cast<DWORD>(deadline - now) : 0;
            ok = GetOverlappedResultEx(pipe, &ov, &count, budget, FALSE);
            if (!ok) { error = GetLastError(); CancelIoEx(pipe, &ov); GetOverlappedResult(pipe, &ov, &count, TRUE); }
        }
        else if (!ok) error = GetLastError();
        CloseHandle(ov.hEvent);
        if (!ok || count == 0) { if (ok) error = ERROR_BROKEN_PIPE; return false; }
        p += count; size -= count;
    }
    error = ERROR_SUCCESS;
    return true;
}

void StartBridge() {
    wchar_t path[32768]; DWORD n = GetModuleFileNameW(nullptr, path, 32768); if (!n || n == 32768) return;
    std::wstring exe(path); exe.resize(exe.find_last_of(L"\\/") + 1); exe += L"AgentStatusBridge.exe";
    std::wstring command = L"\"" + exe + L"\" serve";
    STARTUPINFOW si{}; si.cb = sizeof(si); PROCESS_INFORMATION pi{};
    if (CreateProcessW(exe.c_str(), command.data(), nullptr, nullptr, FALSE, CREATE_NO_WINDOW, nullptr, nullptr, &si, &pi)) { CloseHandle(pi.hThread); CloseHandle(pi.hProcess); }
}

}

int WINAPI wWinMain(HINSTANCE, HINSTANCE, LPWSTR, int) {
    bool strict = false;
    bool responded = false;
    std::string source = "generic", event;
    std::wstring sid;
    DWORD session = 0, identityError = ERROR_SUCCESS;
    auto started = GetTickCount64();
    try {
        int argc; auto argv = CommandLineToArgvW(GetCommandLineW(), &argc);
        if (!argv) { HookResponse(); responded = true; Diagnostic(source, event, sid, session, "argv", GetLastError(), GetLastError(), 0, started); return strict ? 1 : 0; }
        for (int i = 1; i < argc; ++i) {
            if (std::wstring(argv[i]) == L"--strict") strict = true;
            if ((std::wstring(argv[i]) == L"--source" || std::wstring(argv[i]) == L"--event") && i + 1 < argc) {
                bool isSource = std::wstring(argv[i]) == L"--source";
                std::wstring value(argv[++i]); std::string safe;
                for (auto c : value) { if (!((c >= L'a' && c <= L'z') || (c >= L'A' && c <= L'Z') || c == L'-' || c == L'_') || safe.size() >= 64) { LocalFree(argv); HookResponse(); responded = true; Diagnostic(source, event, sid, session, "argument", ERROR_INVALID_PARAMETER, ERROR_INVALID_PARAMETER, 0, started); return strict ? 1 : 0; } safe += static_cast<char>(c); }
                (isSource ? source : event) = safe;
            }
        }
        LocalFree(argv);
        HookResponse();
        responded = true;
        std::string raw; char chunk[4096]; DWORD n;
        while (ReadFile(GetStdHandle(STD_INPUT_HANDLE), chunk, sizeof(chunk), &n, nullptr) && n) {
            raw.append(chunk, n);
            if (raw.size() > 60000) { Diagnostic(source, event, sid, session, "input_too_large", ERROR_FILE_TOO_LARGE, ERROR_FILE_TOO_LARGE, raw.size(), started); return strict ? 1 : 0; }
        }
        if (raw.empty()) { Diagnostic(source, event, sid, session, "input_empty", ERROR_INVALID_DATA, ERROR_INVALID_DATA, 0, started); return strict ? 1 : 0; }
        auto inputBytes = raw.size();
        const char* inputEncoding = "utf8";
        // Cursor 3.18 on Windows prefixes hook stdin with an UTF-8 BOM. The raw
        // object is embedded inside our envelope, where an interior BOM is not
        // legal JSON and System.Text.Json rejects the entire delivery.
        if (raw.size() >= 3 && static_cast<unsigned char>(raw[0]) == 0xef
            && static_cast<unsigned char>(raw[1]) == 0xbb
            && static_cast<unsigned char>(raw[2]) == 0xbf) {
            raw.erase(0, 3);
            inputEncoding = "utf8-bom";
        }
        if (raw.empty()) { Diagnostic(source, event, sid, session, "input_empty", ERROR_INVALID_DATA, ERROR_INVALID_DATA, inputBytes, started, inputEncoding); return strict ? 1 : 0; }
        std::string payload = "{\"op\":\"hook\",\"source\":\"" + source + "\",\"event_override\":\"" + event + "\",\"raw\":" + raw + "}";
        auto name = PipeName(sid, session, identityError);
        if (name.empty()) { Diagnostic(source, event, sid, session, "identity", identityError, identityError, raw.size(), started); return strict ? 1 : 0; }
        HANDLE pipe = CreateFileW(name.c_str(), GENERIC_READ | GENERIC_WRITE, 0, nullptr, OPEN_EXISTING, FILE_FLAG_OVERLAPPED | SECURITY_SQOS_PRESENT | SECURITY_IDENTIFICATION, nullptr);
        DWORD initialError = pipe == INVALID_HANDLE_VALUE ? GetLastError() : ERROR_SUCCESS;
        DWORD finalError = initialError;
        if (pipe == INVALID_HANDLE_VALUE) {
            StartBridge(); auto end = GetTickCount64() + 1500;
            do {
                WaitNamedPipeW(name.c_str(), 30); Sleep(10);
                pipe = CreateFileW(name.c_str(), GENERIC_READ | GENERIC_WRITE, 0, nullptr, OPEN_EXISTING, FILE_FLAG_OVERLAPPED | SECURITY_SQOS_PRESENT | SECURITY_IDENTIFICATION, nullptr);
                finalError = pipe == INVALID_HANDLE_VALUE ? GetLastError() : ERROR_SUCCESS;
            } while (pipe == INVALID_HANDLE_VALUE && GetTickCount64() < end);
        }
        if (pipe == INVALID_HANDLE_VALUE) { Diagnostic(source, event, sid, session, "connect", initialError, finalError, raw.size(), started); return strict ? 1 : 0; }
        auto deadline = GetTickCount64() + 500;
        DWORD length = static_cast<DWORD>(payload.size());
        const char* stage = "write_length";
        bool ok = Transfer(pipe, &length, 4, true, deadline, finalError);
        if (ok) { stage = "write_payload"; ok = Transfer(pipe, payload.data(), length, true, deadline, finalError); }
        DWORD replyLength = 0;
        if (ok) { stage = "read_length"; ok = Transfer(pipe, &replyLength, 4, false, deadline, finalError); }
        if (ok && !(replyLength > 0 && replyLength < 4096)) { ok = false; finalError = ERROR_INVALID_DATA; }
        std::string reply(ok ? replyLength : 0, '\0');
        if (ok) { stage = "read_reply"; ok = Transfer(pipe, reply.data(), replyLength, false, deadline, finalError); }
        if (ok && reply.find("\"ok\":true") == std::string::npos) { ok = false; finalError = ERROR_INVALID_DATA; }
        CloseHandle(pipe);
        Diagnostic(source, event, sid, session, ok ? "accepted" : stage, initialError, finalError, inputBytes, started, inputEncoding);
        return strict && !ok ? 1 : 0;
    } catch (...) { if (!responded) HookResponse(); Diagnostic(source, event, sid, session, "exception", ERROR_UNHANDLED_EXCEPTION, ERROR_UNHANDLED_EXCEPTION, 0, started); return strict ? 1 : 0; }
}
