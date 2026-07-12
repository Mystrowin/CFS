import { CopyChecksum } from "./CopyChecksum";
import { release } from "./release";

export default function Home() {
  return (
    <div className="site-shell">
      <a className="skip-link" href="#main">
        Skip to main content
      </a>

      <header className="site-header">
        <a className="brand" href="#top" aria-label="CFS home">
          <span className="brand-mark" aria-hidden="true">
            CFS
          </span>
          <span className="brand-copy">
            <strong>CFS</strong>
            <small>Compressed File System</small>
          </span>
        </a>
        <nav aria-label="Primary navigation">
          <a href="#how-it-works">How it works</a>
          <a href="#requirements">Requirements</a>
          <a href="#security">Verification</a>
          <a className="nav-download" href="#download">
            Download
          </a>
        </nav>
      </header>

      <main id="main">
        <section className="hero" id="top">
          <div className="hero-copy">
            <div className="eyebrow-row">
              <span className="status-dot" aria-hidden="true" />
              <span>CFS {release.version} Beta</span>
              <span className="eyebrow-divider" aria-hidden="true" />
              <span>Windows x64</span>
            </div>
            <h1>
              Compressed archives
              <span> that open like folders.</span>
            </h1>
            <p className="hero-intro">
              CFS is an experimental Windows beta for creating and working with
              compressed <code>.cfs</code> archives through an Explorer-backed
              workflow.
            </p>
            <div className="hero-actions">
              <a className="primary-button" href={release.setup.url}>
                <span>Download Windows Setup</span>
                <span aria-hidden="true">↓</span>
              </a>
              <a className="text-link" href={release.releaseNotesUrl}>
                Read release notes <span aria-hidden="true">↗</span>
              </a>
            </div>
            <div className="hero-facts" aria-label="Release summary">
              <span>{release.setup.size}</span>
              <span>Windows 10/11</span>
              <span>Self-contained</span>
            </div>
          </div>

          <div className="archive-visual" aria-hidden="true">
            <div className="visual-grid" />
            <div className="archive-card">
              <div className="archive-label">ARCHIVE.CFS</div>
              <div className="compression-lines">
                <span />
                <span />
                <span />
                <span />
                <span />
              </div>
              <div className="archive-ratio">
                <strong>LZMA2</strong>
                <small>compressed blocks</small>
              </div>
            </div>
            <div className="transfer-line">
              <span />
              <span />
              <span />
              <span />
            </div>
            <div className="folder-card">
              <div className="folder-tab" />
              <div className="file-list">
                <span />
                <span />
                <span />
              </div>
              <div className="folder-caption">EXPLORER</div>
            </div>
            <div className="visual-caption">ON-DEMAND FILE ACCESS</div>
          </div>
        </section>

        <aside className="beta-warning" aria-label="Important beta warning">
          <span className="warning-icon" aria-hidden="true">
            !
          </span>
          <div>
            <strong>Experimental beta — keep a separate backup.</strong>
            <p>
              Do not use CFS as the only copy of important or irreplaceable
              files. Use this release with non-critical test data.
            </p>
          </div>
          <a href="#limitations">Review limitations</a>
        </aside>

        <section className="section" id="how-it-works">
          <div className="section-heading">
            <span className="section-index">01</span>
            <div>
              <p className="kicker">A focused Windows workflow</p>
              <h2>From folder to compressed archive—and back.</h2>
            </div>
          </div>
          <div className="steps-grid">
            <article className="step-card">
              <span className="step-number">01</span>
              <div className="step-glyph archive-glyph" aria-hidden="true" />
              <h3>Create or open</h3>
              <p>
                Turn a folder into a <code>.cfs</code> archive, or open an
                existing archive directly in CFS.
              </p>
            </article>
            <article className="step-card">
              <span className="step-number">02</span>
              <div className="step-glyph mount-glyph" aria-hidden="true" />
              <h3>Mount in Explorer</h3>
              <p>
                Browse projected files through Windows Explorer without first
                extracting the entire archive.
              </p>
            </article>
            <article className="step-card">
              <span className="step-number">03</span>
              <div className="step-glyph save-glyph" aria-hidden="true" />
              <h3>Save and unmount</h3>
              <p>
                Save supported changes back into the archive, validate it, and
                close the mounted folder cleanly.
              </p>
            </article>
          </div>
        </section>

        <section className="section download-section" id="download">
          <div className="section-heading">
            <span className="section-index">02</span>
            <div>
              <p className="kicker">Current public release</p>
              <h2>Download CFS {release.version} Beta</h2>
            </div>
          </div>

          <div className="download-layout">
            <article className="primary-download-card">
              <div className="download-card-topline">
                <span className="recommended-badge">Recommended</span>
                <span>{release.published}</span>
              </div>
              <h3>{release.setup.label}</h3>
              <p>{release.setup.description}</p>
              <a className="primary-button wide" href={release.setup.url}>
                <span>Download {release.setup.fileName}</span>
                <span aria-hidden="true">↓</span>
              </a>
              <dl className="file-metadata">
                <div>
                  <dt>Version</dt>
                  <dd>{release.version} Beta</dd>
                </div>
                <div>
                  <dt>Architecture</dt>
                  <dd>{release.architecture}</dd>
                </div>
                <div>
                  <dt>Size</dt>
                  <dd>{release.setup.size}</dd>
                </div>
              </dl>
              <div className="unsigned-note">
                <strong>Unsigned beta installer</strong>
                <p>
                  Microsoft SmartScreen may show a warning. Verify the SHA-256
                  below before running setup.
                </p>
              </div>
            </article>

            <div className="alternative-downloads">
              <h3>Other ways to get CFS</h3>
              {release.alternatives.map((asset) => (
                <a className="asset-row" href={asset.url} key={asset.fileName}>
                  <span className="asset-icon" aria-hidden="true">
                    {asset.label.startsWith("Portable") ? "ZIP" : "SRC"}
                  </span>
                  <span className="asset-copy">
                    <strong>{asset.label}</strong>
                    <small>{asset.description}</small>
                    <span>
                      {asset.fileName} · {asset.size}
                    </span>
                  </span>
                  <span className="asset-arrow" aria-hidden="true">
                    ↓
                  </span>
                </a>
              ))}
              <a className="asset-row" href={release.checksumsUrl}>
                <span className="asset-icon" aria-hidden="true">
                  SHA
                </span>
                <span className="asset-copy">
                  <strong>Published checksums</strong>
                  <small>Verify every release asset independently</small>
                  <span>SHA256SUMS.txt</span>
                </span>
                <span className="asset-arrow" aria-hidden="true">
                  ↗
                </span>
              </a>
            </div>
          </div>
        </section>

        <section className="split-section" id="requirements">
          <div className="requirements-panel">
            <p className="kicker">System requirements</p>
            <h2>Built for supported x64 Windows PCs.</h2>
            <ul className="check-list">
              {release.requirements.map((requirement) => (
                <li key={requirement}>
                  <span aria-hidden="true">✓</span>
                  {requirement}
                </li>
              ))}
            </ul>
          </div>
          <div className="projfs-panel">
            <span className="feature-label">CLIENT-PROJFS</span>
            <h3>Explorer mounting uses Windows Projected File System.</h3>
            <p>
              Setup can enable the Windows feature for you. A restart may be
              required. If ProjFS is unavailable, CFS reports it clearly and
              offers an explicit full-extraction compatibility mode.
            </p>
          </div>
        </section>

        <section className="section security-section" id="security">
          <div className="section-heading compact">
            <span className="section-index">03</span>
            <div>
              <p className="kicker">Verify before you run</p>
              <h2>One published hash. Zero silent installs.</h2>
            </div>
          </div>
          <div className="security-grid">
            <div className="hash-panel">
              <div className="hash-heading">
                <span>SETUP SHA-256</span>
                <CopyChecksum value={release.sha256} />
              </div>
              <code className="hash-value">{release.sha256}</code>
              <div className="command-block">
                <span>PowerShell</span>
                <code>
                  Get-FileHash .\{release.setup.fileName} -Algorithm SHA256
                </code>
              </div>
            </div>
            <div className="update-panel">
              <span className="shield-mark" aria-hidden="true">
                ✓
              </span>
              <h3>Consent-first updates</h3>
              <p>
                CFS checks a public HTTPS manifest, notifies you about newer
                releases, verifies the downloaded setup hash, and asks again
                before requesting administrator approval. It never performs a
                silent update.
              </p>
            </div>
          </div>
        </section>

        <section className="section limitations-section" id="limitations">
          <div className="limitations-copy">
            <p className="kicker">Know before testing</p>
            <h2>Honest beta boundaries.</h2>
            <p>
              CFS is not production-ready and does not claim compatibility with
              every Windows application. Mounting, saving, performance, and
              data-integrity bugs may still exist.
            </p>
            <a className="text-link" href={`${release.repositoryUrl}/blob/main/docs/KNOWN-LIMITATIONS.md`}>
              Read all known limitations <span aria-hidden="true">↗</span>
            </a>
          </div>
          <div className="support-card">
            <span className="support-label">REPORT A PROBLEM</span>
            <h3>Found a reproducible bug?</h3>
            <p>
              Open the public issue tracker with your CFS build, Windows
              version, exact steps, and privacy-reviewed diagnostic logs.
            </p>
            <a className="secondary-button" href={release.issuesUrl}>
              Open GitHub Issues <span aria-hidden="true">↗</span>
            </a>
          </div>
        </section>
      </main>

      <footer>
        <div className="footer-brand">
          <span className="brand-mark small" aria-hidden="true">
            CFS
          </span>
          <div>
            <strong>CFS {release.version} Beta</strong>
            <p>Experimental compressed archive software for Windows.</p>
          </div>
        </div>
        <div className="footer-links">
          <a href={release.repositoryUrl}>Source repository</a>
          <a href={release.releaseNotesUrl}>Release notes</a>
          <a href={release.issuesUrl}>Issue tracker</a>
        </div>
        <p className="copyright">
          Copyright © 2026 Neeraj Pragnya Krishna Vasagiri. All rights reserved.
        </p>
      </footer>
    </div>
  );
}
