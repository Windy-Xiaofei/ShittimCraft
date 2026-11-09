#!/usr/bin/env python3
"""Copy the block and player textures this plugin needs out of a Minecraft client jar.

    python scripts/fetch-textures.py <path to client jar> [output dir]

The jar is the one your launcher downloads, usually:
    %APPDATA%\\.minecraft\\versions\\<version>\\<version>.jar

Textures stay Mojang's property. They are extracted locally for personal use and
are intentionally not committed to this repository.
"""

import os
import sys
import zipfile

# output name -> candidate paths inside the jar, first hit wins
TEXTURES = {
    "bricks.png": ["block/bricks.png"],
    "cobblestone.png": ["block/cobblestone.png"],
    "diamond_block.png": ["block/diamond_block.png"],
    "dirt.png": ["block/dirt.png"],
    "glass.png": ["block/glass.png"],
    "gold_block.png": ["block/gold_block.png"],
    "grass_block_side.png": ["block/grass_block_side.png", "block/grass_side.png"],
    "grass_block_top.png": ["block/grass_block_top.png", "block/grass_top.png"],
    "iron_block.png": ["block/iron_block.png"],
    "netherrack.png": ["block/netherrack.png"],
    "oak_planks.png": ["block/oak_planks.png", "block/planks_oak.png"],
    "obsidian.png": ["block/obsidian.png"],
    "sand.png": ["block/sand.png"],
    "stone.png": ["block/stone.png"],
    "tnt_side.png": ["block/tnt_side.png"],
    "tnt_top.png": ["block/tnt_top.png"],
    "steve.png": [
        "entity/player/wide/steve.png",   # 1.19.9+
        "entity/player/steve.png",
        "entity/steve.png",               # pre-1.19
    ],
}

PREFIX = "assets/minecraft/textures/"


def main() -> int:
    if len(sys.argv) < 2 or sys.argv[1] in ("-h", "--help"):
        print(__doc__.strip())
        return 0 if len(sys.argv) >= 2 else 2

    jar = sys.argv[1]
    if not os.path.isfile(jar):
        print(f"not a file: {jar}", file=sys.stderr)
        return 1

    here = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    out_dir = sys.argv[2] if len(sys.argv) > 2 else os.path.join(here, "assets", "textures")
    os.makedirs(out_dir, exist_ok=True)

    found = missing = 0
    with zipfile.ZipFile(jar) as z:
        names = set(z.namelist())
        for target, candidates in TEXTURES.items():
            src = next((PREFIX + c for c in candidates if (PREFIX + c) in names), None)
            if src is None:
                print(f"  MISS  {target}")
                missing += 1
                continue
            with z.open(src) as fh:
                data = fh.read()
            with open(os.path.join(out_dir, target), "wb") as fh:
                fh.write(data)
            print(f"  ok    {target:<22} <- {src[len(PREFIX):]}")
            found += 1

    print(f"\n{found} written to {out_dir}, {missing} missing")
    if missing:
        print("Missing ones usually mean a very old or a modded jar; the plugin falls back to flat colour.")
    return 0 if found else 1


if __name__ == "__main__":
    raise SystemExit(main())
