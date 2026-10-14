# User guide

How to use DisplayBook: building your library, reading, getting books from a catalog, and syncing your place between devices.

The top-bar buttons are icons. Hover over one for its name, or use a screen reader, which announces each one.

## Your library

The library opens when the app starts.

- **Add EPUB** picks an `.epub` file to add.
- **Add folder** imports every EPUB in a folder and its subfolders. A folder holding an unpacked EPUB (an extracted book) is zipped up and imported as one book. While a folder is importing, a progress bar shows how far it has got, and **Cancel** stops it; books already added stay.
- A book that is already in the library is skipped, even under another file name: books are compared by content.
- **Search** filters by title or author, and the sort menu orders the library by **Recently added**, **Recently opened**, **Title (A–Z)** or **Author (A–Z)**. Pull down to refresh.
- **Select books** turns on selection. Tap books to select them (or **Select all**), then **Delete** to remove them and their files, or **Cancel** to leave selection.

Imported books are copied into the app's own storage, so you can move or delete the original files afterwards.

## Book details

Selecting a book shows its cover, title, author, description and language.

- **Open book** opens it in the reader.
- **Update metadata** looks the book up online (Google Books, then Open Library) and lists suggested changes next to the current values, including a new cover. Choose the ones you want and **Apply selected**, or **Discard** them. **Undo** reverts the last update.

## Reading

Tap the middle of the page to show or hide the toolbar. With it hidden, the book fills the screen.

**Turning pages:**

- Tap the left or right edge of the page, or swipe.
- On a keyboard: Right arrow, Page Down or Space for the next page; Left arrow or Page Up for the previous one; Home and End for the start and end of the chapter. Esc closes an open panel.
- Drag the progress bar to jump through the book.

**The toolbar:**

- **Back** returns to the library.
- **Contents** shows the table of contents; select a chapter to go there.
- **Settings** opens the reader settings below.
- The footer shows the chapter and page ("Page N of M").

**Reader settings:**

| Setting | Choices |
| --- | --- |
| Theme | Original (the book's own styling), Paper, Sepia, Night |
| Font family | Original, Serif, Sans, Humanist, Monospace |
| Pagination | Paginated, or Continuous scroll |
| Columns | One column, or Two columns (wide screens) |
| Reading width | Full width, Wide, Comfortable, Narrow |
| Font size, Line height | Sliders |
| Alignment, Hyphenation, Paragraph spacing, Paragraph indent, Font weight | The book's default, or your choice |
| Word spacing, Letter spacing | Normal, Wide, Extra wide |
| Image treatment | Normal, Dim, Invert, Dim and invert (useful with the Night theme) |

**Reset to defaults** restores them all.

**Looking up a word:** select it and use the lookup button that appears. Definitions come from a dictionary built into the app (Webster's 1913), so lookups work offline.

**Links:** links within the book move the reader. Web and email links open in your browser or mail app.

**Your place is saved** as you read, and the book reopens where you left off. Positions are stored as a place in the text, not a page number, so changing the font, column or window size keeps you at the same sentence.

## Getting books from a catalog

DisplayBook can browse Calibre's content server and other OPDS catalogs on your network, and download books straight into your library. Select **Browse OPDS** in the library's top bar. Servers on your network are found automatically, or you can add one by its address. See [OPDS and Calibre](opds.md) for setting up Calibre and using catalogs.

## Syncing your place between devices

Sign in on the **Settings** page (the gear in the library's top bar) to keep your reading position in sync on every device you use.

1. **Create account** with an email address and password, or **Sign in** to an existing one.
2. Optional: turn on **two-factor authentication**. Verify your email address first (**Send verification email**, then **I've verified — refresh**). Then select **Set up authenticator app**, add the key it shows to an app such as Google Authenticator or Authy, and enter the 6-digit code to confirm. From then on, signing in also asks for a code.
3. Read as usual. Your position is uploaded as you read.

When you open a book, DisplayBook checks for a position from your other devices. If this device has never opened the book, it simply starts there. If the two positions differ, it asks whether to jump to the synced one, and says whether it is newer or older than this device's. When you come back to the app with a book open, it checks again and offers anything you read elsewhere in the meantime.

A book syncs between devices only if each device imported the same file: the copies must be identical.

**Sign out** on the Settings page stops syncing on this device.

## Your data

- **On your device.** Your books, covers, library and reading positions are stored in the app's own storage on each device. Nothing is uploaded unless you sign in.
- **What sync uploads.** When you're signed in, only reading positions are uploaded. For each book, that is the chapter, the place in it, the page shown and when you were there, stored against a fingerprint of the book's contents. Book contents never leave your device.
- **OPDS passwords.** Server passwords you save for OPDS catalogs are stored unencrypted in the app's storage; see [OPDS security notes](opds.md#security-notes).
