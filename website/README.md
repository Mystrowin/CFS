# CFS website

This is the source for the public CFS download website hosted exclusively on
[GitHub Pages](https://mystrowin.github.io/CFS/).

## Requirements

- Node.js 22 or newer

## Local development

```bash
npm install
npm run dev
```

## Production build

```bash
npm run build
```

The static site is generated in `out/`. Pushes affecting `website/` are built
and deployed by `.github/workflows/deploy-pages.yml`.
