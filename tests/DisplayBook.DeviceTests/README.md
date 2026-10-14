# DisplayBook.DeviceTests

xUnit tests that run inside a MAUI app on the device itself, so code that calls MAUI platform APIs
(`FileSystem`, `SecureStorage`, `Preferences`, `Color`) is tested against the real platform
implementations. `DisplayBook.Tests` covers everything that doesn't need MAUI. Code that does
can't run there, so it is tested here.

The design follows CommunityToolkit.Maui's device tests
([CommunityToolkit/Maui#3344](https://github.com/CommunityToolkit/Maui/pull/3344)): one app per
platform that either shows a visual runner or runs every test and reports the results. Instead of
vendoring dotnet/maui's runner as that PR does, it uses the
[DeviceRunners](https://github.com/mattleibow/DeviceRunners) packages, the public NuGet form of
the same runner.

## Running the tests

With `dotnet test`, one platform at a time. The app is built, launched, runs every test, sends the
results back over TCP on port 16384, and exits:

```powershell
dotnet test .\tests\DisplayBook.DeviceTests\DisplayBook.DeviceTests.csproj -f net10.0-windows10.0.19041.0
dotnet test .\tests\DisplayBook.DeviceTests\DisplayBook.DeviceTests.csproj -f net10.0-android        # a booted emulator or connected device
dotnet test .\tests\DisplayBook.DeviceTests\DisplayBook.DeviceTests.csproj -f net10.0-ios            # on a Mac, with a booted simulator
dotnet test .\tests\DisplayBook.DeviceTests\DisplayBook.DeviceTests.csproj -f net10.0-maccatalyst    # on a Mac
```

Always pass `-f`. Without it, `dotnet test` tries every platform, including ones this machine
can't run. `--filter` works as usual (for example `--filter "FullyQualifiedName~BookDatabaseTests"`),
and `--logger trx` writes `test-results/test-results.trx`. The first Windows run may show a
firewall prompt for the results connection.

Started from the IDE or by hand instead, the app shows the visual runner, where tests can be run
and inspected one at a time. That is the way to debug a failing test.

## How the app is put together

- **All of DisplayBook.App is compiled in.** It is an Exe, so it can't be referenced; the `.csproj`
  includes its `.cs` and `.xaml` files instead, with the platform-specific ones added per target
  framework. On Android the app's own `MainActivity` is this app's launcher too.
- **The app's own registrations.** `MauiProgram` calls DisplayBook.App's `AddDisplayBook()`, so
  pages, view models and services come from the same DI registrations and Shell routes the app uses.
  It then swaps out what reaches beyond the device:
  - every named `HttpClient` goes to an in-memory `FakeServer` (`FakeNetwork.Opds`, `.Firebase`,
    `.GoogleBooks`, `.OpenLibrary`, `.Default`); `FakeCalibre` and `FakeFirebase` set up realistic
    servers on them;
  - the file and folder pickers are `FakeBookPicker`, mDNS discovery is `FakeBonjourDiscovery`;
  - `DisplayAlertAsync`, action sheets and prompts are answered by `TestDialogs`, through the keyed
    services MAUI 10 checks before showing a dialog. Queue an answer with
    `TestDialogs.AnswerNextAlert(true)`; with none queued an alert is declined.
- **Pages run in the real app shell.** `TestApp.GetShellAsync()` opens DisplayBook.App's `AppShell`
  in a second window, kept for the whole run; view models' `Shell.Current` calls reach it because it
  is the only window holding a Shell. Navigate with `TestApp.GoToAsync<TPage>(route)` and reset with
  `TestApp.ResetNavigationAsync()`. The app's colours and named styles are loaded into the test
  app's resources; its implicit styles are not, as they break the runner's own UI.

## Writing tests

- Derive from `AppDataTest`. Each test then starts from `AppDataSandbox.ResetAsync()`: no library,
  database rows, OPDS servers, downloads, stored sign-in or fake state. The app's DI singletons hold
  state of their own, so the reset clears them through their APIs. These tests share process-wide
  state and don't run in parallel.
- `AppDataSandbox` refuses to run unless the process is this app (`ApplicationId`
  `com.ahdf.displaybook.devicetests`). Keep that ID different from DisplayBook.App's: it is what
  keeps these tests away from a real library.
- Tests that touch pages or the UI thread use `[UIFact]`/`[UITheory]`. Wait for asynchronous UI with
  `Wait.UntilAsync`, which keeps the dispatcher running.
- `TestEpub.Write` makes small valid EPUBs; different titles give different content hashes.
- No test should need the network: set up routes on `FakeNetwork` instead.
- A class that implements `IDisposable` must be `partial` on Windows (CsWinRT1028 is an error in
  this repository).
- An unhandled exception that ends the app is written to `%TEMP%\DisplayBook.DeviceTests.crash.log`.

## Coverage

`tools/coverage.ps1` runs these tests under `dotnet-coverage` in batches, each in its own app
process. When the code runs slower (under the profiler, or instrumented), the page tests
intermittently hit a native access violation in WinUI's `Microsoft.UI.Xaml.Controls.dll`; a run
without the profiler passes. A batch whose app crashes is run again, and if it crashes every time it
is run once without coverage to prove its tests pass. See the script for details.
