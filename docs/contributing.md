# Contributing

Thanks for helping with DisplayBook. This page covers how to propose a change, the code conventions the build enforces, and the checks to run before you push.

## Workflow

1. Set up a working build with [Getting started](getting-started.md).
2. Create a branch for your change; don't commit to `main`.
3. Keep each change focused, and follow the conventions below. `.editorconfig` and the build's analyzers enforce most of them.
4. Add or update tests for what you changed (see [Testing](testing.md)), and run the suites that cover it.
5. Check for SonarQube Cloud issues before pushing: run `.\tools\sonar-precheck.ps1`, and in VS Code **SonarQube: Analyze Changed Files with SonarQube** (see [Testing](testing.md#checking-for-sonarqube-cloud-issues-before-pushing)).
6. Test the change on each platform it affects.
7. Open a pull request against `main`. Describe what changes for users and how you checked it. CI builds the solution, runs the tests with coverage, and posts SonarQube Cloud's analysis on the pull request.

Commit messages follow the existing history: a short summary line in the imperative ("Fix…", "Add…"), and a body explaining why when that isn't obvious.

## Code style

The repository's `.editorconfig` defines the style, and the build enforces it (`EnforceCodeStyleInBuild`). Most of these rules are errors:

- **Layout.** Tabs for indentation in C#. File-scoped namespaces. Braces on every `if`, `else`, `for` and `while`.
- **Modifiers.** Omit accessibility modifiers that are the default (no `private` on private members). Modifiers go in the configured order.
- **Naming.** Private and protected fields, static ones included, are camelCase with no prefix (`logger`, not `_logger`).
- **Async.** Every task is awaited (CS4014), and async methods actually await (CS1998). `CancellationToken` parameters come last and are forwarded (CA1068, CA2016).
- **Disposal.** Types that own disposable fields implement `IDisposable`, and implement it correctly (CA1001, CA1063).
- **MVVM.** Observable properties are partial properties with `[ObservableProperty]` (MVVM Toolkit's field form is an error).
- **Conventions not enforced by the build.** Explicit types rather than `var`, and collection expressions (`[]`, `[with(StringComparer.Ordinal)]`) where they fit.

`Directory.Build.props` also turns nullable warnings, XML-doc warnings, MVVM Toolkit, xUnit and XAML binding diagnostics, and trimming and AOT warnings into errors.

CA1822 ("member can be static") is turned off: it flags `[RelayCommand]` methods and partial platform implementations, which can't be static. A few older `#pragma` suppressions of CA1822 and Sonar's S2325 remain and are intentional. When you suppress any other rule, use `[SuppressMessage]` or a `#pragma` with a reason.

## Conventions

- **Navigation.** View models call `await Shell.Current.GoToAsync("route")` directly. Register new pages with `AddTransientWithShellRoute` in `MauiProgram.AddDisplayBook()`, pass data as query-string parameters, and read them in the page's `IQueryAttributable`. Don't add a navigation service or a `ShellContent` entry. See [Architecture](architecture.md#composition-and-navigation).
- **Platform code.** Put it in suffixed files (`Foo.android.cs`, `Foo.windows.cs`, `Foo.macios.cs`…), not `#if` blocks.
- **Services.** Interfaces go in `Interfaces/`, implementations in `Services/`. Register everything in `MauiProgram.AddDisplayBook()`.
- **HTTP.** Add a named `HttpClient` and wrap it in a singleton; don't use `AddHttpClient<TInterface, TImpl>()`.
- **JSON.** Use `JsonDocument`, or a source-generated `JsonSerializerContext`; never reflection-based serialization.
- **Logging.** Use `[LoggerMessage]` methods in a nested `static partial class Log`. Log per-page-turn or per-request detail at Debug, not Information.
- **The reader library.** `DisplayBook.Viewer` stays independent of the app: no Firebase, OPDS or other app dependencies, and it must stay trim- and AOT-clean.
- **The reader bridge.** A change to a bridge message or a `window.DisplayBookReader` function needs matching changes in `EpubText.js` and `EpubReaderView.xaml.cs`. Native WebView wiring must be safe to repeat, because the reader is reused across books. See [Architecture](architecture.md#the-reader-engine).
- **Database columns.** Adding a column to `BookDatabase` means updating every place that lists the columns, in the same order; see [Architecture](architecture.md#persistence).
- **Secrets.** Never commit keys, passwords or keystores. Use `Secrets.local.props` or environment variables; see [Configuration and secrets](configuration.md).

## Tests

- **Cover the behaviour.** New behaviour needs a test that would fail without it. Put MAUI-free logic in `DisplayBook.Tests` (link its source file into the project), code that calls MAUI APIs or drives pages in `DisplayBook.DeviceTests`, and reader engine changes in `DisplayBook.ReaderTests`.
- **No tests or assertions just to satisfy SonarQube Cloud.** If a test proves nothing, fix it or remove it. Helpers that fail the test themselves, like `Wait.UntilAsync`, are marked `[AssertionMethod]` instead.

## Adding a top-bar icon

Top-bar actions use Material Symbols (outlined, 24dp) from [google/material-design-icons](https://github.com/google/material-design-icons), licensed under Apache-2.0. The source SVGs are in `src/DisplayBook.App/Resources/Images/icons/`, each the unmodified upstream `24px.svg`. They aren't referenced directly: `DisplayBook.App.csproj` builds each one twice with a different `TintColor`, producing `icon_<name>_light.png` and `icon_<name>_dark.png`, and XAML picks between them with `AppThemeBinding`. (The csproj's `Resources\Images\*` glob is single-level, so it doesn't pick the source files up a second time.)

To add one:

1. Save the upstream `24px.svg` in that folder as `ic_<name>.svg`.
2. Add its two `MauiImage` entries to the csproj: light `#236B5A`, dark `#66E0C1`.
3. Use `icon_<name>_light.png` / `icon_<name>_dark.png` with the `TopBarIconButton` style, and set both `SemanticProperties.Description` and `ToolTipProperties.Text`. An icon has no text for a screen reader to announce.

## Regenerating the dictionary

The reader's dictionary database (`src/DisplayBook.Viewer/Resources/Raw/Dictionary/websters1913.db`) is built by `tools/DictionaryBuilder` from Project Gutenberg's public-domain text of Webster's 1913 dictionary. The source, license and regeneration steps are in `src/DisplayBook.Viewer/Resources/Raw/Dictionary/SOURCE.md`.

## License

By contributing, you agree that your contributions are licensed under the project's [MIT License](../LICENSE.txt).
