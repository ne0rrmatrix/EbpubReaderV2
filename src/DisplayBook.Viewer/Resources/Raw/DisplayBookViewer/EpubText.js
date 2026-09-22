(() => {
    "use strict";

    // The native side calls into these (see EpubReaderView's EvaluateJavaScriptAsync calls), so
    // the property is genuinely added to window rather than being a stray global.
    /** @type {Window & { DisplayBookReader?: Record<string, Function> }} */
    const readerWindow = window;

    const query = new URLSearchParams(window.location.search);
    const BRIDGE_URL = query.get("bridge") ?? "displaybook://bridge";
    const FRAME_STYLE_ID = "display-book-pagination-style";
    const VIRTUAL_COLUMN_CLASS = "display-book-virtual-column";
    const MIN_SWIPE_DISTANCE = 42;
    const SETTINGS_STORAGE_KEY = "displaybook.reader.settings.v1";
    const SETTINGS_STORAGE_VERSION = 1;
    const WIDE_VIEWPORT_MINIMUM = 1200;
    const DEFAULT_SETTINGS = Object.freeze({
        theme: "original",
        fontFamily: "original",
        fontSize: "100%",
        lineHeight: "original",
        paginationMode: "paged",
        columnMode: "single",
        columnCount: "1",
        lineLength: "100%",
        textAlignment: "auto",
        hyphenation: "original",
        paragraphSpacing: "original",
        paragraphIndent: "original",
        wordSpacing: "0",
        letterSpacing: "normal",
        fontWeight: "original",
        imageTreatment: "normal"
    });
    /** @type {Record<string, Set<string>>} */
    const SETTING_CHOICES = {
        theme: new Set(["original", "paper", "sepia", "night"]),
        fontFamily: new Set(["original", "serif", "sans", "humanist", "monospace"]),
        paginationMode: new Set(["paged", "scroll"]),
        columnMode: new Set(["single", "two"]),
        lineLength: new Set(["100%", "75ch", "65ch", "55ch"]),
        textAlignment: new Set(["auto", "left", "justify"]),
        hyphenation: new Set(["original", "auto", "none"]),
        paragraphSpacing: new Set(["original", "0", "0.35em", "0.7em"]),
        paragraphIndent: new Set(["original", "0", "1em", "2em"]),
        wordSpacing: new Set(["0", "0.08em", "0.16em"]),
        letterSpacing: new Set(["normal", "0.03em", "0.06em"]),
        fontWeight: new Set(["original", "normal", "600", "700"]),
        imageTreatment: new Set(["normal", "dim", "invert", "dim-invert"])
    };

    // console.info(label, dataObject) reaches Chrome DevTools (chrome://inspect / Safari Web
    // Inspector / WebView2 DevTools) with the object fully expandable, but Android's
    // WebChromeClient.OnConsoleMessage bridge (used to forward these into logcat/Debug Output
    // for on-device diagnosis without attaching a remote debugger) only sees Chromium's
    // flattened single-string rendering of the console call, which reduces any object argument
    // to "[object Object]". Stringifying it ourselves keeps the data readable everywhere.
    function logDiagnostic(label, data) {
        console.info(`${label} ${JSON.stringify(data)}`);
    }

    // Annotated with the element type each id actually resolves to in index.html, which ships with
    // this script and is the only document it ever runs in -- so these lookups genuinely can't
    // miss. Without the annotations every entry is HTMLElement|null to tooling, which can't tell
    // an <iframe>'s contentDocument or an <input>'s value from a typo.
    const elements = {
        author: /** @type {HTMLElement} */ (document.getElementById("book-author")),
        bookTitle: /** @type {HTMLElement} */ (document.getElementById("book-title")),
        closeContents: /** @type {HTMLButtonElement} */ (document.getElementById("contents-close")),
        contentsList: /** @type {HTMLOListElement} */ (document.getElementById("contents-list")),
        contentsPanel: /** @type {HTMLElement} */ (document.getElementById("contents-panel")),
        contentsToggle: /** @type {HTMLButtonElement} */ (document.getElementById("contents-toggle")),
        error: /** @type {HTMLElement} */ (document.getElementById("reader-error")),
        frame: /** @type {HTMLIFrameElement} */ (document.getElementById("page")),
        loading: /** @type {HTMLElement} */ (document.getElementById("reader-loading")),
        loadingCover: /** @type {HTMLImageElement} */ (document.getElementById("reader-loading-cover")),
        loadingLabel: /** @type {HTMLElement} */ (document.getElementById("reader-loading-label")),
        location: /** @type {HTMLElement} */ (document.getElementById("reader-location")),
        lookupButton: /** @type {HTMLButtonElement} */ (document.getElementById("lookup-button")),
        next: /** @type {HTMLButtonElement} */ (document.getElementById("next-page")),
        previous: /** @type {HTMLButtonElement} */ (document.getElementById("previous-page")),
        progress: /** @type {HTMLElement} */ (document.getElementById("progress-value")),
        progressSlider: /** @type {HTMLInputElement} */ (document.getElementById("book-progress")),
        readerBack: /** @type {HTMLButtonElement} */ (document.getElementById("reader-back")),
        readerShell: /** @type {HTMLElement} */ (document.querySelector(".reader-shell")),
        settingsClose: /** @type {HTMLButtonElement} */ (document.getElementById("settings-close")),
        settingsForm: /** @type {HTMLFormElement} */ (document.getElementById("settings-form")),
        settingsPanel: /** @type {HTMLElement} */ (document.getElementById("settings-panel")),
        settingsReset: /** @type {HTMLButtonElement} */ (document.getElementById("settings-reset")),
        settingsToggle: /** @type {HTMLButtonElement} */ (document.getElementById("settings-toggle")),
        viewport: /** @type {HTMLElement} */ (document.getElementById("book-viewport"))
    };

    /**
     * Mirrors the payload records the native side sends over the bridge -- EpubSpineItem,
     * EpubTocEntry and getSelectionInfo()'s result respectively. Kept here as types only: the
     * shapes themselves are defined in C# (Models/ReaderPublicationPayload.cs) and are duck-typed
     * across the boundary, so these have to be updated alongside those records.
     * @typedef {{ href: string, index: number }} SpineItem
     * @typedef {{ label: string, spineIndex: number, fragment: string }} TocEntry
     * @typedef {{ text: string, left: number, top: number, right: number, bottom: number }} LookupRect
     */

    const state = {
        currentPage: 0,
        currentSpineIndex: 0,
        chromeVisible: false,
        isReady: false,
        loadToken: 0,
        readerReadyNotified: false,
        metadata: { author: "", title: "" },
        /** @type {string | null} */
        coverHref: null,
        pageCount: 1,
        /** @type {{ resolve: (value?: unknown) => void, reject: (reason?: unknown) => void } | null} */
        pendingFrameLoad: null,
        pendingLookupText: "",
        /** @type {LookupRect | null} */
        pendingLookupRect: null,
        resizeTimer: 0,
        safeAreaInsets: { top: 0, bottom: 0 },
        /** @type {SpineItem[]} */
        spine: [],
        /** @type {TocEntry[]} */
        toc: [],
        viewportWidth: 1,
        settings: loadSettings(),
        progressSeek: {
            isLoading: false,
            /** @type {number | null} */
            pendingValue: null,
            /** @type {ScrollBehavior} */
            behavior: "auto"
        }
    };

    function normalizeSettingValue(name, value, fallback) {
        if (typeof value !== "string") {
            return fallback;
        }
        if (SETTING_CHOICES[name]?.has(value)) {
            return value;
        }
        if (name === "columnCount" && (value === "1" || value === "2")) {
            return value;
        }
        if (name === "fontSize" && /^(?:8[5-9]|9\d|1[0-4]\d|150)%$/u.test(value)) {
            return value;
        }
        if (name === "lineHeight" && (value === "original" || /^(?:1\.[2-9]|2(?:\.0)?)$/u.test(value))) {
            return value;
        }
        return fallback;
    }

    /**
     * Every reader setting, keyed exactly as DEFAULT_SETTINGS is. Declared as a type of its own
     * because the settings object is built by copying keys out of DEFAULT_SETTINGS at runtime:
     * without this, the value that reaches getEffectiveSettings()/applySettingsToFrame() is
     * inferred as an empty object and none of the setting names are known to exist on it.
     * @typedef {{ -readonly [K in keyof typeof DEFAULT_SETTINGS]: string }} ReaderSettings
     */

    /**
     * @param {Partial<ReaderSettings>} [settings]
     * @param {ReaderSettings} [base]
     * @returns {ReaderSettings}
     */
    function normalizeSettings(settings, base = DEFAULT_SETTINGS) {
        const normalized = /** @type {ReaderSettings} */ ({});
        for (const name of /** @type {(keyof ReaderSettings)[]} */ (Object.keys(DEFAULT_SETTINGS))) {
            normalized[name] = normalizeSettingValue(name, settings?.[name], base[name]);
        }
        if (normalized.columnMode === "single") {
            normalized.columnCount = "1";
        } else if (normalized.columnMode === "two") {
            normalized.columnCount = "2";
        }
        return normalized;
    }

    function loadSettings() {
        try {
            const rawValue = window.localStorage.getItem(SETTINGS_STORAGE_KEY);
            if (!rawValue) {
                return { ...DEFAULT_SETTINGS };
            }
            const stored = JSON.parse(rawValue);
            if (stored?.version !== SETTINGS_STORAGE_VERSION || typeof stored.settings !== "object") {
                return { ...DEFAULT_SETTINGS };
            }
            return normalizeSettings(stored.settings);
        } catch {
            return { ...DEFAULT_SETTINGS };
        }
    }

    function persistSettings() {
        try {
            window.localStorage.setItem(SETTINGS_STORAGE_KEY, JSON.stringify({
                version: SETTINGS_STORAGE_VERSION,
                settings: state.settings
            }));
        } catch {
            // Settings remain active for this session when storage is unavailable.
        }
    }

    function getEffectiveSettings() {
        const canUseTwoColumns = state.settings.columnMode === "two" && window.innerWidth >= WIDE_VIEWPORT_MINIMUM;
        const useTwoColumns = !state.chromeVisible && canUseTwoColumns;
        const columnCount = useTwoColumns ? "2" : "1";
        return {
            ...state.settings,
            columnCount
        };
    }

    function applySettingsToFrame(frameDocument) {
        const root = frameDocument?.documentElement;
        if (!root) {
            return;
        }

        const settings = getEffectiveSettings();
        const theme = {
            paper: {
                background: "#ffffff",
                text: "#202020",
                link: "#075985",
                chromeSurface: "#f1f3f5",
                chromeInk: "#202020",
                chromeMuted: "#64748b",
                chromeBorder: "rgba(32, 32, 32, 0.18)",
                chromeAccent: "#075985"
            },
            sepia: {
                background: "#f6f1e8",
                text: "#2c241b",
                link: "#7c3f00",
                chromeSurface: "#eadfce",
                chromeInk: "#2c241b",
                chromeMuted: "#75685a",
                chromeBorder: "rgba(44, 36, 27, 0.18)",
                chromeAccent: "#8a5a2b"
            },
            night: {
                background: "#1c2430",
                text: "#e9edf2",
                link: "#8bd7ff",
                chromeSurface: "#263846",
                chromeInk: "#f4f6f8",
                chromeMuted: "#b7c4d1",
                chromeBorder: "rgba(255, 255, 255, 0.18)",
                chromeAccent: "#66e0c1"
            }
        };
        // The app chrome (toolbars, panels) always needs a look, even when the
        // book content itself is left in its unstyled "original" state.
        const chromePreset = theme[settings.theme] ?? theme.paper;
        const contentTheme = theme[settings.theme];
        document.documentElement.style.setProperty("--reader-surface", chromePreset.background);
        document.documentElement.style.setProperty("--reader-chrome-surface", chromePreset.chromeSurface);
        document.documentElement.style.setProperty("--reader-chrome-ink", chromePreset.chromeInk);
        document.documentElement.style.setProperty("--reader-chrome-muted", chromePreset.chromeMuted);
        document.documentElement.style.setProperty("--reader-chrome-border", chromePreset.chromeBorder);
        document.documentElement.style.setProperty("--reader-chrome-accent", chromePreset.chromeAccent);
        const fontFamily = {
            serif: '"Iowan Old Style", Georgia, serif',
            sans: '"Segoe UI", Arial, sans-serif',
            humanist: 'Calibri, "Segoe UI", sans-serif',
            monospace: 'ui-monospace, "Cascadia Code", Consolas, monospace'
        }[settings.fontFamily];
        const imageTreatment = {
            normal: { darken: "1", invert: "0" },
            dim: { darken: "0.8", invert: "0" },
            invert: { darken: "1", invert: "1" },
            "dim-invert": { darken: "0.8", invert: "1" }
        }[settings.imageTreatment];

        const variables = {
            "--USER__backgroundColor": contentTheme?.background,
            "--USER__textColor": contentTheme?.text,
            "--USER__linkColor": contentTheme?.link,
            "--USER__colCount": settings.columnCount,
            "--USER__lineLength": settings.lineLength,
            "--USER__textAlign": settings.textAlignment === "auto" ? undefined : settings.textAlignment,
            "--USER__bodyHyphens": settings.hyphenation === "original" ? undefined : settings.hyphenation,
            "--USER__fontFamily": fontFamily,
            "--USER__fontSize": settings.fontSize,
            "--USER__lineHeight": settings.lineHeight === "original" ? undefined : settings.lineHeight,
            "--USER__paraSpacing": settings.paragraphSpacing === "original" ? undefined : settings.paragraphSpacing,
            "--USER__paraIndent": settings.paragraphIndent === "original" ? undefined : settings.paragraphIndent,
            "--USER__wordSpacing": settings.wordSpacing,
            "--USER__letterSpacing": settings.letterSpacing,
            "--USER__fontWeight": settings.fontWeight === "original" ? undefined : settings.fontWeight,
            "--USER__darkenImages": imageTreatment.darken,
            "--USER__invertImages": imageTreatment.invert,
            "--reader-safe-area-inset-top": `${state.safeAreaInsets.top}px`,
            "--reader-safe-area-inset-bottom": `${state.safeAreaInsets.bottom}px`
        };
        for (const [name, value] of Object.entries(variables)) {
            if (value === undefined) {
                root.style.removeProperty(name);
            } else {
                root.style.setProperty(name, value);
            }
        }
        notifyNative("themeChanged", {
            theme: settings.theme,
            background: chromePreset.background
        });
        if (settings.paginationMode === "scroll") {
            root.style.setProperty("readium-scroll-on", "");
        } else {
            root.style.removeProperty("readium-scroll-on");
        }

        if (settings.columnMode === "two") {
            logDiagnostic("DisplayBook reader columns", {
                viewportWidth: window.innerWidth,
                minimumViewportWidth: WIDE_VIEWPORT_MINIMUM,
                requestedColumnCount: state.settings.columnCount,
                effectiveColumnCount: settings.columnCount,
                renderedColumnCount: getComputedStyle(root).columnCount,
                renderedColumnWidth: getComputedStyle(root).columnWidth
            });
        }
    }

    function updateSettingsControl(control, name, value, isOriginalLineHeight) {
        if (control instanceof RadioNodeList) {
            for (const radio of control) {
                radio.checked = radio.value === value;
            }
        } else if (control instanceof HTMLInputElement || control instanceof HTMLSelectElement) {
            if (name === "fontSize") {
                control.value = value.replace("%", "");
            } else {
                control.value = isOriginalLineHeight ? "1.5" : value;
            }
        }
    }

    function updateSettingsOutput(form, name, value, isOriginalLineHeight) {
        const output = form.querySelector(`[data-for="${name}"]`);
        if (output) {
            output.textContent = isOriginalLineHeight ? "Original" : value;
        }
    }

    function updateSettingsControls() {
        const form = elements.settingsForm;
        if (!form) {
            return;
        }
        for (const [name, value] of Object.entries(state.settings)) {
            const control = form.elements.namedItem(name);
            const isOriginalLineHeight = name === "lineHeight" && value === "original";
            updateSettingsControl(control, name, value, isOriginalLineHeight);
            updateSettingsOutput(form, name, value, isOriginalLineHeight);
        }
    }

    function getScrollPositionRatio() {
        const scroller = getFrameScroller();
        if (!scroller) {
            return 0;
        }
        const maximum = Math.max(0, scroller.scrollHeight - scroller.clientHeight);
        return maximum > 0 ? scroller.scrollTop / maximum : 0;
    }

    function restoreScrollPosition(ratio) {
        const scroller = getFrameScroller();
        if (!scroller) {
            return;
        }
        const maximum = Math.max(0, scroller.scrollHeight - scroller.clientHeight);
        scroller.scrollTop = Math.min(maximum, Math.max(0, ratio * maximum));
        scroller.scrollLeft = 0;
    }

    function notifyNative(type, payload = {}) {
        const message = JSON.stringify({ type, payload });
        window.location.href = `${BRIDGE_URL}?message=${encodeURIComponent(message)}`;
    }

    function getAbsoluteUrl(href, baseUrl) {
        return new URL(href, baseUrl).href;
    }

    // Only for locators coming from native (setLocator): those carry the portable
    // OPF-relative href every current build reports and syncs, not an absolute URL, since an
    // absolute URL embeds this device's own local hosting path and would never match a
    // locator synced from a different device.
    function getSpineIndexByRelativeHref(relativeHref) {
        const normalized = relativeHref.replace(/^\.\//u, "").split("#")[0];
        return state.spine.findIndex((item) => item.href.replace(/^\.\//u, "") === normalized);
    }

    // Compatibility fallback for locators saved to this device's own database before the
    // reader switched to the portable relative-href format above -- those stored the
    // device-local absolute resource URL instead. Matched by comparing the URL's path tail
    // against a spine href rather than an exact absolute-URL match, since this device's local
    // hosting path (and now the reserved combined-document path) has no fixed relationship to
    // the value a much older build would have saved.
    function getSpineIndexByLegacyAbsoluteHref(resourceHref) {
        let pathname;
        try {
            pathname = new URL(resourceHref).pathname;
        } catch {
            return -1;
        }
        const normalized = decodeURIComponent(pathname).replace(/^\/+/u, "");
        return state.spine.findIndex((item) => normalized === item.href || normalized.endsWith(`/${item.href}`));
    }

    // Resolves an <a href> as authored in its ORIGINAL chapter (the combined document
    // deliberately leaves these unrewritten -- see CombinedDocumentBuilder -- so they're still
    // relative to that chapter's own directory, not the combined document's location) against a
    // synthetic base standing in for that chapter's location, then strips back down to a plain
    // epub-relative path to match against state.spine.
    function resolveSpineIndexForHref(rawHref, baseChapterHref) {
        let resolved;
        try {
            resolved = new URL(rawHref, getAbsoluteUrl(baseChapterHref, "https://displaybook-epub.invalid/"));
        } catch {
            return { spineIndex: -1, fragment: "" };
        }
        const relativePath = decodeURIComponent(resolved.pathname.replace(/^\/+/u, ""));
        return {
            spineIndex: state.spine.findIndex((item) => item.href === relativePath),
            fragment: resolved.hash
        };
    }

    function getChapterTitle(spineIndex) {
        const tocItem = state.toc.find((entry) => entry.spineIndex === spineIndex);
        return tocItem?.label?.trim() ?? "";
    }

    function applyLoadingCover() {
        const cover = elements.loadingCover;
        if (!cover) {
            return;
        }
        if (!state.coverHref) {
            cover.hidden = true;
            return;
        }
        cover.hidden = true;
        cover.onload = () => {
            cover.removeAttribute("hidden");
        };
        cover.onerror = () => {
            cover.hidden = true;
        };
        cover.src = state.coverHref;
    }

    function setError(error) {
        const message = error instanceof Error ? error.message : String(error);
        elements.error.textContent = message;
        elements.error.hidden = false;
        elements.loading.hidden = true;
        elements.location.textContent = "Unable to open publication";
        notifyNative("readerError", { message });
        console.error(error);
    }

    function setLoading(isLoading, message = "Loading publication…") {
        elements.loadingLabel.textContent = message;
        elements.loading.hidden = !isLoading;
    }

    function createPaginationStyle(documentElement) {
        const style = documentElement.createElement("style");
        style.id = FRAME_STYLE_ID;
        style.textContent = `
            :root {
                --reader-column-width: 100vw;
                --reader-page-gutter: clamp(16px, 5vw, 72px);
                width: 100% !important;
                min-width: 100% !important;
                max-width: 100% !important;
                height: 100vh !important;
                min-height: 100vh !important;
                max-height: 100vh !important;
                column-width: var(--reader-column-width) !important;
                column-count: var(--USER__colCount, 1) !important;
                column-gap: 0 !important;
                column-fill: auto !important;
                overflow-x: auto !important;
                overflow-y: hidden !important;
                scrollbar-width: none;
                margin: 0 !important;
                padding-inline: 0 !important;
                /* Reserves space for the status bar/notch (top) and navigation bar
                   (bottom) on every column, not just the first/last: the reader
                   window draws edge-to-edge (see ReaderPage's SafeAreaEdges="None"),
                   and Android WebView has no native env(safe-area-inset-*) support,
                   so the native side pushes real inset values in via
                   setSafeAreaInsets(). Without this, a line of text can lay out
                   underneath an opaque system bar -- invisible, but already
                   consumed by pagination's page-count math, so it never reappears
                   on the next page either. */
                padding-top: var(--reader-safe-area-inset-top, env(safe-area-inset-top, 0px)) !important;
                padding-bottom: var(--reader-safe-area-inset-bottom, env(safe-area-inset-bottom, 0px)) !important;
                box-sizing: border-box !important;
                background: var(--USER__backgroundColor, transparent) !important;
            }

            :root::-webkit-scrollbar {
                display: none;
            }

            :root.cover-page {
                column-width: 100vw !important;
                column-count: 1 !important;
                width: 100vw !important;
                min-width: 100vw !important;
                max-width: 100vw !important;
                padding-top: 0 !important;
                padding-bottom: 0 !important;
                overflow: hidden !important;
            }

            :root[style*="readium-scroll-on"] {
                width: 100% !important;
                min-width: 100% !important;
                max-width: 100% !important;
                height: auto !important;
                min-height: 100% !important;
                max-height: none !important;
                column-width: auto !important;
                column-count: auto !important;
                overflow-x: hidden !important;
                overflow-y: auto !important;
            }

            body {
                width: min(100%, calc(var(--USER__lineLength, 100%) + (2 * var(--reader-page-gutter)))) !important;
                min-width: 0 !important;
                max-width: 100% !important;
                min-height: 100% !important;
                margin-left: auto !important;
                margin-right: auto !important;
                padding: 2.25rem var(--reader-page-gutter) !important;
                box-sizing: border-box !important;
                overflow: visible !important;
                background: transparent !important;
            }

            /* Every chapter is a <section> directly under body (see
               CombinedDocumentBuilder); only one is ever visible at a time. EPUB
               chapters sometimes add asymmetric margins to a direct wrapper --
               re-center that wrapper (now one level deeper than body itself)
               without changing paragraph indentation. Left unimportant so a
               publication's own margin/alignment rules (e.g. a class deliberately
               left- or right-aligning a block) still win by specificity instead of
               being forced back to center. */
            body > section[data-chapter-index] > * {
                max-width: 100% !important;
                margin-left: auto;
                margin-right: auto;
                box-sizing: border-box !important;
            }

            /* Some publications use width_10 as a chapter wrapper with a large
               asymmetric percentage margin. Keep the wrapper in the reader column. */
            body .width_10 {
                width: 100% !important;
                max-width: 100% !important;
                margin-left: 0 !important;
                margin-right: 0 !important;
            }

            :root[style*="--USER__fontFamily"] body,
            :root[style*="--USER__fontFamily"] body * {
                font-family: var(--USER__fontFamily) !important;
            }

            :root[style*="readium-scroll-on"] body {
                width: min(100%, calc(var(--USER__lineLength, 100%) + (2 * var(--reader-page-gutter)))) !important;
                max-width: 100% !important;
                min-height: 100% !important;
                overflow: visible !important;
            }

            :root.cover-page section[data-chapter-index].cover-page {
                display: flex !important;
                align-items: center !important;
                justify-content: center !important;
                width: 100vw !important;
                max-width: none !important;
                height: 100vh !important;
                min-height: 100vh !important;
                padding: 0 !important;
                margin: 0 !important;
                overflow: hidden !important;
            }

            :root.cover-page section[data-chapter-index].cover-page img,
            :root.cover-page section[data-chapter-index].cover-page svg {
                display: block !important;
                width: auto !important;
                height: auto !important;
                max-width: 100vw !important;
                max-height: 100vh !important;
                margin: 0 !important;
                object-fit: contain !important;
            }

            :root.cover-page section[data-chapter-index].cover-page svg {
                width: 100vw !important;
                height: 100vh !important;
            }

            img, svg, video, canvas, iframe {
                max-width: 100% !important;
            }

            /* Blank filler columns appended by padColumnsToFullSpread. They exist
               only to occupy a column box, so every publication style that could
               give a bare <div> a size or a float has to be neutralised. */
            .${VIRTUAL_COLUMN_CLASS} {
                break-before: column;
                -webkit-column-break-before: always;
                display: block !important;
                float: none !important;
                width: auto !important;
                min-height: 0 !important;
                max-height: none !important;
                margin: 0 !important;
                padding: 0 !important;
                border: 0 !important;
                font-size: 1px !important;
                line-height: 0 !important;
                visibility: hidden !important;
            }

            h1, h2, h3, h4, h5, h6, figure, blockquote, img, svg, video, table {
                break-inside: avoid;
            }

            a, button, input, select, textarea {
                -webkit-user-select: auto;
                user-select: auto;
            }
        `;
        documentElement.head.appendChild(style);
    }

    // Re-run every time the visible chapter changes (see showSection), not just once: each
    // chapter is its own <section> sharing the one combined document, so "is the current
    // chapter a cover page" has to be re-evaluated per section instead of once per (previously
    // separate) chapter document.
    function applyCoverPageLayout(frameDocument) {
        const root = frameDocument.documentElement;
        const section = getActiveSectionElement();
        if (!root || !section) {
            return;
        }

        const media = section.querySelectorAll("img, svg");
        const text = (section.textContent ?? "").replace(/\s+/gu, "").trim();
        const hasOnlyCoverMedia = media.length === 1 &&
            !section.querySelector("video, audio, canvas, table, form") &&
            text.length === 0;
        const isCoverPage = section.classList.contains("cover-page") || hasOnlyCoverMedia;

        if (isCoverPage) {
            for (const svg of section.querySelectorAll("svg")) {
                svg.setAttribute("preserveAspectRatio", "xMidYMid meet");
            }
        }

        root.classList.toggle("cover-page", isCoverPage);
        for (const candidate of frameDocument.querySelectorAll("section[data-chapter-index]")) {
            candidate.classList.toggle("cover-page", candidate === section && isCoverPage);
        }
    }

    function getFrameScroller() {
        const frameDocument = elements.frame.contentDocument;
        return frameDocument?.scrollingElement ?? frameDocument?.documentElement ?? null;
    }

    function getScrollWidth() {
        const frameDocument = elements.frame.contentDocument;
        if (!frameDocument) {
            return state.viewportWidth;
        }

        const root = frameDocument.documentElement;
        const body = frameDocument.body;
        return Math.max(
            state.viewportWidth,
            root?.scrollWidth ?? 0,
            body?.scrollWidth ?? 0
        );
    }

    // How many columns the root multi-column container is actually rendering per viewport
    // right now -- read back from the computed style rather than taken from the settings,
    // because a cover page is forced to a single full-width column (:root.cover-page) even
    // while the two-column setting is on.
    function getRenderedColumnsPerPage(frameDocument) {
        const view = frameDocument.defaultView;
        const root = frameDocument.documentElement;
        if (!view || !root) {
            return 1;
        }

        const computed = view.getComputedStyle(root);
        const columnWidth = Number.parseFloat(computed.columnWidth);
        const widthBasedCount = Number.isFinite(columnWidth) && columnWidth > 0
            ? Math.max(1, Math.floor(state.viewportWidth / columnWidth))
            : 1;
        const declaredCount = Number.parseInt(computed.columnCount, 10);
        // With both column-count and column-width set, the used count is the smaller of the two.
        return Number.isFinite(declaredCount) && declaredCount > 0
            ? Math.min(declaredCount, widthBasedCount)
            : widthBasedCount;
    }

    function clearVirtualColumns(frameDocument) {
        for (const stale of frameDocument.body?.querySelectorAll(`.${VIRTUAL_COLUMN_CLASS}`) ?? []) {
            stale.remove();
        }
    }

    // CSS multi-column lays a chapter out into as many columns as its text needs, which in a
    // two-column spread is often an odd number. The scroller can only ever reach
    // scrollWidth - viewportWidth, so the final spread gets clamped back by half a page and
    // re-shows the previous spread's right-hand column next to the real last one -- content
    // that looks duplicated at the end of the chapter. Pad the flow out to a whole number of
    // spreads with empty "virtual" columns instead (the same trick Readium's navigator plays
    // with its readium-virtual-page elements), so the last scroll position is a real page
    // boundary and the leftover half-spread renders blank.
    //
    // Runs before every getScrollWidth()-based measurement, and clears its own previous padding
    // first: leaving it in would fold the last measurement's padding into this one's width.
    function padColumnsToFullSpread(frameDocument) {
        const body = frameDocument.body;
        if (!body) {
            return;
        }

        clearVirtualColumns(frameDocument);

        const columnsPerPage = getRenderedColumnsPerPage(frameDocument);
        if (columnsPerPage < 2) {
            return;
        }

        const columnWidth = state.viewportWidth / columnsPerPage;
        const totalColumns = Math.round(getScrollWidth() / columnWidth);
        const orphanColumns = totalColumns % columnsPerPage;
        if (orphanColumns === 0) {
            return;
        }

        // Where a forced column break isn't supported, a full-viewport-height filler is what
        // pushes the next column instead -- it can't share a column with the text above it.
        const supportsColumnBreak = typeof CSS?.supports === "function" &&
            (CSS.supports("break-before", "column") || CSS.supports("-webkit-column-break-before", "always"));
        const fillerHeight = supportsColumnBreak
            ? "0px"
            : `${frameDocument.documentElement.clientHeight}px`;

        for (let i = orphanColumns; i < columnsPerPage; i++) {
            const filler = frameDocument.createElement("div");
            filler.className = VIRTUAL_COLUMN_CLASS;
            filler.setAttribute("aria-hidden", "true");
            filler.style.height = fillerHeight;
            // A completely empty element generates no line box and so can't force a column
            // break of its own; a zero-width space gives it something to break before.
            filler.textContent = "​";
            body.appendChild(filler);
        }
    }

    function getActiveSectionElement() {
        return elements.frame.contentDocument?.getElementById(`chapter-${state.currentSpineIndex}`) ?? null;
    }

    // Device-independent reading position: a character offset into the CURRENT CHAPTER's text
    // (counted across all SHOW_TEXT nodes under its <section> in document order), independent of
    // how the current device paginates the chapter into columns/pages. Sampled/resolved via the
    // same Range API getSelectionInfo() already uses, just driven by caretRangeFromPoint instead
    // of a user selection.
    const CHAR_OFFSET_SAMPLE_INSET_X = 6;
    const CHAR_OFFSET_SAMPLE_Y_FRACTIONS = [0.15, 0.35, 0.5, 0.65, 0.85];

    function getCaretRangeAtPoint(frameDocument, x, y) {
        if (typeof frameDocument.caretRangeFromPoint === "function") {
            return frameDocument.caretRangeFromPoint(x, y);
        }
        if (typeof frameDocument.caretPositionFromPoint === "function") {
            const position = frameDocument.caretPositionFromPoint(x, y);
            if (!position?.offsetNode) {
                return null;
            }
            const range = frameDocument.createRange();
            range.setStart(position.offsetNode, position.offset);
            range.collapse(true);
            return range;
        }
        return null;
    }

    function textOffsetOfRange(frameDocument, range) {
        const root = getActiveSectionElement();
        if (!range?.startContainer || !root) {
            return null;
        }
        const walker = frameDocument.createTreeWalker(root, NodeFilter.SHOW_TEXT);
        let offset = 0;
        let node = walker.nextNode();
        while (node) {
            if (node === range.startContainer) {
                return offset + range.startOffset;
            }
            offset += node.textContent.length;
            node = walker.nextNode();
        }
        return null;
    }

    function getCharOffsetAtViewportStart() {
        const frameDocument = elements.frame.contentDocument;
        if (!frameDocument?.body) {
            return null;
        }

        const frameHeight = Math.max(1, elements.frame.clientHeight);
        for (const fraction of CHAR_OFFSET_SAMPLE_Y_FRACTIONS) {
            const range = getCaretRangeAtPoint(frameDocument, CHAR_OFFSET_SAMPLE_INSET_X, Math.round(frameHeight * fraction));
            const offset = textOffsetOfRange(frameDocument, range);
            if (offset !== null) {
                return offset;
            }
        }
        return null;
    }

    function findRangeAtTextOffset(frameDocument, targetOffset) {
        const root = getActiveSectionElement();
        if (!root) {
            return null;
        }
        const walker = frameDocument.createTreeWalker(root, NodeFilter.SHOW_TEXT);
        let offset = 0;
        let node = walker.nextNode();
        let lastNode = null;
        while (node) {
            const length = node.textContent.length;
            if (targetOffset <= offset + length) {
                const range = frameDocument.createRange();
                const localOffset = Math.max(0, Math.min(length, targetOffset - offset));
                range.setStart(node, localOffset);
                range.collapse(true);
                return range;
            }
            offset += length;
            lastNode = node;
            node = walker.nextNode();
        }
        if (lastNode) {
            const range = frameDocument.createRange();
            range.setStart(lastNode, lastNode.textContent.length);
            range.collapse(true);
            return range;
        }
        return null;
    }

    function scrollRangeIntoView(range) {
        const scroller = getFrameScroller();
        if (!range || !scroller) {
            return false;
        }

        const target = range.startContainer.nodeType === Node.ELEMENT_NODE
            ? range.startContainer
            : range.startContainer.parentElement;

        if (state.settings.paginationMode === "scroll") {
            if (!target) {
                return false;
            }
            target.scrollIntoView({ block: "start" });
            return true;
        }

        // Prefer the Range's own rect (most precise -- it's the exact character position, not
        // just its containing element); fall back to the containing element's rect when the
        // Range one is empty, which happens on some Chromium builds (observed on Android's
        // WebView, not WebView2) for a collapsed Range positioned exactly at a text-node
        // boundary. Either way, compute and apply the target page ourselves via the same
        // scrollToCurrentPage() every other navigation path uses, rather than calling the
        // element's scrollIntoView() and immediately reading scrollLeft back: scrollIntoView's
        // effect on scrollLeft is not guaranteed synchronous, and reading it back on the very
        // next line raced a still-in-flight scroll on Android, reporting page 0 (wherever the
        // scroller happened to already be) before correcting itself moments later once a
        // "scroll" event caught up -- which looked, from the native side, like an accepted
        // synced position silently failing to apply.
        const rect = range.getClientRects()[0] ?? target?.getBoundingClientRect();
        if (!rect) {
            return false;
        }

        const targetLeft = scroller.scrollLeft + rect.left;
        state.currentPage = Math.max(0, Math.min(state.pageCount - 1, Math.round(targetLeft / state.viewportWidth)));
        scrollToCurrentPage();
        return true;
    }

    function resolveCharOffset(offset) {
        if (typeof offset !== "number" || offset < 0) {
            return false;
        }
        const frameDocument = elements.frame.contentDocument;
        if (!frameDocument?.body) {
            return false;
        }
        const range = findRangeAtTextOffset(frameDocument, offset);
        const applied = scrollRangeIntoView(range);
        logDiagnostic("DisplayBook reader resolveCharOffset", {
            offset,
            spineIndex: state.currentSpineIndex,
            rangeFound: range !== null,
            applied,
            resultingPage: state.currentPage,
            pageCount: state.pageCount,
            viewportWidth: state.viewportWidth
        });
        return applied;
    }

    function updateProgress() {
        const position = state.currentSpineIndex + ((state.currentPage + 1) / Math.max(1, state.pageCount));
        const percentage = Math.min(100, Math.max(0, (position / state.spine.length) * 100));
        elements.progress.style.width = `${percentage}%`;
        elements.progressSlider.value = String(Math.round(percentage * 10));
        elements.progressSlider.setAttribute("aria-valuetext", `${Math.round(percentage)}% through book`);
    }

    function previewProgress(value) {
        const percentage = Math.min(100, Math.max(0, Number(value) / 10));
        elements.progress.style.width = `${percentage}%`;
        elements.progressSlider.setAttribute("aria-valuetext", `${Math.round(percentage)}% through book`);
    }

    function updateTocHighlight() {
        const links = /** @type {NodeListOf<HTMLAnchorElement>} */ (
            elements.contentsList.querySelectorAll("a[data-spine-index]"));
        for (const link of links) {
            const isCurrent = Number(link.dataset.spineIndex) === state.currentSpineIndex;
            if (isCurrent) {
                link.setAttribute("aria-current", "page");
            } else {
                link.removeAttribute("aria-current");
            }
        }
    }

    function updateUi() {
        const item = state.spine[state.currentSpineIndex];
        const isScrollMode = state.settings.paginationMode === "scroll";
        const atFirstPage = state.currentSpineIndex === 0 && state.currentPage === 0;
        const atLastPage = state.currentSpineIndex === state.spine.length - 1 && state.currentPage >= state.pageCount - 1;
        elements.previous.disabled = !state.isReady || (isScrollMode ? state.currentSpineIndex === 0 : atFirstPage);
        elements.next.disabled = !state.isReady || (isScrollMode ? state.currentSpineIndex === state.spine.length - 1 : atLastPage);
        elements.progressSlider.disabled = !state.isReady && !state.progressSeek.isLoading;
        const chapterTitle = getChapterTitle(state.currentSpineIndex);
        const pageInfo = isScrollMode
            ? "Continuous scroll"
            : `Page ${state.currentPage + 1} of ${state.pageCount}`;
        elements.location.textContent = chapterTitle
            ? `${chapterTitle} · ${pageInfo}`
            : pageInfo;
        elements.bookTitle.textContent = state.metadata.title;
        elements.author.textContent = state.metadata.author;
        document.title = `${state.metadata.title} · EPUB Reader`;
        updateProgress();
        updateTocHighlight();
        if (item) {
            const frameAriaLabel = chapterTitle
                ? `${chapterTitle}, page ${state.currentPage + 1}`
                : `Page ${state.currentPage + 1}`;
            elements.frame.setAttribute("aria-label", frameAriaLabel);
        }
        if (state.isReady && item) {
            notifyNative("locationChanged", {
                resourceHref: item.href,
                page: state.currentPage,
                pageCount: state.pageCount,
                charOffset: getCharOffsetAtViewportStart() ?? -1
            });
        }
    }

    /** @param {ScrollBehavior} [behavior] */
    function scrollToCurrentPage(behavior = "auto") {
        const scroller = getFrameScroller();
        if (!scroller) {
            return;
        }

        if (state.settings.paginationMode === "scroll") {
            scroller.scrollLeft = 0;
            return;
        }

        const maxScroll = Math.max(0, getScrollWidth() - state.viewportWidth);
        const left = Math.min(maxScroll, state.currentPage * state.viewportWidth);
        if (typeof scroller.scrollTo === "function") {
            scroller.scrollTo({ left, top: 0, behavior });
        } else {
            scroller.scrollLeft = left;
            scroller.scrollTop = 0;
        }
    }

    // notify=false lets a caller that is about to resolve and report the REAL target position
    // itself (goToSpineIndex, right after switching chapters) skip the notifyNative this would
    // otherwise send for the merely provisional page-0-of-the-new-chapter position that
    // showSection() just reset state.currentPage to -- without it, that transient position was
    // getting reported as a locationChanged, which the native side treats as the definitive
    // "reader ready" locator (see EpubReaderView's pendingStartLocator handling) and saves/syncs
    // immediately, a heartbeat before the correct one arrives right behind it. Every other
    // caller (resize, setSettings, setSafeAreaInsets) is the only source of a position update
    // for that change and still wants its notification, so they keep the default.
    /**
     * @param {boolean} [preservePosition]
     * @param {number | null} [preservedScrollRatio]
     * @param {boolean} [notify]
     */
    function measurePageLayout(preservePosition = false, preservedScrollRatio = null, notify = true) {
        const oldPageCount = Math.max(1, state.pageCount);
        const oldPosition = state.currentPage / Math.max(1, oldPageCount - 1);
        const scrollRatio = preservePosition && state.settings.paginationMode === "scroll"
            ? (preservedScrollRatio ?? getScrollPositionRatio())
            : 0;
        state.viewportWidth = Math.max(1, elements.frame.clientWidth);
        const frameDocument = elements.frame.contentDocument;
        if (!frameDocument?.documentElement) {
            return;
        }

        if (state.settings.paginationMode === "scroll") {
            clearVirtualColumns(frameDocument);
            state.pageCount = 1;
            state.currentPage = 0;
            restoreScrollPosition(scrollRatio);
            if (notify) {
                updateUi();
            }
            return;
        }

        const columnCount = Number(getEffectiveSettings().columnCount);
        frameDocument.documentElement.style.setProperty("--reader-column-width", `${state.viewportWidth / columnCount}px`);
        padColumnsToFullSpread(frameDocument);
        const scrollWidth = getScrollWidth();
        state.pageCount = Math.max(1, Math.ceil((scrollWidth - 1) / state.viewportWidth));
        if (preservePosition && oldPageCount > 1) {
            state.currentPage = Math.min(state.pageCount - 1, Math.round(oldPosition * (state.pageCount - 1)));
        } else {
            state.currentPage = Math.min(state.currentPage, state.pageCount - 1);
        }
        scrollToCurrentPage();
        if (notify) {
            updateUi();
        }
    }

    function waitForNextFrame() {
        return new Promise((resolve) => {
            window.requestAnimationFrame(() => {
                window.requestAnimationFrame(resolve);
            });
        });
    }

    function isElementNode(node) {
        // `instanceof Element` fails for nodes that belong to the reading
        // frame's document: it's a different window/realm than this script's,
        // so its elements aren't instances of *this* window's Element
        // constructor even though they're genuine elements. nodeType is a
        // plain data property and works the same across realms.
        return Boolean(node) && node.nodeType === Node.ELEMENT_NODE;
    }

    function isInteractiveTarget(target) {
        return isElementNode(target) && Boolean(target.closest("a, button, input, select, textarea, video, audio, summary, [contenteditable=\"true\"]"));
    }

    function hasTextSelection(frameDocument) {
        const selection = frameDocument.defaultView?.getSelection();
        return Boolean(selection && !selection.isCollapsed && selection.toString().trim());
    }

    // The single source of truth for "what's selected and where". Both the in-page
    // lookup button and the native platform bridges (which call this directly via
    // window.DisplayBookReader.getSelectionInfo()) go through here so the reported
    // selection rect is always frame-offset-adjusted the same way. The rect is in the
    // OUTER document's coordinate space -- i.e. CSS pixels of the page hosting the
    // <iframe> -- which is the same coordinate space the native WebView control itself
    // is measured in, so callers can use it directly to anchor UI.
    function getSelectionInfo() {
        const frameDocument = elements.frame.contentDocument;
        const selection = frameDocument?.defaultView?.getSelection();
        if (!selection || selection.isCollapsed || selection.rangeCount === 0) {
            return null;
        }

        const text = selection.toString().trim();
        if (!text) {
            return null;
        }

        const selectionRect = selection.getRangeAt(0).getBoundingClientRect();
        const frameRect = elements.frame.getBoundingClientRect();
        return {
            text,
            left: frameRect.left + selectionRect.left,
            top: frameRect.top + selectionRect.top,
            right: frameRect.left + selectionRect.right,
            bottom: frameRect.top + selectionRect.bottom
        };
    }

    const LOOKUP_BUTTON_LABEL_MAX_LENGTH = 24;

    function hideLookupButton() {
        elements.lookupButton.hidden = true;
        state.pendingLookupText = "";
        state.pendingLookupRect = null;
    }

    function truncateForLookupLabel(text) {
        return text.length > LOOKUP_BUTTON_LABEL_MAX_LENGTH
            ? `${text.slice(0, LOOKUP_BUTTON_LABEL_MAX_LENGTH)}…`
            : text;
    }

    const LOOKUP_BUTTON_GAP = 10;
    const LOOKUP_BUTTON_MARGIN = 8;

    // The lookup button lives in the OUTER document (a sibling of the <iframe> in
    // .book-viewport), not inside the reading frame -- an in-frame button was tried
    // twice before and was reliably killed by the reading frame's own pagination
    // reflows. Living outside the frame avoids that, but the button is still
    // positioned dynamically, right above the selection (or below it if there's no
    // room), using the same rect getSelectionInfo() reports -- just translated from
    // document/viewport coordinates into .book-viewport's own local coordinate space,
    // since that's the button's positioned ancestor.
    function positionLookupButton(info) {
        const viewport = elements.viewport;
        const button = elements.lookupButton;
        if (!viewport || !button) {
            return;
        }

        const containerRect = viewport.getBoundingClientRect();
        const buttonRect = button.getBoundingClientRect();
        const selLeft = info.left - containerRect.left;
        const selRight = info.right - containerRect.left;
        const selTop = info.top - containerRect.top;
        const selBottom = info.bottom - containerRect.top;

        const maxLeft = Math.max(LOOKUP_BUTTON_MARGIN, containerRect.width - buttonRect.width - LOOKUP_BUTTON_MARGIN);
        const left = Math.min(
            Math.max(LOOKUP_BUTTON_MARGIN, ((selLeft + selRight) / 2) - (buttonRect.width / 2)),
            maxLeft
        );

        const above = selTop - LOOKUP_BUTTON_GAP - buttonRect.height;
        const maxTop = Math.max(LOOKUP_BUTTON_MARGIN, containerRect.height - buttonRect.height - LOOKUP_BUTTON_MARGIN);
        const top = above >= LOOKUP_BUTTON_MARGIN
            ? above
            : Math.min(selBottom + LOOKUP_BUTTON_GAP, maxTop);

        button.style.left = `${left}px`;
        button.style.top = `${top}px`;
    }

    function installSelectionLookupHandler(frameDocument) {
        hideLookupButton();
        let settleTimer = 0;

        function refreshOrHideButton() {
            const info = getSelectionInfo();
            if (!info) {
                hideLookupButton();
                return;
            }

            state.pendingLookupText = info.text;
            state.pendingLookupRect = info;
            elements.lookupButton.textContent = `Look up "${truncateForLookupLabel(info.text)}"`;
            elements.lookupButton.hidden = false;
            positionLookupButton(info);
        }

        frameDocument.addEventListener("selectionchange", () => {
            window.clearTimeout(settleTimer);
            settleTimer = window.setTimeout(refreshOrHideButton, 220);
        });

        const scroller = frameDocument.scrollingElement;
        scroller?.addEventListener("scroll", refreshOrHideButton, { passive: true });
    }

    function setReaderChromeVisible(isVisible) {
        state.chromeVisible = isVisible;
        elements.readerShell.classList.toggle("reader-shell--immersive", !isVisible);
        elements.readerShell.dataset.chromeVisible = String(isVisible);
        if (!isVisible) {
            closeContents();
            closeSettings();
        }
        notifyNative("chromeVisibilityChanged", { visible: isVisible });
        window.requestAnimationFrame(() => {
            if (state.isReady) {
                elements.frame.contentDocument?.documentElement.style.setProperty(
                    "--USER__colCount",
                    getEffectiveSettings().columnCount);
                measurePageLayout(true);
            }
        });
    }

    function toggleReaderChrome() {
        setReaderChromeVisible(elements.readerShell.classList.contains("reader-shell--immersive"));
    }

    function installFrameInputHandlers(frameDocument) {
        installSelectionLookupHandler(frameDocument);

        let pointerStart = null;
        frameDocument.addEventListener("click", (event) => {
            const link = isElementNode(event.target) ? event.target.closest("a[href]") : null;
            if (link) {
                handleContentLink(event, link);
                return;
            }

            if (isInteractiveTarget(event.target) || hasTextSelection(frameDocument)) {
                return;
            }

            const width = Math.max(1, frameDocument.documentElement.clientWidth);
            if (event.clientX <= width * 0.22) {
                goPrevious();
            } else if (event.clientX >= width * 0.78) {
                goNext();
            } else {
                toggleReaderChrome();
            }
        });

        frameDocument.addEventListener("pointerdown", (event) => {
            if (!isInteractiveTarget(event.target)) {
                pointerStart = { time: Date.now(), x: event.clientX };
            }
        });

        frameDocument.addEventListener("pointerup", (event) => {
            if (!pointerStart || isInteractiveTarget(event.target)) {
                pointerStart = null;
                return;
            }

            const distance = event.clientX - pointerStart.x;
            const elapsed = Date.now() - pointerStart.time;
            pointerStart = null;
            if (elapsed < 900 && Math.abs(distance) >= MIN_SWIPE_DISTANCE) {
                event.preventDefault();
                if (distance < 0) {
                    goNext();
                } else {
                    goPrevious();
                }
            }
        });

        frameDocument.addEventListener("keydown", handleNavigationKey);
        const scroller = frameDocument.scrollingElement;
        scroller?.addEventListener("scroll", () => {
            if (!state.isReady || state.viewportWidth <= 0) {
                return;
            }
            if (state.settings.paginationMode === "scroll") {
                return;
            }
            const page = Math.round(scroller.scrollLeft / state.viewportWidth);
            if (page !== state.currentPage && page >= 0 && page < state.pageCount) {
                logDiagnostic("DisplayBook reader scroll listener correcting page", {
                    priorPage: state.currentPage,
                    correctedPage: page,
                    scrollLeft: scroller.scrollLeft,
                    viewportWidth: state.viewportWidth
                });
                state.currentPage = page;
                updateUi();
            }
        }, { passive: true });
    }

    function handleNavigationKey(event) {
        if (event.key === "Escape") {
            if (!elements.settingsPanel.hidden) {
                closeSettings();
                event.preventDefault();
            } else if (!elements.contentsPanel.hidden) {
                closeContents();
                event.preventDefault();
            }
            return;
        }
        if (isElementNode(event.target) && event.target.matches("input, select, textarea, [contenteditable=\"true\"]")) {
            return;
        }

        if (event.key === "ArrowRight" || event.key === "PageDown" || event.key === " ") {
            event.preventDefault();
            goNext();
        } else if (event.key === "ArrowLeft" || event.key === "PageUp") {
            event.preventDefault();
            goPrevious();
        } else if (event.key === "Home") {
            event.preventDefault();
            goToPage(0);
        } else if (event.key === "End") {
            event.preventDefault();
            goToPage(state.pageCount - 1);
        }
    }

    function handleContentLink(event, link) {
        // Always stop the iframe from following the link itself: an href that
        // doesn't resolve to a spine chapter (an external URL, mailto:, a
        // resource outside the spine) must never be allowed to navigate the
        // frame for real, or the reader loses control of it entirely.
        event.preventDefault();
        const rawHref = link.getAttribute("href");
        if (!rawHref) {
            return;
        }

        const sourceSection = link.closest("section[data-chapter-index]");
        const baseHref = sourceSection?.dataset.chapterHref ?? state.spine[state.currentSpineIndex]?.href;
        if (!baseHref) {
            return;
        }

        const { spineIndex: targetIndex, fragment } = resolveSpineIndexForHref(rawHref, baseHref);
        if (targetIndex < 0) {
            return;
        }

        goToSpineIndex(targetIndex, { fragment });
    }

    /**
     * @param {number} page
     * @param {ScrollBehavior} [behavior]
     */
    function goToPage(page, behavior = "smooth") {
        if (!state.isReady) {
            return;
        }
        if (state.settings.paginationMode === "scroll") {
            return;
        }
        state.currentPage = Math.min(Math.max(0, page), state.pageCount - 1);
        scrollToCurrentPage(behavior);
        updateUi();
    }

    function getProgressTarget(value) {
        const normalized = Math.min(1, Math.max(0, Number(value) / 1000));
        const scaledPosition = normalized * state.spine.length;
        const targetIndex = normalized >= 1
            ? state.spine.length - 1
            : Math.min(state.spine.length - 1, Math.floor(scaledPosition));
        return {
            chapterRatio: normalized >= 1 ? 1 : scaledPosition - targetIndex,
            targetIndex
        };
    }

    /**
     * @param {{ targetIndex: number, chapterRatio: number }} target
     * @param {ScrollBehavior} [behavior]
     */
    function applyProgressTarget(target, behavior = "auto") {
        if (!state.isReady) {
            return;
        }

        if (state.settings.paginationMode === "scroll") {
            restoreScrollPosition(target.chapterRatio);
            updateUi();
            return;
        }

        const page = Math.min(
            state.pageCount - 1,
            Math.max(0, Math.round(target.chapterRatio * (state.pageCount - 1)))
        );
        goToPage(page, behavior);
    }

    // Switching chapters is now a synchronous DOM show/hide (see goToSpineIndex) rather than an
    // async fetch+parse, so -- unlike the old fetch-backed version of this function -- there's no
    // in-flight load for a later seek to race against; each call runs to completion before the
    // next `input`/`change` event can fire.
    /**
     * @param {string | number} value
     * @param {ScrollBehavior} [behavior]
     */
    function seekToProgress(value, behavior = "auto") {
        if (!state.isReady || state.spine.length === 0) {
            return;
        }

        const target = getProgressTarget(value);
        if (target.targetIndex !== state.currentSpineIndex) {
            goToSpineIndex(target.targetIndex);
        }
        applyProgressTarget(target, behavior);
    }

    function goNext() {
        if (!state.isReady) {
            return;
        }
        if (state.settings.paginationMode !== "scroll" && state.currentPage < state.pageCount - 1) {
            goToPage(state.currentPage + 1);
            return;
        }
        if (state.currentSpineIndex < state.spine.length - 1) {
            goToSpineIndex(state.currentSpineIndex + 1);
        }
    }

    function goPrevious() {
        if (!state.isReady) {
            return;
        }
        if (state.settings.paginationMode !== "scroll" && state.currentPage > 0) {
            goToPage(state.currentPage - 1);
            return;
        }
        if (state.currentSpineIndex > 0) {
            goToSpineIndex(state.currentSpineIndex - 1, { openAtEnd: true });
        }
    }

    // Toggles which chapter <section> is visible within the one already-loaded combined
    // document (see CombinedDocumentBuilder) -- replaces the old per-chapter fetch+srcdoc
    // navigation entirely. display:none siblings contribute no layout width, so pagination
    // (measurePageLayout/getScrollWidth) naturally scopes to whichever section this shows.
    function showSection(spineIndex) {
        const frameDocument = elements.frame.contentDocument;
        const sections = /** @type {NodeListOf<HTMLElement> | undefined} */ (
            frameDocument?.querySelectorAll("section[data-chapter-index]"));
        if (!frameDocument || !sections || sections.length === 0) {
            return false;
        }

        const target = String(spineIndex);
        let found = false;
        for (const section of sections) {
            const isTarget = section.dataset.chapterIndex === target;
            section.style.display = isTarget ? "block" : "none";
            if (isTarget) {
                found = true;
            }
        }
        if (!found) {
            return false;
        }

        state.currentSpineIndex = spineIndex;
        state.currentPage = 0;
        applyCoverPageLayout(frameDocument);
        return true;
    }

    // The single entry point for "navigate to this chapter", used by page turns, TOC clicks,
    // in-book links, and setLocator alike -- mirrors the old loadResource/handleFrameLoad
    // structure (including its exact position-resolution precedence: charOffset, then
    // openAtEnd, then a fragment, then an explicit page, then plain page 0) but synchronous,
    // since there's no longer any I/O to await between "chapter selected" and "chapter visible".
    function goToSpineIndex(spineIndex, options = {}) {
        if (!showSection(spineIndex)) {
            return false;
        }

        measurePageLayout(false, null, false);
        const { fragment = "", openAtEnd = false, charOffset = null, page = null } = options;
        let resolution = "none";
        if (typeof charOffset === "number" && charOffset >= 0 && resolveCharOffset(charOffset)) {
            // Position already applied by resolveCharOffset.
            resolution = "charOffset";
        } else if (openAtEnd) {
            state.currentPage = state.pageCount - 1;
            scrollToCurrentPage();
            resolution = "openAtEnd";
        } else if (fragment) {
            const fragmentId = fragment.slice(1);
            const target = elements.frame.contentDocument?.getElementById(decodeURIComponent(fragmentId));
            if (target) {
                target.scrollIntoView({ block: "start" });
                state.currentPage = Math.min(state.pageCount - 1, Math.max(0, Math.round((getFrameScroller()?.scrollLeft ?? 0) / state.viewportWidth)));
                resolution = "fragment";
            }
        } else if (typeof page === "number" && page > 0) {
            // Falls back to the caller's page number (e.g. setLocator's normalizedPage) when
            // there's no charOffset to resolve -- without this, a locator that couldn't resolve
            // a charOffset (a legacy locator, or one whose text wasn't found) would silently
            // land on page 0 of the target chapter instead of anywhere close to the right spot.
            state.currentPage = Math.min(state.pageCount - 1, Math.max(0, page));
            scrollToCurrentPage();
            resolution = "page";
        }
        logDiagnostic("DisplayBook reader goToSpineIndex", {
            spineIndex,
            options,
            resolution,
            resultingPage: state.currentPage,
            pageCount: state.pageCount,
            viewportWidth: state.viewportWidth
        });
        updateUi();
        updateTocHighlight();
        return true;
    }

    function renderContents() {
        elements.contentsList.replaceChildren();
        for (const entry of state.toc) {
            const listItem = document.createElement("li");
            const link = document.createElement("a");
            link.href = "#";
            link.dataset.spineIndex = String(entry.spineIndex);
            link.textContent = entry.label;
            link.addEventListener("click", (event) => {
                event.preventDefault();
                goToSpineIndex(entry.spineIndex, { fragment: entry.fragment });
                closeContents();
            });
            listItem.appendChild(link);
            elements.contentsList.appendChild(listItem);
        }
    }

    function openContents() {
        elements.contentsPanel.hidden = false;
        elements.contentsToggle.setAttribute("aria-expanded", "true");
    }

    function closeContents() {
        elements.contentsPanel.hidden = true;
        elements.contentsToggle.setAttribute("aria-expanded", "false");
    }

    function openSettings() {
        if (elements.readerShell.classList.contains("reader-shell--immersive")) {
            return;
        }
        closeContents();
        elements.settingsPanel.hidden = false;
        elements.settingsToggle.setAttribute("aria-expanded", "true");
        elements.settingsForm.elements.namedItem("theme")?.[0]?.focus();
    }

    function closeSettings() {
        if (!elements.settingsPanel.hidden) {
            elements.settingsPanel.hidden = true;
            elements.settingsToggle.setAttribute("aria-expanded", "false");
        }
    }

    function bindHostEvents() {
        elements.frame.addEventListener("load", () => handleCombinedDocumentLoad().catch(setError));
        elements.readerBack.addEventListener("click", () => notifyNative("requestExit"));
        elements.previous.addEventListener("click", goPrevious);
        elements.next.addEventListener("click", goNext);
        elements.lookupButton.addEventListener("click", () => {
            if (state.pendingLookupText) {
                notifyNative("dictionaryLookupRequested", {
                    text: state.pendingLookupText,
                    rect: state.pendingLookupRect
                        ? {
                            left: state.pendingLookupRect.left,
                            top: state.pendingLookupRect.top,
                            right: state.pendingLookupRect.right,
                            bottom: state.pendingLookupRect.bottom
                        }
                        : null
                });
            }

            hideLookupButton();
        });
        elements.progressSlider.addEventListener("input", () => {
            seekToProgress(elements.progressSlider.value);
            previewProgress(elements.progressSlider.value);
        });
        elements.progressSlider.addEventListener("change", () => {
            seekToProgress(elements.progressSlider.value);
        });
        elements.contentsToggle.addEventListener("click", () => {
            if (elements.contentsPanel.hidden) {
                openContents();
            } else {
                closeContents();
            }
        });
        elements.closeContents.addEventListener("click", closeContents);
        elements.settingsToggle.addEventListener("click", () => {
            if (elements.settingsPanel.hidden) {
                openSettings();
            } else {
                closeSettings();
            }
        });
        elements.settingsClose.addEventListener("click", closeSettings);
        elements.settingsForm.addEventListener("input", (event) => {
            const control = event.target;
            if (!(control instanceof HTMLInputElement || control instanceof HTMLSelectElement) || !control.name) {
                return;
            }
            const value = control.name === "fontSize" ? `${control.value}%` : control.value;
            setSettings({ [control.name]: value });
        });
        elements.settingsForm.addEventListener("change", (event) => {
            const control = event.target;
            if (!(control instanceof HTMLInputElement || control instanceof HTMLSelectElement) || !control.name) {
                return;
            }
            const value = control.name === "fontSize" ? `${control.value}%` : control.value;
            setSettings({ [control.name]: value });
        });
        elements.settingsReset.addEventListener("click", () => setSettings(DEFAULT_SETTINGS));
        updateSettingsControls();
        document.addEventListener("keydown", handleNavigationKey);
        window.addEventListener("resize", () => {
            window.clearTimeout(state.resizeTimer);
            state.resizeTimer = window.setTimeout(() => {
                if (state.isReady) {
                    logDiagnostic("DisplayBook reader resize->measurePageLayout", {
                        priorPage: state.currentPage,
                        priorPageCount: state.pageCount,
                        priorViewportWidth: state.viewportWidth,
                        newFrameClientWidth: elements.frame.clientWidth
                    });
                    applySettingsToFrame(elements.frame.contentDocument);
                    measurePageLayout(true);
                }
            }, 120);
        });
    }

    function setLocator(resourceHref, page = 0, charOffset = -1) {
        if (!resourceHref) {
            updateUi();
            return;
        }

        // Prefer the portable relative-href match (what every current build reports and
        // syncs); fall back to the legacy absolute-URL match for locators saved to this
        // device's own database before that switch. If neither resolves -- e.g. a locator
        // synced from a device whose copy of the book doesn't line up, or from a build
        // predating one of these formats -- fall through to opening at the current/first
        // chapter rather than doing nothing: the native side is waiting for a
        // locationChanged to know the reader finished loading, and it must always get one
        // or the loading screen hangs forever.
        const byRelativeHref = getSpineIndexByRelativeHref(resourceHref);
        const byLegacyAbsoluteHref = byRelativeHref >= 0 ? -1 : getSpineIndexByLegacyAbsoluteHref(resourceHref);
        const isGenuineMatch = byRelativeHref >= 0 || byLegacyAbsoluteHref >= 0;
        const targetIndex = isGenuineMatch ? Math.max(byRelativeHref, byLegacyAbsoluteHref) : state.currentSpineIndex;

        // page/charOffset only mean anything relative to the chapter they were captured
        // in. If we couldn't actually find that chapter, applying them to whatever
        // chapter we fell back to would land on a plausible-looking but meaningless
        // position -- and that bad position would then get saved right back as if it
        // were real. Land on the start of the fallback chapter instead.
        const normalizedPage = isGenuineMatch ? page : 0;
        const normalizedCharOffset = isGenuineMatch && typeof charOffset === "number" && charOffset >= 0 ? charOffset : null;

        logDiagnostic("DisplayBook reader setLocator", {
            requestedResourceHref: resourceHref,
            requestedPage: page,
            requestedCharOffset: charOffset,
            byRelativeHref,
            byLegacyAbsoluteHref,
            isGenuineMatch,
            currentSpineIndex: state.currentSpineIndex,
            targetIndex,
            normalizedPage,
            normalizedCharOffset,
            viewportWidth: state.viewportWidth,
            frameClientWidth: elements.frame.clientWidth,
            frameClientHeight: elements.frame.clientHeight
        });

        if (targetIndex !== state.currentSpineIndex) {
            goToSpineIndex(targetIndex, { charOffset: normalizedCharOffset, page: normalizedPage });
            return;
        }

        if (normalizedCharOffset === null || !resolveCharOffset(normalizedCharOffset)) {
            logDiagnostic("DisplayBook reader setLocator: falling back to page in current chapter", { normalizedPage });
            goToPage(normalizedPage);
        } else {
            updateUi();
        }
    }

    function setSettings(settings = {}) {
        const preservedScrollRatio = state.settings.paginationMode === "scroll" ? getScrollPositionRatio() : 0;
        state.settings = normalizeSettings({ ...state.settings, ...settings }, state.settings);
        persistSettings();
        updateSettingsControls();
        const frameDocument = elements.frame.contentDocument;
        if (!frameDocument?.documentElement) {
            return;
        }

        applySettingsToFrame(frameDocument);
        window.requestAnimationFrame(() => {
            window.requestAnimationFrame(() => {
                if (state.isReady) {
                    measurePageLayout(true, preservedScrollRatio);
                }
            });
        });
    }

    function clearSelection() {
        const frameDocument = elements.frame.contentDocument;
        frameDocument?.defaultView?.getSelection()?.removeAllRanges();
        hideLookupButton();
    }

    // Android has no native CSS env(safe-area-inset-*) support (unlike WKWebView on
    // iOS/macOS, where the CSS fallback above already resolves it), so the native side
    // measures the status bar/notch and navigation bar insets itself and pushes them
    // in here -- see ReaderWebViewHandler.android.cs.
    function setSafeAreaInsets(top = 0, bottom = 0) {
        const topPx = Math.max(0, Number(top) || 0);
        const bottomPx = Math.max(0, Number(bottom) || 0);
        if (state.safeAreaInsets.top === topPx && state.safeAreaInsets.bottom === bottomPx) {
            return;
        }

        state.safeAreaInsets = { top: topPx, bottom: bottomPx };
        const frameDocument = elements.frame.contentDocument;
        if (!frameDocument?.documentElement) {
            return;
        }

        applySettingsToFrame(frameDocument);
        if (state.isReady) {
            logDiagnostic("DisplayBook reader setSafeAreaInsets->measurePageLayout", {
                topPx,
                bottomPx,
                priorPage: state.currentPage,
                priorPageCount: state.pageCount,
                priorViewportWidth: state.viewportWidth,
                newFrameClientWidth: elements.frame.clientWidth
            });
            measurePageLayout(true);
        }
    }

    function loadPublication(payload) {
        openPublication(payload).catch(setError);
    }

    // Answers EpubReaderView's post-resume liveness probe (see RecoverReaderIfNeededAsync). The
    // reader is only usable if this shell is still running AND still holding the book it was given:
    // when the OS reclaims the WebView's web content process in the background, the native side
    // can't tell from its own state that any of this is gone, so it asks.
    function isReaderAlive() {
        return state.spine.length > 0 &&
            Boolean(elements.frame.contentDocument?.querySelector("section[data-chapter-index]"));
    }

    readerWindow.DisplayBookReader = { setLocator, setSettings, clearSelection, getSelectionInfo, setSafeAreaInsets, loadPublication, isReaderAlive };

    // Everything here is per-book; called fresh every time openPublication() runs, including for
    // the second and later books in a session where the reader shell (this whole script/DOM) is
    // never reloaded. Bumping loadToken here also invalidates any combined-document load still
    // in flight from whichever book was being opened before (see openPublication).
    function resetPublicationState() {
        state.loadToken += 1;
        state.currentPage = 0;
        state.currentSpineIndex = 0;
        state.isReady = false;
        state.readerReadyNotified = false;
        state.metadata = { author: "", title: "" };
        state.coverHref = null;
        state.pageCount = 1;
        state.pendingFrameLoad = null;
        state.spine = [];
        state.toc = [];
        state.progressSeek = { isLoading: false, pendingValue: null, behavior: "auto" };
        hideLookupButton();
        setReaderChromeVisible(false);
        elements.contentsList.replaceChildren();
        elements.error.hidden = true;
    }

    /// Called once per book, whenever the native side hands this (already-booted) shell a
    /// publication via window.DisplayBookReader.loadPublication. Unlike the old opf-path-based
    /// flow, `payload` already carries the fully-parsed spine/TOC/metadata (see
    /// EpubPublicationParser/ReaderPublicationPayload on the native side) and the URL of a single
    /// document containing every chapter (see CombinedDocumentBuilder) -- this function loads
    /// that one document and shows its first chapter; nothing here ever fetches book content
    /// itself. The token guard lets a later call abort an earlier one still awaiting the frame's
    /// load event instead of both racing to mutate `state`.
    async function openPublication(payload) {
        if (!payload?.combinedHref || !Array.isArray(payload.spine) || payload.spine.length === 0) {
            setError(new Error("The reader did not receive a publication to open."));
            return;
        }

        resetPublicationState();
        const token = state.loadToken;
        setLoading(true, "Opening publication…");

        state.metadata = { author: payload.author ?? "", title: payload.title ?? "" };
        state.spine = payload.spine;
        state.toc = payload.toc ?? [];
        state.coverHref = payload.coverHref ?? null;
        renderContents();
        applyLoadingCover();

        await new Promise((resolve, reject) => {
            state.pendingFrameLoad = { resolve, reject };
            elements.frame.removeAttribute("srcdoc");
            elements.frame.src = payload.combinedHref;
        });
        if (token !== state.loadToken) {
            return;
        }

        applySettingsToFrame(elements.frame.contentDocument);
        state.isReady = true;
        if (!showSection(0)) {
            throw new Error("The combined reading document is missing its chapters.");
        }

        measurePageLayout();
        updateUi();
        updateTocHighlight();
        setLoading(false);
        elements.error.hidden = true;
        // readerReady is a one-time-per-book handshake: the native side waits for it (or, if a
        // start locator is set, for the locationChanged that follows its own setLocator call) to
        // know the reader finished loading and clear its own loading overlay.
        if (!state.readerReadyNotified) {
            state.readerReadyNotified = true;
            notifyNative("readerReady", {
                resourceHref: state.spine[0].href,
                page: state.currentPage,
                pageCount: state.pageCount
            });
        }
    }

    // Fires once when the combined document itself (not a chapter -- there's only ever one
    // navigation per book now) finishes loading into the iframe.
    async function handleCombinedDocumentLoad() {
        const pendingLoad = state.pendingFrameLoad;
        state.pendingFrameLoad = null;
        if (!pendingLoad) {
            return;
        }

        try {
            const frameDocument = elements.frame.contentDocument;
            if (!frameDocument?.head || !frameDocument.documentElement) {
                throw new Error("The combined reading document did not load correctly.");
            }

            createPaginationStyle(frameDocument);
            await waitForNextFrame();
            installFrameInputHandlers(frameDocument);
            const frameFonts = frameDocument.fonts;
            if (frameFonts) {
                await frameFonts.ready;
            }
            pendingLoad.resolve();
        } catch (error) {
            pendingLoad.reject(error);
        }
    }

    function initialize() {
        bindHostEvents();
        setReaderChromeVisible(false);

        // Tells the native side this shell (index.html/EpubText.js) has finished its
        // book-independent boot and is ready to receive a book via loadPublication -- distinct
        // from readerReady, which fires per-book once that book's first chapter has actually
        // loaded. See EpubReaderView.EnsureReaderShellLoadedAsync.
        notifyNative("shellReady");
    }

    try {
        initialize();
    } catch (error) {
        setError(error);
    }
})();
