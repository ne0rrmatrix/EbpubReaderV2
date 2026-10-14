# Building and running

DisplayBook targets .NET 10 and builds for four platforms. Set up the tools in [Getting started](getting-started.md) first.

| Platform | Target framework | Minimum version | Builds on |
| --- | --- | --- | --- |
| Windows | `net10.0-windows10.0.19041.0` | Windows 10 build 19041 | Windows |
| Android | `net10.0-android` | Android API 26 (Android 8.0) | Windows, macOS |
| iOS | `net10.0-ios` | iOS 15.0 | macOS (compiles on Windows; packaging and running needs a Mac) |
| macOS (Mac Catalyst) | `net10.0-maccatalyst` | Mac Catalyst 15.0 | macOS (compiles on Windows; packaging and running needs a Mac) |

The app id is `com.ahdf.EpubReaderV2` and the display name is **EpubReaderV2**.

## The whole solution

```powershell
dotnet build .\DisplayBook.slnx -c Debug
```

On Windows this builds every project for every platform it can compile, which is what CI does.

## Windows

```powershell
dotnet build .\src\DisplayBook.App\DisplayBook.App.csproj -f net10.0-windows10.0.19041.0 -c Debug
.\src\DisplayBook.App\bin\Debug\net10.0-windows10.0.19041.0\win-x64\DisplayBook.App.exe
```

The app is unpackaged (`WindowsPackageType=None`): the built `.exe` runs directly. In Visual Studio, choose **Windows Machine** and press **F5**.

If the build fails with `MSB3021` (cannot copy `DisplayBook.App.exe`), the app is still running from a previous build. Close it, or build to another folder with `-o`.

## Android

Start an emulator or connect a device with USB debugging on, then:

```powershell
dotnet maui device list
dotnet build .\src\DisplayBook.App\DisplayBook.App.csproj -f net10.0-android -c Debug
```

To build, install and launch on the default device in one step:

```powershell
dotnet build .\src\DisplayBook.App\DisplayBook.App.csproj -f net10.0-android -c Debug -t:Run
```

In Visual Studio, pick the emulator or device as the target and press **F5**.

Release builds are `.apk` files. They're signed with the app's own keystore when `Secrets.local.props` provides one, and with the Android SDK's debug key otherwise; see [Configuration and secrets](configuration.md#android-signing).

## iOS

On a Mac with Xcode:

```bash
dotnet build src/DisplayBook.App/DisplayBook.App.csproj -f net10.0-ios -c Debug
```

For a booted simulator, pass its runtime identifier, then install and launch the app (list simulators with `xcrun simctl list devices available`):

```bash
dotnet build src/DisplayBook.App/DisplayBook.App.csproj -f net10.0-ios -c Debug -p:RuntimeIdentifier=iossimulator-arm64
xcrun simctl install <SIMULATOR_UDID> src/DisplayBook.App/bin/Debug/net10.0-ios/iossimulator-arm64/DisplayBook.App.app
xcrun simctl launch <SIMULATOR_UDID> com.ahdf.EpubReaderV2
```

A physical device needs an Apple Developer signing identity and provisioning profile; Visual Studio or VS Code with the .NET MAUI extension is the easiest way to pick a device and launch. Release iOS builds use the interpreter (`UseInterpreter=all`).

## Mac Catalyst

```bash
dotnet build src/DisplayBook.App/DisplayBook.App.csproj -f net10.0-maccatalyst -c Debug
open src/DisplayBook.App/bin/Debug/net10.0-maccatalyst/maccatalyst-arm64/EpubReaderV2.app
```

## The reader library on its own

`DisplayBook.Viewer` builds like any class library:

```powershell
dotnet build .\src\DisplayBook.Viewer\DisplayBook.Viewer.csproj -c Debug
dotnet build .\src\DisplayBook.Viewer\DisplayBook.Viewer.csproj -f net10.0-windows10.0.19041.0 -c Debug
```

It is trimmable and AOT-clean: trim and AOT warnings are errors. See [The reader control](reader.md).

## Shared build settings

Every project picks these up from the repository root:

- **`global.json`** pins the .NET SDK (10.0.401, rolling forward to later patches).
- **`Directory.Build.props`**: nullable reference types, the preview C# language version (`LangVersion=preview`), code style enforced in the build, NuGet vulnerability auditing, and the list of warnings treated as errors (nullable, XML docs, MVVM Toolkit, xUnit, XAML bindings, trimming and AOT).
- **`Directory.Build.targets`** excludes each platform's source files (`*.android.cs`, `*.windows.cs`, `*.ios.cs`, `*.macios.cs`…) from the other platforms' builds, so platform code lives in suffixed files rather than `#if` blocks.
- **`.editorconfig`**: code style and analyzer severities; see [Contributing](contributing.md#code-style).

Both `Directory.Build` files also hold the `SonarPrecheck` build mode that `tools/sonar-precheck.ps1` uses. Normal and CI builds never turn it on; see [Testing](testing.md#checking-for-sonarqube-cloud-issues-before-pushing).

## Clean builds

```powershell
dotnet clean .\src\DisplayBook.App\DisplayBook.App.csproj
dotnet build .\src\DisplayBook.App\DisplayBook.App.csproj -c Debug
```

If something still looks stale, delete the project's `bin` and `obj` folders. More fixes are in [Troubleshooting](troubleshooting.md).
