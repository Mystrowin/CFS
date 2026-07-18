# CFS 0.3 blocked work register

This file records work that is blocked, deferred for an external dependency, or unsafe to continue. A blocked item is never treated as complete and must be resolved before a release gate that depends on it can pass.

## Open items

| Status | Requirement | Attempt / evidence | Suspected cause | Dependencies | Next action |
| --- | --- | --- | --- | --- | --- |
| BLOCKED | Production Authenticode signing | No production certificate or signing service is configured in the repository. | External certificate and timestamp authority are required. | Final installer, release manifest, production VM acceptance. | Configure protected production signing outside VirtualBox; pin certificate public-key hash and timestamp authority. |
| BLOCKED | Windows 10/11 VirtualBox acceptance matrix | No disposable Windows 10/11 test VM definition is present in this project. | VM images and snapshots are external environment prerequisites. | Explorer, installer, recovery, and final acceptance gates. | Create isolated VirtualBox VMs with host sharing disabled and record image/snapshot identifiers. |

## Resolved items

| Status | Requirement | Resolution evidence |
| --- | --- | --- |
| RESOLVED | Live broker persistence after candidate/backup transaction change | Release broker plus `tests\Cfs.Persistence.Tests`: **8/8 passed** on 2026-07-18. The live test proves overwrite, atomic replace, truncate, create, rename, recursive delete, unchanged-block offset preservation, a stable no-loop clean state, later edits after manifest refresh, shutdown, and reopen. The injected-failure case proves the prior archive remains valid and unchanged while the dirty marked mount is preserved and shutdown reports `CFS_E_COMMIT_FAILED`. |

## Recording rule

Every future blocked item must include the task, attempted command or change, exact failure, relevant logs/files, suspected cause, dependent work, and next action. Dependent safety-critical work must stop until its blocker is resolved.
