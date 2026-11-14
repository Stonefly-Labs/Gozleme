import type { Metadata } from "next";
import { BrowserTelemetry } from "@/telemetry/BrowserTelemetry";
import "./globals.css";

export const metadata: Metadata = {
  title: "Gözleme — System status",
  description: "Live status of the Gözleme API and database.",
};

export default function RootLayout({ children }: Readonly<{ children: React.ReactNode }>) {
  return (
    <html lang="en">
      <body>
        <BrowserTelemetry />
        {children}
      </body>
    </html>
  );
}
