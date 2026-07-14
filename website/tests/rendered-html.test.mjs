import assert from "node:assert/strict";
import { access, readFile } from "node:fs/promises";
import test from "node:test";

const templateRoot = new URL("../", import.meta.url);

test("exports the CFS release landing page for GitHub Pages", async () => {
  const html = await readFile(new URL("../out/index.html", import.meta.url), "utf8");
  assert.match(html, /<title>CFS 0\.1\.0 Beta/);
  assert.match(html, /Compressed archives/);
  assert.match(html, /Download Windows Setup/);
  assert.match(html, /Experimental beta/);
  assert.match(html, /d3b64fc8167b39d40b92b74de0272f2d73ac62fdcb962e5cdede5fe04a0cd91e/);
  assert.match(html, /CFS-0\.1\.0-Beta-Setup\.exe/);
  assert.match(
    html,
    /<!-- Cloudflare Web Analytics --><script type='module' src='https:\/\/static\.cloudflareinsights\.com\/beacon\.min\.js'/,
  );
  assert.doesNotMatch(html, /codex-preview|Your site is taking shape|react-loading-skeleton/);
});

test("keeps release data centralized and removes starter assets", async () => {
  const [css, page, layout, release, packageJson] = await Promise.all([
    readFile(new URL("../app/globals.css", import.meta.url), "utf8"),
    readFile(new URL("../app/page.tsx", import.meta.url), "utf8"),
    readFile(new URL("../app/layout.tsx", import.meta.url), "utf8"),
    readFile(new URL("../app/release.ts", import.meta.url), "utf8"),
    readFile(new URL("../package.json", import.meta.url), "utf8"),
  ]);

  assert.match(release, /export const release: CfsRelease/);
  assert.match(release, /github\.com\/Mystrowin\/CFS\/releases\/download/);
  assert.match(page, /import \{ release \} from "\.\/release"/);
  assert.match(layout, /https:\/\/mystrowin\.github\.io\/CFS\//);
  assert.match(layout, /og\.png/);
  assert.match(css, /prefers-reduced-motion:\s*reduce/);
  assert.doesNotMatch(packageJson, /react-loading-skeleton/);
  assert.doesNotMatch(page + layout, /codex-preview|_sites-preview|Starter Project/);

  await assert.rejects(
    access(new URL("app/_sites-preview", templateRoot)),
  );
});
