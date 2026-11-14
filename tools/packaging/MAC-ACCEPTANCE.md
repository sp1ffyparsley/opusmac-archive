# Mac installation and acceptance

This package contains an independent file manager for Apple Silicon Macs, with its
own .NET runtime. Running it does not require Python, the .NET SDK, or an existing
Directory Opus installation. The supported minimum in this build is macOS 13.

A Windows build can verify the package's bytes, native architectures, executable
archive modes, and Windows behavior. It cannot establish that the native macOS
window, permissions, file metadata, Trash, or desktop integration work on your M4.
Keep that distinction when reading the included receipts.

## Keep the extracted package together

Extract Directory-Opus-Mac-osx-arm64.tar.gz with Archive Utility. The osx-arm64
folder contains Directory Opus Mac.app and its verification and signing scripts.
Keep them together until verification is complete. Run commands from Terminal in
that extracted folder; the paths below are relative to it.

The original package has release-manifest.json, SHA256SUMS, and a source
fingerprint. The archive also has a SHA-256 sidecar in the release directory.
These establish the identity and integrity of this build, not a developer
certificate, Apple notarization, or a completed Mac acceptance result.

## Sign the personal copy on your Mac

For this private M4 installation, explicitly select local ad-hoc signing:

    ./Sign-Mac.command --adhoc

The script creates a new sibling folder and signs its native executable files
and app bundle with normal macOS codesign. The original package remains intact.
Its only runtime entitlement allows .NET's JIT. It does not change Gatekeeper,
TCC, quarantine attributes, system integrity protection, or Finder associations.

The explicit ad-hoc mode uses normal local code signing and records
hardened_runtime=false. Developer ID mode enables the hardened runtime and the
JIT entitlement required for the managed application.

An ad-hoc signature supports local code integrity. It is not a Developer ID
certificate or Apple notarization, and it does not promise Gatekeeper acceptance.
If macOS blocks launching the copied app, use the system's own displayed
security decision flow for this app after reviewing the build and its hashes;
the scripts never automate a protection bypass.

If you have a Developer ID certificate in your Mac keychain, use its exact
identity instead:

    ./Sign-Mac.command "Developer ID Application: Your Name (TEAMID)"

For a notarized distribution, a third argument names an already configured
notarytool keychain profile. The second argument must be a new output folder:

    ./Sign-Mac.command "Developer ID Application: Your Name (TEAMID)" "../OpusMac-signed" "your-notary-profile"

Notarization needs Apple's developer tooling and the existing keychain profile.
The script submits the signed app, requires Apple's Accepted result, staples and
validates the ticket, and records Gatekeeper assessment. It never creates,
prints, or uploads credentials from this repository.

The signed package has a new SHA256SUMS, signing-receipt.json, and an
unsigned-build-manifest.json recording the original build. Signing necessarily
changes executable bytes and adds signature resources. Check the current signed
package against its own SHA256SUMS. An ad-hoc receipt explicitly records that it
was not notarized.

## Execute the packaged checks on the M4

In the new signed package folder, run:

    ./Verify-Mac.command --launch

This uses bundled executables and standard macOS utilities. It creates a fresh,
physical folder under ~/Library/Logs/OpusMac/Verification, keeps its fixtures and
receipts, and never uses your existing file collections as test inputs. Physical
paths avoid the /var and /tmp alias ambiguity in filesystem mutation checks.

The automatic checks cover the package inventory and hashes, ARM64 native files,
bundle identity, native file copy and exclusive rename, metadata, symbolic links,
real Trash receipts and restore, shared operation and search/tool fixtures, and
a separate headless UI self-test. The core result lists each executed case.
Missing fixture capabilities are recorded as unverified, never as passing; the
Mac verifier requires all requested core cases to have executed successfully.

The separate UI self-test renders and exercises real application controls with
disposable directories. It does not prove the native macOS window server,
system dialogs, Quick Look, TCC, or Finder file activation.

Receipts are retained outside the app:

- verification.json: machine-readable package and execution gates.
- Core/checks.json: individual core cases, failures, and unverified cases.
- core-output.txt and ui-output.txt: console output.
- UI-render: UI diagnostic output and rendered checks.
- architecture.txt, package-hashes.txt, plist.txt, and signature.txt.
- Manual/Source and Manual/Destination: disposable folders for desktop checks.

The --launch option asks Launch Services to open a native desktop instance using
those manual folders and a separate application profile. A successful request is
recorded as accepted. Observe the resulting window to determine whether it
actually opened and behaves correctly.

Finder may create .DS_Store outside the app while you inspect its folder. The
verifier tolerates that one harmless Finder metadata filename outside the
sealed app. Every file inside Directory Opus Mac.app remains covered exactly.
A changed payload, added executable, missing file, or unexpected file inside the
app fails verification. Retain the diagnostic stage and receipts on failure.

## Complete the native desktop checks

Use the disposable Manual folders first. Record the macOS version, M4 model,
display scaling, filesystem formats, and the date beside verification.json.

1. Open the app, resize and maximize it, test light and dark appearance and
   display scaling, then close and reopen it. Confirm readable columns and
   controls, keyboard focus, source/destination selection, tab restoration,
   previews, filtering, and the operation/error panel. Headless rendering is
   supporting evidence; judge the actual Mac window.

2. Copy, move, rename, replace with confirmation, create a folder, Trash, and Undo
   fixture files and nested folders. Repeat with spaces, Unicode, quotes, leading
   hyphens, empty files, and long names allowed by the filesystem. Confirm that
   a collision never overwrites without the chosen policy, failures preserve
   sources, a completed operation has a correct receipt, and Undo does not repeat
   a committed restore.

3. Confirm file, directory, and broken symbolic links keep their link identity
   and do not cause traversal into an external target. Check resource forks,
   extended attributes, permissions/ACLs, and directory timestamps using fixture
   data. The native suite checks resource forks, extended attributes, mode,
   timestamps, links, and actual Trash restore; inspect ACLs and inherited folder
   permissions on the volumes you intend to use.

4. Repeat copy, move, and Trash with disposable fixtures on an external drive.
   Use APFS and any other format you actually need, including cross-volume
   operations and read-only destinations. A volume that cannot provide the
   requested semantics must produce a clear failure or unsupported result while
   retaining the source. Do not treat a local APFS result as evidence for a
   network filesystem, removable format, or cloud placeholder.

5. Exercise Desktop, Documents, Downloads, network mounts, and removable volumes
   as relevant. Grant or deny each requested macOS folder permission through the
   normal system dialog and check both outcomes. The app must remain responsive,
   explain denied access, and recover after permission is granted. Grant access
   only to folders you actually intend to manage.

6. Test Quick Look, Open, Reveal in Finder, and Open Terminal with fixture names
   containing spaces, apostrophes, Unicode, and shell metacharacters. Confirm the
   correct file/folder opens. Reopen Quick Look and then close the file manager;
   its owned preview helper should terminate. These actions use native APIs or
   argument lists, not interpolated shell commands.

7. Test native file/folder activation, opening the app again while it is already
   running, and the Mac keyboard/clipboard actions exposed by the UI. Confirm
   requests reach the intended existing window and closing the application saves
   the intended profile. The optional public.folder registration uses Alternate
   handler rank; it does not make a universal Finder replacement.

8. Test a large real-shaped fixture directory, deep search, cancellation,
   interrupt/restart during a staged operation, and reconnecting an offline
   volume. Record elapsed time, memory, responsiveness, and the operation journal
   outcome. Performance receipts from Windows do not establish M4 measurements.

9. Test the features you depend on from Directory Opus against your own workflow.
   Compare visual layout and behavior explicitly. This independent application
   has not established complete Directory Opus feature parity, plugin parity, or
   a one-to-one reproduction. A successful core suite is not a parity sign-off.

10. In separate panes, open a folder on the startup disk and a folder on an
    already-mounted external volume with a different capacity. Use
    `df -k "/path/to/folder"` in Terminal for each path and compare available
    space and total capacity to the pane's readout and meter tooltip, allowing
    for rounding and normal disk activity. Swap panes or navigate between
    volumes and confirm that the meter follows the folder. If a second volume
    is unavailable, mark this case unverified.

11. Using disposable folders, save a parent format and a child format that
    inherits its columns, sort, and view. Change the parent and revisit the
    child. Check Format Lock through a navigation, unlock to recover the
    folder's format, and use Reset to User Default without replacing the saved
    child rule. Relaunch with the same isolated profile and confirm the rules
    persist. Record the observed Mac behavior and compare it separately with
    the same workflow in the original Windows app.

Only after these checks pass should you copy the accepted .app to /Applications
(or your user's Applications folder) and begin using it with normal working
files. Copy it so the signed package and hash baseline remain available. Keep
normal backups while qualifying file-management software. Existing Finder and
Directory Opus installations are not modified.

## Interpreting a failure

A nonzero verification exit is not a completed acceptance result. Open the
receipt directory printed by the script and inspect the named failing log or
Core/checks.json. The script retains its owned fixtures so a failure can be
reproduced without touching production data. Do not retry a completed move or
restore manually merely because a later metadata/receipt step reported an error;
inspect the actual source and destination and the operation journal first.

The Windows publication's source fingerprint must match throughout publishing.
A changing source tree stops promotion and keeps only a diagnostic stage. Each
successful publication creates a new directory; prior releases are retained.

## Primary implementation references

- Avalonia macOS packaging and signing:
  https://docs.avaloniaui.net/docs/deployment/macos
- Apple copyfile flags and metadata semantics:
  https://github.com/apple-oss-distributions/copyfile/blob/main/copyfile.h
  https://github.com/apple-oss-distributions/copyfile/blob/main/copyfile.3
- Apple FileManager Trash operation and resulting item URL:
  https://developer.apple.com/documentation/foundation/filemanager/trashitem(at:resultingitemurl:)
- Apple notarizing macOS software:
  https://developer.apple.com/documentation/security/notarizing-macos-software-before-distribution
- Apple local ad-hoc signing and the separate hardened-runtime signing option:
  https://developer.apple.com/documentation/xcode/embedding-a-helper-tool-in-a-sandboxed-app

### Dual Folder Trees

Import Dual Horizontal and Dual Vertical from copies of the original .osy files. Select a different folder in each tree and confirm only the attached file display navigates. Drag both tree dividers to different widths, switch orientation, then save and reopen a named Lister. Relaunch from the same test profile and verify both widths and folder identities. Compare placement, tree controls and split behavior against the actual Windows Lister in both orientations.

### Reference keyboard checks

With the file list focused, use F8 and Shift+F8 to cycle through both Folder Trees, source only, destination only and neither. Verify Lister > Favorites Bar and Ctrl+Shift+B stay in sync, including after relaunch. Use Ctrl+Shift+N and Ctrl+Shift+C and paste into a text field to check filename and absolute path. Use Ctrl+U on a disposable folder with two identical files and a third distinct file. Use Ctrl+Y with disposable source and destination folders and verify the reviewed copy plan produces the expected destination bytes. Record native key delivery, displayed results and any differences from the Windows utility panel.

### Details + Thumbnails

In a disposable folder with several images, press Ctrl+Shift+H and verify the leading Thumbnail column renders images. Compare its width, row height and scroll behavior against the installed Windows app. Save and reopen the tab, then use Ctrl+Shift+D and verify compact Details returns with the same selected file. If a Windows folder-format export includes a Thumbnail column, import a copy and compare its order and width.

### Navigation Lock

Use two disposable folder trees with matching and missing descendants. Enable Navigation Lock from the toolbar and verify navigation in both directions. Test dismiss, reset and unlock from the warning, then confirm switching tabs resets the paired bases. In Preferences, disable the out-of-sync warning and enable quiet cancellation. Confirm an unmatched descendant leaves the lock enabled and going outside the current base turns it off. Relaunch and confirm the preference values persist.

### Paired Folders

Create a disposable literal pair with descendant matching and set it as the default for Dual Display, Navigation Lock and Synchronize. Verify each entry point uses the counterpart. Check all three If non-existent choices with a missing destination. Enable quiet Navigation Lock cancellation, leave the current bases by moving into a folder with a valid configured counterpart, and verify the lock follows and remains active. Navigate into an unrelated folder and verify the lock cancels. Compare placement and Preferences with the Windows app.

Enable Always display primary folder at the left/top and open Dual Display from the secondary folder in both orientations. Verify the primary is left or above, the same secondary tab stays active, and restarting preserves the displayed pane order.

Enable Automatically link paired tabs on a disposable pair. Open the counterpart, switch to unrelated tabs, and select a linked tab on each side in turn. Check that the counterpart comes forward, then unlink or close one endpoint and verify the other is cleared.

With both displays open, Control-click a tab to link it with the active opposite tab. Switch the other side away and Shift-click the linked tab to bypass the link for one click. Verify normal selection activates both tabs and Control-click on the active pair clears the link. Record whether native macOS delivers Control-click as a primary click or a context-menu gesture.

Repeat the manual link and unlink workflow from the folder-tab context menu. Also test regex capture expansion in the Paired Folders Test folder preview, native Browse, and both automatic Dual Display options.

Use Ctrl+Shift-click or Navigation Lock in the linked tab's context menu. Verify matching child and parent paths follow while both endpoints are visible. Switch to an unrelated tab and verify navigation is independent. Return to the linked tabs and turn off the lock from the tab menu.

### Folder aliases

Create two aliases for disposable folders with matching descendants. Enter a mixed-case alias with a subfolder suffix in the location field. Configure a literal pair by alias, then test a regex pair with Expand folder aliases enabled. Use folder names containing brackets and dollar signs to verify the Test pair result matches the Synchronize destination. Retarget one alias and verify the saved pair follows it.

Mount a disposable volume with a distinctive name. Enter /$VolumeName in the location field, then try the same alias with a subfolder suffix and as a literal paired target. Verify the displayed path is on the mounted volume. Unmount and repeat the location input to confirm a missing-volume message without navigation.

With that volume mounted, use a leading /$VolumeName in a regex folder pair with Expand folder aliases enabled. Verify the preview retains the captured subfolder. Unmount and verify the regex rule stops resolving.

In Folder Aliases, show the built-in rows and verify they are read-only. Choose a new destination for a custom alias with Browse and confirm a saved pair resolves the new target.

### Home Folder

Verify Home and Alt+Home return to each tab's initial folder. Set a disposable specific Home folder and verify the button, location alias and middle-click all use it. Drop a disposable file on Home and verify the source and destination bytes, then reset Home from the button context menu.

### Folder sizes

With disposable folders containing known file sizes, verify Ctrl+K calculates selected folders and all visible folders when none are selected. Enable automatic sizing, revisit the containing directory and verify totals arrive without blocking folder browsing. Check Local and removable drives against both a local and a network volume. Disable automatic sizing and verify subsequent listings leave folder sizes blank.

Create an automatic-size wildcard override and an exact exception. Verify each against disposable paths, including after relaunch, and confirm Ctrl+K still calculates inside the excluded folder.

Enable folder-size history caching. Calculate a disposable folder's size, navigate away and change a nested file, then use Back and Forward to confirm the prior total is restored. Refresh and calculate again to verify the new bytes.

Enable Update folder sizes after file operations. Calculate a disposable folder's size, move a known-size fixture into it and Undo. Verify the measured folder follows both changes while an unmeasured sibling stays blank with automatic sizing disabled.

Hover over a local folder name in Details. Verify its tooltip shows the expected bytes, and move the pointer away while another folder is calculating to check that the old result does not appear.

Use disposable symbolic links to test the two Folder Sizes link preferences. Include a loop, verify it does not inflate the total, and verify manual sizing can measure a link even when linked rows are excluded from automatic sizing.

### Print Folder export

Use a build that includes Print / Export Folder Listing. With a disposable tree, verify the native source and save pickers, cancel each picker, export a CSV that contains a comma and a Unicode filename, and confirm an existing output is preserved. Export a grouped TSV to the clipboard, paste into a Mac editor and verify nested order and calculated sizes. Apply the active lister columns with Current, reopen the dialog after relaunch and verify the last successful column and sort choices. Track printer setup, exact visual comparison and remaining reference controls separately.

Drag a Print Folder column divider with the pointer. Verify the per-column and total counts, save Normal text with Header and Footer, and check the saved column offset and matching folder/date labels. Reopen and verify the width persists, then compare the same fixture with Windows, including a filename wider than its column. Verify changing width leaves CSV/TSV field data unchanged.

Define a named filename filter and reopen Print Folder. Select it with Use filter enabled and verify the matching rows. Turn Use filter off without clearing the pattern and verify the same export includes the formerly excluded rows. Edit and delete the preset, relaunch, and confirm the settings changes persist. Toggle Flat View off without changing its selected mode and verify only top-level rows are exported.

In Print Folder > Edit, reorder two visible columns and change a width and sort direction. Verify Cancel leaves the preview alone, apply the same edits and verify the preview and real CSV follow the new order. Reopen the lister Folder Format and confirm its columns and rules were unaffected.

### macOS Printer destination

Select Printer in Print Folder and open the system print panel. Cancel, verify the lister responds and confirm no job or file appeared. Reopen with a disposable listing longer than two pages, including Unicode and long names. In the native PDF menu choose Save as PDF into the fixture directory. Open the saved PDF in Preview and verify the first and last entries, ordering, page count, visible columns and Unicode. If a physical printer is available, choose it explicitly, print a one-page disposable listing and verify the paper. Repeat with different paper and orientation settings, then compare against the installed Windows print workflow. Record whether the first/last document labels match the reference per-page behavior.

Select Printer and verify Font is enabled. Choose two different installed faces and point sizes, Save as PDF with each, and verify the actual fonts and visible size change in the PDFs. Relaunch and check the last completed choice. Confirm canceling the font picker does not change the draft and changing to File or Clipboard disables Font. Compare wide and proportional faces against Windows for column alignment and pagination.

For proportional fonts, print rows with varying character widths and values that fit each column. Verify matching left edges across rows in the PDF. Change the font and one divider and confirm the column positions change. Test overlong names and wide layouts separately, including final rows on multipage output.

Repeat Save as PDF for None, Header, Footer, and Header and Footer using a listing at least three pages long. Verify the requested top and bottom labels on every page, including the last, and check first/last rows for clipping. Print a second job with labels off and verify the prior setting did not affect it.

### File Type Summary

Select disposable JPEG variants and a video. Verify selected extension counts in the status bar, hover to open the chart, click and compare selected count against all visible size grouped by type. Clear selection, repeat with a filter, and compare the status badges, chart and mode switches to the installed Windows app.

Select four or more file types and narrow one pane. Verify the type badges reduce to fit and the more count grows. Hover the totals for the full status, then widen the pane and verify up to three badges return. Compare the treatment and typography with the installed Windows status bar.

### Join Files

With a build that includes Join Files, select disposable numbered parts and verify natural order and the proposed output. Use Add and a real file drop to append local files, reorder using the move buttons, and verify the exact joined bytes and unchanged inputs. Repeat with a same-path replacement during review and an existing output path, confirming the operation rejects both without leaving a stage file. Compare the result and dialog with installed Windows Opus.

### Split File

Split a disposable file with a byte-sized chunk and verify the exact numbered names and lengths. Join the pieces in filename order and compare their output bytes with the input. Repeat with a pre-existing middle piece and confirm no output was created or replaced. Cancel during staging, verify the input remains intact and confirm no staging siblings remain. Compare Automatic against installed Windows with fixed and removable destinations.

Repeat Split with UUEncode selected. Verify each encoded part opens as ASCII envelope text. Join with UUDecode and compare the bytes to the input. Exchange a disposable set with installed Windows in both directions to verify filename suffixes, headers, chunk sizes and encoding compatibility. Verify a truncated set is rejected without publishing output.

Drag one Join Files row above and below another using the actual pointer. Check the visual cue, verify leaving the list cancels the reorder, and join the files to confirm the final bytes follow the displayed order. Compare the interaction with the Windows reference.

Add later-numbered parts first, then add an earlier part in a second batch. Verify the numbered list resorts. Edit the output filename and add another part to confirm the typed output stays intact. Manually reorder, add another batch and verify the reviewed order is preserved. Use a path long enough to confirm the piece filename remains visible.

Open Split File and inspect the CD/DVD preset menu. Choose a preset and confirm it fills the editable field. Try a larger size than the source and verify the dialog stays open. Replace it with a typed size, verify keyboard submit and cancel, and compare actual preset byte counts with the installed Windows app.

Run AdvancedChecks on the Apple Silicon package to verify the deterministic late-collision rollback and cancellation during a UUencoded staged write. Confirm the source hash and the competing piece survive and no temporary output remains.

### Create Hard Links

Create a hard link in the destination pane on the same APFS volume. Compare device and inode of the two paths and verify Undo removes the destination entry while the original stays readable. Replace the link path with an independent file with the same bytes and timestamp and verify Undo refuses it. Check collisions, a long Unicode path and refusal on a different volume.

Open Edit > Create Advanced Links > Create Hard Links and repeat with the right pane as source. Verify the resulting link appears on the left and Undo leaves the right-hand source intact. Compare the nesting and shortcut access with the installed Operations toolbar.

### Update All and Update Existing

Select a disposable folder whose destination includes newer, older, equal-time and destination-only files. Verify Update Existing touches only older matches and Update All also creates missing selected paths. Replace a target with same bytes and time during review and verify the native identity check refuses it. Compare placement, timestamp precision and confirmation behavior with installed Windows.

Run OperationChecks for the Update backup race and comparison-to-plan identity cases. The injected replacement must survive at its original path, the earlier reviewed object and source must remain unchanged, and no staging folder may remain.

Review an Update All plan for a missing file in an existing nested folder. Swap the reviewed destination folder with another at the same path before applying and verify the replacement remains untouched. Repeat with two levels of missing folders and verify later requests use the directories created by the plan.

### Select Other

Compare Select Same Extensions, Random Item, Empty Folders and Zero-Byte Folders against installed Windows with disposable files, including a hidden child. Use both panes to compare the four source/destination commands, check name/stem matches and verify unrelated selection is replaced only on the intended pane. Compare matching of compound extensions and filtered rows separately.

Run Copy File Names on a disposable selection, change the selection and use Reselect Files. Verify the previous file and existing selection remain selected. Repeat after renaming and after replacing the old file at the same path. Compare command history scope and move behavior with Windows.

### Checkbox Mode

Enable Checkbox Mode in one tab and verify the other tab is unaffected. Use Space and direct checkbox clicks while keeping a different file selected for Preview. Copy only checked items, drag only selected items, and verify both conversion commands and Invert Selection. Repeat in Details, Tiles and Filmstrip and relaunch to verify mode persistence. Compare folder format behavior with Windows.

Set Checkbox Mode on in a parent Folder Format with subfolders and off in one child. Visit the parent, an inherited child, the exception and an unrelated folder. Compare mode changes and mark lifetime to Windows. Lock the format on the inherited child, navigate away and unlock, then relaunch and verify the rules persisted.

### Select Duplicates

Find duplicate bytes in two disposable folders, show the results in the file display, and choose oldest and newest in Select Duplicates. Check which paths remain selected. Change one file to different bytes with the same size and timestamp before applying and verify selection is refused. Compare the native workflow with the installed Windows duplicate collection.

Exercise each duplicate comparison mode on files with equal names and different bytes, different extensions with one stem, equal sizes and genuinely equal content. Verify the mode label, selected keeper and the effect of disabling recursion. Compare Windows MD5 settings separately from the port full-checksum mode.

Use the native pickers to add two unrelated roots and a reference file outside both. Disable recursion, find copies in both roots, and verify that a pair unrelated to the reference is excluded. Choose the oldest keeper with the external reference as the oldest copy. Confirm only scanned paths appear and both scanned copies are selected.

Set Minimum size to 1 KB with matching 1024-byte and smaller files. Verify only the 1024-byte copies are listed. Try a negative value and confirm the dialog stays open with a validation error. Compare the boundary with Windows.

Use disposable byte-identical files in a supported File Provider location. Evict one copy and confirm Ignore offline files skips it without downloading it. Disable the setting and check whether the provider recalls it for comparison. Evict a result after a scan and before applying Select Duplicates with the setting enabled, then verify selection is refused. Compare with the Windows reference and record the provider and macOS version.

Create an APFS hard link to a disposable file and a separate copy with identical contents. Scan with Ignore hard links off and on. Verify the option collapses aliases and retains the separate copy, then check the exact retained path against the Windows reference. Use an outside hard-link alias as a reference and verify it remains excluded from actionable rows.

Open Find Duplicate Files from Tools and its shortcut. Verify the utility panel docks below the file displays, leaves the Lister interactive before scanning, and closes without moving the active folder. Check both columns at compact and large window sizes, then compare the remaining results dialog and panel layout against installed Windows Opus.

Run a complete duplicate scan from the dock and verify results replace the source file display without an extra modal. Switch to the other pane before using the dock's Select button and confirm only the original result display is affected. Run a scan with no matches and check that the display is cleared. Cancel or invalidate a scan during work and verify the lister does not publish incomplete or stale results.

Scan two distinct duplicate groups in Details and verify each has a collapsible header. Sort and filter within the groups, then scan again and navigate to a normal folder. Confirm grouping and selection behave consistently with the Windows reference. Compare group labels and alternate view modes separately.

Repeat a size-based scan with Number groups off and on. Check that labels change without changing membership. Compare name, stem, name plus size and checksum labels and numbering order against Windows.

Select MD5 checksum and scan a known pair. Verify the group header agrees with the system md5 command and compare formatting with the Windows reference. Edit one file in place with the same length and restored timestamp and confirm Select refuses the stale scan.

Compare same-size files with a shared first half and a different second half. Verify they group at 50 percent, show the partial-match warning, and separate at 100 percent. Compare vendor sampling and rounding on the same fixture.

Search duplicate copies across two roots with *.mov and a semicolon exclusion. Verify only eligible candidates appear. Select a saved filename filter and compare its behavior with the Windows duplicate panel. Use a different-extension file as an explicit reference and compare whether it can match the eligible candidates.

Create duplicate files in sibling folders, exclude one by name pattern and the other by absolute path, and compare the remaining results with Windows. Repeat using regex: and a path that is also listed as a scan root.

From a single lister, scan with Destination and New tab selected. Verify the second pane opens, the original file display remains intact, and the panel Select affects only the destination result tab. Close the result tab and verify the previous folder is restored. Compare tab labels and collection persistence with Windows.

Save a duplicate preset with multiple roots, a filter, checksum accuracy, exclusions and a destination tab. Relaunch, select it and verify all inputs restore before scanning. Replace and then remove the disposable preset. Compare preset menu placement and behavior to Windows.

Keep a newer duplicate in a preferred folder while Keep oldest is selected. Verify the newer copy remains unselected, including when it is in a nested folder. Reverse two preferred folders and verify the keeper changes. Save and relaunch, then clear the remembered list. Compare ordering and folder-tree behavior with Windows.

In Select Duplicates, uncheck a child branch and verify the parent becomes indeterminate. Toggle the parent and verify descendants follow. With a pair split across included and excluded folders, apply the selection and confirm the excluded copy stays unselected. Compare exact checkbox semantics and keyboard behavior against Windows.

Open a duplicate selection tree with at least three sibling folders. Click the first checkbox and Shift-click the third. Verify the middle checkbox and any descendants in affected branches follow. Repeat in reverse and with a collapsed branch. Verify both bulk controls and compare anchor semantics against Windows.

Open duplicate results in a new tab, switch to a normal folder tab and return. Confirm the Duplicate Files label, grouped rows and Select behavior return. While away, replace a result with a separate file at the same path, then confirm the result tab requires a new scan and offers no actionable rows.
