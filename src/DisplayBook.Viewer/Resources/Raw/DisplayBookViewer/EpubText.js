(() => {
    "use strict";

    const query = new URLSearchParams(window.location.search);
    const OPF_PATH = query.get("opf");
    const BRIDGE_URL = query.get("bridge") ?? "displaybook://bridge";
    const READER_STYLESHEETS = [
        "ReadiumCSS-before.css",
        "ReadiumCSS-default.css",
        "ReadiumCSS-after.css"
    ];
    const FRAME_STYLE_ID = "display-book-pagination-style";
    const MIN_SWIPE_DISTANCE = 42;
    const SETTINGS_STORAGE_KEY = "displaybook.reader.settings.v1";
    const SETTINGS_STORAGE_VERSION = 1;
    const WIDE_VIEWPORT_MINIMUM = 1200;
    const DEFAULT_SETTINGS = Object.freeze({
        theme: "sepia",
        fontFamily: "serif",
        fontSize: "100%",
        lineHeight: "1.5",
        paginationMode: "paged",
        columnMode: "single",
        columnCount: "1",
        lineLength: "100%",
        textAlignment: "auto",
        hyphenation: "auto",
        paragraphSpacing: "0",
        paragraphIndent: "1em",
        wordSpacing: "0",
        letterSpacing: "normal",
        fontWeight: "normal",
        imageTreatment: "normal"
    });
    const SETTING_CHOICES = {
        theme: new Set(["paper", "sepia", "night"]),
        fontFamily: new Set(["serif", "sans", "humanist", "monospace"]),
        paginationMode: new Set(["paged", "scroll"]),
        columnMode: new Set(["single", "two"]),
        lineLength: new Set(["100%", "75ch", "65ch", "55ch"]),
        textAlignment: new Set(["auto", "left", "justify"]),
        hyphenation: new Set(["auto", "none"]),
        paragraphSpacing: new Set(["0", "0.35em", "0.7em"]),
        paragraphIndent: new Set(["0", "1em", "2em"]),
        wordSpacing: new Set(["0", "0.08em", "0.16em"]),
        letterSpacing: new Set(["normal", "0.03em", "0.06em"]),
        fontWeight: new Set(["normal", "600", "700"]),
        imageTreatment: new Set(["normal", "dim", "invert", "dim-invert"])
    };

    const elements = {
        author: document.getElementById("book-author"),
        bookTitle: document.getElementById("book-title"),
        closeContents: document.getElementById("contents-close"),
        contentsList: document.getElementById("contents-list"),
        contentsPanel: document.getElementById("contents-panel"),
        contentsToggle: document.getElementById("contents-toggle"),
        error: document.getElementById("reader-error"),
        frame: document.getElementById("page"),
        loading: document.getElementById("reader-loading"),
        location: document.getElementById("reader-location"),
        next: document.getElementById("next-page"),
        previous: document.getElementById("previous-page"),
        progress: document.getElementById("progress-value"),
        progressSlider: document.getElementById("book-progress"),
        readerBack: document.getElementById("reader-back"),
        readerShell: document.querySelector(".reader-shell"),
        settingsClose: document.getElementById("settings-close"),
        settingsForm: document.getElementById("settings-form"),
        settingsPanel: document.getElementById("settings-panel"),
        settingsReset: document.getElementById("settings-reset"),
        settingsToggle: document.getElementById("settings-toggle")
    };

    const state = {
        currentPage: 0,
        currentSpineIndex: 0,
        chromeVisible: false,
        documentUrl: "",
        isReady: false,
        loadToken: 0,
        manifest: new Map(),
        metadata: { author: "", title: "" },
        opfUrl: "",
        pageCount: 1,
        pendingLoad: null,
        publicationStyles: new Map(),
        readerStyles: new Map(),
        resourceCache: new Map(),
        textCache: new Map(),
        resizeTimer: 0,
        spine: [],
        toc: [],
        viewportWidth: 1,
        preloadPromise: null,
        settings: loadSettings(),
        progressSeek: {
            isLoading: false,
            pendingValue: null,
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
        if (name === "lineHeight" && /^(?:1\.[2-9]|2(?:\.0)?)$/u.test(value)) {
            return value;
        }
        return fallback;
    }

    function normalizeSettings(settings, base = DEFAULT_SETTINGS) {
        const normalized = {};
        for (const name of Object.keys(DEFAULT_SETTINGS)) {
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
        return {
            ...state.settings,
            columnCount: state.chromeVisible ? "1" : (canUseTwoColumns ? "2" : "1")
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
        }[settings.theme];
        document.documentElement.style.setProperty("--reader-surface", theme.background);
        document.documentElement.style.setProperty("--reader-chrome-surface", theme.chromeSurface);
        document.documentElement.style.setProperty("--reader-chrome-ink", theme.chromeInk);
        document.documentElement.style.setProperty("--reader-chrome-muted", theme.chromeMuted);
        document.documentElement.style.setProperty("--reader-chrome-border", theme.chromeBorder);
        document.documentElement.style.setProperty("--reader-chrome-accent", theme.chromeAccent);
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
            "--USER__backgroundColor": theme.background,
            "--USER__textColor": theme.text,
            "--USER__linkColor": theme.link,
            "--USER__colCount": settings.columnCount,
            "--USER__lineLength": settings.lineLength,
            "--USER__textAlign": settings.textAlignment,
            "--USER__bodyHyphens": settings.hyphenation,
            "--USER__fontFamily": fontFamily,
            "--USER__fontSize": settings.fontSize,
            "--USER__lineHeight": settings.lineHeight,
            "--USER__paraSpacing": settings.paragraphSpacing,
            "--USER__paraIndent": settings.paragraphIndent,
            "--USER__wordSpacing": settings.wordSpacing,
            "--USER__letterSpacing": settings.letterSpacing,
            "--USER__fontWeight": settings.fontWeight,
            "--USER__darkenImages": imageTreatment.darken,
            "--USER__invertImages": imageTreatment.invert
        };
        for (const [name, value] of Object.entries(variables)) {
            root.style.setProperty(name, value);
        }
        notifyNative("themeChanged", {
            theme: settings.theme,
            background: theme.background
        });
        if (settings.paginationMode === "scroll") {
            root.style.setProperty("readium-scroll-on", "");
        } else {
            root.style.removeProperty("readium-scroll-on");
        }

        if (settings.columnMode === "two") {
            console.info("DisplayBook reader columns", {
                viewportWidth: window.innerWidth,
                minimumViewportWidth: WIDE_VIEWPORT_MINIMUM,
                requestedColumnCount: state.settings.columnCount,
                effectiveColumnCount: settings.columnCount,
                renderedColumnCount: getComputedStyle(root).columnCount,
                renderedColumnWidth: getComputedStyle(root).columnWidth
            });
        }
    }

    function updateSettingsControls() {
        const form = elements.settingsForm;
        if (!form) {
            return;
        }
        for (const [name, value] of Object.entries(state.settings)) {
            const control = form.elements.namedItem(name);
            if (control instanceof RadioNodeList) {
                for (const radio of control) {
                    radio.checked = radio.value === value;
                }
            } else if (control instanceof HTMLInputElement || control instanceof HTMLSelectElement) {
                control.value = name === "fontSize" ? value.replace("%", "") : value;
            }
            const output = form.querySelector(`[data-for="${name}"]`);
            if (output) {
                output.textContent = value;
            }
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

    function getLocalNameNodes(root, localName) {
        return Array.from(root.getElementsByTagNameNS("*", localName));
    }

    function stripFragment(url) {
        const parsedUrl = new URL(url, window.location.href);
        parsedUrl.hash = "";
        return parsedUrl.href;
    }

    function getAbsoluteUrl(href, baseUrl) {
        return new URL(href, baseUrl).href;
    }

    async function fetchText(url) {
        const cacheKey = stripFragment(url);
        const cachedText = state.textCache.get(cacheKey);
        if (cachedText !== undefined) {
            return cachedText;
        }

        const response = await fetch(url, { cache: "no-store" });
        if (!response.ok) {
            throw new Error(`Unable to load ${url} (${response.status})`);
        }
        const text = await response.text();
        state.textCache.set(cacheKey, text);
        return text;
    }

    function rewriteStylesheetUrls(cssText, stylesheetUrl) {
        return cssText.replace(/url\(([^)]*)\)/giu, (match, rawValue) => {
            let normalizedValue = rawValue.trim();
            const firstCharacter = normalizedValue[0];
            const lastCharacter = normalizedValue.at(-1);
            if ((firstCharacter === "\"" || firstCharacter === "'") && firstCharacter === lastCharacter) {
                normalizedValue = normalizedValue.slice(1, -1).trim();
            }

            if (/^(?:data:|blob:|https?:|\/\/|#)/iu.test(normalizedValue)) {
                return match;
            }

            return `url("${getAbsoluteUrl(normalizedValue, stylesheetUrl)}")`;
        });
    }

    async function preloadReaderStyles() {
        await Promise.all(READER_STYLESHEETS.map(async (stylesheet) => {
            const stylesheetUrl = getAbsoluteUrl(stylesheet, window.location.href);
            const cssText = await fetchText(stylesheetUrl);
            state.readerStyles.set(stripFragment(stylesheetUrl), cssText);
        }));
    }

    async function preloadPublicationStyles(manifest) {
        const stylesheetItems = Array.from(manifest.values()).filter((item) => item.mediaType === "text/css");
        await Promise.all(stylesheetItems.map(async (item) => {
            try {
                const cssText = await fetchText(item.href);
                state.publicationStyles.set(stripFragment(item.href), cssText);
            } catch (error) {
                console.warn(`Unable to preload publication stylesheet ${item.href}.`, error);
            }
        }));
    }

    function createCachedResource(htmlText, resourceUrl) {
        const resourceDocument = new DOMParser().parseFromString(htmlText, "text/html");
        const base = resourceDocument.createElement("base");
        base.href = resourceUrl;
        resourceDocument.head.insertBefore(base, resourceDocument.head.firstChild);

        const stylesheetLinks = Array.from(resourceDocument.querySelectorAll("link[href]"));
        for (const link of stylesheetLinks) {
            const rel = (link.getAttribute("rel") ?? "").split(/\s+/u);
            if (!rel.includes("stylesheet")) {
                continue;
            }

            const stylesheetUrl = getAbsoluteUrl(link.getAttribute("href"), resourceUrl);
            const cssText = state.publicationStyles.get(stripFragment(stylesheetUrl));
            if (cssText === undefined) {
                continue;
            }

            const style = resourceDocument.createElement("style");
            style.textContent = rewriteStylesheetUrls(cssText, stylesheetUrl);
            link.replaceWith(style);
        }

        return `<!DOCTYPE html>${resourceDocument.documentElement.outerHTML}`;
    }

    async function preloadResource(item) {
        const resourceUrl = stripFragment(item.href);
        if (state.resourceCache.has(resourceUrl)) {
            return;
        }

        const htmlText = await fetchText(item.href);
        state.resourceCache.set(resourceUrl, htmlText);
    }

    async function preloadResourceStyles(item) {
        const resourceUrl = stripFragment(item.href);
        const htmlText = state.resourceCache.get(resourceUrl);
        if (htmlText === undefined) {
            return;
        }

        const resourceDocument = new DOMParser().parseFromString(htmlText, "text/html");
        const stylesheetLinks = Array.from(resourceDocument.querySelectorAll("link[rel~='stylesheet'][href]"));
        await Promise.all(stylesheetLinks.map(async (link) => {
            const stylesheetUrl = getAbsoluteUrl(link.getAttribute("href"), resourceUrl);
            const cssText = await fetchText(stylesheetUrl);
            state.publicationStyles.set(stripFragment(stylesheetUrl), cssText);
        }));
    }

    async function preloadRemainingResources() {
        const pendingItems = state.spine.slice(1);
        let nextIndex = 0;
        const workerCount = Math.min(4, pendingItems.length);
        const workers = Array.from({ length: workerCount }, async () => {
            while (nextIndex < pendingItems.length) {
                const item = pendingItems[nextIndex++];
                try {
                    await preloadResource(item);
                } catch (error) {
                    console.warn(`Unable to preload publication resource ${item.href}.`, error);
                }
            }
        });

        await Promise.all(workers);
    }

    function parsePackage(packageText, opfUrl) {
        const packageDocument = new DOMParser().parseFromString(packageText, "application/xml");
        if (packageDocument.querySelector("parsererror")) {
            throw new Error("The EPUB package document is not valid XML.");
        }

        const manifest = new Map();
        for (const item of getLocalNameNodes(packageDocument, "item")) {
            const id = item.getAttribute("id");
            const href = item.getAttribute("href");
            if (!id || !href) {
                continue;
            }

            manifest.set(id, {
                href: getAbsoluteUrl(href, opfUrl),
                id,
                mediaType: item.getAttribute("media-type") ?? "",
                properties: item.getAttribute("properties") ?? ""
            });
        }

        const spineElement = getLocalNameNodes(packageDocument, "spine")[0];
        if (!spineElement) {
            throw new Error("The EPUB package does not contain a spine.");
        }

        const spine = [];
        for (const itemReference of getLocalNameNodes(spineElement, "itemref")) {
            if (itemReference.getAttribute("linear") === "no") {
                continue;
            }

            const item = manifest.get(itemReference.getAttribute("idref"));
            if (item) {
                spine.push({
                    ...item,
                    index: spine.length,
                    pageCount: 1,
                    label: ""
                });
            }
        }

        if (spine.length === 0) {
            throw new Error("The EPUB package does not contain readable spine resources.");
        }

        const title = getLocalNameNodes(packageDocument, "title")[0]?.textContent?.trim() ?? "Untitled publication";
        const author = getLocalNameNodes(packageDocument, "creator")[0]?.textContent?.trim() ?? "";

        return {
            manifest,
            metadata: { author, title },
            spine,
            nav: Array.from(manifest.values()).find((item) => item.properties.split(/\s+/u).includes("nav"))
        };
    }

    function getSpineIndex(url) {
        const resourceUrl = stripFragment(url);
        return state.spine.findIndex((item) => stripFragment(item.href) === resourceUrl);
    }

    function getSpineLabel(spineIndex) {
        const item = state.spine[spineIndex];
        const tocItem = state.toc.find((entry) => entry.spineIndex === spineIndex);
        if (tocItem) {
            return tocItem.label;
        }
        return item?.href.split("/").pop() ?? "Publication";
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
        elements.loading.textContent = message;
        elements.loading.hidden = !isLoading;
    }

    function addStylesheet(documentElement, href, insertBefore) {
        return new Promise((resolve) => {
            const link = documentElement.createElement("link");
            link.rel = "stylesheet";
            link.href = getAbsoluteUrl(href, window.location.href);
            link.onload = resolve;
            link.onerror = resolve;
            insertBefore(link, documentElement.head.firstChild);
        });
    }

    function createPaginationStyle(documentElement) {
        const style = documentElement.createElement("style");
        style.id = FRAME_STYLE_ID;
        style.textContent = `
            :root {
                --reader-column-width: 100vw;
                --reader-page-gutter: clamp(1rem, 5vw, 4.5rem);
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
                background: var(--USER__backgroundColor, #f6f1e8) !important;
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
                width: 100% !important;
                min-width: 0 !important;
                max-width: var(--USER__lineLength, 100%) !important;
                min-height: 100% !important;
                margin: 0 !important;
                padding: 2.25rem var(--reader-page-gutter) !important;
                box-sizing: border-box !important;
                overflow: visible !important;
                background: transparent !important;
            }

            :root[style*="--USER__fontFamily"] body,
            :root[style*="--USER__fontFamily"] body * {
                font-family: var(--USER__fontFamily) !important;
            }

            :root[style*="readium-scroll-on"] body {
                max-width: var(--USER__lineLength, 100%) !important;
                min-height: 100% !important;
                overflow: visible !important;
            }

            body.cover-page {
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

            body.cover-page img,
            body.cover-page svg {
                display: block !important;
                width: auto !important;
                height: auto !important;
                max-width: 100vw !important;
                max-height: 100vh !important;
                margin: 0 !important;
                object-fit: contain !important;
            }

            img, svg, video, canvas, iframe {
                max-width: 100% !important;
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

    function applyCoverPageLayout(frameDocument) {
        const body = frameDocument.body;
        const root = frameDocument.documentElement;
        if (!body || !root) {
            return;
        }

        const media = body.querySelectorAll("img, svg");
        const text = (body.textContent ?? "").replace(/\s+/gu, "").trim();
        const hasOnlyCoverMedia = media.length === 1 &&
            !body.querySelector("video, audio, canvas, table, form") &&
            text.length === 0;
        const isCoverPage = body.classList.contains("cover-page") ||
            (state.currentSpineIndex === 0 && hasOnlyCoverMedia);

        root.classList.toggle("cover-page", isCoverPage);
        body.classList.toggle("cover-page", isCoverPage);
    }

    async function prepareFrame(frameDocument) {
        if (!frameDocument.head || !frameDocument.documentElement) {
            throw new Error("The EPUB resource does not contain a usable HTML document.");
        }

        const stylesheetPromises = [];
        for (const stylesheet of [...READER_STYLESHEETS].reverse()) {
            const stylesheetUrl = getAbsoluteUrl(stylesheet, window.location.href);
            const cssText = state.readerStyles.get(stripFragment(stylesheetUrl));
            if (cssText === undefined) {
                stylesheetPromises.push(addStylesheet(frameDocument, stylesheet, frameDocument.head.insertBefore.bind(frameDocument.head)));
                continue;
            }

            const style = frameDocument.createElement("style");
            style.textContent = cssText;
            frameDocument.head.insertBefore(style, frameDocument.head.firstChild);
        }
        createPaginationStyle(frameDocument);
        applyCoverPageLayout(frameDocument);
        await Promise.all(stylesheetPromises);
        applySettingsToFrame(frameDocument);

        if (frameDocument.fonts?.ready) {
            await frameDocument.fonts.ready;
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
        const links = elements.contentsList.querySelectorAll("a[data-spine-index]");
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
        elements.location.textContent = isScrollMode
            ? `${getSpineLabel(state.currentSpineIndex)} · Continuous scroll`
            : `${getSpineLabel(state.currentSpineIndex)} · Page ${state.currentPage + 1} of ${state.pageCount}`;
        elements.bookTitle.textContent = state.metadata.title;
        elements.author.textContent = state.metadata.author;
        document.title = `${state.metadata.title} · EPUB Reader`;
        updateProgress();
        updateTocHighlight();
        if (item) {
            elements.frame.setAttribute("aria-label", `${getSpineLabel(state.currentSpineIndex)}, page ${state.currentPage + 1}`);
        }
        if (state.isReady && item) {
            notifyNative("locationChanged", {
                resourceHref: item.href,
                page: state.currentPage,
                pageCount: state.pageCount
            });
        }
    }

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

    function measurePageLayout(preservePosition = false, preservedScrollRatio = null) {
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
            state.pageCount = 1;
            state.currentPage = 0;
            state.spine[state.currentSpineIndex].pageCount = 1;
            restoreScrollPosition(scrollRatio);
            updateUi();
            return;
        }

        const columnCount = Number(getEffectiveSettings().columnCount);
        frameDocument.documentElement.style.setProperty("--reader-column-width", `${state.viewportWidth / columnCount}px`);
        const scrollWidth = getScrollWidth();
        state.pageCount = Math.max(1, Math.ceil((scrollWidth - 1) / state.viewportWidth));
        if (preservePosition && oldPageCount > 1) {
            state.currentPage = Math.min(state.pageCount - 1, Math.round(oldPosition * (state.pageCount - 1)));
        } else {
            state.currentPage = Math.min(state.currentPage, state.pageCount - 1);
        }
        state.spine[state.currentSpineIndex].pageCount = state.pageCount;
        scrollToCurrentPage();
        updateUi();
    }

    function waitForNextFrame() {
        return new Promise((resolve) => {
            window.requestAnimationFrame(() => {
                window.requestAnimationFrame(resolve);
            });
        });
    }

    function isInteractiveTarget(target) {
        return target instanceof Element && Boolean(target.closest("a, button, input, select, textarea, video, audio, summary, [contenteditable=\"true\"]"));
    }

    function hasTextSelection(frameDocument) {
        const selection = frameDocument.defaultView?.getSelection();
        return Boolean(selection && !selection.isCollapsed && selection.toString().trim());
    }

    function setReaderChromeVisible(isVisible) {
        state.chromeVisible = isVisible;
        elements.readerShell.classList.toggle("reader-shell--immersive", !isVisible);
        elements.readerShell.dataset.chromeVisible = String(isVisible);
        if (!isVisible) {
            closeContents();
            closeSettings();
        }
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
        let pointerStart = null;
        frameDocument.addEventListener("click", (event) => {
            const link = event.target instanceof Element ? event.target.closest("a[href]") : null;
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
        if (event.target instanceof Element && event.target.matches("input, select, textarea, [contenteditable=\"true\"]")) {
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
        const targetUrl = getAbsoluteUrl(link.getAttribute("href"), state.documentUrl);
        const targetIndex = getSpineIndex(targetUrl);
        if (targetIndex < 0) {
            return;
        }

        event.preventDefault();
        const target = new URL(targetUrl);
        loadResource(targetIndex, target.hash).catch(setError);
    }

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

    function processPendingProgressSeek() {
        if (state.progressSeek.isLoading || state.progressSeek.pendingValue === null || state.spine.length === 0) {
            return;
        }

        const requestedValue = state.progressSeek.pendingValue;
        const behavior = state.progressSeek.behavior;
        state.progressSeek.pendingValue = null;
        const target = getProgressTarget(requestedValue);
        if (target.targetIndex === state.currentSpineIndex && state.isReady) {
            applyProgressTarget(target, behavior);
            return;
        }

        state.progressSeek.isLoading = true;
        loadResource(target.targetIndex)
            .then(() => {
                if (state.progressSeek.pendingValue === null) {
                    applyProgressTarget(target, "auto");
                }
            })
            .catch(setError)
            .finally(() => {
                state.progressSeek.isLoading = false;
                processPendingProgressSeek();
            });
    }

    function seekToProgress(value, behavior = "auto") {
        if (state.spine.length === 0) {
            return;
        }

        state.progressSeek.pendingValue = value;
        state.progressSeek.behavior = behavior;
        processPendingProgressSeek();
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
            loadResource(state.currentSpineIndex + 1).catch(setError);
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
            loadResource(state.currentSpineIndex - 1, "", true).catch(setError);
        }
    }

    async function loadResource(spineIndex, fragment = "", openAtEnd = false) {
        const item = state.spine[spineIndex];
        if (!item) {
            return;
        }

        state.isReady = false;
        state.currentSpineIndex = spineIndex;
        state.currentPage = 0;
        state.pageCount = 1;
        state.documentUrl = stripFragment(item.href);
        setLoading(true, `Loading ${getSpineLabel(spineIndex)}…`);
        updateUi();

        const token = ++state.loadToken;
        await new Promise((resolve, reject) => {
            state.pendingLoad = { fragment, openAtEnd, reject, resolve, token };
            const cachedHtml = state.resourceCache.get(stripFragment(item.href));
            if (cachedHtml !== undefined) {
                elements.frame.removeAttribute("src");
                elements.frame.srcdoc = createCachedResource(cachedHtml, stripFragment(item.href));
            } else {
                elements.frame.removeAttribute("srcdoc");
                elements.frame.src = fragment ? `${item.href}${fragment}` : item.href;
            }
        });
    }

    async function handleFrameLoad() {
        const pendingLoad = state.pendingLoad;
        if (!pendingLoad?.token || pendingLoad.token !== state.loadToken) {
            return;
        }

        try {
            await prepareFrame(elements.frame.contentDocument);
            if (pendingLoad.token !== state.loadToken) {
                return;
            }
            await waitForNextFrame();
            installFrameInputHandlers(elements.frame.contentDocument);
            state.isReady = true;
            measurePageLayout();
            if (pendingLoad.openAtEnd) {
                state.currentPage = state.pageCount - 1;
                scrollToCurrentPage();
            } else if (pendingLoad.fragment) {
                const fragmentId = pendingLoad.fragment.slice(1);
                const target = elements.frame.contentDocument.getElementById(decodeURIComponent(fragmentId));
                if (target) {
                    target.scrollIntoView({ block: "start" });
                    state.currentPage = Math.min(state.pageCount - 1, Math.max(0, Math.round(getFrameScroller().scrollLeft / state.viewportWidth)));
                }
            }
            updateUi();
            updateTocHighlight();
            setLoading(false);
            elements.error.hidden = true;
            notifyNative("readerReady", {
                resourceHref: state.spine[state.currentSpineIndex].href,
                page: state.currentPage,
                pageCount: state.pageCount
            });
            pendingLoad.resolve();
        } catch (error) {
            pendingLoad.reject(error);
        } finally {
            if (state.pendingLoad === pendingLoad) {
                state.pendingLoad = null;
            }
        }
    }

    async function loadContents(navItem) {
        if (!navItem) {
            return;
        }

        const navUrl = navItem.href;
        const navText = await fetchText(navUrl);
        const navDocument = new DOMParser().parseFromString(navText, "application/xhtml+xml");
        const links = Array.from(navDocument.querySelectorAll("nav a[href], a[href]"));
        const toc = [];
        for (const link of links) {
            const href = link.getAttribute("href");
            if (!href) {
                continue;
            }
            const targetUrl = getAbsoluteUrl(href, navUrl);
            const target = new URL(targetUrl);
            const spineIndex = getSpineIndex(target.href);
            if (spineIndex < 0) {
                continue;
            }
            toc.push({
                fragment: target.hash,
                label: link.textContent.trim() || getSpineLabel(spineIndex),
                spineIndex
            });
        }

        state.toc = toc;
        elements.contentsList.replaceChildren();
        for (const entry of toc) {
            const listItem = document.createElement("li");
            const link = document.createElement("a");
            link.href = "#";
            link.dataset.spineIndex = String(entry.spineIndex);
            link.textContent = entry.label;
            link.addEventListener("click", (event) => {
                event.preventDefault();
                loadResource(entry.spineIndex, entry.fragment).then(closeContents).catch(setError);
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
        elements.frame.addEventListener("load", () => handleFrameLoad().catch(setError));
        elements.readerBack.addEventListener("click", () => notifyNative("requestExit"));
        elements.previous.addEventListener("click", goPrevious);
        elements.next.addEventListener("click", goNext);
        elements.progressSlider.addEventListener("input", (event) => {
            seekToProgress(event.target.value);
            previewProgress(event.target.value);
        });
        elements.progressSlider.addEventListener("change", (event) => {
            seekToProgress(event.target.value);
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
                    applySettingsToFrame(elements.frame.contentDocument);
                    measurePageLayout(true);
                }
            }, 120);
        });
    }

    function setLocator(resourceHref, page = 0) {
        const targetIndex = getSpineIndex(resourceHref);
        if (targetIndex < 0) {
            return;
        }

        if (targetIndex !== state.currentSpineIndex) {
            loadResource(targetIndex).then(() => goToPage(page)).catch(setError);
            return;
        }

        goToPage(page);
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

    window.DisplayBookReader = { setLocator, setSettings };

    async function initialize() {
        bindHostEvents();
        setReaderChromeVisible(false);
        setLoading(true, "Opening publication…");
        if (!OPF_PATH) {
            throw new Error("The reader did not receive an EPUB package path.");
        }
        const opfUrl = getAbsoluteUrl(OPF_PATH, window.location.href);
        state.opfUrl = opfUrl;
        const packageText = await fetchText(opfUrl);
        const publication = parsePackage(packageText, opfUrl);
        state.manifest = publication.manifest;
        state.metadata = publication.metadata;
        state.spine = publication.spine;
        await loadContents(publication.nav);
        await Promise.all([
            preloadReaderStyles(),
            preloadResource(state.spine[0])
        ]);
        await preloadResourceStyles(state.spine[0]);
        await loadResource(0);
        state.preloadPromise = Promise.all([
            preloadPublicationStyles(publication.manifest),
            preloadRemainingResources()
        ]).catch((error) => {
            console.warn("Unable to preload the complete publication.", error);
        });
    }

    initialize().catch(setError);
})();
