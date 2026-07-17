const copyButton = document.querySelector("#copy-checksum");

if (copyButton) {
  copyButton.addEventListener("click", async () => {
    const checksum = copyButton.dataset.checksum;
    const label = copyButton.querySelector(".copy-label");
    const icon = copyButton.querySelector(".copy-icon");

    try {
      await navigator.clipboard.writeText(checksum);
      label.textContent = "Copied";
      icon.textContent = "✓";

      window.setTimeout(() => {
        label.textContent = "Copy SHA-256";
        icon.textContent = "⧉";
      }, 1800);
    } catch {
      label.textContent = "Copy failed";
    }
  });
}
