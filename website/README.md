# CFS website

This is the traditional static source for the public CFS download website,
hosted exclusively on [GitHub Pages](https://mystrowin.github.io/CFS/).
The current page highlights CFS 0.2.0 Beta and keeps the 0.1.0 Beta release
available through archived GitHub release links.

## Files

- `index.html` contains the page structure and content.
- `styles.css` contains the visual design and responsive layout.
- `script.js` contains the checksum-copy interaction.
- `og.png` is the social sharing image.
- `sitemap.xml` lists the public pages for search engines.
- `robots.txt` allows crawling and points to the sitemap.

## Preview locally

Open `index.html` in a web browser. No packages, framework, or build step are
required.

Pushes affecting `website/` are deployed directly by
`.github/workflows/deploy-pages.yml`.
