import type { Metadata } from "next";
import "./globals.css";

const title = "CFS 0.1.0 Beta — Compressed archives for Windows";
const description =
  "Download CFS 0.1.0 Beta for Windows: compressed .cfs archives with an Explorer-backed workflow.";
const siteUrl = "https://mystrowin.github.io/CFS/";

export const metadata: Metadata = {
    metadataBase: new URL(siteUrl),
    title,
    description,
    applicationName: "CFS",
    authors: [{ name: "Neeraj Pragnya Krishna Vasagiri" }],
    robots: { index: true, follow: true },
    openGraph: {
      type: "website",
      title,
      description,
      url: siteUrl,
      siteName: "CFS",
      images: [
        {
          url: `${siteUrl}og.png`,
          width: 1200,
          height: 630,
          alt: "CFS 0.1.0 Beta — compressed archives that open like folders",
        },
      ],
    },
    twitter: {
      card: "summary_large_image",
      title,
      description,
      images: [`${siteUrl}og.png`],
    },
};

export default function RootLayout({
  children,
}: Readonly<{
  children: React.ReactNode;
}>) {
  return (
    <html lang="en">
      <body>{children}</body>
    </html>
  );
}
