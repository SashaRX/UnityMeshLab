# Texture Channel Packer

Open **Tools → Mesh Lab → Texture Channel Packer**.

Drop a PNG, JPG/JPEG or TGA file onto a slot thumbnail, or use its browse button.
Files can come from anywhere on disk. Source files are read directly; they are
never copied into the project and their Unity import settings are never changed.
TGA inputs support raw/RLE 24/32-bit true color and 8-bit grayscale, with either
image origin. Paletted TGA, PSD, TIFF, BMP and EXR are not supported.

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

Full-resolution resizing and RLE encoding run on a worker while the editor stays
responsive. Cancel in the progress dialog to discard staged outputs; closing the
window also cancels the export. Files are staged beside their destinations and
installed only after encoding completes, so an encoding failure leaves existing
textures intact and their `.meta` GUIDs are retained. If an output appears or
changes during packing, save is refused so its overwrite can be confirmed again.
A failure installing a later file restores earlier files in the same batch.
Source collision checks
resolve symbolic links and junctions; replacing a hard-linked destination detaches
that link without modifying other links. Under Assets, ignored folders such as
`Textures~` are rejected before export; a missing texture importer is reported as
an error. Filesystem path checks probe the destination's actual case rules,
including case-insensitive macOS volumes and case-sensitive Windows directories.
FBX selection changes made during export are applied when the export finishes.

Sources are cached for the open window; **Reload source files** picks up external
edits. Paths and settings survive domain reload; temporary GPU textures are
released when the window closes or reloads.
