import path from "node:path";
import { fileURLToPath } from "node:url";

export const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "../../../..");
export const readerSource = "src/DisplayBook.Viewer/Resources/Raw/DisplayBookViewer/EpubText.js";

// monocart-coverage-reports turns Chromium's V8 coverage into LCOV, which SonarQube Cloud reads
// (sonar.javascript.lcov.reportPaths). The path is rewritten to the repository-relative source
// path so the report lines up with the file SonarQube analyses.
export const coverageOptions = {
    name: "EpubText.js",
    outputDir: path.join(repoRoot, "artifacts", "coverage", "reader-js"),
    reports: ["lcovonly", "console-summary"],
    entryFilter: (entry) => entry.url.endsWith("/DisplayBookViewer/EpubText.js"),
    sourcePath: () => readerSource
};
