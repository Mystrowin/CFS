import type { NextConfig } from "next";

const nextConfig: NextConfig = {
  output: "export",
  basePath: "/CFS",
  assetPrefix: "/CFS/",
  trailingSlash: true,
  images: { unoptimized: true },
};

export default nextConfig;
