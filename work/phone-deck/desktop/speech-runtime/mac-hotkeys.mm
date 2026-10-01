#import <Cocoa/Cocoa.h>
#include <Carbon/Carbon.h>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <cstdint>
#include <chrono>
#include <thread>
#include <unistd.h>

static const UInt32 signature = 0x5048444B;
static bool toggleDown = false, holdDown = false;
static void publish(const char * command) {
    std::puts(command);
    std::fflush(stdout);
}

static OSStatus handle(EventHandlerCallRef, EventRef event, void *) {
    EventHotKeyID key;
    if (GetEventParameter(event, kEventParamDirectObject, typeEventHotKeyID,
            nullptr, sizeof(key), nullptr, &key) != noErr || key.signature != signature)
        return eventNotHandledErr;
    const UInt32 kind = GetEventKind(event);
    if (key.id == 1 && kind == kEventHotKeyPressed && !toggleDown) {
        toggleDown = true; publish("toggle");
    }
    if (key.id == 1 && kind == kEventHotKeyReleased) toggleDown = false;
    if (key.id == 2 && kind == kEventHotKeyPressed && !holdDown) {
        holdDown = true; publish("start");
    }
    if (key.id == 2 && kind == kEventHotKeyReleased && holdDown) {
        holdDown = false; publish("stop");
    }
    return noErr;
}

static UInt32 shortcutCode(const char * id) {
    const char * names[] = {"Ctrl+Alt+Space", "Ctrl+Alt+V", "Ctrl+Alt+B", "Ctrl+Alt+N",
        "Ctrl+Alt+F8", "Ctrl+Alt+F9", "Ctrl+Alt+F10", "Ctrl+Alt+F11"};
    const UInt32 codes[] = {49, 9, 11, 45, 100, 101, 109, 103};
    for (int i = 0; i < 8; ++i) if (std::strcmp(id, names[i]) == 0) return codes[i];
    return UINT32_MAX;
}

int main(int argc, const char ** argv) {
    const char * tap = argc == 3 ? argv[1] : "Ctrl+Alt+Space";
    const char * holdKey = argc == 3 ? argv[2] : "Ctrl+Alt+V";
    const UInt32 tapCode = shortcutCode(tap), holdCode = shortcutCode(holdKey);
    if ((argc != 1 && argc != 3) || tapCode == UINT32_MAX || holdCode == UINT32_MAX || tapCode == holdCode) {
        publish("unavailable"); return 1;
    }
    @autoreleasepool {
        // Carbon's application event queue needs an application loop on the OS main thread.
        // The .NET HTTP process has no Cocoa loop, so this small, fixed-command helper owns it.
        [NSApplication sharedApplication];
        [NSApp setActivationPolicy:NSApplicationActivationPolicyAccessory];
        [NSApp finishLaunching];
        EventTypeSpec types[] = {
            {kEventClassKeyboard, kEventHotKeyPressed},
            {kEventClassKeyboard, kEventHotKeyReleased}
        };
        EventHandlerRef handler = nullptr;
        if (InstallApplicationEventHandler(handle, 2, types, nullptr, &handler) != noErr) {
            publish("unavailable"); return 1;
        }
        EventHotKeyRef toggleRef = nullptr, holdRef = nullptr;
        EventHotKeyID toggle = {signature, 1}, hold = {signature, 2};
        const OSStatus first = RegisterEventHotKey(tapCode, controlKey | optionKey, toggle,
            GetApplicationEventTarget(), 0, &toggleRef);
        const OSStatus second = RegisterEventHotKey(holdCode, controlKey | optionKey, hold,
            GetApplicationEventTarget(), 0, &holdRef);
        const pid_t parent = getppid();
        std::thread([parent] {
            while (getppid() == parent) std::this_thread::sleep_for(std::chrono::seconds(1));
            // A receiver crash must not leave its registered shortcuts behind.
            std::_Exit(0);
        }).detach();
        if (first != noErr || second != noErr) {
            if (first == noErr) UnregisterEventHotKey(toggleRef);
            if (second == noErr) UnregisterEventHotKey(holdRef);
            RemoveEventHandler(handler); publish("shortcut-conflict"); return 1;
        }
        // No-argument packaging probes keep their original ready token.
        if (argc == 1) publish("ready");
        else { std::printf("ready:%s:%s\n", tap, holdKey); std::fflush(stdout); }
        [NSApp run];
        if (first == noErr) UnregisterEventHotKey(toggleRef);
        if (second == noErr) UnregisterEventHotKey(holdRef);
        RemoveEventHandler(handler);
    }
    return 0;
}
