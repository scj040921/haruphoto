# Preview interaction review

Scope: existing preview metadata and clipboard actions. Evidence: source inspection, then window regression.

1. High severity, high confidence: PreviewRating_ValueChanged refreshes rating-sorted results and reopens the old numeric index. Sorting can move the photo; this opens another photo and resets zoom. Preserve photo identity and viewport when it remains in results.
2. High severity, high confidence: PreviewCategory_Changed calls RefreshPhotos; map preview currentView becomes the gallery query. Favorite filtering has the same scope issue. Preserve location scope and update the saved gallery query separately.
3. Medium severity, high confidence: CopyToClipboardAsync swallows exceptions while both callers always report success. Return the outcome and keep failure feedback actionable.

Acceptance: rating reorder preserves photo and zoom; map metadata edits retain location count; gallery rating filters remove nonmatching photos; failed clipboard actions do not report success.

4. Medium severity, high confidence: WinUI RatingControl uses -1 for no rating, while the library uses 0. Normalize the control value into 0 through 5 before persistence.

Validation: 170 real-window smoke checks passed, including explicit rating handlers, map scope and zoom preservation, empty rating normalization and unavailable-file clipboard failure.
