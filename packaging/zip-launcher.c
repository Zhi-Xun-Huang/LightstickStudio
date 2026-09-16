#define WIN32_LEAN_AND_MEAN
#include <windows.h>

/* Native bootstrap: Windows APIs only, with no .NET or C runtime beside it.
   The actual WinUI process runs from app/, preserving all resource paths. */
static WCHAR appDirectory[32768];
static WCHAR application[32768];
static WCHAR commandLine[32768];
static STARTUPINFOW startup;
static PROCESS_INFORMATION process;

static void Fail(BOOL selfTest, LPCWSTR message)
{
    if (!selfTest) MessageBoxW(NULL, message, L"Lightstick Studio", MB_OK | MB_ICONERROR);
    ExitProcess(1);
}

void WINAPI StudioEntry(void)
{
    LPCWSTR args = GetCommandLineW();
    DWORD length;
    DWORD attributes;
    DWORD written;
    BOOL selfTest;
    HANDLE output;
    const char ready[] = "{\"ready\":true,\"launcher\":\"native-zip\"}\r\n";

    /* Skip the launcher filename, preserving all remaining arguments verbatim. */
    if (*args == L'"') {
        ++args;
        while (*args && *args != L'"') ++args;
        if (*args) ++args;
    } else {
        while (*args && *args != L' ' && *args != L'\t') ++args;
    }
    while (*args == L' ' || *args == L'\t') ++args;
    selfTest = lstrcmpW(args, L"--self-test") == 0;

    length = GetModuleFileNameW(NULL, appDirectory, 32768);
    if (!length || length >= 32768) Fail(selfTest, L"無法取得程式位置。");
    while (length && appDirectory[length - 1] != L'\\' && appDirectory[length - 1] != L'/') --length;
    if (!length || length + 4 >= 32768) Fail(selfTest, L"程式路徑太長。");
    appDirectory[length] = L'\0';
    lstrcatW(appDirectory, L"app");

    if (lstrlenW(appDirectory) + 28 >= 32768) Fail(selfTest, L"程式路徑太長。");
    lstrcpyW(application, appDirectory);
    lstrcatW(application, L"\\LightstickStudio.WinUI.exe");
    attributes = GetFileAttributesW(application);
    if (attributes == INVALID_FILE_ATTRIBUTES || (attributes & FILE_ATTRIBUTE_DIRECTORY))
        Fail(selfTest, L"找不到 app 資料夾內的主程式。\n\n請完整解壓 ZIP，並保留啟動器與 app 資料夾的相對位置。");

    if (selfTest) {
        output = GetStdHandle(STD_OUTPUT_HANDLE);
        if (output && output != INVALID_HANDLE_VALUE)
            WriteFile(output, ready, sizeof(ready) - 1, &written, NULL);
        ExitProcess(0);
    }

    if (lstrlenW(application) + lstrlenW(args) + 5 >= 32768)
        Fail(FALSE, L"啟動參數太長。");
    lstrcpyW(commandLine, L"\"");
    lstrcatW(commandLine, application);
    lstrcatW(commandLine, L"\"");
    if (*args) {
        lstrcatW(commandLine, L" ");
        lstrcatW(commandLine, args);
    }
    startup.cb = sizeof(startup);
    if (!CreateProcessW(application, commandLine, NULL, NULL, FALSE, 0, NULL,
            appDirectory, &startup, &process))
        Fail(FALSE, L"無法啟動 app 中的 Lightstick Studio。\n\n請確認 ZIP 已完整解壓，並保留 app 內全部檔案。");
    CloseHandle(process.hThread);
    CloseHandle(process.hProcess);
    ExitProcess(0);
}
