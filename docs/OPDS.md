# OPDS / Calibre Library Browsing

DisplayBook can connect to remote [OPDS](https://opds-spec.org/) catalogs, with first-class support for the **Calibre Content server**. DisplayBook acts as a **discovery client only**: it looks for Calibre servers on the local network, browses their catalogs, and downloads books into your local library. It never advertises itself as a server.

## How it works

| Capability | Mechanism |
| --- | --- |
| Server discovery | mDNS/Bonjour scan for the `_calibre._tcp` service type (Calibre publishes "Books in calibre" with a TXT record of `path=/opds`) |
| Manual servers | Any OPDS feed URL entered by hand — always works, even when mDNS is unavailable (e.g. Android networks that block mDNS) |
| Catalog browsing | Parses Atom-based OPDS 1.0 and OPDS 2.0 (RDF) feeds: entries, covers, pagination, search |
| Downloads | HTTP GET with resume support (HTTP range requests), up to 3 concurrent items, batch enqueue, pause/resume/cancel |
| Persistence | Server profiles in `Opds/servers.json`, feed cache in the app data directory |

## Setting up a Calibre server

DisplayBook connects to the standard Calibre Content server. To start it:

1. In Calibre, click **Connect/share** → **Start Content server**. The button then shows **Stop Content server** plus the address, for example `[192.168.1.5, port 8080]`.
2. The OPDS root is served at `http://<ip>:<port>/opds` by default (Calibre advertises `path=/opds` in the mDNS TXT record). The default port is **8080**.
3. Make sure the server host's firewall allows Calibre (port 8080) and that both devices are on the same network.
4. Optional: enable authentication via Calibre **Preferences → Sharing over the net → Require username and password to access the content server**. When set, add the server in DisplayBook with the matching username and password (HTTP Basic authentication).

> **Tip:** Verify the endpoint in a browser first — open `http://<ip>:<port>/opds` on any device; a valid OPDS feed is what DisplayBook will load.

## Using OPDS in DisplayBook

1. Open the **OPDS** section (from the library page).
2. **Discovered servers** appear automatically from the mDNS scan; tap one to connect.
3. To connect to a server that mDNS can't see, choose **Add server** and enter:
   - **Name** — a label for your records
   - **URL** — the OPDS root feed, e.g. `http://192.168.1.5:8080/opds`
   - **Username / password** — optional, only if the server requires authentication
4. Browse catalogs: feeds render as book lists with covers, titles, authors, and pagination (`Next`/`Previous`). Navigation links (sub-collections, author/tag facets) open as new catalog pages; search feeds are supported when the server provides a search link.
5. Open a book's details to see its cover, metadata, and all available formats (EPUB, PDF, MOBI, …). Each format link can be downloaded.
6. The **Downloads** page shows the queue with per-item progress. You can pause/resume a download (resumable via HTTP range) or cancel it. Finished books are imported into the local library.

## Sample configuration

Server profiles are stored in the app data directory at `Opds/servers.json` (camelCase JSON, `null` values omitted). Example of a manually added Calibre server:

```json
[
  {
    "id": "a1b2c3d4e5f60718293a4b5c6d7e8f90",
    "name": "Home Calibre",
    "url": "http://192.168.1.5:8080/opds",
    "type": "manual",
    "lastSeen": "2026-01-15T10:30:00.0000000",
    "isEnabled": true,
    "username": "reader",
    "password": "secret",
    "metadata": {}
  }
]
```

A discovered server has `"type": "discovered"` and carries mDNS details in `metadata` (service name, host, port, path) so the profile can be refreshed when the network address changes.

### Example OPDS feed (what DisplayBook parses)

```xml
<?xml version="1.0" encoding="utf-8"?>
<feed xmlns="http://www.w3.org/2005/Atom">
  <id>urn:uuid:catalog-root</id>
  <title>My Calibre Library</title>
  <updated>2026-01-15T10:00:00Z</updated>
  <totalResults>2</totalResults>
  <entry>
    <id>urn:uuid:book-1</id>
    <title>The Hobbit</title>
    <author><name>J.R.R. Tolkien</name></author>
    <updated>2026-01-10T09:00:00Z</updated>
    <summary>An unexpected party.</summary>
    <category term="Fantasy" label="Genre" scheme="http://purl.org/ontology/bibo/Genre" />
    <link rel="self" type="application/atom+xml;profile=opds-catalog"
          href="http://192.168.1.5:8080/opds/detail/1" />
    <link rel="http://opds-spec.org/acquisition" type="application/epub+zip"
          href="http://192.168.1.5:8080/opds/download/1" />
    <link rel="http://opds-spec.org/thumbnail" type="image/jpeg"
          href="http://192.168.1.5:8080/opds/cover/1" />
  </entry>
  <entry>
    <id>urn:uuid:book-2</id>
    <title>Dune</title>
    <author><name>Frank Herbert</name></author>
    <updated>2026-01-11T08:30:00Z</updated>
    <link rel="self" type="application/atom+xml;profile=opds-catalog"
          href="http://192.168.1.5:8080/opds/detail/2" />
    <link rel="http://opds-spec.org/acquisition" type="application/epub+zip"
          href="http://192.168.1.5:8080/opds/download/2" />
  </entry>
</feed>
```

DisplayBook understands the standard link roles: `self` (entry details), `http://opds-spec.org/acquisition` (downloadable formats), `http://opds-spec.org/thumbnail` and `image` (covers), `next`/`prev` (pagination), `up` (parent catalog), and `search` (search forms). Unknown MIME types fall back to a name derived from the file extension.

## Security notes

- Credentials are stored **in plain text** in `Opds/servers.json` inside the app data directory. Only store them for servers you trust, and prefer HTTPS for anything reachable outside your LAN.
- Calibre on a home network is usually served over plain HTTP on port 8080; this is the expected default for LAN use. For internet exposure, Calibre's docs recommend enabling authentication **and** HTTPS (or a reverse proxy with TLS).
- Discovered servers are re-resolved on each scan; a discovered profile is refreshed when Calibre's host or port changes.

## Limitations

- mDNS discovery works only on the same local network/subnet (and some networks block multicast entirely) — manual URL entry always works as a fallback.
- DisplayBook parses OPDS Atom feeds; the HTML "book list" pages of the Calibre server interface are not rendered in-app.
- Downloads require the server to serve the requested format link; formats Calibre hides (e.g. DRM-protected books) are not downloadable.
