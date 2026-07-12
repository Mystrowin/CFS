export type ReleaseAsset = {
  label: string;
  description: string;
  fileName: string;
  size: string;
  url: string;
};

export type CfsRelease = {
  version: string;
  label: string;
  tag: string;
  published: string;
  architecture: string;
  requirements: readonly string[];
  setup: ReleaseAsset;
  alternatives: readonly ReleaseAsset[];
  sha256: string;
  releaseNotesUrl: string;
  checksumsUrl: string;
  repositoryUrl: string;
  issuesUrl: string;
};

const releaseBase =
  "https://github.com/Mystrowin/CFS/releases/download/v0.1.0-beta";

export const release: CfsRelease = {
  version: "0.1.0",
  label: "Beta",
  tag: "v0.1.0-beta",
  published: "July 12, 2026",
  architecture: "x64",
  requirements: [
    "Windows 10 version 1809 or newer",
    "Windows 11",
    "64-bit Windows",
    "Windows Projected File System for Explorer mounting",
  ],
  setup: {
    label: "Windows setup",
    description: "Machine-wide installer for supported x64 Windows PCs",
    fileName: "CFS-0.1.0-Beta-Setup.exe",
    size: "50.5 MB",
    url: `${releaseBase}/CFS-0.1.0-Beta-Setup.exe`,
  },
  alternatives: [
    {
      label: "Portable package",
      description: "Run CFS without machine-wide installation",
      fileName: "CFS-0.1.0-Beta-win-x64.zip",
      size: "72.6 MB",
      url: `${releaseBase}/CFS-0.1.0-Beta-win-x64.zip`,
    },
    {
      label: "Publishable source",
      description: "Formatted, reproducible source bundle",
      fileName: "CFS-0.1.0-Beta-Source.zip",
      size: "5.5 MB",
      url: `${releaseBase}/CFS-0.1.0-Beta-Source.zip`,
    },
  ],
  sha256: "d3b64fc8167b39d40b92b74de0272f2d73ac62fdcb962e5cdede5fe04a0cd91e",
  releaseNotesUrl:
    "https://github.com/Mystrowin/CFS/releases/tag/v0.1.0-beta",
  checksumsUrl: `${releaseBase}/SHA256SUMS.txt`,
  repositoryUrl: "https://github.com/Mystrowin/CFS",
  issuesUrl: "https://github.com/Mystrowin/CFS/issues",
};
