import { test, expect, payload, spine } from "./support/reader.js";

test.describe("booting", () => {
    test("reports shellReady and starts with the toolbar hidden", async ({ reader }) => {
        await reader.open();

        await expect(reader.page.locator(".reader-shell")).toHaveClass(/reader-shell--immersive/u);
        expect(reader.messages.map((message) => message.type)).toContain("shellReady");
        expect(await reader.call("isReaderAlive")).toBe(false);
    });

    test("restores stored settings and ignores invalid ones", async ({ reader }) => {
        await reader.open({ storedSettings: JSON.stringify({ version: 1, settings: { theme: "night", fontSize: "120%", lineHeight: "1.8", columnMode: "two", fontFamily: "comic-sans" } }) });

        await expect(reader.page.locator('input[name="theme"][value="night"]')).toBeChecked();
        await expect(reader.page.locator('select[name="fontFamily"]')).toHaveValue("original");
        await expect(reader.page.locator('output[data-for="fontSize"]')).toHaveText("120%");
        await expect(reader.page.locator('output[data-for="lineHeight"]')).toHaveText("1.8");
    });

    for (const [name, stored] of [["an old version", JSON.stringify({ version: 0, settings: { theme: "night" } })], ["corrupt JSON", "{ not json"]]) {
        test(`falls back to defaults for ${name}`, async ({ reader }) => {
            await reader.open({ storedSettings: stored });

            await expect(reader.page.locator('input[name="theme"][value="original"]')).toBeChecked();
        });
    }
});

test.describe("opening a publication", () => {
    test("shows the first chapter, the title and the contents", async ({ reader }) => {
        await reader.open();
        await reader.load();

        await expect(reader.page.locator("#book-title")).toHaveText("The Test Book");
        await expect(reader.page.locator("#book-author")).toHaveText("Ada Author");
        await expect(reader.page).toHaveTitle("The Test Book · EPUB Reader");
        await expect(reader.page.locator("#reader-loading")).toBeHidden();
        await expect(reader.location()).toHaveText(/^Cover · Page 1 of \d+$/u);
        await expect(reader.page.locator("#contents-list a")).toHaveText(["Cover", "Chapter One", "Chapter Two", "The Middle"]);
        await expect(reader.page.locator('#contents-list a[aria-current="page"]')).toHaveText("Cover");
        expect(reader.lastOf("readerReady").payload).toMatchObject({ resourceHref: spine[0].href, page: 0 });
        expect(await reader.call("isReaderAlive")).toBe(true);
        // The cover chapter is a single image, laid out as a cover page.
        await expect(reader.frame.locator("html")).toHaveClass(/cover-page/u);
    });

    test("rejects a publication without a spine", async ({ reader }) => {
        await reader.open();

        await reader.call("loadPublication", payload({ spine: [] }));

        const error = await reader.waitForMessage("readerError");
        expect(error.payload.message).toBe("The reader did not receive a publication to open.");
        await expect(reader.page.locator("#reader-error")).toBeVisible();
        await expect(reader.location()).toHaveText("Unable to open publication");
    });

    test("reports a combined document with no chapters", async ({ reader }) => {
        await reader.open();
        reader.combined = (await import("./support/reader.js")).combinedDocument({ sections: false });

        await reader.call("loadPublication", payload());

        const error = await reader.waitForMessage("readerError");
        expect(error.payload.message).toBe("The combined reading document is missing its chapters.");
    });

    test("opens a second book in the same shell", async ({ reader }) => {
        await reader.open();
        await reader.load();
        await reader.call("setLocator", spine[2].href, 0, -1);

        await reader.load(payload({ title: "Another Book", author: "", coverHref: null, toc: [] }));

        await expect(reader.page.locator("#book-title")).toHaveText("Another Book");
        await expect(reader.location()).toHaveText(/^Page 1 of \d+$/u);
        await expect(reader.page.locator("#contents-list a")).toHaveCount(0);
    });

    test("a newer publication supersedes one still loading", async ({ reader }) => {
        await reader.open();

        await reader.page.evaluate((value) => {
            window.DisplayBookReader.loadPublication({ ...value, title: "First" });
            window.DisplayBookReader.loadPublication({ ...value, title: "Second" });
        }, payload());

        await reader.waitForMessage("readerReady");
        await expect(reader.page.locator("#book-title")).toHaveText("Second");
    });
});

test.describe("turning pages", () => {
    test.beforeEach(async ({ reader }) => {
        await reader.open();
        await reader.load();
        await reader.call("setLocator", spine[1].href, 0, -1);
        await expect(reader.location()).toHaveText(/^Chapter One · Page 1 of \d+$/u);
    });

    // The page buttons are never displayed (EpubText.css hides them; pages turn by tap, swipe
    // or key), but they are still wired up, so they are clicked from script.
    test("next and previous buttons move a page at a time", async ({ reader }) => {
        await reader.page.locator("#next-page").evaluate((button) => button.click());
        await expect(reader.location()).toHaveText(/Page 2 of/u);

        await reader.page.locator("#previous-page").evaluate((button) => button.click());
        await expect(reader.location()).toHaveText(/Page 1 of/u);
        await expect.poll(() => reader.lastOf("locationChanged")?.payload.resourceHref).toBe(spine[1].href);
    });

    test("keys move through the chapter", async ({ reader }) => {
        await reader.frame.locator("body").press("ArrowRight");
        await expect(reader.location()).toHaveText(/Page 2 of/u);
        await reader.frame.locator("body").press("PageDown");
        await expect(reader.location()).toHaveText(/Page 3 of/u);
        await reader.frame.locator("body").press("ArrowLeft");
        await expect(reader.location()).toHaveText(/Page 2 of/u);
        await reader.frame.locator("body").press("PageUp");
        await expect(reader.location()).toHaveText(/Page 1 of/u);
        await reader.frame.locator("body").press("End");
        const pages = Number((await reader.location().textContent()).match(/of (\d+)/u)[1]);
        await expect(reader.location()).toHaveText(new RegExp(`Page ${pages} of ${pages}`, "u"));
        await reader.frame.locator("body").press("Home");
        await expect(reader.location()).toHaveText(/Page 1 of/u);
        await reader.frame.locator("body").press(" ");
        await expect(reader.location()).toHaveText(/Page 2 of/u);
    });

    test("moving past the end of a chapter opens the next, and back opens the previous at its end", async ({ reader }) => {
        await reader.frame.locator("body").press("End");
        await reader.frame.locator("body").press("ArrowRight");
        await expect(reader.location()).toHaveText(/^Chapter Two · Page 1 of/u);

        await reader.frame.locator("body").press("ArrowLeft");
        await expect(reader.location()).toHaveText(/^Chapter One · Page (\d+) of \1$/u);
    });

    test("tapping the edges turns pages and the middle toggles the toolbar", async ({ reader }) => {
        const box = await reader.page.locator("#page").boundingBox();
        await reader.page.mouse.click(box.x + box.width * 0.95, box.y + box.height / 2);
        await expect(reader.location()).toHaveText(/Page 2 of/u);
        await reader.page.mouse.click(box.x + box.width * 0.05, box.y + box.height / 2);
        await expect(reader.location()).toHaveText(/Page 1 of/u);

        await reader.page.mouse.click(box.x + box.width / 2, box.y + box.height / 2);
        await expect(reader.page.locator(".reader-shell")).not.toHaveClass(/reader-shell--immersive/u);
        await reader.waitForMessage("chromeVisibilityChanged", (message) => message.visible === true);
    });

    test("swiping turns pages", async ({ reader }) => {
        const swipe = (fromX, toX) => reader.frame.locator("body").evaluate((body, [from, to]) => {
            const options = { bubbles: true, clientY: 200 };
            body.dispatchEvent(new PointerEvent("pointerdown", { ...options, clientX: from }));
            body.dispatchEvent(new PointerEvent("pointerup", { ...options, clientX: to }));
        }, [fromX, toX]);

        await swipe(600, 300);
        await expect(reader.location()).toHaveText(/Page 2 of/u);
        await swipe(300, 600);
        await expect(reader.location()).toHaveText(/Page 1 of/u);
        // Too short to count as a swipe.
        await swipe(300, 320);
        await expect(reader.location()).toHaveText(/Page 1 of/u);
    });

    test("the progress slider jumps through the book", async ({ reader }) => {
        await reader.page.locator("#book-progress").evaluate((slider) => {
            slider.value = "800";
            slider.dispatchEvent(new Event("input", { bubbles: true }));
            slider.dispatchEvent(new Event("change", { bubbles: true }));
        });
        await expect(reader.location()).toHaveText(/^The Middle · /u);
        await expect(reader.page.locator("#book-progress")).toHaveAttribute("aria-valuetext", /% through book$/u);

        await reader.page.locator("#book-progress").evaluate((slider) => {
            slider.value = "1000";
            slider.dispatchEvent(new Event("change", { bubbles: true }));
        });
        await expect(reader.location()).toHaveText(/^The Middle · Page (\d+) of \1$/u);
    });
});

test.describe("links in the book", () => {
    test.beforeEach(async ({ reader }) => {
        await reader.open();
        await reader.load();
        await reader.call("setLocator", spine[1].href, 0, -1);
        await reader.frame.locator("body").press("End");
    });

    test("an internal link opens the target chapter at its fragment", async ({ reader }) => {
        await reader.frame.locator("#internal-link").click();

        await expect(reader.location()).toHaveText(/^The Middle · Page \d+ of/u);
        await expect(reader.location()).not.toHaveText(/Page 1 of/u);
    });

    test("external and mail links are handed to the app", async ({ reader }) => {
        await reader.frame.locator("#external-link").click();
        await reader.waitForMessage("openExternalLink", (message) => message.url === "https://example.com/more");

        await reader.frame.locator("#mail-link").click();
        await reader.waitForMessage("openExternalLink", (message) => message.url === "mailto:author@example.com");
    });

    test("links that go nowhere leave the reader where it is", async ({ reader }) => {
        const before = await reader.location().textContent();

        await reader.frame.locator("#missing-link").click();
        await reader.frame.locator("#ftp-link").click();

        await expect(reader.location()).toHaveText(before);
        expect(reader.messages.some((message) => message.type === "openExternalLink")).toBe(false);
    });
});

test.describe("toolbar and panels", () => {
    test.beforeEach(async ({ reader }) => {
        await reader.open();
        await reader.load();
        await reader.showChrome();
    });

    test("the contents panel jumps to a chapter and closes", async ({ reader }) => {
        await reader.page.locator("#contents-toggle").click();
        await expect(reader.page.locator("#contents-panel")).toBeVisible();

        await reader.page.locator("#contents-list a", { hasText: "Chapter Two" }).click();

        await expect(reader.location()).toHaveText(/^Chapter Two · /u);
        await expect(reader.page.locator("#contents-panel")).toBeHidden();
        await expect(reader.page.locator('#contents-list a[aria-current="page"]')).toHaveText("Chapter Two");
    });

    test("panels close with their buttons, their toggles and Escape", async ({ reader }) => {
        const contents = reader.page.locator("#contents-panel");
        const settings = reader.page.locator("#settings-panel");

        await reader.page.locator("#contents-toggle").click();
        await reader.page.locator("#contents-toggle").click();
        await expect(contents).toBeHidden();
        await reader.page.locator("#contents-toggle").click();
        await reader.page.locator("#contents-close").click();
        await expect(contents).toBeHidden();
        await reader.page.locator("#contents-toggle").click();
        await reader.page.keyboard.press("Escape");
        await expect(contents).toBeHidden();

        await reader.page.locator("#settings-toggle").click();
        await expect(settings).toBeVisible();
        await reader.page.keyboard.press("Escape");
        await expect(settings).toBeHidden();
        await reader.page.locator("#settings-toggle").click();
        await reader.page.locator("#settings-toggle").click();
        await expect(settings).toBeHidden();
        await reader.page.locator("#settings-toggle").click();
        await reader.page.locator("#settings-close").click();
        await expect(settings).toBeHidden();
        await reader.page.keyboard.press("Escape");
    });

    test("hiding the toolbar closes any open panel", async ({ reader }) => {
        await reader.page.locator("#settings-toggle").click();

        // The open panel covers the middle of the window, so the tap goes to the page directly.
        await reader.frame.locator("body").evaluate((body) => {
            const width = body.ownerDocument.documentElement.clientWidth;
            body.dispatchEvent(new MouseEvent("click", { bubbles: true, clientX: width / 2, clientY: 200 }));
        });

        await expect(reader.page.locator(".reader-shell")).toHaveClass(/reader-shell--immersive/u);
        await expect(reader.page.locator("#settings-panel")).toBeHidden();
    });

    test("the back button asks the app to close the reader", async ({ reader }) => {
        await reader.page.locator("#reader-back").click();

        await reader.waitForMessage("requestExit");
    });
});

test.describe("settings", () => {
    test.beforeEach(async ({ reader }) => {
        await reader.open();
        await reader.load();
        await reader.call("setLocator", spine[1].href, 0, -1);
        await reader.showChrome();
        await reader.page.locator("#settings-toggle").click();
    });

    test("themes restyle the book and are reported to the app", async ({ reader }) => {
        for (const theme of ["night", "sepia", "paper", "original"]) {
            await reader.page.locator(`input[name="theme"][value="${theme}"]`).check();
            await reader.waitForMessage("themeChanged", (message) => message.theme === theme);
        }

        await reader.page.locator('input[name="theme"][value="night"]').check();
        await expect.poll(() => reader.frame.locator("html").evaluate((root) => root.style.getPropertyValue("--USER__backgroundColor"))).toBe("#1c2430");
        const stored = await reader.page.evaluate(() => JSON.parse(window.localStorage.getItem("displaybook.reader.settings.v1")));
        expect(stored.settings.theme).toBe("night");
    });

    test("every typography control reaches the book", async ({ reader }) => {
        const choices = {
            fontFamily: "serif", lineLength: "65ch", textAlignment: "justify", hyphenation: "auto",
            paragraphSpacing: "0.35em", paragraphIndent: "1em", imageTreatment: "dim-invert",
            fontWeight: "700", wordSpacing: "0.08em", letterSpacing: "0.03em"
        };
        for (const [name, value] of Object.entries(choices)) {
            await reader.page.locator(`select[name="${name}"]`).selectOption(value);
        }
        await reader.page.locator('input[name="fontSize"]').fill("130");
        await reader.page.locator('input[name="lineHeight"]').fill("1.8");

        const style = (name) => reader.frame.locator("html").evaluate((root, variable) => root.style.getPropertyValue(variable), name);
        await expect.poll(() => style("--USER__fontSize")).toBe("130%");
        expect(await style("--USER__lineHeight")).toBe("1.8");
        expect(await style("--USER__textAlign")).toBe("justify");
        expect(await style("--USER__invertImages")).toBe("1");
        await expect(reader.page.locator('output[data-for="fontSize"]')).toHaveText("130%");

        await reader.page.locator("#settings-reset").click();
        await expect.poll(() => style("--USER__fontSize")).toBe("100%");
        expect(await style("--USER__textAlign")).toBe("");
        await expect(reader.page.locator('output[data-for="lineHeight"]')).toHaveText("Original");
    });

    test("continuous scroll replaces pages and paging comes back", async ({ reader }) => {
        await reader.page.locator('select[name="paginationMode"]').selectOption("scroll");
        await expect(reader.location()).toHaveText("Chapter One · Continuous scroll");

        await reader.page.keyboard.press("Escape");
        await reader.frame.locator("html").evaluate((root) => { root.ownerDocument.scrollingElement.scrollTop = 600; });
        await reader.page.locator("#next-page").evaluate((button) => button.click());
        await expect(reader.location()).toHaveText("Chapter Two · Continuous scroll");
        await reader.page.locator("#previous-page").evaluate((button) => button.click());
        await expect(reader.location()).toHaveText("Chapter One · Continuous scroll");
        await reader.page.locator("#book-progress").evaluate((slider) => {
            slider.value = "300";
            slider.dispatchEvent(new Event("change", { bubbles: true }));
        });
        await reader.call("setLocator", spine[2].href, 0, 100);
        await expect(reader.location()).toHaveText("Chapter Two · Continuous scroll");
        await reader.call("setSettings", { lineHeight: "2" });

        await reader.call("setSettings", { paginationMode: "paged" });
        await expect(reader.location()).toHaveText(/^Chapter Two · Page \d+ of/u);
    });

    test("two columns are used on a wide window", async ({ reader }) => {
        await reader.page.setViewportSize({ width: 1400, height: 800 });
        await reader.page.locator('select[name="columnMode"]').selectOption("two");

        await expect.poll(() => reader.frame.locator("html").evaluate((root) => root.style.getPropertyValue("--USER__colCount"))).toBe("2");
        await reader.page.keyboard.press("Escape");
        // The new layout can still be settling (a later re-measure lands back on page 1), so
        // press End again until the last page sticks.
        await expect(async () => {
            await reader.frame.locator("body").press("End");
            await expect(reader.location()).toHaveText(/Page (\d+) of \1/u, { timeout: 1000 });
        }).toPass();
    });
});

test.describe("positions from the app", () => {
    test.beforeEach(async ({ reader }) => {
        await reader.open();
        await reader.load();
    });

    test("setLocator opens a chapter at a page", async ({ reader }) => {
        await reader.call("setLocator", spine[2].href, 2, -1);

        await expect(reader.location()).toHaveText(/^Chapter Two · Page 3 of/u);
        await reader.waitForMessage("locationChanged", (message) => message.resourceHref === spine[2].href && message.page === 2);
    });

    test("setLocator resolves a character offset", async ({ reader }) => {
        await reader.call("setLocator", spine[3].href, 0, 5000);
        await expect(reader.location()).not.toHaveText(/^The Middle · Page 1 of/u);

        // Within the chapter already open, too.
        await reader.call("setLocator", spine[3].href, 0, 10);
        await expect(reader.location()).toHaveText(/^The Middle · Page 1 of/u);
        const reported = await reader.waitForMessage("locationChanged", (message) => message.resourceHref === spine[3].href && message.charOffset >= 0);
        expect(reported.payload.charOffset).toBeGreaterThanOrEqual(0);
    });

    test("setLocator accepts a legacy absolute URL", async ({ reader }) => {
        await reader.call("setLocator", `https://old-host.invalid/books/${spine[2].href}`, 0, -1);

        await expect(reader.location()).toHaveText(/^Chapter Two · /u);
    });

    test("setLocator falls back to the current chapter for an unknown book position", async ({ reader }) => {
        await reader.call("setLocator", spine[1].href, 1, -1);
        await expect(reader.location()).toHaveText(/^Chapter One · Page 2 of/u);

        await reader.call("setLocator", "OEBPS/Text/not-in-this-book.xhtml", 7, 900);
        await expect(reader.location()).toHaveText(/^Chapter One · Page 1 of/u);

        await reader.call("setLocator", "not a url at all", 0, -1);
        await reader.call("setLocator", "", 0, -1);
        await expect(reader.location()).toHaveText(/^Chapter One · Page 1 of/u);
    });

    test("safe-area insets are applied and repeated ones ignored", async ({ reader }) => {
        await reader.call("setSafeAreaInsets", 24, 16);
        await reader.call("setSafeAreaInsets", 24, 16);
        await reader.call("setSafeAreaInsets", "junk", -5);

        expect(await reader.frame.locator("html").evaluate((root) => root.style.getPropertyValue("--reader-safe-area-inset-top"))).toBe("0px");
    });

    test("resizing the window re-paginates", async ({ reader }) => {
        await reader.call("setLocator", spine[1].href, 0, -1);
        const before = await reader.location().textContent();

        // Shorter, not narrower: ReadiumCSS caps the line length, so width alone may not change the count.
        await reader.page.setViewportSize({ width: 1000, height: 400 });

        await expect(reader.location()).not.toHaveText(before);
    });
});

test.describe("selecting text", () => {
    test.beforeEach(async ({ reader }) => {
        await reader.open();
        await reader.load();
        await reader.call("setLocator", spine[1].href, 0, -1);
    });

    test("offers a lookup for the selection and hands it to the app", async ({ reader }) => {
        await reader.frame.locator("section#chapter-1 p").first().evaluate((paragraph) => {
            const range = paragraph.ownerDocument.createRange();
            const text = paragraph.firstChild;
            range.setStart(text, 0);
            range.setEnd(text, "Chapter 1, paragraph 1. The quick brown fox jumps over".length);
            const selection = paragraph.ownerDocument.defaultView.getSelection();
            selection.removeAllRanges();
            selection.addRange(range);
        });

        const button = reader.page.locator("#lookup-button");
        await expect(button).toBeVisible();
        await expect(button).toHaveText('Look up "Chapter 1, paragraph 1. …"');
        const info = await reader.call("getSelectionInfo");
        expect(info.text).toBe("Chapter 1, paragraph 1. The quick brown fox jumps over");
        expect(info.right).toBeGreaterThan(info.left);

        // The click that ends a selection doesn't turn the page. (A real press elsewhere clears the
        // selection before its click arrives, so only the click event itself is sent.)
        await reader.frame.locator("body").evaluate((body) => {
            const width = body.ownerDocument.documentElement.clientWidth;
            body.dispatchEvent(new MouseEvent("click", { bubbles: true, clientX: width * 0.95, clientY: 200 }));
        });
        await expect(reader.location()).toHaveText(/Page 1 of/u);

        await button.click();
        const lookup = await reader.waitForMessage("dictionaryLookupRequested");
        expect(lookup.payload.text).toBe(info.text);
        await expect(button).toBeHidden();
    });

    test("clearSelection removes the selection and the button", async ({ reader }) => {
        await reader.frame.locator("section#chapter-1 h1").selectText();
        await expect(reader.page.locator("#lookup-button")).toBeVisible();

        await reader.call("clearSelection");

        await expect(reader.page.locator("#lookup-button")).toBeHidden();
        expect(await reader.call("getSelectionInfo")).toBeNull();
    });

    test("the lookup button with nothing pending just hides", async ({ reader }) => {
        await reader.page.locator("#lookup-button").evaluate((button) => {
            button.hidden = false;
            button.click();
        });

        await expect(reader.page.locator("#lookup-button")).toBeHidden();
        expect(reader.messages.some((message) => message.type === "dictionaryLookupRequested")).toBe(false);
    });
});
