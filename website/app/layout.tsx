import type { Metadata } from "next";
import { headers } from "next/headers";
import { Geist, Geist_Mono } from "next/font/google";
import "./globals.css";

const geistSans = Geist({
  variable: "--font-geist-sans",
  subsets: ["latin"],
});

const geistMono = Geist_Mono({
  variable: "--font-geist-mono",
  subsets: ["latin"],
});

const title = "CFS 0.1.0 Beta — Compressed archives for Windows";
const description =
  "Download CFS 0.1.0 Beta for Windows: compressed .cfs archives with an Explorer-backed workflow.";

export async function generateMetadata(): Promise<Metadata> {
  const requestHeaders = await headers();
  const host =
    requestHeaders.get("x-forwarded-host") ??
    requestHeaders.get("host") ??
    "localhost:3000";
  const protocol =
    requestHeaders.get("x-forwarded-proto") ??
    (host.startsWith("localhost") ? "http" : "https");
  const origin = `${protocol}://${host}`;

  return {
    metadataBase: new URL(origin),
    title,
    description,
    applicationName: "CFS",
    authors: [{ name: "Neeraj Pragnya Krishna Vasagiri" }],
    robots: { index: true, follow: true },
    openGraph: {
      type: "website",
      title,
      description,
      url: origin,
      siteName: "CFS",
      images: [
        {
          url: `${origin}/og.png`,
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
      images: [`${origin}/og.png`],
    },
  };
}

export default function RootLayout({
  children,
}: Readonly<{
  children: React.ReactNode;
}>) {
  return (
    <html lang="en">
      <body
        className={`${geistSans.variable} ${geistMono.variable} antialiased`}
      >
        {children}
      </body>
    </html>
  );
}
