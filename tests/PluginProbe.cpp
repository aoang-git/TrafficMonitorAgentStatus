#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <iostream>
#include <string>
#include <thread>
#include <vector>
#pragma warning(push)
#pragma warning(disable:4100)
#include "../plugin/PluginInterface.h"
#pragma warning(pop)

int wmain(int argc, wchar_t** argv) {
    const wchar_t* path = argc > 1 ? argv[1] : L"AgentStatus.dll";
    int cycles = argc > 2 ? _wtoi(argv[2]) : 1;
    for (int i = 0; i < cycles; ++i) {
        auto dll = LoadLibraryW(path); if (!dll) { std::cerr << "LoadLibrary failed: " << GetLastError(); return 1; }
        auto factory = reinterpret_cast<ITMPlugin*(*)()>(GetProcAddress(dll, "TMPluginGetInstance"));
        if (!factory) return 2;
        auto plugin = factory();
        if (plugin != factory() || plugin->GetAPIVersion() != 6 || plugin->GetItem(-1) || plugin->GetItem(1)) return 3;
        auto item = plugin->GetItem(0); if (!item) return 4;
        plugin->DataRequired();
        if (!item->GetItemValueText() || !plugin->GetTooltipInfo()) return 5;
        if (i == 0) {
            std::wstring text = item->GetItemValueText();
            int bytes = WideCharToMultiByte(CP_UTF8, 0, text.c_str(), -1, nullptr, 0, nullptr, nullptr);
            std::string utf8(bytes, '\0'); WideCharToMultiByte(CP_UTF8, 0, text.c_str(), -1, utf8.data(), bytes, nullptr, nullptr);
            std::cout << "api=" << plugin->GetAPIVersion() << " value=" << utf8.c_str() << "\n";
        }
        // Exercise getters racing with DataRequired and TLS destruction before unloading.
        std::vector<std::thread> readers;
        for (int t = 0; t < 4; ++t) readers.emplace_back([&] { for (int k = 0; k < 200; ++k) { std::wstring v = item->GetItemValueText(); std::wstring tip = plugin->GetTooltipInfo(); } });
        for (int k = 0; k < 200; ++k) plugin->DataRequired();
        for (auto& thread : readers) thread.join();
        FreeLibrary(dll);
    }
    std::cout << "PASS: ABI, getters, concurrent refresh, unload cycles=" << cycles << "\n";
    return 0;
}
