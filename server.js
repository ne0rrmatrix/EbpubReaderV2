const http = require("node:http");
const fs = require("node:fs");
const path = require("node:path");
const { URL } = require("node:url");

const rootDirectory = path.resolve(__dirname);
const port = Number.parseInt(process.env.PORT ?? "0", 10);

const mimeTypes = new Map([
    [".css", "text/css; charset=utf-8"],
    [".gif", "image/gif"],
    [".html", "text/html; charset=utf-8"],
    [".jpeg", "image/jpeg"],
    [".jpg", "image/jpeg"],
    [".js", "text/javascript; charset=utf-8"],
    [".json", "application/json; charset=utf-8"],
    [".opf", "application/oebps-package+xml; charset=utf-8"],
    [".svg", "image/svg+xml"],
    [".xhtml", "application/xhtml+xml; charset=utf-8"],
    [".xml", "application/xml; charset=utf-8"],
    [".webp", "image/webp"]
]);

function isInsideRoot(filePath) {
    const relativePath = path.relative(rootDirectory, filePath);
    return relativePath === "" || (!relativePath.startsWith("..") && !path.isAbsolute(relativePath));
}

function getFilePath(requestUrl) {
    const pathname = decodeURIComponent(requestUrl.pathname);
    const relativePath = pathname === "/" ? "index.html" : pathname.replace(/^[/\\]+/, "");
    const filePath = path.resolve(rootDirectory, relativePath);

    if (!isInsideRoot(filePath)) {
        return null;
    }

    return filePath;
}

function sendText(response, statusCode, message) {
    response.writeHead(statusCode, {
        "Content-Type": "text/plain; charset=utf-8",
        "Cache-Control": "no-store"
    });
    response.end(message);
}

const server = http.createServer((request, response) => {
    if (request.method !== "GET" && request.method !== "HEAD") {
        sendText(response, 405, "Method Not Allowed");
        return;
    }

    let requestUrl;
    try {
        requestUrl = new URL(request.url ?? "/", "http://localhost");
    } catch {
        sendText(response, 400, "Bad Request");
        return;
    }

    let filePath;
    try {
        filePath = getFilePath(requestUrl);
    } catch {
        sendText(response, 400, "Bad Request");
        return;
    }

    if (!filePath) {
        sendText(response, 403, "Forbidden");
        return;
    }

    fs.stat(filePath, (statError, stats) => {
        if (statError || !stats.isFile()) {
            sendText(response, 404, "Not Found");
            return;
        }

        const extension = path.extname(filePath).toLowerCase();
        const headers = {
            "Content-Type": mimeTypes.get(extension) ?? "application/octet-stream",
            "Content-Length": stats.size,
            "Cache-Control": "no-cache"
        };
        response.writeHead(200, headers);

        if (request.method === "HEAD") {
            response.end();
            return;
        }

        const stream = fs.createReadStream(filePath);
        stream.on("error", () => {
            if (!response.headersSent) {
                sendText(response, 500, "Unable to read file");
                return;
            }
            response.destroy();
        });
        stream.pipe(response);
    });
});

server.on("error", (error) => {
    console.error("Unable to start the ebook server:", error.message);
    process.exitCode = 1;
});

server.listen(Number.isFinite(port) ? port : 0, "127.0.0.1", () => {
    const address = server.address();
    const activePort = typeof address === "object" && address ? address.port : port;
    console.log(`EPUB reader available at http://127.0.0.1:${activePort}/`);
});
