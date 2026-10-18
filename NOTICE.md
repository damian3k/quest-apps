# Third-party notices

Quest Apps' own code is released under the MIT License (see `LICENSE`).

## Avalonia UI and .NET

Quest Apps is built with [Avalonia UI](https://github.com/AvaloniaUI/Avalonia) and
[.NET](https://github.com/dotnet/runtime), both under the MIT License.

## adb (Android SDK Platform-Tools)

The Windows release package contains `adb\adb.exe`, `adb\AdbWinApi.dll` and
`adb\AdbWinUsbApi.dll`: an unmodified copy of Google's
[Android SDK Platform-Tools](https://developer.android.com/tools/releases/platform-tools)
(version 35.0.1). These files are Google's work, are not covered by the MIT License
above, and remain subject to Google's terms. The adb source code is part of the
Android Open Source Project and is available under the Apache License 2.0.

The source repository does not contain these files. To build from source on Windows,
download Platform-Tools from the link above and copy those three files into an
`adb\` folder next to `QuestApps.csproj`. On macOS and Linux the app uses the adb
installed on the system.

## Trademarks

Quest Apps is an independent project. It is not affiliated with, endorsed by or
sponsored by Meta Platforms, Inc. "Meta" and "Meta Quest" are trademarks of
Meta Platforms, Inc., used here only to say which devices the app works with.
"Android" is a trademark of Google LLC.
