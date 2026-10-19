ELIXAR  (version 1.2.30)
The cure for a full C: drive
Made by Elisar
=======================

NEW IN 1.2.30
- Tools > Sysprep > "Answer file...": full support for the Sysprep answer
  file (unattend.xml). Set the computer name, owner, product key, time zone,
  languages and keyboards, which setup screens to skip, a local admin
  account, CopyProfile, keeping drivers and first sign-in commands.
- Check reads the file the way Windows Setup does and finds wrong names,
  keys, languages, passes and processor types before Sysprep runs.
- Save to Sysprep's folder, Save as, Load an existing file, or run Sysprep
  with it (the command is shown and asked first; /mode:vm for VMs).
- The password is only encoded in the file, so "Delete the answer file
  after setup" is on by default.

NEW IN 1.2.29
- Tools > Sysprep: gets the PC ready for Sysprep before you capture an image.
  Checks BitLocker, Store apps installed for one account only, an upgraded
  Windows, Sysprep's state from earlier runs, a pending restart, Store
  updates, other accounts, domain and free space, and reads Sysprep's log.
  Each problem comes with Microsoft's fix (asked first, restore point made).
  "Open Sysprep..." opens Windows' Sysprep; you choose its options there.

NEW IN 1.2.28 (security check)
- Undo never fills a drive: files you remove stay on their own drive when it
  is nearly full (a move there needs no room), files from C: go to your
  second drive, and if no drive has room nothing is moved and Elixar says so.
- Old copies of Elixar.exe left in Program Files after an update are removed.

NEW IN 1.2.27
- New version numbers: 1.2.27 is the release after 2.2.7. Elixar knows it is
  newer than every earlier version.
- Start the new version once: if an older Elixar is installed on this PC, it
  offers to update that copy in place. Nothing is uninstalled, so your
  settings, schedules and email sign-in stay as they are.
- Every question and message appears in Elixar's own look instead of the
  grey Windows box: light or dark, right to left in Hebrew, lists in a card
  with sizes on the right, a red button when something is deleted, and the
  safe choice on Enter/Esc. Ctrl+C still copies the text.

NEW IN 2.2.6
- Dark theme: no light lines left around lists, at the end of list headers
  or under group titles (like "3 similar photos").

NEW IN 2.2.5
- Dashboard: the Auto-delete tile has an on/off switch. On: every day old
  Recycle Bin items (30+ days by default) and expired Undo items are deleted,
  and the Safe clean runs when C: is low. Elixar asks once before turning it
  on. The details stay in Automation.
- Dark theme: no white frames; list headers, tabs, boxes, drop-downs and
  disabled buttons are dark; coloured text is easier to read.

NEW IN 2.2.4
- Files you remove yourself (Smart scan, Duplicates) go to Elixar's Undo
  instead of the Recycle Bin, kept 7, 30 or 90 days (your choice, 30 by
  default). Tools > Undo puts them back, also files from USB drives.
- Automation > Recycle Bin and Undo (the daily tidy):
  * empties only what has been in your Recycle Bin for more than 14, 30, 60
    or 90 days (recent deletions stay), on every internal drive
  * deletes Undo items whose time is up
  * optional: runs the Safe clean right away when C: is under your limit
  * "Empty old Recycle Bin items now..." does it at once
  Click Save in Automation once to start it.

NEW IN 2.2.3 (security)
- Uninstallers start through cmd.exe /d: an "AutoRun" command any program can
  set for your account no longer runs with administrator rights.
- Elixar refuses to start if an Elixar.exe.config, .local or .manifest was put
  next to it (Windows or .NET would obey them). Elixar.exe now has a manifest.
- .NET "profiler" settings for your account (COR_PROFILER and similar) are
  found before Elixar asks for administrator rights, and you can remove them.
- As administrator, programs Elixar starts get Windows' own Program Files,
  Path and ComSpec, even if they were changed for your account.
- Docker's prune runs with your normal rights.
- Update downloads have a size limit (the signature check is unchanged).

NEW IN 2.2.2
- OneDrive tab: appears only when OneDrive is signed in on this PC with your
  account email (Elixar connects to nothing; it reads OneDrive's settings).
  Lists files you haven't opened for 1-12 months and makes the ones you pick
  online-only, like OneDrive's own "Free up space": nothing is deleted, they
  stay in OneDrive and File Explorer. "Always keep on this device" files are
  left alone. The Dashboard suggests it when 1 GB or more could be freed.
- Smart scan: similar photos (resized, edited, burst shots), empty folders,
  and "Compress selected (keep them)" for files you rarely open.
- Tools > Leftovers: folders of apps you removed (AppData, ProgramData), 3
  months untouched. Nothing ticked; what you pick goes to Undo for 7 days.
- Dashboard: what grew this week. Footer: Export report (print to PDF).
- Settings: minimize Elixar to the icon next to the clock (shows C: free).
- A monthly reminder to Smart scan your other drives.
- Security: OneDrive files are checked by OneDrive's own cloud mark; empty
  folders are removed one at a time only if still empty; hard-linked files
  are never compressed; huge pictures are skipped; report text is web-safe.

NEW IN 2.2.1
- Your other drives (D:, E:, USB): pick one on the Dashboard.
  * Smart scan: reads the drive once and finds duplicate files (you choose
    which copy stays: oldest, newest or shortest path) and big files not used
    for 6 months, a year or 2 years. Nothing is ticked for you; what you pick
    goes to the Recycle Bin. Windows, apps, games and AppData are never listed.
  * Clean D:: its Recycle Bin, disk-check leftovers (found.000), Steam shader
    cache and unfinished downloads. Nothing ticked; never in the weekly clean.
  * Elixar tells a data drive (no Windows on it) from one with its own Windows.
- Duplicates (Tools): "Add a whole drive..." searches any drive, or all.
- Low-space alerts cover C: and every internal drive.
- A USB stick has no Recycle Bin: Elixar warns before deleting there.

NEW IN 2.2
- Dashboard: shows which disk each drive is on and this PC's name, for
  example "Disk 0 of ELISAR-PC" (the number Windows' Disk Management uses).
- Tabs along the top with Settings on the right.
- Security: 11 fixes so programs with normal rights can't use Elixar's
  administrator rights (schedules start through a protected launcher;
  Program Files, Temp and AppData are taken from Windows' own records;
  links open without admin rights; PowerShell and DLLs only from Windows;
  TLS 1.2+ only; email moves check the mailbox first). See the user guide.
- After updating: open Elixar once (as usual). It moves your weekly clean and
  email schedules to the protected launcher by itself.

NEW IN 2.1
- New Dashboard: a ring shows how full C: is, with one plain sentence and a
  "Free ... safely" button (safe caches and temp files only; it asks first).
  Six tiles: Disk health, Space trend, Last clean, Automation, Email reports,
  Undo. Plus quick wins, other drives and recent activity.
- Drive switcher: C:, any other drive (how full it is, its biggest folders
  and largest files) or All drives side by side. Overview / Details for C:.
- Security: 14 fixes from a full review (uninstallers in folders other
  programs can change, exact publisher check, duplicates, email servers,
  email addresses kept out of the shared log...). See the user guide.

NEW IN 2.0
- Email account (Settings): connect Gmail or Outlook/Hotmail with their own
  safe sign-in (no password, send-only), or any other email (Yahoo, iCloud,
  AOL, GMX, Zoho...) with an app password over secure connections.
- Email reports: weekly auto-clean report, alerts (C: almost full, a drive
  that may fail) and a weekly summary with a free-space chart. They never
  contain links or file names. Settings shows how the last email went.
- Email cleaning (Email tab, optional): finds spam, old ads, notifications,
  newsletters and large old emails and moves the ones you pick to the trash
  (restorable). OFF until you turn it on in Settings; Elixar asks you first.
  Starred, flagged and Important emails are never touched, and Elixar only
  looks at senders, dates and sizes, never at what your emails say.

NEW IN 1.9.1
- The blue "Windows protected your PC" box: click More info > Run anyway once,
  then Settings > "Install Elixar on this PC" and start Elixar from the Start
  menu. The installed copy has no "downloaded" mark, so the box stops.
- Security: 7 fixes against other programs tricking Elixar's administrator
  rights (swapped files, links, fake tools, Temp files). See the user guide.

NEW IN 1.9
- Disk health (Tools, and a button on the Dashboard): reads your drives' own
  health, wear, temperature and error data and warns before a drive fails.
  Read only: nothing is written to the drives, nothing is downloaded or sent.
  The daily low-space check also warns you if a drive's health gets worse.
- Settings tab: switch between English and Hebrew, and light, dark or
  "follow Windows", then click "Apply and restart Elixar".
- Security: Windows tools (PowerShell, cmd, schtasks...) are always started
  from the Windows folder by full path, never a same-named file next to Elixar.

NEW IN 1.8
- Free space for me (Cleaner): type how many GB you want back; Elixar ticks the safest items.
- Windows.old (Cleaner, Caution) and the hibernation file (Tools > System space).
- Move off C: (Tools): move Downloads, Videos, Documents... to another drive, safely.
- More caches: Telegram, WhatsApp, Zoom, Firefox/Brave/Opera/Vivaldi, Docker.
- Space trend and Folder map (Dashboard).
- Settings (footer): Hebrew or English, light or dark, Install Elixar on this PC,
  Check for updates (works once Elixar is published on GitHub; see source\README.md).

USER GUIDE
Open UserGuide.html in this folder, or in Elixar press F1 (or click
"User guide" in the footer > Open / Save a copy). It works offline and
your browser can print it or save it as PDF (Ctrl+P).

CHECK YOUR DOWNLOAD (optional)
Elixar is not signed with a paid certificate, so compare its fingerprint:
open a Command Prompt in this folder and type
   certutil -hashfile Elixar.exe SHA256
The result must match SHA256.txt. In the program, the footer link
"Verify this copy" shows the same fingerprint.

FIRST: RUN THE SAFE SELF-TEST
1. Unzip this folder anywhere (for example to your Desktop).
2. Double-click "Run self-test (safe).bat".
   If Windows says "Windows protected your PC": click More info > Run anyway.
   (Install Elixar from its Settings tab afterwards and the box stops.)
   - It only creates and deletes files inside its own test folder in Temp.
   - Your real cleanup items are only measured; nothing of yours is deleted.
   - The program window opens with cleaning switched off, measures, and closes.
3. A message says "All tests passed" or how many failed.
   The details are in selftest-report.txt in this folder.

THEN: CLEAN
1. Double-click "Elixar.exe" and click Yes on the Windows prompt.
2. Click a row to read what it removes. Tick what you want, click "Clean selected".
   You see the full list and the estimated space before anything is deleted.
- Green "Safe" items are ticked by default. Orange "Caution" items are never
  ticked for you: read their note first.
- If Stremio, Chrome or Edge is open, it asks before closing them.
- A restore point is created first (you can untick that).

DASHBOARD TAB (opens first)
- Drive bar: drive C: split into Critical, Caution, Review, Safe, other and free.
- Four cards: how many GB are in each level and what to do about it.
- "Everything on C:": every known location, grouped from Critical to Safe,
  biggest first, with its share of used space and what to do.
  Folders inside other listed folders are never counted twice.
- "Find largest files": scans the whole drive (2-5 minutes, read-only) and
  lists files over 50 MB from Critical to Safe. Double-click one to show it
  in its folder. Nothing is deleted from the Dashboard.
- F5 or "Refresh" measures again. It also refreshes after every clean.
- Double-click a folder row, or click "Explore C:\", to see what is inside it,
  biggest first. Double-click again to go deeper, Backspace to go up.
  This also shows what "Other used" is made of. Read-only.

APPS TAB - PROGRAMS
- Every installed program with its size on disk and when it was last used
  (estimated from Windows' own records). Orange = no use found in 90 days.
- Click a column to sort. "Uninstall..." runs the app's own uninstaller,
  the same one Windows uses. "Open folder" shows where it lives.
- This is the safe way to shrink the Critical part of C:.

APPS TAB - BUILT-IN AND STORE APPS (Copilot, Xbox, News, Solitaire...)
- Optional (green): safe to remove, e.g. Copilot, News, Weather, Tips,
  Solitaire, Clipchamp, Candy Crush.
- Your decision (blue): removable, the note says what you lose, e.g. the
  Xbox app (Game Pass), Game Bar (Win+G recording), Xbox sign-in (some games).
- Keep (gray, locked): the Microsoft Store, Windows Security, picture and
  video codecs, language packs, driver panels and parts of Windows.
  These can never be ticked.
- Nothing is ticked for you. A restore point is made first.
- Removed apps can be reinstalled from the Microsoft Store anytime.

REMOVING APPS FROM OTHER TABS
- Dashboard and Cleaner: "Remove apps..." jumps to the list above.
- Largest files: right-click a file > "Uninstall <app>..." when the file
  belongs to an installed app.
- Folder drill-down: "Uninstall <app>..." when the folder you are in, or the
  one you selected, belongs to an installed app.

TOOLS TAB
- Drivers: shows this PC's hardware (CPU, graphics, motherboard, disks,
  network) and finds drivers it doesn't need:
    Old versions: Windows keeps every driver version you ever installed
      (often old NVIDIA drivers, 1 GB+ each). The newest is kept.
    Not for this Windows: built for another kind of Windows (32-bit/ARM).
    No hardware in this PC uses these: leftovers from devices you no longer
      have (pre-2015 ones are marked "made for Windows 7/8").
    Not plugged in now: devices like a phone or controller. Never ticked
      for you - your decision.
  Never offered: drivers a device uses, drivers Windows needs to start,
  printer and extension drivers. Each one is backed up to Undo, then
  removed with Windows' own pnputil.
- Startup: everything that starts with Windows, checked for who signed it
  and where it runs from. "Needs a look" = hidden scripts, programs in
  temp or download folders, broken or missing signatures. Turn entries off
  or on (the same switch as Task Manager), remove leftovers whose program
  is gone (kept in Undo), or scan any of them with Microsoft Defender.
  Not signed does not mean harmful: many small tools are not signed.
- System space:
    WinSxS: see what DISM can really clean, then clean it.
    Reserved storage: turn off to free ~7 GB (turn on again before big updates).
    Restore points: see how much space they use and set a limit.
    Compact OS: Windows compresses its own files (~2 GB), reversible.
    WSL and Docker disks: shrink virtual disks that only ever grow.
- Duplicates: finds files with exactly the same content in your own folders
  (Documents, Pictures, Videos, Music, Desktop, Downloads, or folders you add).
  Windows, apps and AppData are never searched. Extra copies go to the
  Recycle Bin, and at least one copy of each file is always kept.
- Undo: Caution items (like $GetCurrent, $WinREAgent, Adobe and NVIDIA update
  files) and removed drivers are kept here for 7 days, on your D: drive when
  there is one, so C: space is freed right away. Restore puts them back
  exactly where they were. After 7 days they are deleted for good.

NEW IN THE CLEANER TAB (none ticked by default, none in the weekly clean)
- Discord, Microsoft Teams, Spotify, Epic Games Launcher, EA app and Steam caches
- VS Code cache, old VS Code extension versions, old Claude Code versions
- Developer caches (npm, pip, yarn, NuGet, Gradle, Go) and NuGet packages
- NVIDIA app update downloads (kept in Undo for 7 days)
If an app is open, Elixar asks before closing it.

PRIVACY (Cleaner tab, never ticked for you, never in the weekly clean)
- Browsing history and cookies for Chrome, Edge and Brave; Firefox cookies.
  Saved passwords, bookmarks and logins are never touched.
- Recent files and jump lists, typed history (Win+R, Explorer address and
  search bars, Open/Save windows), the clipboard and the DNS cache.

PHONE TAB
- Android (Samsung, Xiaomi, Pixel...): plug in with a USB cable after turning
  on "USB debugging" (the tab shows how). Elixar uses Google's free Android
  tools; the button downloads them and checks Google's signature.
    Clean: cache of every app, gallery thumbnails, WhatsApp statuses (safe);
      deleted photos in the trash, WhatsApp sent media, Telegram downloads,
      old .apk files (your decision - copied to this PC first).
    Apps: pre-installed extras you can remove for your account only (can be
      put back), your own apps by size and last use.
    Big files: move big videos and files to this PC (copied, checked, then
      deleted from the phone).
- iPhone: install "Apple Devices" from the Microsoft Store, plug in, unlock
  and tap Trust. Elixar shows storage and moves big photos and videos to
  this PC. Apple doesn't let a PC clean apps, so the Clean tab lists what to
  clean on the iPhone itself.
- Copies go to "Phone backup" on your second drive (or Pictures).

SECURITY (new in 1.7)
- Schedules run a protected copy in C:\Program Files\Elixar that only
  administrators can change. Open the Automation tab and click Save once
  after updating, so existing schedules use it.
- The Undo folder is locked to administrators; anything another program
  puts there is ignored, and Restore never writes into Windows' folders.
- Every file is checked for its real location just before it is deleted,
  so a link planted in a temp folder can't redirect Elixar. Windows'
  own folders, drive roots and your main folders are never deleted.
- Uninstallers of apps installed for your account only run with your
  normal rights, not as administrator.
- Logs and the last scan are kept in C:\ProgramData\Elixar.

AUTOMATION TAB
- Low-space alert: a Windows notification when C: drops under the limit you
  choose (default 15%), checked daily and a few minutes after you sign in.
- Weekly auto-clean: cleans only the default Safe items on the day and time
  you choose. Never Caution items, Recycle Bin, browser caches or DISM.
  Open apps are skipped, never closed. A notification shows what was freed.
- Click Save to turn them on or off. They run through Windows Task Scheduler
  even when the program is closed, from the protected copy in Program Files.

RESCAN (Cleaner tab)
- Click "Rescan (F5)" or press F5 any time to measure again.
- The "Change" column shows how much each item grew (orange) or shrank
  (green) since the previous scan, and the line under the free space shows
  when that scan was and how much free space changed.
- The last scan is remembered (C:\ProgramData\Elixar\last-scan.txt), so tomorrow's scan is
  compared with today's. After cleaning, the automatic rescan shows what
  each item freed.

WHAT IT NEVER TOUCHES
Windows system files, Program Files, the Windows\Installer folder,
your documents, pictures, downloads or any other personal files.

DASHBOARD
"Save scan for dashboard" reads drive C: (nothing is deleted) and saves
c-drive-scan.json to your Desktop for the "Elixar Drive Map" dashboard.

FILES
Elixar.exe         the program (uses .NET Framework 4, built into Windows 10/11)
Run self-test (safe).bat    the safe self-test
UserGuide.html              the full user guide (also built into Elixar: F1)
SHA256.txt                  the fingerprint of Elixar.exe, to check your download
C:\ProgramData\Elixar\      cleaner-log.txt (what was cleaned), last-scan.txt,
                            auto-clean-history.txt (weekly clean results)
C:\Program Files\Elixar\    protected copy used by the schedules
selftest-report.txt         created by the self-test
source\                     the program's source code

UPDATING FROM "C DRIVE CLEANER"
- Elixar is the same program with a new name. Delete the old
  "C Drive Cleaner" folder after you have extracted Elixar.
- If you had the low-space alert or weekly clean switched on, open the
  Automation tab once and click Save. Elixar moves those schedules to its
  own name and removes the old ones.
