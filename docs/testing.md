# Testing

DisplayBook has three test suites, a coverage script that runs them all, and CI that runs that script and analyzes the results with SonarQube Cloud.

| Suite | What it tests | Runs on |
| --- | --- | --- |
| [`tests/DisplayBook.Tests`](#unit-tests) | Code that doesn't need MAUI: parsers, matchers, EPUB handling, sync logic, view model logic | `dotnet test`, anywhere |
| [`tests/DisplayBook.DeviceTests`](#device-tests) | Code that calls MAUI APIs, and the app's real pages in the real `AppShell` | Inside a MAUI app on Windows, Android, iOS or Mac Catalyst |
| [`tests/DisplayBook.ReaderTests`](#reader-tests) | The JavaScript reader engine (`EpubText.js`) | Headless Chromium, through Playwright |

```powershell
dotnet test .\tests\DisplayBook.Tests\DisplayBook.Tests.csproj
dotnet test .\tests\DisplayBook.DeviceTests\DisplayBook.DeviceTests.csproj -f net10.0-windows10.0.19041.0
cd .\tests\DisplayBook.ReaderTests; npm ci; npx playwright install chromium; npx playwright test
```

## Unit tests

`DisplayBook.Tests` is a plain xUnit project. It does **not** reference `DisplayBook.App`, because that would pull in MAUI. Instead its `.csproj` links individual source files:

```xml
<Compile Include="..\..\src\DisplayBook.App\Services\Opds\OpdsParserService.cs" Link="App\Services\Opds\OpdsParserService.cs" />
```

To test MAUI-free code from the app, add its file to that list, or the test project can't see it. Code that calls MAUI APIs (`FileSystem`, `SecureStorage`, `Preferences`, or anything reached through `BookStorageService`) belongs in the device tests instead. A class that is linked in but needs a few MAUI calls is split into two partial files, as `BookDetailsViewModel` is (see [Architecture](architecture.md#composition-and-navigation)).

Run one class or test with `--filter`:

```powershell
dotnet test .\tests\DisplayBook.Tests\DisplayBook.Tests.csproj --filter "FullyQualifiedName~OpdsParserServiceTests"
```

## Device tests

`DisplayBook.DeviceTests` runs xUnit inside a MAUI app on the device itself, so code that calls MAUI platform APIs is tested against the real platform implementations. The design follows CommunityToolkit.Maui's device tests ([CommunityToolkit/Maui#3344](https://github.com/CommunityToolkit/Maui/pull/3344)), using the [DeviceRunners](https://github.com/mattleibow/DeviceRunners) packages rather than a vendored copy of dotnet/maui's runner.

### Running them

`dotnet test` builds the app, launches it, runs every test, receives the results over TCP on port 16384, and closes it. Run one platform at a time:

```powershell
dotnet test .\tests\DisplayBook.DeviceTests\DisplayBook.DeviceTests.csproj -f net10.0-windows10.0.19041.0
dotnet test .\tests\DisplayBook.DeviceTests\DisplayBook.DeviceTests.csproj -f net10.0-android        # a booted emulator or connected device
dotnet test .\tests\DisplayBook.DeviceTests\DisplayBook.DeviceTests.csproj -f net10.0-ios            # on a Mac, with a booted simulator
dotnet test .\tests\DisplayBook.DeviceTests\DisplayBook.DeviceTests.csproj -f net10.0-maccatalyst    # on a Mac
```

- **Always pass `-f`.** Without it, `dotnet test` tries every platform, including ones this machine can't run.
- **Options.** `--filter` works as usual (`--filter "FullyQualifiedName~BookDatabaseTests"`), and `--logger trx` writes `test-results/test-results.trx`.
- **Firewall prompt.** The first Windows run may ask to allow the results connection.
- **Debugging.** Started from the IDE or by hand, the app shows the visual runner instead, where tests can be run and inspected one at a time.

### How the test app is built

- **All of DisplayBook.App is compiled in.** The app is an executable, so it can't be referenced; the test project includes its `.cs` and `.xaml` files instead, with each platform's files added for that platform. On Android, the app's own `MainActivity` is the test app's launcher too.
- **The app's own registrations.** The test app's `MauiProgram` calls `AddDisplayBook()`, so pages, view models and services come from the same registrations and Shell routes the app uses. It then replaces everything that reaches beyond the device:
  - Every named `HttpClient` goes to an in-memory `FakeServer` (`FakeNetwork.Opds`, `.Firebase`, `.GoogleBooks`, `.OpenLibrary`, `.Default`). `FakeCalibre` and `FakeFirebase` set up realistic servers on them.
  - The file and folder pickers are `FakeBookPicker`, and mDNS discovery is `FakeBonjourDiscovery`.
  - Alerts, action sheets and prompts are answered by `TestDialogs`, through the keyed services MAUI 10 checks before showing a dialog. Queue an answer with `TestDialogs.AnswerNextAlert(true)`; with none queued, an alert is declined.
- **Pages run in the real app shell.** `TestApp.GetShellAsync()` opens DisplayBook.App's `AppShell` in a second window that stays open for the whole run. View models' `Shell.Current` calls reach it, because it is the only window holding a Shell. Navigate with `TestApp.GoToAsync<TPage>(route)` and reset with `TestApp.ResetNavigationAsync()`. The app's colours and named styles are loaded into the test app's resources; its implicit styles are not, because they break the runner's own UI.

### Writing device tests

- **Start from a clean sandbox.** Derive from `AppDataTest`. Each test then starts from `AppDataSandbox.ResetAsync()`: no library, database rows, OPDS servers, downloads, stored sign-in or fake state. The app's DI singletons hold state of their own, so the reset clears them through their APIs. Because the tests share process-wide state, they don't run in parallel.
- **Keep the test app's id distinct.** `AppDataSandbox` refuses to run unless the process is the test app (`ApplicationId` `com.ahdf.displaybook.devicetests`). Keep that id different from DisplayBook.App's; it is what keeps these tests away from a real library.
- **UI tests.** Tests that touch pages or the UI thread use `[UIFact]`/`[UITheory]`. Wait for asynchronous UI with `Wait.UntilAsync`, which keeps the dispatcher running.
- **Waits are assertions.** `Wait.UntilAsync` fails the test when its condition never holds, so it is an assertion. It is marked `[AssertionMethod]` (`Support/AssertionMethodAttribute.cs`) so SonarQube Cloud counts it as one, including through `TestApp.WaitForPageAsync`. Don't add assertions that repeat what a wait already checked; assert only what the test hasn't proved yet.
- **Test books.** `TestEpub.Write` makes small valid EPUBs; different titles give different content hashes.
- **No network.** No test should need the network; set up routes on `FakeNetwork` instead.
- **Disposable classes.** A class that implements `IDisposable` must be `partial` on Windows (CsWinRT1028 is an error in this repository).
- **Crash log.** An unhandled exception that ends the app is written to `%TEMP%\DisplayBook.DeviceTests.crash.log`.

## Reader tests

`DisplayBook.ReaderTests` tests `src/DisplayBook.Viewer/Resources/Raw/DisplayBookViewer/EpubText.js` in headless Chromium, the engine behind the Windows reader's WebView2 and Android's WebView. It writes JavaScript coverage as LCOV for SonarQube Cloud. It needs Node.js.

```powershell
cd tests\DisplayBook.ReaderTests
npm ci
npx playwright install chromium
npx playwright test
```

- **How the reader is hosted.** `tests/support/reader.js` loads the real `index.html` and `EpubText.js` from the repository. It answers every request to `https://displaybook.invalid` itself, the host name the app uses on Windows: the reader files, a generated four-chapter combined document (the shape `CombinedDocumentBuilder` produces), and its cover image.
- **How bridge messages are captured.** The reader sends messages to the app by navigating to its bridge URL, which it takes from the `bridge` query parameter. The tests point it at `https://displaybook.invalid/bridge`, so each message becomes a request that the route records (in `reader.messages`) and then aborts, as `EpubReaderView` cancels it.
- **Same-task messages coalesce.** Two messages sent in the same task merge, because the second navigation replaces the first, in the app as here. Assert on the page's state, or on messages that are sent on their own.
- **Assertions Sonar recognizes.** SonarQube Cloud recognizes `expect(…)` as an assertion but not `expect.poll(…)`. A test that only waits needs a plain `expect` on something the wait didn't already prove.

## Coverage

```powershell
.\tools\coverage.ps1                    # -SkipDeviceTests / -SkipReaderTests to leave a suite out
```

It runs the unit tests and the Windows device tests under `dotnet-coverage` (a local tool in `dotnet-tools.json`), and the reader tests under Chromium's JavaScript coverage. It writes, under `artifacts/coverage/`:

| File | Contents |
| --- | --- |
| `coverage.xml` | Merged .NET coverage (Visual Studio XML), read by SonarQube Cloud |
| `unit.xml`, `device-windows-*.xml` | The per-suite reports it merges |
| `reader-js/lcov.info` | `EpubText.js` coverage, read by SonarQube Cloud |

The device tests run in batches, each in its own app process. Under the profiler, the page tests intermittently hit a native access violation in WinUI's `Microsoft.UI.Xaml.Controls.dll`, though a run without the profiler passes. A batch whose app crashes is run again, and if it crashes every time, it is run once without coverage to prove its tests pass. A failing test fails the run straight away.

## Continuous integration

`.github/workflows/build.yml` runs on every push to `main` and every pull request, on Windows:

1. Installs Java (for the scanner), the .NET SDK from `global.json`, Node.js (for the reader tests) and the MAUI workload, and decodes the Android keystore when its secret is present.
2. Starts the SonarQube Cloud scanner.
3. Builds `DisplayBook.slnx`.
4. Runs `tools/coverage.ps1`.
5. Ends the scanner, which uploads the analysis.

Platform-only code that no test here can run (Android, iOS and Mac files and folders, the app's platform entry points), the tests themselves, and `tools/` are excluded from coverage. Results are on [SonarQube Cloud](https://sonarcloud.io/project/overview?id=ne0rrmatrix_EbpubReaderV2).

## Checking for SonarQube Cloud issues before pushing

SonarQube Cloud reports issues from Sonar's own rules and from every compiler diagnostic in the build, including suggestion-level ones that never show in the build output. Check before pushing, so a pull request doesn't need another round of CI.

**C# and PowerShell:** run the precheck script.

```powershell
.\tools\sonar-precheck.ps1          # issues on the lines you changed since origin/main
.\tools\sonar-precheck.ps1 -All     # every issue in the solution
.\tools\sonar-precheck.ps1 -Offline # don't call SonarQube Cloud
```

The script builds the solution with `SonarPrecheck=true`. That build mode (in `Directory.Build.props` and `Directory.Build.targets`):

- builds into separate `bin/sonar` and `obj/sonar` folders, so it never touches the normal build or a running app;
- adds the `SonarAnalyzer.CSharp` analyzer, with the project's quality profile read from SonarQube Cloud's public API;
- writes each compilation's SARIF log, the file the scanner imports compiler diagnostics from.

The script then reports what the pull request analysis would:

- only issues on lines changed since the branch left `origin/main`;
- not issues SonarQube Cloud already has on `main`, whether open or marked accepted or false positive;
- in test projects, only test-specific Sonar rules (such as "add at least one assertion") plus compiler diagnostics, as the scanner does.

The first run takes about a minute and a half; later runs are incremental. Normal and CI builds never use this mode.

**JavaScript, CSS and HTML:** in VS Code, install [SonarQube for IDE](https://marketplace.visualstudio.com/items?itemName=SonarSource.sonarlint-vscode) and bind it to the SonarQube Cloud project (connected mode, organization `ne0rrmatrix`, project `ne0rrmatrix_EbpubReaderV2`), so it uses the same rules. Before committing, run **SonarQube: Analyze Changed Files with SonarQube** from the Command Palette. Its automatic analysis only covers files open in the editor, so changes made outside the editor are missed without this.

**PowerShell:** the precheck script checks changed `.ps1` files too, with [PSScriptAnalyzer](https://github.com/PowerShell/PSScriptAnalyzer) standing in for SonarQube Cloud's PowerShell rules, which have no local analyzer. Install it once with `Install-Module PSScriptAnalyzer -Scope CurrentUser`; without it, the script warns and skips PowerShell. Each SonarQube Cloud rule is mapped to its PSScriptAnalyzer equivalent and reported under the Sonar key (for example `S8626 … (PSAvoidAssignmentToAutomaticVariable)`). `Write-Host` is reported only inside functions not named `Show-…`, as SonarQube Cloud's S8677 does. A few rules have no equivalent and are only checked on CI: S3776 (cognitive complexity), S1135 (TODO comments), and S8621 (indentation of multi-line pipelines; PSScriptAnalyzer's indentation rule checks all indentation, so it would report what SonarQube Cloud doesn't). SonarQube for IDE has no PowerShell analyzer. A function that writes to the console by design can say so with `[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingWriteHost', '', Justification = '…')]` on its `param` block, as `Invoke-Checked` in `tools/coverage.ps1` does.
