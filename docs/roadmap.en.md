# Roadmap for personal photo editing

[简体中文](roadmap.md) · [Home](../README.en.md) · [Released features and known limitations](../CHANGELOG.md)

This roadmap focuses on everyday photos, portraits, group shots and covers. Items 41–65 are proposals. Versions and dates are still open. Our immediate priorities are finding drafts after an upgrade (48), showing whether an edit was saved (49), reducing slider lag (54), and correcting mistakes in person detection (55).

## Import, edit and share

| No. | Plan | What success looks like |
| --- | --- | --- |
| 41 | Drag photos into the library or paste a screenshot | Reopen the photo after the source app closes. Transparent edges survive the import. |
| 42 | Name and save export settings | Reuse a “Chat photo” or “Application photo” preset and verify the dimensions and file size of the result. |
| 43 | Open large RAW files faster | Measure time to the first editable preview and peak memory. Switching photos cancels the previous decode. |
| 44 | Refine cutout edges around hair, glasses and shoulders | Check against dark and light backgrounds, recover fine strands and glasses arms, and remove edge halos. |
| 45 | Keep several looks for one original | A natural version and a warm version can each have their own crop, masks, undo history and export. |
| 46 | Test more phone photos and lower-spec Windows PCs | Use backlit faces, night scenes and groups with small faces to measure detection errors, correction time and slider lag. |
| 47 | Understand more offline English editing requests | Different phrasings and combined requests produce a reviewable proposal that respects locked controls and the selected person. |

## Libraries, drafts and recovery

| No. | Plan | What success looks like |
| --- | --- | --- |
| 48 | Help find the old library after an upgrade | When a library opens empty, show its location and the photo and draft counts in possible old libraries. Offer to resume, restore or start fresh. |
| 49 | Make save status clear | Distinguish a preview, a saved draft and an applied edit. A failed write preserves the work on screen so the user can retry and recover the last stroke. |
| 50 | Gather unfinished drafts in one place | Open a photo straight into its crop, brush or redaction tool. Completed items leave the draft list. |
| 51 | Reopen restored libraries from a recent list | Tell libraries with the same name apart after a restart. Keep the location visible when an external drive is disconnected. |
| 52 | Show which originals must travel with a backup | Count originals on external drives and those currently offline, so users know what to copy to a new PC. |
| 53 | Retry an interrupted export or backup | If a drive fills up or disconnects, choose another destination. Keep earlier successful files and identify incomplete output. |

## Preview, people and local adjustments

| No. | Plan | What success looks like |
| --- | --- | --- |
| 54 | Make exposure and mask sliders feel more responsive | See the adjustment while dragging and a detailed preview on release. Measure typical and slower responses for both JPEG and large RAW files. |
| 55 | Correct false or missed person detections | Dismiss a graphic mistaken for a person on a cover, or add a missed person in a group. Preview the target and keep corrections after reopening. |
| 56 | Intersect or subtract two masks | Brighten just the sunlit half of a face, with an editable edge preview and undo. |
| 57 | Compare the same detail at 100% | Drag a divider across an eye, strand of hair or text edge while the before and after views stay at the same position and zoom. |

## Sharing and everyday controls

| No. | Plan | What success looks like |
| --- | --- | --- |
| 58 | Export several sizes in one operation | Make an avatar, a chat photo and a high-resolution copy from one original, each with its own framing, dimensions and size limit. |
| 59 | Show safe areas for avatars and covers | Preview a circular avatar crop or a banner overlay to see whether a face or title will be cut off. Guides only appear in the editor. |
| 60 | Improve keyboard editing and display scaling | Switch people, refine masks and undo strokes with the keyboard. Focus stays visible at 150% and 200% Windows scaling. |
| 61 | Clarify who an AI request refers to | For “brighten her face” in a group shot, let the user choose the person and preview the affected area before applying the edit. |
| 62 | Review privacy before sharing | Check redactions, crop boundaries and what happens to location metadata in one place. Verify both exported files and images copied to the clipboard. |
| 63 | Pin frequently used tools | Keep exposure, crop or the mask brush within reach across restarts and language changes. |
| 64 | Preview a diagnostic summary before sharing it | Review the version, photo format and steps to reproduce. Photos, full file paths and secrets are excluded by default. |
| 65 | Redesign the workspace around the editing workflow | Arrange the main actions in editing order: import, adjust, compare and export. Keep frequent tools easy to find, reveal advanced options on demand, and use consistent labels, icons, type sizes, spacing, contrast and selection states. |

**How will we evaluate the redesign?** Ask first-time users to brighten and export a photo, adjust just one person in a group, and resume a draft after closing the app. Compare completion time, wrong turns and requests for help against the current interface. Test both languages and Windows display scaling so users can identify the active tool, selected person and next action.
