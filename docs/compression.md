# CFS 0.2.0 Beta compression method

The current prototype uses independent per-file LZMA2 compression through the official 7-Zip LZMA SDK 26.02 reduced `7zr.exe` tool.

Source:

- Official page: https://www.7-zip.org/sdk.html
- Vendored tool: `third_party/lzma-sdk/bin/x64/7zr.exe`
- Vendored docs: `third_party/lzma-sdk/DOC/`
- Downloaded SDK archive: `third_party/lzma-sdk/lzma2602.7z`

License verification:

- The official 7-Zip LZMA SDK page lists version 26.02 as containing LZMA/LZMA2 support.
- The same page states that the LZMA SDK is public domain and may be used, sold, modified, compiled, and distributed for commercial or non-commercial purposes.
- This satisfies the current prototype requirement for an LZMA2 implementation without mandatory source disclosure.

Archive behavior:

- Each file is compressed independently.
- CFS stores each compressed file block separately inside the `.cfs` file.
- Edits append changed/new LZMA2 blocks and a new manifest.
- Unchanged compressed blocks keep their existing offsets.

This prototype does not use solid compression.
