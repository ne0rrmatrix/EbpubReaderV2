import fs from "node:fs/promises";
import path from "node:path";
import MCR from "monocart-coverage-reports";
import { coverageOptions } from "./coverage-options.js";

export default async function globalTeardown() {
    await MCR(coverageOptions).generate();

    // On Windows the report writes source paths with backslashes, which SonarQube Cloud's LCOV
    // import can't resolve ("Could not resolve 1 file paths"), so the file showed as uncovered.
    const lcov = path.join(coverageOptions.outputDir, "lcov.info");
    const text = await fs.readFile(lcov, "utf8");
    await fs.writeFile(lcov, text.replace(/^SF:(.*)$/gmu, (_, file) => `SF:${file.replaceAll("\\", "/")}`));
}
