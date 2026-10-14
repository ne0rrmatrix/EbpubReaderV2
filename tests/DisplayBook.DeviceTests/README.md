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

## Writing tests

- The app's source files are compiled in (`<Compile Include="..\..\src\DisplayBook.App\...">` in
  the `.csproj`), the same way `DisplayBook.Tests` does it, because `DisplayBook.App` is an Exe and
  can't be referenced. Link the file under test there.
- Put any test that reads or writes app data, secure storage or preferences in
  `[Collection(AppDataCollection.Name)]`, and call `AppDataSandbox.Reset()` in its constructor.
  Those stores are process-wide, so these tests must not run in parallel, and each one starts from
  empty.
- `AppDataSandbox.Reset()` deletes the library, the database and the stored sign-in. It refuses to
  run unless the process is this app (`ApplicationId` `com.ahdf.displaybook.devicetests`). Keep
  that ID different from `DisplayBook.App`'s: it is what keeps these tests away from a real library.
- Use `StubHttpHandler` for services that call a REST API. No test here should need the network.
- A class that implements `IDisposable` must be `partial` on Windows (CsWinRT1028 is an error in
  this repository).
