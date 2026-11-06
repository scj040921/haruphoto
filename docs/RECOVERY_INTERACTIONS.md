# Preview, query and import recovery

Photo loading: failed decodes and missing paths show an actionable error with retry and adjacent-photo navigation. Retry bypasses the image cache; stale request generations cannot replace the active photo. Final-photo failure offers Previous, and disabled navigation arrows are visibly dimmed. The error explanation is not duplicated in full in the metadata sidebar.

Query changes: delayed search is flushed before pagination. New queries reset the viewport and page. Clearing filters suppresses intermediate callbacks, stops queued search and synchronizes the All photos sidebar selection. Metadata-only updates retain their existing viewport-preservation path.

Import: a linked cancellation scope applies to scanning and is checked again before records are committed. Import actions are disabled during a scan, Cancel is visible, and cleanup always restores controls. Cancellation does not remove existing records or original files. A cancelled folder scan does not register the watched folder. Concurrent drops are not advertised as accepted while scanning.

Validation: 197 real-window smoke checks passed. New cases exercise pending search pagination, clear/sidebar synchronization, repaired-file retry, final-photo failure escape, atomic import cancellation and successful retry. Tests use isolated generated photos. Production build and desktop replacement follow validation.
