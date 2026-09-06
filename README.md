# Display Book EPUB Reader

This prototype is a browser-based reflowable EPUB reader. It uses a same-origin iframe for the active EPUB resource and CSS multi-column layout for horizontal pagination.

## Run it

Install Node.js 18 or newer, then start the local server from this directory:

```text
npm start
```

The server selects an available random port and prints the reader URL. Open that URL in a browser.

## Included behavior

- Reads the publication title, author, manifest, and spine from `content.opf`.
- Loads the canonical unpacked EPUB resources without rewriting the book files.
- Injects the local Readium CSS layers and navigator pagination styles after each resource loads.
- Turns pages horizontally with the Previous/Next buttons, keyboard controls, click zones, and touch swipes.
- Crosses chapter boundaries when turning past the first or last page of a spine resource.
- Builds the table of contents from `nav.xhtml`.
- Recalculates page counts when the reader viewport is resized.

## Scope

This is a reflowable EPUB prototype. EPUB archive parsing, DRM, fixed-layout publications, annotations, search, and advanced media overlays are outside the current scope.