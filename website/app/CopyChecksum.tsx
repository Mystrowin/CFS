"use client";

import { useState } from "react";

export function CopyChecksum({ value }: { value: string }) {
  const [copied, setCopied] = useState(false);

  async function copy() {
    let success = false;
    try {
      await navigator.clipboard.writeText(value);
      success = true;
    } catch {
      const field = document.createElement("textarea");
      field.value = value;
      field.setAttribute("readonly", "");
      field.style.position = "fixed";
      field.style.opacity = "0";
      document.body.appendChild(field);
      field.select();
      success = document.execCommand("copy");
      field.remove();
    }

    setCopied(success);
    if (success) window.setTimeout(() => setCopied(false), 1800);
  }

  return (
    <button className="copy-button" type="button" onClick={copy}>
      <span aria-hidden="true">{copied ? "✓" : "⧉"}</span>
      <span aria-live="polite">{copied ? "Copied" : "Copy SHA-256"}</span>
    </button>
  );
}
