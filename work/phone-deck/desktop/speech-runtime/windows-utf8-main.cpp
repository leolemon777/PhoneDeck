#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <string>
#include <vector>
#include <cstdio>
int phonedeck_utf8_main(int argc, char **argv);
int wmain(int argc, wchar_t **wide) {
    std::vector<std::string> values;
    values.reserve(argc);
    for (int i = 0; i < argc; ++i) {
        const int bytes = WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, wide[i], -1, nullptr, 0, nullptr, nullptr);
        if (!bytes) { std::fputs("Invalid Unicode argument\n", stderr); return 2; }
        std::string value(bytes, '\0');
        WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, wide[i], -1, &value[0], bytes, nullptr, nullptr);
        values.push_back(std::move(value));
    }
    std::vector<char *> argv;
    for (auto &value : values) argv.push_back(&value[0]);
    argv.push_back(nullptr);
    return phonedeck_utf8_main(argc, argv.data());
}
