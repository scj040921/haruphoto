# Local map exploration

Goal: continue browsing saved places without repeatedly closing photos and manually searching the map. Existing entry was a marker click only; marker hover already scaled the stack.

- My places opens a native keyboard-accessible flyout containing thumbnails, names and counts. Filtering searches saved labels and notes locally, without sending text to a geocoding service. Empty library and no-match search have different recovery messages.
- Choosing a saved location opens its scoped photos and uses the existing camera transition. During placement it assigns selected photos to the chosen existing location instead.
- Previous/next place follows the stable local-name order. The position indicator shows the current index and count. Endpoints are disabled; no wrap surprises. Returning to the map retains the original exploration camera.
- Page Up/Page Down switch locations while the place scene is active. Escape cancels placement first, then closes a place, then leaves the map. Input fields retain normal text editing.
- Marker keyboard focus shares hover feedback. Existing Windows and app reduced-motion switches continue to apply. Photo-strip transitions use compositor opacity; no animation loop is added.
- Small windows use the existing adaptive action rows and horizontal action overflow. Network tile failure does not disable local place discovery or photo viewing.

Acceptance: local label/note filtering and empty states; live flyout rendering; previous/next camera and photo synchronization; disabled single-place navigation; existing compact-window and photo-transition regression.

Validation: 176 real-window smoke checks passed, including live place-list rendering, annotation filtering, empty states, adjacent camera/photo navigation, single-place disabled endpoints and compact-window layouts.
