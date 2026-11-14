import type { Metadata } from "next";
import "./globals.css";

export const metadata: Metadata = {
  title: "Gözleme — System status",
  description: "Live status of the Gözleme API and database.",
};

export default function RootLayout({ children }: Readonly<{ children: React.ReactNode }>) {
  return (
    <html lang="en">
      <body>{children}</body>
    </html>
  );
}
