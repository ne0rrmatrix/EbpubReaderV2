# websters1913.db — source and licensing

## Content

`websters1913.db` is a SQLite lookup database built from the 1913 edition of
*Webster's Revised Unabridged Dictionary*, as digitized by Project Gutenberg:

- **Project Gutenberg EBook #29765**, "Webster's Unabridged Dictionary, by Various"
- Release date: August 22, 2009 (most recently updated July 6, 2025)
- Source text: <https://www.gutenberg.org/ebooks/29765>
  (plain text: <https://www.gutenberg.org/ebooks/29765.txt.utf-8>)

## License

The underlying 1913 dictionary text is **public domain in the United States** — its
copyright expired long ago, and Project Gutenberg distributes it "for the use of
anyone anywhere ... with almost no restrictions whatsoever." There is no attribution
or share-alike requirement, so it is compatible with bundling inside this MIT-licensed
project.

This is deliberately the plain 1913 text, **not** GCIDE (the GNU Collaborative
International Dictionary of English) — GCIDE layers later additions from other
sources under separate, less permissive terms. Sticking to the unmodified 1913 PG
text avoids any licensing ambiguity.

## Regenerating

Built by `tools/DictionaryBuilder`:

```
dotnet run --project tools/DictionaryBuilder -- <path-to-pg29765.txt> src/DisplayBook.Viewer/Resources/Raw/Dictionary/websters1913.db
```

Download the source text fresh from
<https://www.gutenberg.org/ebooks/29765.txt.utf-8> — it is not committed to this repo
(only the generated `.db` is).

## Schema

```sql
CREATE TABLE Definitions (
    Word TEXT NOT NULL,       -- display form, original casing (e.g. "AARD-VARK")
    WordKey TEXT NOT NULL,    -- lookup key: lowercased, and a second row without
                               -- hyphens where that differs (e.g. "aardvark")
    Definition TEXT NOT NULL  -- all senses concatenated into one block
);
CREATE INDEX IX_Definitions_WordKey ON Definitions(WordKey);
```

~101,000 unique headwords. Parsing accepts both the dictionary's `Defn:`-prefixed
sense paragraphs and its bare numbered-sense paragraphs (e.g. "1. ..."); it drops
etymology-only paragraphs since they're not useful for a quick reading lookup. As
with any bulk parse of 1913-era typeset text, expect the occasional rough edge —
this is a casual-reading aid, not a scholarly edition.
