# DisplayBook.ReaderTests

Browser tests for the reader engine, `src/DisplayBook.Viewer/Resources/Raw/DisplayBookViewer/EpubText.js`,
in headless Chromium (the engine behind the Windows reader's WebView2 and Android's WebView), with
JavaScript coverage written as LCOV for SonarQube Cloud.

```powershell
cd tests\DisplayBook.ReaderTests
npm ci
npx playwright install chromium
npx playwright test
```

`tools/coverage.ps1` runs them too, and writes `artifacts/coverage/reader-js/lcov.info`.

## How the reader is hosted

`tests/support/reader.js` loads the real `index.html` and `EpubText.js` from the repository and
answers every request to `https://displaybook.invalid` itself, the host name ReaderAssetHost uses on
Windows: the shell files, a generated four-chapter combined document (the shape
CombinedDocumentBuilder produces), and its cover image.

The reader talks to the app by navigating to its bridge URL, which it takes from the `bridge` query
parameter. The tests point it at `https://displaybook.invalid/bridge`, so each message becomes a
request the route records (in `reader.messages`) and then aborts, as EpubReaderView cancels it.
Two messages sent in the same task coalesce (the second navigation replaces the first), in the app
as here, so assert on the page's state, or on messages that are sent on their own.
