# Texture Channel Packer

Open **Tools → Mesh Lab → Open Mesh Lab**, then select the **Texture Packer** tab.
Saved layouts containing the former standalone window transfer its packing settings
to this tab and close the old window automatically.

Drop a PNG, JPG/JPEG, TGA, PSD or TIF/TIFF file onto a slot thumbnail, or use its browse button.
Files can come from anywhere on disk. Source files are read directly; they are
never copied into the project and their Unity import settings are never changed.
TGA inputs support raw/RLE 24/32-bit true color and 8-bit grayscale, with either
image origin. PSD reads the saved merged RGB/grayscale image, including alpha;
save with Photoshop's compatibility option enabled so that composite is present.
PSD supports 8/16/32-bit channels and raw, RLE or ZIP compression. Layer edits
must be saved before dropping the file. TIFF reads the first image, including
alpha, from standard strips or tiles with raw, PackBits, LZW or Deflate compression.
16-bit channels are reduced to 8-bit; floating-point channels are clamped to 0–1.
PSD CMYK/Lab/indexed modes and PSB, TIFF CMYK/palette, BigTIFF, JPEG-compressed
TIFF and floating-point TIFF prediction are not supported. Paletted TGA, BMP
and EXR are also unsupported. Export an RGB PNG or a TIFF without those options.
PSD/TIFF sources are limited to 64 megapixels to bound decoder memory.

- **Albedo:** a whole RGB image plus an optional separate alpha, or separate R/G/B/A inputs.
- **MRCA:** separate R/G/B/A inputs by default; whole RGB plus optional alpha is also available.
- **Normal:** a whole RGB image, with an option to retain its alpha. No channel splitting.

Every scalar slot selects its source R, G, B or A channel. For example, put the
albedo in RGB, a mask in A, then choose R on the mask slot. An assigned alpha
slot overrides the RGB image's alpha. Empty scalar slots use an editable byte
constant (255 initially). With whole RGB, an empty alpha slot uses the RGB alpha
when enabled, otherwise the alpha constant. Files without alpha are opaque.
MRCA channel meaning is determined by your shader; no channel is inverted.

X and Y resolution are shared by all outputs (64–8192). The default lock keeps
X = Y; unlock for rectangular outputs. Images are resized across their full
extent with bilinear interpolation of stored channel bytes, without cropping,
gamma conversion, normal renormalization or premultiplication. Preview is a
small representation; final export is built at the requested resolution.

Selecting an FBX asset (or a scene instance linked to it) fills the base name and
output folder while **Use selected FBX name / folder** is enabled. Only the
leading `SM_` is removed. You can edit the name and folder manually. `T_` is
optional and is not duplicated if already present. Each block has a suffix
dropdown: `_Albedo`, `_Normal`, `_MRCA`.

Assigning an input enables its output. Save one block or all enabled blocks.
The output is a 32-bit TGA with RLE packets and eight alpha bits. Existing files
require an overwrite confirmation; output names must be distinct and cannot
overwrite loaded sources. Exporting under Assets imports only the resulting
TGA: the Albedo block uses sRGB, MRCA uses linear data, Normal uses the normal-map
importer regardless of the chosen filename suffix.
Outside Assets, only the file is written. Saving next to a model requires a
writable folder (registry/package-cache models may need a different destination).

Dropping a file immediately assigns its name and shows the loading state. Drag
hover and assignment do not read or check files on disk, so a slow disk or network
path cannot block those UI events. The thumbnail appears when background decoding
finishes; full decoding cannot be instantaneous for arbitrarily large sources.
Source reads and TGA/PSD/TIFF decoding run on workers; PNG/JPEG decoding uses Unity's
asynchronous texture downloader with local file URLs, without mipmaps. TGA reads
only its header and pixel payload from a stream. Read leases and file identity
checks on Windows, or background content hashes on Unix, reject source rewrites
or replacements during decoding. PNG RGBA pixels use bounded 4 MiB bulk copies;
other layouts use short time-budgeted conversions across editor updates.
At most two source decoders run at once, and repeated
assignments of one file share one load. Full-resolution resizing and RLE encoding
run in the background. Resize parallelizes rows with at most four workers,
leaving a processor available when possible; outputs are encoded sequentially
to avoid retaining several large output buffers. Preview is bounded to 256 pixels.
Cancel directly in the window to discard staged outputs; closing the
Mesh Lab window or switching to another tab also cancels loading and export. Unity texture creation and AssetDatabase
imports remain on the editor thread; Unity's own import phase can still pause
the editor. Importing is limited to one output per editor update. After files
commit, the import queue finishes their settings even if the window closes;
assembly reload drains that queue before unloading. Files are staged beside their destinations and
installed only after encoding completes, so an encoding failure leaves existing
textures intact and their `.meta` GUIDs are retained. If an output appears or
changes during packing, save is refused so its overwrite can be confirmed again.
Existing outputs are hashed before confirmation and verified on a worker before
installation; preserved file sizes and timestamps do not bypass this check.
Installation verifies the displaced backup against that snapshot and rolls back
on a mismatch. File installation and rollback use short, same-update asset editing scopes.
Backup hashing runs on a worker with Unity auto-refresh temporarily held; the
independent queue releases it on the editor thread and then imports the outputs.
Rollback retains recovery backups when an output has been edited externally.
A failure installing a later file restores earlier files in the same batch.
Source collision and Assets destination checks
resolve symbolic links and directory junctions; replacing a hard-linked destination detaches
that link without modifying other links. Under Assets, ignored folders such as
`Textures~` are rejected before export; a missing texture importer is reported as
an error. Filesystem path checks probe the destination's actual case rules,
including case-insensitive macOS volumes and case-sensitive Windows directories.
FBX selection changes made during export are applied when the export finishes.

Sources are cached while the tab is active; **Reload source files** picks up external
edits. Paths and settings are stored in project-specific editor preferences and survive
tab switches and domain reload. Temporary GPU textures are released when the tab
deactivates or Mesh Lab closes or reloads.
Clearing or replacing a slot cancels unused loads, so stale results cannot
recreate thumbnails. Failed cleanup attempts are reported and other staged files
are still cleaned. TGA decoding rejects impossible payload sizes before allocating
pixels, and supports cancellation between packets. No worker waits block the editor.
