## Fadi Tools

Fadi Tools is an all-in-one Windows app: convert files, compress videos and pictures, edit photos, videos and PDFs, rename files, find duplicates, inspect files and download from the internet. It works offline, it's free, and it's in English and Arabic.

**Download:** get **Fadi.Tools.zip** (everything included), unzip it and open **Fadi Tools.exe**. Or get **Fadi.Tools.exe** on its own; it downloads its engines the first time.
If Windows says "Windows protected your PC", click **More info → Run anyway**.

## What's new in 1.9.0

### File Transfer: send big files straight to a friend
- A new **File Transfer** page under Download. Drop files or folders of any size (10–20 GB is fine), press **Start sending**, and share the code (**Copy code**, or **Copy as a message** with instructions). Your friend opens **Receive**, pastes it, sees what's coming and its size, picks where to save it, and accepts.
- **Straight from PC to PC.** Your PC hosts the transfer itself: Fadi Tools asks your router to open a port while you send (UPnP) and closes it afterwards. Nothing sits in between, so the speed is limited only by your upload and your friend's download. Six connections run at once to keep far-away transfers at full speed.
- **When your PC can't host** (your internet provider shares your address, or the router won't open the port), a small server program, [sendme](https://github.com/n0-computer/sendme) (about 4 MB, free and open source), connects the two PCs directly through "hole punching" when it can, or through a relay when it can't. It's downloaded only when needed, asked first, and listed with its folder on the page and in Settings → Add-ons. If GitHub's download servers are busy, it tries a few more times and then says plainly that it's on GitHub's side.
- **Choose how it connects:** Automatic (hosting, then the server), Host only, or Server only. The port can be changed (default 47821). These live in File Transfer's own settings (the gear).
- **Connection check** before the code goes out: whether your router opened the port, whether your internet provider shares your address, IPv6, and whether the server is ready as a backup.
- **Picks up where it left off.** If the connection drops, it reconnects by itself. If you stop, close the app or the PC restarts, accept the same files into the same folder again and it carries on from the last finished piece.
- **Private and checked.** Only someone with the code can connect; everything is encrypted (AES-256) with a password only the code carries, and every piece is checked as it arrives (HMAC-SHA256), so the files arrive exactly as sent. The finished screen says **Verified**.
- **Live progress:** percent, how much of how much, speed, time left, and the route (straight from your friend's PC, over your local network, or through the server). It also shows in Activity, with a notification when done.
- **Accept before receiving.** Nothing downloads until the friend accepts; they choose the folder, it checks there's enough free space, and a name that's already there gets " (2)".
- **Keeps the PC awake** while sending or receiving, so Windows doesn't go to sleep in the middle.
- **Short codes.** The code carries only what's needed: about 65 characters when your PC can host for sure, about 170 when the server's backup route is included.
- **"I forwarded this port"** switch: when you've forwarded the port in your router yourself, hosting is trusted and the code stays short.
- **Cancel and delete** while receiving (stops and removes the unfinished files), and **Delete what arrived** for a transfer that was stopped earlier.
- **PCs on your network show up by themselves.** Every PC with Fadi Tools open on the same Wi-Fi or network appears in the friends panel under **On your network**. Drop files on one (or click it) and they go to it, with no code to share; it pops up there to accept. It shows your Windows user name, or the name you set in File Transfer's settings.
- **Send from anywhere:** right-click files → **Fadi Tools → Send to a friend**, right-click a folder → **Send with Fadi Tools**, or drop on the new **Send** slice of the Donut Wheel. The send card pops up by the mouse; Fadi Tools' own window stays where it is.
- **Allow through Windows Firewall** (once): a button that adds one firewall rule for Fadi Tools (Windows asks for administrator permission), replacing any "Cancel" answered before, so Windows never blocks your friend in the middle of a transfer.
- **Friends: send again without a code, wherever they are.** When your friend receives something from you, **Remember (name) as a friend** (on by default) makes you friends: both PCs keep a shared key. After that, your friend appears in the **Friends** panel (newest first, with when you last sent or received): drop files on them, or click them and choose files. It's handed straight to their PC, which proves who's who with the key, so nobody else can pretend to be your friend. They accept it there (or it's accepted automatically, if they chose that). If their PC can't be reached, you get the code to share as before. Right-click a friend (or the ⋯ on their row) to **Rename** them, switch **Accepted automatically / Asks first**, or **Forget** them. Fadi Tools must be running (open or by the clock) on their PC to receive.
- **Friends server:** reaching a friend's PC anywhere uses a second small server program, [dumbpipe](https://github.com/n0-computer/dumbpipe) (about 3 MB, from the makers of the transfer server). It gives your PC a permanent address; n0's free servers only introduce the two PCs, the offer goes directly when it can. Downloaded the first time it's needed, asked first, and listed in Settings → Add-ons.
- **A friends panel on the right.** Friends and the PCs on your network sit in one list, on both tabs. **Drop files on a friend and they go to them at once**; the row lights up as you drag over it. A green dot shows whose PC can be reached right now (checked when you open the page and about once a minute).
- **Click a friend and the middle becomes theirs** (like Blip): their round badge and name, with **Paste**, **Send files** and **Send a folder**. Drop files anywhere there, or press Ctrl+V, and they go to that friend at once. Click the friend again (or **Everyone**) to go back.
- **Rename a friend.** The name you give is only on your PC; leave it empty to go back to theirs.
- **The send card.** Dropping files anywhere else (or choosing them) pops up a card with a **picture of what's going** (a photo's or video's own thumbnail; for a folder, a zip or a program, the picture Windows shows for it), its name and size, and who it's for: tick one or **several friends at once**, and/or get a code for anyone else. While it sends, the same card shows how it's going for each person, wherever Fadi Tools' window is.
- **The receiver sees the picture too**, before accepting: the sender's PC sends along a small thumbnail.
- **A pop-up by the clock when someone sends to you** and Fadi Tools isn't in front: the picture, who it's from, the size, and **Accept** or **Decline**, without opening Fadi Tools. For a friend whose files are accepted automatically it shows the progress, then **Show in folder**.
- **Pause and Resume** while receiving: the download holds exactly where it is and carries on from there (in the page and in the pop-up).
- **Recent:** the last transfers either way, with who, the size and when, and **Show in folder** and **Send again**.
- **File Transfer settings** (the gear at the top): your name, where received files go, how it connects and the port, the "I forwarded this port" switch, showing this PC on your network, Windows Firewall, and the two servers with their folders. The page itself is only about sending and receiving.
- **Speed check:** when a PC is connected to its router at 100 Mbps (an older cable or port), the page says so, since that caps any transfer at about 12 MB/s.

### Video studio: steadier cut-outs, click to pick, and read aloud
- **Remove background on a video now uses an AI made for video** ([Robust Video Matting](https://github.com/PeterL1n/RobustVideoMatting)). It remembers the frames before, so hair and edges stay steady instead of flickering, and it's several times quicker than before. The first time, it downloads the model (about 15 MB) and the AI runtime (about 7 MB, if it isn't here yet) from GitHub, asked first.
- **Keep only what you click.** Right-click a video clip → **Keep only what I click…**. A frame from under the playhead opens: click the person or thing to keep (more clicks if a part is missing), right-click anything that should go, and the picked part lights up straight away. Press **Cut out** and the pick is followed through the whole clip, forwards and backwards from that frame; the result goes over the original like Remove background does. Also in the command palette.
- **Click to pick in the Photos studio.** On a picture layer: **Click to pick what to keep…** (in the layer's panel and its right-click menu) hides everything else with a mask that can be painted back. With a selection tool: **Click to pick…** turns the clicked thing into the selection.
- Click-to-pick uses MobileSAM and Segment Anything's decoder (about 37 MB, free and open source), downloaded once from Hugging Face the first time, asked first. Everything runs on your PC.
- **Read the captions aloud.** Right-click a captions clip → **Read the captions aloud…**: every caption is read at its own time onto a sound track. Arabic captions use the Arabic voice and the others the voice you pick; a caption whose voice runs longer than the caption is read a little faster so it ends near the next one.
- **Read aloud.** The Video studio's add bar has a new **Voice** item. Type or paste a text (the selected text or caption is filled in for you), pick a voice (English woman, English man, or Arabic man), set the speed, **Listen**, then **Add to video**: the speech goes on a sound track at the playhead. Arabic text picks the Arabic voice by itself. Free and open-source voices ([Piper](https://github.com/rhasspy/piper)) run by [sherpa-onnx](https://github.com/k2-fsa/sherpa-onnx), about 67 MB per voice plus the engine, downloaded from GitHub the first time, asked first, and offline after that.
- Settings → Add-ons lists the AI cut-out models and the read-aloud voices with their folder and size, and removes them.

### Download: pick the video's format
- Next to **Download** there's a format button: **MP4**, **MOV** or **MKV** (remembered).
- **MP4** and **MOV** are made H.264 video with AAC sound, which plays everywhere: phones, WhatsApp, iPhone, Mac, Windows and video editors. When the site's own copy already is, it's just put in the new container (no quality lost, takes a second); when it isn't (YouTube's 1440p and 4K, for example), it's converted, with its own percentage in the list.
- **MKV** keeps the site's own copy exactly as it is: no conversion, highest quality.
- The list shows the format on each download (for example "1080p MOV").

### Merge PDFs in the order you choose
- **Merge PDFs** and **Combine into PDF** on the Donut Wheel now open a window instead of joining the files straight away (they used to come out in whatever order Windows handed them over).
- Put the files in order by dragging a row, with the up and down arrows, **By name** or **By date**, or reverse the list. Each row shows the first page, the page count, the size and the date, and a button opens the file to check which one it is.
- **Moving files around is animated** in this window and in Merge videos and Merge sound files: when you sort, reverse, use the arrows or drop a row, the rows glide to their new places and the ones that moved glow for a moment, so you can see where each file went. A dragged row follows the mouse.
- The up and down arrows on a row always sit in the same place (the one that can't be used on the first or last row is shown faint), in all three merge windows.
- Give the new PDF a name before it's made. A PDF locked with a password is marked and left out.

### Crop videos from the Donut Wheel
- Drop a video on the new **Crop** slice and a window opens with a frame from it and the crop frame on top: drag the frame or its handles, snap it to a ratio (Free, Original, 1:1, 16:9, 9:16, 4:3, 3:4), or type the size in pixels.
- Slide along the video to look at any moment, or press **Play** (or **Space**) to watch it playing with the frame on top, so nothing important falls outside.
- Phone videos filmed upright show upright, and the size is the real one.
- A **Volume** slider (0–200%) sets how loud it plays in the window, and the cropped copy is saved at that volume. Left at 100% the sound is kept exactly as it was; turned all the way down, the copy has no sound.
- **Crop video** saves "video Cropped.mp4" next to the original. Progress shows in the window and in Activity, and it can be cancelled.

### Listen inside the Trim and Merge windows
- Click anywhere on a song's waveform (or a video's strip) to play from there, right in the Trim window. A playhead shows where it's playing. No other player (like VLC) opens any more.
- Videos play in the picture above the strip.
- **Space** plays or pauses. **I** and **O** set the start and the end where it's playing, without stopping.
- Dragging the ends still works as before. Dragging outside the kept part moves the nearer end; a plain click now plays instead.
- **Merge sound files** plays inside its window too: the play button on a row plays that song (pressed again it pauses), **Preview** plays them all in order, a line under the playing song shows how far it got, and **Space** plays or pauses.

### Choose the Donut Wheel's key
- Settings → Donut Wheel Customization has a new **Key that opens it**. Click it and press Shift, Ctrl, Alt, almost any other key, or a mouse side button. Escape cancels.
- The hint that follows the mouse, the Settings switch and the tour show the key you picked.

### Photos studio: make the canvas bigger
- The **Crop** frame can now be dragged **past the edges** by its corners or sides: the canvas grows there (the new space is see-through). The part being added shows a checker pattern, and the new size shows under the frame.
- **The canvas follows the picture.** Moving or sizing a picture (dragging, or typing a size or place) makes the canvas grow with it past the edge, live, and shrink with it when it gets smaller, so there's no empty space left around it. Other layers keep what of them can be seen. A picture that was already partly off the canvas (after cropping on purpose) doesn't drag the canvas along. Where things are on screen doesn't jump. A switch in the picture's panel, **Grow the canvas when it goes past the edge**, turns this off (it's remembered).
- **Back to the original size (W × H)** appears in the panel, in the right-click menu and while cropping whenever the canvas has been made bigger or smaller: one click puts the canvas and the pictures back exactly as they were, with nothing left to fit afterwards. Undo works too.
- **Stretch from the sides.** Picture layers have a handle in the middle of each side too: drag one to make the picture wider or taller while the opposite side stays put (hold Shift to keep its shape). The corners still resize it keeping its shape. Typing W or H also keeps the shape.
- Pictures that aren't turned now land on whole pixels, so a saved picture no longer has a faint white (half see-through) line along the picture's edge.
- When a picture is bigger than the canvas or moved partly off it, its panel says so and offers **Make the canvas fit everything** (also in the right-click menu, and **Fit the frame to everything** while cropping). Nothing outside the canvas is shown or saved, so this brings it all back in.

### Fixed
- Video studio: pressing play (or Space), above all right after clicking somewhere on the timeline, showed the picture frozen for a few frames before it caught up. Now the video's decoders get ready at the playhead while it's paused, and when they still need a moment, the sound and the picture wait for the first frame and start together.
- Video studio: clicking somewhere on the timeline while playing took about a second with big or heavy videos (above 720p, 60 fps, or VP9 / AV1 / HEVC, like YouTube's downloads). Such videos now get a light copy for the preview, made once in the background when they're added (720p, quick to jump around in, kept in the temporary folder for 3 days): jumping then takes about a tenth of a second. Exporting still uses the original.
- The play buttons (Video studio, Crop and Trim windows) can be clicked quickly one after another: a quick second click was taken as a double-click and ignored.
- The Donut Wheel's windows and the send card stay on top of other windows; they now have a **minimize** button (and their taskbar button minimizes them too), and keep working while minimized.
- Closing a menu, a drop-down list or a pop-up panel by clicking elsewhere in Fadi Tools sometimes sent the whole window behind other programs (Windows handed the focus away while the menu closed). It stays in front now.
- Photos studio: with the Move tool, dragging the empty space around the picture (or a spot with nothing to move) now moves the view, like the Hand tool. The mouse shows a hand there.
- Photos studio: typing a number in X, Y, W, H or the angle (then Enter or clicking away) showed "Something went wrong: String cannot be of zero length" and the number wasn't used. These boxes work again.
- Typing a new text on a video showed nothing in the preview until it was played: the text was drawn at the very first moment of its fade-in, when it's still invisible. The text being edited now shows whole while the video is paused (the fade-in still plays in the preview and in the exported video).

### Free and open-source parts downloaded on demand (not built into Fadi Tools)
- Robust Video Matting model: GPL-3.0, Peter Lin et al.
- MobileSAM image encoder: Apache-2.0, Chaoning Zhang et al. Segment Anything mask decoder: Apache-2.0, Meta. ONNX exports by vietanhdev (AnyLabeling).
- sherpa-onnx: Apache-2.0, k2-fsa. Piper voices (en_US lessac and ryan, ar_JO kareem) with espeak-ng data: their own licences (MIT / CC / GPL-3.0 for espeak-ng data).
- ONNX Runtime: MIT, Microsoft.
