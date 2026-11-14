# Status and continuation priorities

**Retired source experiment.** Windows test evidence exists for earlier revisions; physical Apple Silicon acceptance and complete modern Directory Opus parity do not.

| Area | State |
| --- | --- |
| Dual-pane UI, browsing, settings, operation journal and tools | Implemented; inspect fresh test results before relying on changes |
| HEIF preview/cache and timestamp invalidation | Implemented; Windows has prior evidence; Mac color/orientation still requires visual tests |
| Native Mac packaging | Cross-compilation/archive checks previously performed; not equivalent to executing on Mac |
| Integrated Office/video experience | Incomplete; some paths use native/external tools rather than a full in-pane replacement |
| Companion indexed search | Protocol client implemented; companion is a separate retired project, not a required silent install |
| Exact proprietary layout/commands/configuration parity | Not established |
| Security, accessibility and sustained production use | Not certified |

Historical baseline before public sanitization: 178 core passes, zero failures, five unverified; an earlier packaged snapshot had 81 headless UI passes and two skips. Those numbers belong to historical runs. Newer preview changes were present in the source after that package. See VALIDATION.md for this publication's actual checks.

For a deliberate fork, first validate build/tests from a clean checkout, then exercise the exact application on Apple Silicon. Prioritize data safety, native permissions/Trash, cancellation and recovery before feature count. Measure real folder/preview bottlenecks and compare workflows rather than claiming a percentage of proprietary parity. Keep reusable results, not repeated test dumps.
