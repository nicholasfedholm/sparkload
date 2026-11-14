SparkLoad Audit Kit v0.1
========================

This kit measures how long Vintage Story takes to load on your computer and
how much memory your mods use, so the results can be compared. You do not
need to install anything and you do not need to type any paths.

HOW TO USE (5 steps)

1. Unzip this kit anywhere you like (Desktop, Downloads, any folder).

2. Put the file SparkVS-0.4.0.zip into your Vintage Story Mods folder.
   To open that folder: press the Windows key + R, paste
       %APPDATA%\VintagestoryData\Mods
   and press Enter. Drag SparkVS-0.4.0.zip in. Do not unzip it.

3. Close Vintage Story if it is open. Then double-click RUN-AUDIT.cmd
   (in the kit folder). A black window opens and Vintage Story starts.
   If Windows shows a "protected your PC" screen, click "More info" and then
   "Run anyway" (the kit is not code-signed).

4. Pick your own world in the menu and load into it as you normally do.
   Stand still for about 5 minutes. If you like, grind a quern a bit while you
   wait. Then close the game normally (Exit in the menu). Do not close the
   black window yourself; it finishes on its own after the game closes.

5. Wait until the black window shows a file called VS-Audit-<date and time>.zip
   and a folder opens showing it. Open that zip and read MANIFEST.txt inside it
   to see exactly what is in it. Then send that one file.
   (It is also in the "output" folder of this kit.)

IF THE GAME IS NOT FOUND

If the black window says Vintage Story was not found, open the file
sparkload\portable-kit.json with Notepad, put the full path of
Vintagestory.exe between the quotes after "game_exe", save, and run
RUN-AUDIT.cmd again. Example:
    "game_exe": "D:\\Games\\Vintagestory\\Vintagestory.exe",
(Use two backslashes for each one.) Leave the other lines empty.
If your data folder is not in the usual place, fill in "data_dir" the same way.

IF YOU CLOSED THE BLACK WINDOW TOO EARLY

Double-click Collect-Audit.cmd. It packs the newest run into a zip.

WHAT THE KIT DOES

- Starts Vintage Story the normal way and watches how long loading takes.
- Watches memory use by mod while you play (this can make the game pause for
  a second or two every few minutes).
- When you close the game, writes a report and packs ONE zip containing:
  the loading report, the SparkVS capture from this session, the memory
  watch results, your client and server logs from this session, a list of your
  mods (file name, size and checksum), basic computer info (Windows version,
  processor, graphics card, memory, disk type and free space), and a
  MANIFEST.txt that lists every file in the zip.
- Some of these files show folder paths, which can include your Windows user
  name. Look through MANIFEST.txt and the files before sending if that matters.

WHAT THE KIT DOES NOT DO

- It installs nothing, except the one mod zip (SparkVS-0.4.0.zip) that you
  place yourself in step 2.
- It never changes, copies or opens your saves. It does not copy your mods.
  It does not collect settings files, passwords, screenshots or anything not
  listed above.
- It never closes your game and never limits its memory. You close the game.
- It uploads nothing. The zip stays on your computer until you send it.

HOW TO REMOVE IT

Delete the kit folder, and delete SparkVS-0.4.0.zip from your Mods folder.
That is all; nothing else was installed or changed.
