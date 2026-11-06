# Library and update recovery iteration

Confirmed source issues:
- Removing a map preview photo refreshed gallery query indices before the reverse hero animation. The chosen photo could be wrong and browsing was interrupted.
- Background update copy called any cross-channel candidate a new update, including an older Stable offered to a Nightly install.
- Map tile failures had a message but no direct retry action.

Behavior:
- After confirmation, library-record removal continues at the same position within the remaining location photos. Removing the final photo closes both preview and empty place scene and returns to exploration. Original files and existing undo remain intact.
- While a removal confirmation is open, background keyboard commands are blocked.
- Same-channel candidates are described as updates. Other channels are described as switches; semantic downgrades explicitly state version rollback. Existing channel-selection rules, asset verification and installer security are unchanged.
- Retry is available in the map notice and place More menu. It preserves camera and ready tile imagery, replaces failed views and disables duplicate retry actions until completion. Errors return controls to an operable state. Automated map tests use local QA tiles and do not request public tile services.

Validation: core checks cover channel-switch/downgrade wording. Real-window checks cover repeated retry, retained camera and ready imagery, continued map preview after removal and return after the last location photo is removed. All removals use temporary fixture files.

Result: 190 real-window checks and 125 core regression checks passed. Final production build and desktop deployment follow these checks.
