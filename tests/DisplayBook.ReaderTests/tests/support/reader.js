import fs from "node:fs/promises";
import path from "node:path";
import { test as base, expect } from "@playwright/test";
import MCR from "monocart-coverage-reports";
import { coverageOptions, repoRoot } from "./coverage-options.js";

// The reader is hosted the way WebView2 hosts it on Windows: everything under this origin is
// answered in-process (ReaderAssetHost.windows.cs), so here every request to it is answered by
// page.route instead -- the shell from the repository, the book from the generated document below.
export const origin = "https://displaybook.invalid";
const viewerFolder = path.join(repoRoot, "src", "DisplayBook.Viewer", "Resources", "Raw", "DisplayBookViewer");

// EpubText.js reports to native by navigating to its bridge URL (taken from the "bridge" query
// parameter). Pointing it at this origin turns every message into an interceptable request; the
// route records it and aborts the navigation, exactly as EpubReaderView cancels it.
const bridgeUrl = `${origin}/bridge`;
export const shellUrl = `${origin}/DisplayBookViewer/index.html?bridge=${encodeURIComponent(bridgeUrl)}`;

const contentTypes = { ".js": "text/javascript", ".css": "text/css" };

const coverPng = Buffer.from(
    "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==",
    "base64");

export const spine = [
    { href: "OEBPS/Text/cover.xhtml", index: 0 },
    { href: "OEBPS/Text/ch1.xhtml", index: 1 },
    { href: "OEBPS/Text/ch2.xhtml", index: 2 },
    { href: "OEBPS/Text/ch3.xhtml", index: 3 }
];

export const toc = [
    { label: "Cover", spineIndex: 0, fragment: "" },
    { label: "Chapter One", spineIndex: 1, fragment: "" },
    { label: "Chapter Two", spineIndex: 2, fragment: "" },
    { label: "The Middle", spineIndex: 3, fragment: "#middle" }
];

function paragraphs(chapter, count) {
    let html = "";
    for (let index = 1; index <= count; index++) {
        const id = chapter === 3 && index === Math.floor(count / 2) ? ' id="middle"' : "";
        html += `<p${id}>Chapter ${chapter}, paragraph ${index}. The quick brown fox jumps over the lazy dog while the reader turns another page of this rather long chapter.</p>`;
    }
    return html;
}

/** The single combined document CombinedDocumentBuilder would produce for the book above. */
export function combinedDocument({ sections = true } = {}) {
    const body = !sections ? "<p>No chapters here.</p>" : [
        `<img src="../OEBPS/Images/cover.png" alt="Cover">`,
        `<h1>Chapter One</h1>${paragraphs(1, 60)}
         <p><a id="internal-link" href="ch3.xhtml#middle">Jump to the middle of chapter three</a></p>
         <p><a id="external-link" href="https://example.com/more">Read more online</a></p>
         <p><a id="mail-link" href="mailto:author@example.com">Write to the author</a></p>
         <p><a id="missing-link" href="not-in-the-book.xhtml">A chapter that isn't in the book</a></p>
         <p><a id="ftp-link" href="ftp://example.com/file">An unsupported link</a></p>`,
        `<h1>Chapter Two</h1>${paragraphs(2, 60)}`,
        `<h1>Chapter Three</h1>${paragraphs(3, 60)}`
    ].map((content, index) =>
        `<section id="chapter-${index}" data-chapter-index="${index}" data-chapter-href="${spine[index].href}">${content}</section>`).join("");
    return `<!DOCTYPE html><html><head><meta charset="utf-8">
        <link rel="stylesheet" href="../DisplayBookViewer/ReadiumCSS-before.css">
        <link rel="stylesheet" href="../DisplayBookViewer/ReadiumCSS-default.css">
        <link rel="stylesheet" href="../DisplayBookViewer/ReadiumCSS-after.css">
        <style>section[data-chapter-index]{display:none}</style></head><body>${body}</body></html>`;
}

export function payload(overrides = {}) {
    return {
        combinedHref: "../__displaybook_reader__/combined.html",
        title: "The Test Book",
        author: "Ada Author",
        spine,
        toc,
        coverHref: "../OEBPS/Images/cover.png",
        ...overrides
    };
}

/**
 * A reader shell in a fresh page: routes the origin, loads index.html, and waits for shellReady.
 * `messages` collects every bridge message in order.
 */
export class Reader {
    /** @param {import("@playwright/test").Page} page */
    constructor(page) {
        this.page = page;
        /** @type {{ type: string, payload: any }[]} */
        this.messages = [];
        this.combined = combinedDocument();
    }

    async open({ storedSettings } = {}) {
        if (storedSettings !== undefined) {
            await this.page.addInitScript((value) => {
                window.localStorage.setItem("displaybook.reader.settings.v1", value);
            }, storedSettings);
        }

        await this.page.route(`${origin}/**`, async (route) => {
            const url = new URL(route.request().url());
            if (url.pathname === "/bridge") {
                this.messages.push(JSON.parse(url.searchParams.get("message")));
                await route.abort("aborted");
                return;
            }
            if (url.pathname.startsWith("/DisplayBookViewer/")) {
                const file = path.join(viewerFolder, decodeURIComponent(url.pathname.slice("/DisplayBookViewer/".length)));
                const contentType = contentTypes[path.extname(file)] ?? "text/html";
                await route.fulfill({ body: await fs.readFile(file), contentType });
                return;
            }
            if (url.pathname === "/__displaybook_reader__/combined.html") {
                await route.fulfill({ body: this.combined, contentType: "text/html" });
                return;
            }
            if (url.pathname === "/OEBPS/Images/cover.png") {
                await route.fulfill({ body: coverPng, contentType: "image/png" });
                return;
            }
            await route.fulfill({ status: 404, body: "" });
        });

        // Only until the navigation commits: the shell sends its first bridge messages while it is
        // still loading, and those (aborted) navigations keep Playwright from seeing its load event.
        // shellReady is the real signal that it has booted.
        await this.page.goto(shellUrl, { waitUntil: "commit" });
        await this.waitForMessage("shellReady");
        return this;
    }

    /** Opens the test book (or the given payload) and waits for readerReady. */
    async load(publication = payload()) {
        const before = this.messages.length;
        await this.page.evaluate((value) => window.DisplayBookReader.loadPublication(value), publication);
        await expect.poll(() => this.messages.slice(before).some((message) => message.type === "readerReady")).toBe(true);
        return this;
    }

    async waitForMessage(type, predicate = () => true) {
        await expect.poll(() => this.messages.some((message) => message.type === type && predicate(message.payload)), {
            message: `bridge message ${type}`
        }).toBe(true);
        return this.messages.findLast((message) => message.type === type && predicate(message.payload));
    }

    lastOf(type) {
        return this.messages.findLast((message) => message.type === type);
    }

    call(method, ...args) {
        return this.page.evaluate(({ method, args }) => window.DisplayBookReader[method](...args), { method, args });
    }

    get frame() {
        return this.page.frameLocator("#page");
    }

    location() {
        return this.page.locator("#reader-location");
    }

    /** Shows the toolbar the way a tap in the middle of the page does. */
    async showChrome() {
        const box = await this.page.locator("#page").boundingBox();
        await this.page.mouse.click(box.x + box.width / 2, box.y + box.height / 2);
        await expect(this.page.locator(".reader-shell")).not.toHaveClass(/reader-shell--immersive/u);
    }
}

// Every test gets a page whose JavaScript coverage is collected and added to the shared report.
export const test = base.extend({
    reader: async ({ page }, use) => {
        await page.coverage.startJSCoverage({ resetOnNavigation: false });
        await use(new Reader(page));
        const coverage = await page.coverage.stopJSCoverage();
        await MCR(coverageOptions).add(coverage);
    }
});

export { expect };
